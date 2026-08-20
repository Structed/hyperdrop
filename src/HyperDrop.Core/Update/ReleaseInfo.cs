namespace HyperDrop.Core.Update;

/// <summary>
/// One published release, reduced to the facts an update needs.
/// </summary>
/// <param name="Version">Version parsed from the tag, with the leading <c>v</c> removed.</param>
/// <param name="Tag">The tag exactly as published, for display and for naming the download folder.</param>
/// <param name="ReleaseUrl">The release page, used as the fallback when a swap is not possible.</param>
/// <param name="PackageName">File name of the downloadable archive.</param>
/// <param name="PackageUrl">Direct download for the archive.</param>
/// <param name="ChecksumUrl">
/// The matching <c>.sha256</c>, or <c>null</c> when the release does not publish one.
/// </param>
/// <param name="PackageSizeBytes">Archive size, so the UI can show a meaningful progress bar.</param>
public sealed record ReleaseInfo(
    Version Version,
    string Tag,
    string ReleaseUrl,
    string? PackageName,
    string? PackageUrl,
    string? ChecksumUrl,
    long PackageSizeBytes)
{
    /// <summary>
    /// Whether this release can be installed in place. A release with no archive, or with an
    /// archive but no checksum to verify it against, is only ever offered as a link.
    /// </summary>
    public bool IsInstallable =>
        !string.IsNullOrWhiteSpace(PackageUrl) &&
        !string.IsNullOrWhiteSpace(PackageName) &&
        !string.IsNullOrWhiteSpace(ChecksumUrl);

    /// <summary>
    /// Reads the version out of a release tag. Releases are tagged <c>v{year}.{month}.{day}.{build}</c>,
    /// which <see cref="System.Version"/> understands once the <c>v</c> is removed.
    /// </summary>
    /// <remarks>
    /// A tag that does not parse is rejected rather than guessed at. Offering to "update" to
    /// something whose ordering is unknown is worse than staying quiet.
    /// </remarks>
    public static bool TryParseTag(string? tag, out Version version)
    {
        version = new Version(0, 0);

        if (string.IsNullOrWhiteSpace(tag))
        {
            return false;
        }

        var trimmed = tag.Trim();

        if (trimmed.StartsWith('v') || trimmed.StartsWith('V'))
        {
            trimmed = trimmed[1..];
        }

        return Version.TryParse(trimmed, out version!);
    }
}
