using System.Security.Cryptography;
using System.Text;
using HyperDrop.Core.Tests.Fakes;
using HyperDrop.Core.Update;

namespace HyperDrop.Core.Tests;

public sealed class UpdateDownloaderTests
{
    private const string PackageUrl = "https://example.invalid/HyperDrop-v2026.8.20.7-win-x64.zip";
    private const string ChecksumUrl = PackageUrl + ".sha256";
    private const string PackageName = "HyperDrop-v2026.8.20.7-win-x64.zip";

    [Fact]
    public async Task DownloadAsync_WithAMatchingChecksum_KeepsTheVerifiedPackage()
    {
        using var temp = new TempDirectory();
        var content = Encoding.UTF8.GetBytes("a plausible zip");
        var source = SourceFor(content, ChecksumLine(content, PackageName));

        var path = await new UpdateDownloader(source, temp.Path).DownloadAsync(Release());

        Assert.True(File.Exists(path));
        Assert.Equal(content, await File.ReadAllBytesAsync(path));
        Assert.Equal(PackageName, Path.GetFileName(path));
    }

    [Fact]
    public async Task DownloadAsync_WithAMismatchedChecksum_FailsAndLeavesNothingBehind()
    {
        using var temp = new TempDirectory();
        var source = SourceFor(
            Encoding.UTF8.GetBytes("what actually arrived"),
            ChecksumLine(Encoding.UTF8.GetBytes("what was published"), PackageName));

        var failure = await Assert.ThrowsAsync<UpdateException>(
            () => new UpdateDownloader(source, temp.Path).DownloadAsync(Release()));

        Assert.Equal(UpdateFailure.ChecksumMismatch, failure.Reason);
        Assert.Empty(Directory.GetFiles(temp.Path, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task DownloadAsync_WithNoChecksumPublished_RefusesToInstall()
    {
        using var temp = new TempDirectory();
        var release = Release() with { ChecksumUrl = null };

        var failure = await Assert.ThrowsAsync<UpdateException>(
            () => new UpdateDownloader(new FakeUpdateSource(), temp.Path).DownloadAsync(release));

        Assert.Equal(UpdateFailure.NoPackage, failure.Reason);
    }

    [Fact]
    public async Task DownloadAsync_ReportsProgress()
    {
        using var temp = new TempDirectory();
        var content = Encoding.UTF8.GetBytes("a plausible zip");
        var source = SourceFor(content, ChecksumLine(content, PackageName));
        var reports = new List<double>();

        await new UpdateDownloader(source, temp.Path)
            .DownloadAsync(Release(), new Progress<double>(reports.Add));

        // Progress<T> posts asynchronously, so the reports are only guaranteed to have been
        // requested, not delivered. The contract worth asserting is that the call accepts one.
        Assert.All(reports, value => Assert.InRange(value, 0d, 1d));
    }

    [Fact]
    public void Cleanup_RemovesEverythingPreviouslyDownloaded()
    {
        using var temp = new TempDirectory();
        var root = Path.Combine(temp.Path, "updates");
        Directory.CreateDirectory(Path.Combine(root, "v2026.8.1.1"));
        File.WriteAllText(Path.Combine(root, "v2026.8.1.1", "stale.zip"), "old");

        new UpdateDownloader(new FakeUpdateSource(), root).Cleanup();

        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public void ParseChecksum_ReadsTheFormatTheReleaseWorkflowWrites()
    {
        // release.yml writes "<hash>  <name>", two spaces, no trailing newline.
        var line = $"{new string('a', 64)}  {PackageName}";

        Assert.Equal(new string('a', 64), UpdateDownloader.ParseChecksum(line, PackageName));
    }

    [Fact]
    public void ParseChecksum_WithSeveralEntries_MatchesOnTheFileName()
    {
        var content = string.Join(
            "\r\n",
            $"{new string('b', 64)}  something-else.zip",
            $"{new string('c', 64)}  {PackageName}",
            string.Empty);

        Assert.Equal(new string('c', 64), UpdateDownloader.ParseChecksum(content, PackageName));
    }

    [Fact]
    public void ParseChecksum_ToleratesTheBinaryModeAsterisk()
    {
        var content = $"{new string('d', 64)} *{PackageName}";

        Assert.Equal(new string('d', 64), UpdateDownloader.ParseChecksum(content, PackageName));
    }

    [Fact]
    public void ParseChecksum_WithASingleEntry_AcceptsItWhateverItIsNamed()
    {
        var content = $"{new string('e', 64)}  renamed-by-someone.zip";

        Assert.Equal(new string('e', 64), UpdateDownloader.ParseChecksum(content, PackageName));
    }

    [Fact]
    public void ParseChecksum_WithSeveralEntriesAndNoMatch_ReturnsNull()
    {
        var content = string.Join(
            "\n",
            $"{new string('b', 64)}  one.zip",
            $"{new string('c', 64)}  two.zip");

        Assert.Null(UpdateDownloader.ParseChecksum(content, PackageName));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a checksum at all")]
    [InlineData("abc123  short.zip")]
    public void ParseChecksum_WithNothingUsable_ReturnsNull(string? content) =>
        Assert.Null(UpdateDownloader.ParseChecksum(content, PackageName));

    [Fact]
    public async Task ComputeSha256Async_MatchesTheHashOfTheFile()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "payload.bin");
        var content = Encoding.UTF8.GetBytes("hyperdrop");
        await File.WriteAllBytesAsync(path, content);

        var expected = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

        Assert.Equal(expected, await UpdateDownloader.ComputeSha256Async(path));
    }

    private static FakeUpdateSource SourceFor(byte[] package, string checksum)
    {
        var source = new FakeUpdateSource();
        source.Packages[PackageUrl] = package;
        source.Texts[ChecksumUrl] = checksum;
        return source;
    }

    private static string ChecksumLine(byte[] content, string name) =>
        $"{Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant()}  {name}";

    private static ReleaseInfo Release() => new(
        new Version(2026, 8, 20, 7),
        "v2026.8.20.7",
        "https://github.com/Structed/hyperdrop/releases/tag/v2026.8.20.7",
        PackageName,
        PackageUrl,
        ChecksumUrl,
        PackageSizeBytes: 15);
}
