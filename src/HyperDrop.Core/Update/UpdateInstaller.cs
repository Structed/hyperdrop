using System.Diagnostics;
using System.IO.Compression;

namespace HyperDrop.Core.Update;

/// <summary>
/// Replaces the files HyperDrop runs from with the contents of a verified package, then starts the
/// new build.
/// </summary>
/// <remarks>
/// <para>
/// No helper process and no installer. Windows refuses to delete or overwrite a running image, but
/// it does allow that image to be <em>renamed</em> within its directory, and a renamed executable
/// keeps running perfectly well. So the swap renames the current files aside to <c>.old</c>, moves
/// the new ones into place, and relaunches. The leftovers are deleted on the next startup, which
/// doubles as evidence that the new build actually starts.
/// </para>
/// <para>
/// Staging happens inside the installation folder rather than in the temp directory so that every
/// move is a rename on one volume, instead of a copy that could half-finish.
/// </para>
/// </remarks>
public sealed class UpdateInstaller
{
    /// <summary>Suffix given to the files being replaced.</summary>
    internal const string BackupSuffix = ".old";

    private const string StagingFolderName = ".hyperdrop-update";

    /// <summary>Antivirus can hold a freshly written file open for a moment.</summary>
    private const int MoveAttempts = 5;

    private static readonly TimeSpan MoveRetryDelay = TimeSpan.FromMilliseconds(150);

    private readonly Func<string, string, bool> _launcher;

    public UpdateInstaller(
        string? executablePath = null,
        Func<string, string, bool>? launcher = null)
    {
        ExecutablePath = executablePath
            ?? Environment.ProcessPath
            ?? throw new InvalidOperationException("The running executable path could not be determined.");

        InstallDirectory = Path.GetDirectoryName(ExecutablePath)
            ?? throw new InvalidOperationException("The running executable has no containing folder.");

        _launcher = launcher ?? Launch;
    }

    /// <summary>
    /// The running image. <see cref="Environment.ProcessPath"/> rather than
    /// <c>Assembly.Location</c>, which is empty under single-file publishing.
    /// </summary>
    public string ExecutablePath { get; }

    public string InstallDirectory { get; }

