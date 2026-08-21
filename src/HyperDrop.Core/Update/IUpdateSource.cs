namespace HyperDrop.Core.Update;

/// <summary>
/// Where releases are read from. The seam that keeps the update logic testable without a network,
/// in the same way <see cref="Abstractions.IVmProvider"/> keeps machine enumeration testable
/// without a hypervisor.
/// </summary>
public interface IUpdateSource
{
    /// <summary>
    /// The most recent published release, or <c>null</c> when there is none this app can read.
    /// Drafts and prereleases are excluded by the source.
    /// </summary>
    Task<ReleaseInfo?> GetLatestReleaseAsync(CancellationToken cancellationToken = default);

    /// <summary>Reads a small text asset, such as the <c>.sha256</c> beside a package.</summary>
    Task<string> GetTextAsync(string url, CancellationToken cancellationToken = default);

    /// <summary>
    /// Copies a package into <paramref name="destination"/>, reporting progress from 0 to 1 when
    /// the length is known.
    /// </summary>
    Task DownloadAsync(
        string url,
        Stream destination,
        long expectedLength,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default);
}
