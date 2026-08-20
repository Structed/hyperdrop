using System.Net;
using System.Text;
using HyperDrop.Core.Tests.Fakes;
using HyperDrop.Core.Update;

namespace HyperDrop.Core.Tests;

public sealed class GitHubReleaseSourceTests
{
    private const string Repository = "Structed/hyperdrop";

    [Fact]
    public async Task GetLatestReleaseAsync_ReadsTheReleaseAndPicksTheWindowsPackage()
    {
        var release = await SourceFor(ReleasePayload()).GetLatestReleaseAsync();

        Assert.NotNull(release);
        Assert.Equal(new Version(2026, 8, 20, 7), release.Version);
        Assert.Equal("v2026.8.20.7", release.Tag);
        Assert.Equal("HyperDrop-v2026.8.20.7-win-x64.zip", release.PackageName);
        Assert.Equal(74_215_003, release.PackageSizeBytes);
        Assert.Equal(
            "https://github.com/Structed/hyperdrop/releases/download/v2026.8.20.7/HyperDrop-v2026.8.20.7-win-x64.zip",
            release.PackageUrl);
    }

    [Fact]
    public async Task GetLatestReleaseAsync_PairsThePackageWithItsChecksum()
    {
        var release = await SourceFor(ReleasePayload()).GetLatestReleaseAsync();

        Assert.NotNull(release);
        Assert.EndsWith("-win-x64.zip.sha256", release.ChecksumUrl);
        Assert.True(release.IsInstallable);
    }

    [Fact]
    public async Task GetLatestReleaseAsync_WithNoWindowsPackage_IsOfferedAsALinkOnly()
    {
        var payload = ReleasePayload(assets: """
            { "name": "source.tar.gz", "browser_download_url": "https://example.invalid/source.tar.gz", "size": 10 }
            """);

        var release = await SourceFor(payload).GetLatestReleaseAsync();

        Assert.NotNull(release);
        Assert.False(release.IsInstallable);
        Assert.Equal("https://github.com/Structed/hyperdrop/releases/tag/v2026.8.20.7", release.ReleaseUrl);
    }

    [Fact]
    public async Task GetLatestReleaseAsync_WithAPackageButNoChecksum_IsOfferedAsALinkOnly()
    {
        var payload = ReleasePayload(assets: """
            {
              "name": "HyperDrop-v2026.8.20.7-win-x64.zip",
              "browser_download_url": "https://example.invalid/HyperDrop-v2026.8.20.7-win-x64.zip",
              "size": 10
            }
            """);

        var release = await SourceFor(payload).GetLatestReleaseAsync();

        Assert.NotNull(release);
        Assert.False(release.IsInstallable);
    }

    [Theory]
    [InlineData("nightly")]
    [InlineData("")]
    public async Task GetLatestReleaseAsync_WithATagItCannotOrder_ReportsNothing(string tag)
    {
        var release = await SourceFor(ReleasePayload(tag)).GetLatestReleaseAsync();

        Assert.Null(release);
    }

    [Fact]
    public async Task GetLatestReleaseAsync_AsksTheLatestReleaseEndpoint()
    {
        var handler = StubHttpMessageHandler.ReturningJson(ReleasePayload());
        using var client = new HttpClient(handler);

        await new GitHubReleaseSource(client, Repository).GetLatestReleaseAsync();

        Assert.Equal(
            new Uri("https://api.github.com/repos/Structed/hyperdrop/releases/latest"),
            Assert.Single(handler.Requests).RequestUri);
    }

