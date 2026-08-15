using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Threading.Channels;
using HyperDrop.Core.Abstractions;
using HyperDrop.Core.Model;

namespace HyperDrop.Core.HyperV;

/// <summary>
/// Copies files into a Windows guest over PowerShell Direct, for VMs where the Guest Service
/// Interface is unavailable.
/// </summary>
/// <remarks>
/// <para>
/// A single <c>powershell.exe</c> worker is kept alive for the whole batch and driven over a tiny
/// line protocol on its standard input/output. Creating a <c>PSSession</c> to a VM takes seconds,
/// so reusing one across files matters.
/// </para>
/// <para>
/// The worker streams the file in chunks and reports exact byte counts, which makes progress here
/// more precise than the whole-percent values the Guest Service Interface reports. The trade-off is
/// that it needs guest credentials and only works against Windows guests.
/// </para>
/// <para>
/// Hosting the engine in a child process rather than referencing the PowerShell SDK keeps roughly
/// 150 MB out of the build output. Credentials are written to the child's standard input and never
/// appear on a command line or on disk.
/// </para>
/// <para>
/// The copier does not own <see cref="GuestCredentials"/>: the queue creates a new copier whenever
/// the target changes, and the same credentials are reused across all of them.
/// </para>
/// </remarks>
public sealed class PowerShellDirectFileCopier : IGuestFileCopier
{
    private const string ResourceName = "HyperDrop.Core.HyperV.PowerShellDirectWorker.ps1";
    private const int DefaultChunkSize = 2 * 1024 * 1024;

    private static readonly TimeSpan StartupTimeout = TimeSpan.FromMinutes(2);

    private readonly string _vmName;
    private readonly GuestCredentials _credentials;
    private readonly int _chunkSize;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly StringBuilder _diagnostics = new();

    private Process? _worker;
    private Channel<string>? _output;
    private bool _disposed;

    public PowerShellDirectFileCopier(
        string vmName,
        GuestCredentials credentials,
        int chunkSizeBytes = DefaultChunkSize)
    {
        _vmName = vmName;
        _credentials = credentials;
        _chunkSize = Math.Clamp(chunkSizeBytes, 64 * 1024, 16 * 1024 * 1024);
    }

    public string DisplayName => "PowerShell Direct";

    public async Task CopyAsync(
        GuestCopyRequest request,
        IProgress<CopyProgress> progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(progress);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!File.Exists(request.SourcePath))
        {
            throw new HyperDropException($"\"{request.SourcePath}\" no longer exists on this PC.");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var output = await EnsureWorkerAsync(cancellationToken).ConfigureAwait(false);

            progress.Report(CopyProgress.FromBytes(0, request.SizeBytes));
            await SendCommandAsync(BuildCopyCommand(request)).ConfigureAwait(false);
            await ReadCopyResultAsync(request, progress, output, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // A chunked copy cannot be interrupted cleanly, so the worker is torn down and the
            // next file starts a fresh session.
            await KillWorkerAsync().ConfigureAwait(false);
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_worker is { HasExited: false })
        {
            try
            {
                await _worker.StandardInput.WriteLineAsync("QUIT").ConfigureAwait(false);
                await _worker.StandardInput.FlushAsync().ConfigureAwait(false);
                await _worker.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or InvalidOperationException)
            {
                // Fall through to a hard kill.
            }
        }

        await KillWorkerAsync().ConfigureAwait(false);

        _gate.Dispose();
    }

    private async Task ReadCopyResultAsync(
        GuestCopyRequest request,
        IProgress<CopyProgress> progress,
        ChannelReader<string> output,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            string line;

            try
            {
                line = await output.ReadAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (ChannelClosedException)
            {
                throw new HyperDropException(
                    "The PowerShell Direct session ended unexpectedly.",
                    Diagnostics());
            }

            if (line.StartsWith("#P ", StringComparison.Ordinal))
            {
                if (long.TryParse(line.AsSpan(3), CultureInfo.InvariantCulture, out var sent))
                {
                    progress.Report(CopyProgress.FromBytes(sent, request.SizeBytes));
                }

                continue;
            }

            if (line == "#OK")
            {
                progress.Report(CopyProgress.Complete(request.SizeBytes));
                return;
            }

            if (line.StartsWith("#E ", StringComparison.Ordinal))
            {
                var message = line[3..].Trim();
                throw new HyperDropException(message, HyperVErrorMessages.RemedyFor(message));
            }

            if (line.StartsWith("#FATAL ", StringComparison.Ordinal))
            {
                throw new HyperDropException(line[7..].Trim());
            }
        }
    }

    private string BuildCopyCommand(GuestCopyRequest request)
    {
        // Paths are base64-encoded so spaces and non-ASCII names survive the line protocol intact.
        var source = Convert.ToBase64String(Encoding.UTF8.GetBytes(request.SourcePath));
        var destination = Convert.ToBase64String(Encoding.UTF8.GetBytes(request.DestinationPath));
        var overwrite = request.OverwriteExisting ? '1' : '0';
        var createFullPath = request.CreateFullPath ? '1' : '0';

        return $"COPY {source} {destination} {overwrite} {createFullPath}";
    }

