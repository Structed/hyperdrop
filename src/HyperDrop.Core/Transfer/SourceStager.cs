using HyperDrop.Core.Model;

namespace HyperDrop.Core.Transfer;

/// <summary>
/// Copies sources the Hyper-V management service cannot read into a location it can.
/// </summary>
public interface ISourceStager
{
    /// <summary>True when <paramref name="sourcePath"/> must be staged before Hyper-V can read it.</summary>
    bool NeedsStaging(string sourcePath);

    /// <summary>Copies the source to a staging location and returns the new path.</summary>
    Task<string> StageAsync(
        string sourcePath,
        IProgress<CopyProgress> progress,
        CancellationToken cancellationToken);

    /// <summary>Deletes a previously staged file. Must not throw.</summary>
    void Cleanup(string stagedPath);
}

/// <summary>
/// Stages network sources into <c>%ProgramData%\HyperDrop\staging</c>.
/// </summary>
/// <remarks>
/// <c>CopyFilesToGuest</c> is executed by the Hyper-V Virtual Machine Management service, not by
/// the user. That service cannot resolve per-user drive mappings and generally has no credentials
/// for remote shares, so a file on <c>Z:\</c> or <c>\\server\share</c> fails with a bare access
/// error. Copying it somewhere local first turns that into a working transfer.
/// </remarks>
public sealed class LocalSourceStager : ISourceStager
{
    private const int BufferSize = 1024 * 1024;

    private readonly string _stagingRoot;

    public LocalSourceStager(string? stagingRoot = null)
    {
        _stagingRoot = stagingRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "HyperDrop",
            "staging");
    }

    public bool NeedsStaging(string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            return false;
        }

        // UNC paths are never readable by the management service in the user's context.
        if (sourcePath.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return true;
        }

        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(sourcePath));
            if (string.IsNullOrEmpty(root))
            {
                return false;
            }

            // Mapped network drives exist only inside the interactive user's session.
            return new DriveInfo(root).DriveType == DriveType.Network;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public async Task<string> StageAsync(
        string sourcePath,
        IProgress<CopyProgress> progress,
        CancellationToken cancellationToken)
    {
        var folder = Path.Combine(_stagingRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);

        var destination = Path.Combine(folder, Path.GetFileName(sourcePath));

        try
        {
            await using var source = new FileStream(
                sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, useAsync: true);

            await using var target = new FileStream(
                destination, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, useAsync: true);

            var total = source.Length;
            var buffer = new byte[BufferSize];
            long copied = 0;

            progress.Report(CopyProgress.FromBytes(0, total));

            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                copied += read;
                progress.Report(CopyProgress.FromBytes(copied, total));
            }

            return destination;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Cleanup(destination);
            throw new HyperDropException(
                $"Could not copy \"{Path.GetFileName(sourcePath)}\" from its network location.",
                "Copy the file to a local folder and try again.",
                ex);
        }
        catch (OperationCanceledException)
        {
            Cleanup(destination);
            throw;
        }
    }

    public void Cleanup(string stagedPath)
    {
        try
        {
            var folder = Path.GetDirectoryName(stagedPath);

            if (folder is not null &&
                folder.StartsWith(_stagingRoot, StringComparison.OrdinalIgnoreCase) &&
                Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Staging files are disposable; a leftover copy is not worth surfacing to the user.
        }
    }
}

/// <summary>A stager that never stages, used in tests and when staging is switched off.</summary>
public sealed class NoOpSourceStager : ISourceStager
{
    public bool NeedsStaging(string sourcePath) => false;

    public Task<string> StageAsync(
        string sourcePath,
        IProgress<CopyProgress> progress,
        CancellationToken cancellationToken) => Task.FromResult(sourcePath);

    public void Cleanup(string stagedPath)
    {
    }
}