    [Fact]
    public async Task GetLatestReleaseAsync_WhenGitHubRefuses_ReportsANetworkFailure()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden));
        using var client = new HttpClient(handler);
        var source = new GitHubReleaseSource(client, Repository);

        var failure = await Assert.ThrowsAsync<UpdateException>(() => source.GetLatestReleaseAsync());

        Assert.Equal(UpdateFailure.Network, failure.Reason);
    }

    [Fact]
    public async Task GetLatestReleaseAsync_WithAnUnreadableAnswer_ReportsANetworkFailure()
    {
        var source = SourceFor("{ not json");

        var failure = await Assert.ThrowsAsync<UpdateException>(() => source.GetLatestReleaseAsync());

        Assert.Equal(UpdateFailure.Network, failure.Reason);
    }

    [Fact]
    public async Task DownloadAsync_WritesTheContentAndReportsCompletion()
    {
        var content = Encoding.UTF8.GetBytes(new string('x', 4096));
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(content),
        });

        using var client = new HttpClient(handler);
        using var destination = new MemoryStream();
        var reports = new List<double>();

        await new GitHubReleaseSource(client, Repository).DownloadAsync(
            "https://example.invalid/package.zip",
            destination,
            content.Length,
            new SynchronousProgress(reports.Add));

        Assert.Equal(content, destination.ToArray());
        Assert.Equal(1d, reports[^1]);
    }

    [Fact]
    public async Task DownloadAsync_WhenTheDownloadFails_ReportsANetworkFailure()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        using var client = new HttpClient(handler);
        using var destination = new MemoryStream();

        var failure = await Assert.ThrowsAsync<UpdateException>(() =>
            new GitHubReleaseSource(client, Repository)
                .DownloadAsync("https://example.invalid/package.zip", destination, 10));

        Assert.Equal(UpdateFailure.Network, failure.Reason);
    }

    [Fact]
    public void CreateClient_SendsWhatTheGitHubApiRequires()
    {
        using var client = GitHubReleaseSource.CreateClient("2026.8.20.7");

        Assert.Contains("HyperDrop", client.DefaultRequestHeaders.UserAgent.ToString());
        Assert.Contains(
            client.DefaultRequestHeaders.Accept,
            header => header.MediaType == "application/vnd.github+json");
        Assert.True(client.DefaultRequestHeaders.Contains("X-GitHub-Api-Version"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1.0.0+3f2a1c9 (local build)")]
    public void CreateClient_WithAVersionTheHeaderWouldReject_StillBuilds(string? version)
    {
        using var client = GitHubReleaseSource.CreateClient(version);

        Assert.NotEmpty(client.DefaultRequestHeaders.UserAgent);
    }

    private static GitHubReleaseSource SourceFor(string payload) =>
        new(new HttpClient(StubHttpMessageHandler.ReturningJson(payload)), Repository, ownsClient: true);

    /// <summary>A trimmed-down copy of what <c>/releases/latest</c> actually answers with.</summary>
    private static string ReleasePayload(string tag = "v2026.8.20.7", string? assets = null) =>
        $$"""
        {
          "tag_name": "{{tag}}",
          "name": "{{tag}}",
          "draft": false,
          "prerelease": false,
          "html_url": "https://github.com/Structed/hyperdrop/releases/tag/v2026.8.20.7",
          "published_at": "2026-08-20T09:14:11Z",
          "assets": [
            {{assets ?? """
            {
              "name": "HyperDrop-v2026.8.20.7-win-x64.zip",
              "browser_download_url": "https://github.com/Structed/hyperdrop/releases/download/v2026.8.20.7/HyperDrop-v2026.8.20.7-win-x64.zip",
              "size": 74215003,
              "content_type": "application/zip"
            },
            {
              "name": "HyperDrop-v2026.8.20.7-win-x64.zip.sha256",
              "browser_download_url": "https://github.com/Structed/hyperdrop/releases/download/v2026.8.20.7/HyperDrop-v2026.8.20.7-win-x64.zip.sha256",
              "size": 82,
              "content_type": "text/plain"
            }
            """}}
          ]
        }
        """;

    /// <summary>
    /// <see cref="Progress{T}"/> delivers on the synchronisation context, which a test has none of,
    /// so reports would arrive after the assertions.
    /// </summary>
    private sealed class SynchronousProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
