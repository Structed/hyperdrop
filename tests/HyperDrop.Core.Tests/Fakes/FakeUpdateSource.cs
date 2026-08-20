using HyperDrop.Core.Update;

namespace HyperDrop.Core.Tests.Fakes;

/// <summary>
/// An update source backed by in-memory content, so the update logic can be tested with no network.
/// </summary>
internal sealed class FakeUpdateSource : IUpdateSource
{
    public ReleaseInfo? Release { get; set; }

    public Exception? FailWith { get; set; }

    public Dictionary<string, string> Texts { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, byte[]> Packages { get; } = new(StringComparer.OrdinalIgnoreCase);

    public int LatestReleaseCalls { get; private set; }

    public Task<ReleaseInfo?> GetLatestReleaseAsync(CancellationToken cancellationToken = default)
    {
        LatestReleaseCalls++;

        return FailWith is not null
            ? Task.FromException<ReleaseInfo?>(FailWith)
            : Task.FromResult(Release);
    }

    public Task<string> GetTextAsync(string url, CancellationToken cancellationToken = default) =>
        Texts.TryGetValue(url, out var text)
            ? Task.FromResult(text)
            : Task.FromException<string>(new UpdateException(UpdateFailure.Network));

    public async Task DownloadAsync(
        string url,
        Stream destination,
        long expectedLength,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!Packages.TryGetValue(url, out var content))
        {
            throw new UpdateException(UpdateFailure.Network);
        }

        await destination.WriteAsync(content, cancellationToken);
        progress?.Report(1d);
    }
}