    private async Task SendCommandAsync(string command)
    {
        var worker = _worker ?? throw new HyperDropException("The PowerShell Direct session is not running.");

        await worker.StandardInput.WriteLineAsync(command).ConfigureAwait(false);
        await worker.StandardInput.FlushAsync().ConfigureAwait(false);
    }

    private async Task<ChannelReader<string>> EnsureWorkerAsync(CancellationToken cancellationToken)
    {
        if (_worker is { HasExited: false } && _output is not null)
        {
            return _output.Reader;
        }

        await KillWorkerAsync().ConfigureAwait(false);

        var startInfo = new ProcessStartInfo
        {
            FileName = ResolvePowerShellPath(),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-EncodedCommand");
        startInfo.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(LoadWorkerScript())));

        // Windows PowerShell 5.1 cannot load PowerShell 7's Core-edition modules. If this process
        // was itself launched from a pwsh session, the inherited PSModulePath points at them and
        // module resolution fails with a confusing "module could not be loaded" error. Clearing it
        // makes the child compute its own correct default.
        startInfo.Environment.Remove("PSModulePath");

        var worker = Process.Start(startInfo)
            ?? throw new HyperDropException("Could not start Windows PowerShell for PowerShell Direct.");

        _worker = worker;
        _diagnostics.Clear();

        var channel = Channel.CreateUnbounded<string>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        _output = channel;

        _ = Task.Run(() => PumpAsync(worker, channel.Writer), CancellationToken.None);
        _ = Task.Run(() => PumpDiagnosticsAsync(worker), CancellationToken.None);

        await HandshakeAsync(worker, channel.Reader, cancellationToken).ConfigureAwait(false);

        return channel.Reader;
    }

    /// <summary>
    /// Feeds credentials and connection details to the worker and waits for it to report that the
    /// guest session is live.
    /// </summary>
    private async Task HandshakeAsync(
        Process worker,
        ChannelReader<string> output,
        CancellationToken cancellationToken)
    {
        await worker.StandardInput.WriteLineAsync(_credentials.UserName).ConfigureAwait(false);
        await worker.StandardInput.WriteLineAsync(_credentials.RevealPassword()).ConfigureAwait(false);
        await worker.StandardInput.WriteLineAsync(_vmName).ConfigureAwait(false);
        await worker.StandardInput.WriteLineAsync(_chunkSize.ToString(CultureInfo.InvariantCulture))
            .ConfigureAwait(false);
        await worker.StandardInput.FlushAsync().ConfigureAwait(false);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(StartupTimeout);

        try
        {
            while (true)
            {
                var line = await output.ReadAsync(timeout.Token).ConfigureAwait(false);

                if (line == "#READY")
                {
                    return;
                }

                if (line.StartsWith("#FATAL ", StringComparison.Ordinal))
                {
                    var message = line[7..].Trim();

                    throw new HyperDropException(
                        $"Could not open a PowerShell Direct session to \"{_vmName}\": {message}",
                        "Check the guest username and password, and that the guest is running Windows and has finished booting.");
                }
            }
        }
        catch (ChannelClosedException)
        {
            throw new HyperDropException(
                $"Windows PowerShell exited before connecting to \"{_vmName}\".",
                Diagnostics());
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new HyperDropException(
                $"Timed out opening a PowerShell Direct session to \"{_vmName}\".",
                "Confirm the guest has finished booting and accepts PowerShell Direct sign-ins.");
        }
    }

    private static async Task PumpAsync(Process worker, ChannelWriter<string> writer)
    {
        try
        {
            while (await worker.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                await writer.WriteAsync(line).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // The worker went away; completing the channel surfaces that to the reader.
        }
        finally
        {
            writer.TryComplete();
        }
    }

    private async Task PumpDiagnosticsAsync(Process worker)
    {
        try
        {
            while (await worker.StandardError.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                lock (_diagnostics)
                {
                    if (_diagnostics.Length < 2000)
                    {
                        _diagnostics.AppendLine(line);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // Nothing further to collect.
        }
    }

    private string? Diagnostics()
    {
        lock (_diagnostics)
        {
            return _diagnostics.Length == 0 ? null : _diagnostics.ToString().Trim();
        }
    }

    private async Task KillWorkerAsync()
    {
        var worker = _worker;
        _worker = null;
        _output?.Writer.TryComplete();
        _output = null;

        if (worker is null)
        {
            return;
        }

        try
        {
            if (!worker.HasExited)
            {
                worker.Kill(entireProcessTree: true);
                await worker.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException or SystemException)
        {
            // Already gone.
        }
        finally
        {
            worker.Dispose();
        }
    }

    /// <summary>
    /// Uses Windows PowerShell 5.1, which ships in the box and supports <c>New-PSSession -VMName</c>.
    /// </summary>
    private static string ResolvePowerShellPath()
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");

        return File.Exists(path) ? path : "powershell.exe";
    }

    private static string LoadWorkerScript()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{ResourceName}' is missing.");

        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
