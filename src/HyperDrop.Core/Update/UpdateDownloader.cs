using System.Globalization;
using System.Security.Cryptography;

namespace HyperDrop.Core.Update;

/// <summary>
/// Fetches a release package and proves it arrived intact before anything is allowed to install it.
/// </summary>
/// <remarks>
/// Verification is not optional. <c>release.yml</c> publishes a <c>.sha256</c> next to every zip,
/// and an update that replaces the running executable is exactly the wrong place to trust a
/// download that only "looks finished".
/// </remarks>
public sealed class UpdateDownloader
{
    private readonly IUpdateSource _source;

    public UpdateDownloader(IUpdateSource source, string? downloadRoot = null)
    {
        ArgumentNullException.ThrowIfNull(source);

        _source = source;
        DownloadRoot = downloadRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HyperDrop",
            "updates");
    }

    /// <summary>Where packages are downloaded, under <c>%LOCALAPPDATA%</c> by default.</summary>
    public string DownloadRoot { get; }

    /// <summary>
    /// Downloads and verifies a release package, returning the path to the verified archive.
    /// </summary>
    /// <exception cref="UpdateException">
    /// The release has nothing to install, GitHub could not be reached, or the download did not
    /// match its published checksum.
    /// </exception>
    public async Task<string> DownloadAsync(
        ReleaseInfo release,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(release);

        if (!release.IsInstallable)
        {
            throw new UpdateException(UpdateFailure.NoPackage);
        }

        var folder = Path.Combine(DownloadRoot, SanitiseForPath(release.Tag));
        var packagePath = Path.Combine(folder, release.PackageName!);
        var partialPath = packagePath + ".partial";

        try
        {
            Directory.CreateDirectory(folder);

            if (File.Exists(partialPath))
            {
                File.Delete(partialPath);
            }

            using (var file = File.Create(partialPath))
            {
                await _source
                    .DownloadAsync(release.PackageUrl!, file, release.PackageSizeBytes, progress, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new UpdateException(UpdateFailure.Network, ex);
        }

        var published = ParseChecksum(
            await _source.GetTextAsync(release.ChecksumUrl!, cancellationToken).ConfigureAwait(false),
            release.PackageName!);

        var actual = await ComputeSha256Async(partialPath, cancellationToken).ConfigureAwait(false);

        if (published is null || !string.Equals(published, actual, StringComparison.OrdinalIgnoreCase))
        {
            TryDelete(partialPath);
            throw new UpdateException(UpdateFailure.ChecksumMismatch);
        }

        try
        {
            File.Move(partialPath, packagePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new UpdateException(UpdateFailure.Network, ex);
        }

        return packagePath;
    }

    /// <summary>
    /// Removes everything previously downloaded. Called once an update has been applied, and again
    /// on startup, so a package cannot sit in the profile forever.
    /// </summary>
    public void Cleanup()
    {
        try
        {
            if (Directory.Exists(DownloadRoot))
            {
                Directory.Delete(DownloadRoot, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A stale download is a few megabytes, not a reason to interrupt anyone.
        }
    }

    /// <summary>
    /// Reads a hash out of the <c>sha256sum</c> format that <c>release.yml</c> writes:
    /// <c>"&lt;hash&gt;  &lt;file name&gt;"</c>.
    /// </summary>
    /// <remarks>
    /// Matches on the file name when the file lists several, and accepts the single entry of a
    /// one-line file whatever it claims to be named, because that is what a per-asset checksum is.
    /// The <c>*name</c> spelling that binary-mode tools emit is tolerated.
    /// </remarks>
    internal static string? ParseChecksum(string? content, string packageName)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        string? onlyHash = null;
        var lines = 0;

        foreach (var line in content.Split('\n'))
        {
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 0 || !IsHash(parts[0]))
            {
                continue;
            }

            lines++;
            onlyHash ??= parts[0];

            if (parts.Length > 1 &&
                string.Equals(parts[1].TrimStart('*'), packageName, StringComparison.OrdinalIgnoreCase))
            {
                return parts[0];
            }
        }

        return lines == 1 ? onlyHash : null;
    }

    internal static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken = default)
    {
        using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static bool IsHash(string candidate) =>
        candidate.Length == 64 && candidate.All(char.IsAsciiHexDigit);

    private static string SanitiseForPath(string tag)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string([.. tag.Select(c => invalid.Contains(c) ? '_' : c)]);

        return cleaned.Length == 0
            ? DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture)
            : cleaned;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The caller is already reporting a failure; a leftover file does not add to it.
        }
    }
}