    /// <summary>
    /// Whether the installation folder can be written to. A copy unzipped into <c>Program Files</c>
    /// or onto a read-only share cannot be swapped, and HyperDrop deliberately runs unelevated, so
    /// the answer decides between updating and pointing at the Releases page.
    /// </summary>
    public bool CanInstall()
    {
        var probe = Path.Combine(InstallDirectory, $".hyperdrop-write-probe-{Guid.NewGuid():N}");

        try
        {
            using (var stream = File.Create(probe, 1, FileOptions.DeleteOnClose))
            {
                stream.WriteByte(0);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>
    /// Extracts a verified package over the installation, rolling back if any part of it fails.
    /// </summary>
    /// <exception cref="UpdateException">
    /// The folder is not writable, the package is not usable, or the swap failed and the previous
    /// version was restored.
    /// </exception>
    public void Apply(string packagePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packagePath);

        if (!CanInstall())
        {
            throw new UpdateException(UpdateFailure.DestinationNotWritable);
        }

        var staging = Path.Combine(InstallDirectory, StagingFolderName);
        var executableName = Path.GetFileName(ExecutablePath);

        try
        {
            DeleteDirectory(staging);
            Directory.CreateDirectory(staging);

            // ExtractToDirectory rejects entries that would escape the destination, so a crafted
            // archive cannot write outside the staging folder.
            ZipFile.ExtractToDirectory(packagePath, staging);

            var staged = Directory
                .GetFiles(staging, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(staging, path))
                .ToList();

            // A package that does not contain the executable would otherwise "succeed" while
            // leaving the app exactly as it was.
            if (!staged.Any(relative =>
                    string.Equals(Path.GetFileName(relative), executableName, StringComparison.OrdinalIgnoreCase)))
            {
                throw new UpdateException(UpdateFailure.SwapFailed);
            }

            Swap(staging, staged);
        }
        catch (UpdateException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException)
        {
            throw new UpdateException(UpdateFailure.SwapFailed, ex);
        }
        finally
        {
            DeleteDirectory(staging);
        }
    }

    /// <summary>
    /// Starts the freshly installed executable. The caller shuts this process down afterwards.
    /// </summary>
    /// <exception cref="UpdateException">The new version could not be started.</exception>
    public void Relaunch()
    {
        if (!_launcher(ExecutablePath, InstallDirectory))
        {
            throw new UpdateException(UpdateFailure.LaunchFailed);
        }
    }

    /// <summary>
    /// Deletes the files left behind by a previous update. Safe to call on every startup: reaching
    /// this point is what proves the new build runs.
    /// </summary>
    public static void CleanupPreviousVersion(string? directory = null)
    {
        directory ??= Path.GetDirectoryName(Environment.ProcessPath);

        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
        {
            return;
        }

        try
        {
            foreach (var stale in Directory.EnumerateFiles(directory, "*" + BackupSuffix, SearchOption.AllDirectories))
            {
                try
                {
                    File.Delete(stale);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Still locked, or not ours to delete. It is inert either way.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Enumeration itself failed. Cleaning up is never worth failing a launch over.
        }
    }

    /// <summary>
    /// Moves every staged file into place, renaming what it replaces aside first so the previous
    /// version can be put back if anything goes wrong part-way through.
    /// </summary>
    private void Swap(string staging, List<string> staged)
    {
        var renamed = new List<(string Target, string Backup)>();
        var installed = new List<string>();

        try
        {
            foreach (var relative in staged)
            {
                var target = Path.Combine(InstallDirectory, relative);
                var folder = Path.GetDirectoryName(target);

                if (!string.IsNullOrEmpty(folder))
                {
                    Directory.CreateDirectory(folder);
                }

                if (File.Exists(target))
                {
                    var backup = target + BackupSuffix;

                    if (File.Exists(backup))
                    {
                        File.Delete(backup);
                    }

                    MoveWithRetry(target, backup);
                    renamed.Add((target, backup));
                }

                MoveWithRetry(Path.Combine(staging, relative), target);
                installed.Add(target);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Rollback(renamed, installed);
            throw new UpdateException(UpdateFailure.SwapFailed, ex);
        }
    }

    /// <summary>
    /// Undoes a partial swap. The newly installed files are deletable because none of them is
    /// running: the image that is running has already been renamed aside.
    /// </summary>
    private static void Rollback(List<(string Target, string Backup)> renamed, List<string> installed)
    {
        for (var i = installed.Count - 1; i >= 0; i--)
        {
            try
            {
                File.Delete(installed[i]);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Leave it; the backup restore below is the part that matters.
            }
        }

        for (var i = renamed.Count - 1; i >= 0; i--)
        {
            var (target, backup) = renamed[i];

            try
            {
                if (File.Exists(backup) && !File.Exists(target))
                {
                    File.Move(backup, target);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Nothing further can be done here, and the exception being handled is the one
                // worth reporting.
            }
        }
    }

    private static void MoveWithRetry(string source, string destination)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(source, destination);
                return;
            }
            catch (IOException) when (attempt < MoveAttempts)
            {
                Thread.Sleep(MoveRetryDelay);
            }
            catch (UnauthorizedAccessException) when (attempt < MoveAttempts)
            {
                Thread.Sleep(MoveRetryDelay);
            }
        }
    }

    /// <remarks>
    /// UseShellExecute is false so the new process inherits this one's token and therefore its
    /// integrity level, which is what keeps drag &amp; drop working after an update.
    /// </remarks>
    private static bool Launch(string executablePath, string workingDirectory)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(executablePath)
            {
                UseShellExecute = false,
                WorkingDirectory = workingDirectory,
            });

            return process is not null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return false;
        }
    }

    private static void DeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Staging is disposable by definition.
        }
    }
}
