using HyperDrop.Core.Settings;
using HyperDrop.Core.Tests.Fakes;
using HyperDrop.Core.Update;

namespace HyperDrop.Core.Tests;

public sealed class UpdateCheckerTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);

    private static readonly Version Current = new(2026, 8, 1, 3);

    [Theory]
    [InlineData("v2026.8.20.7")]
    [InlineData("V2026.8.20.7")]
    [InlineData("2026.8.20.7")]
    [InlineData("  v2026.8.20.7  ")]
    public void TryParseTag_AcceptsTheTagsReleasesActuallyUse(string tag)
    {
        Assert.True(ReleaseInfo.TryParseTag(tag, out var version));
        Assert.Equal(new Version(2026, 8, 20, 7), version);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("nightly")]
    [InlineData("v")]
    [InlineData("release-2026-08-20")]
    public void TryParseTag_RejectsAnythingItCannotOrder(string? tag) =>
        Assert.False(ReleaseInfo.TryParseTag(tag, out _));

    [Fact]
    public async Task CheckAsync_WithANewerRelease_OffersIt()
    {
        var release = ReleaseFor("v2026.8.20.7");
        var checker = CheckerFor(release, out _, out var settings);

        var result = await checker.CheckAsync(settings);

        Assert.Equal(UpdateOutcome.UpdateAvailable, result.Outcome);
        Assert.Same(release, result.Release);
    }

    [Theory]
    [InlineData("v2026.7.30.1")]
    [InlineData("v2026.8.1.3")]
    public async Task CheckAsync_WithNothingNewer_ReportsUpToDate(string tag)
    {
        var checker = CheckerFor(ReleaseFor(tag), out _, out var settings);

        var result = await checker.CheckAsync(settings);

        Assert.Equal(UpdateOutcome.UpToDate, result.Outcome);
        Assert.Null(result.Release);
    }

    [Fact]
    public async Task CheckAsync_WithNoRelease_ReportsUpToDate()
    {
        var checker = CheckerFor(release: null, out _, out var settings);

        var result = await checker.CheckAsync(settings);

        Assert.Equal(UpdateOutcome.UpToDate, result.Outcome);
    }

    [Fact]
    public async Task CheckAsync_RecordsWhenItRan()
    {
        var checker = CheckerFor(ReleaseFor("v2026.8.20.7"), out _, out var settings);

        await checker.CheckAsync(settings);

        Assert.Equal(Now, settings.LastUpdateCheckUtc);
    }

    [Fact]
    public async Task CheckAsync_OnADeveloperBuild_NeverAsksAndNeverOffers()
    {
        var source = new FakeUpdateSource { Release = ReleaseFor("v2026.8.20.7") };
        var checker = new UpdateChecker(source, UpdateChecker.DeveloperVersion, new FixedTimeProvider(Now));

        var result = await checker.CheckAsync(new AppSettings());

        Assert.Equal(UpdateOutcome.UpToDate, result.Outcome);
        Assert.Equal(0, source.LatestReleaseCalls);
    }

    [Theory]
    [InlineData(1, 0, 0, -1)]
    [InlineData(1, 0, 0, 0)]
    public void IsDeveloperBuild_RecognisesThePlaceholderVersionInEitherShape(
        int major,
        int minor,
        int build,
        int revision)
    {
        var version = revision < 0 ? new Version(major, minor, build) : new Version(major, minor, build, revision);
        var checker = new UpdateChecker(new FakeUpdateSource(), version);

        Assert.True(checker.IsDeveloperBuild);
    }

    [Fact]
    public void IsDeveloperBuild_IsFalseForAPublishedVersion() =>
        Assert.False(new UpdateChecker(new FakeUpdateSource(), Current).IsDeveloperBuild);

    [Fact]
    public async Task CheckAsync_WithTheSkippedVersion_StaysQuiet()
    {
        var checker = CheckerFor(ReleaseFor("v2026.8.20.7"), out _, out var settings);
        settings.SkippedUpdateVersion = "2026.8.20.7";

        var result = await checker.CheckAsync(settings);

        Assert.Equal(UpdateOutcome.Skipped, result.Outcome);
    }

    [Fact]
    public async Task CheckAsync_AskedDirectly_IgnoresASkip()
    {
        var checker = CheckerFor(ReleaseFor("v2026.8.20.7"), out _, out var settings);
        settings.SkippedUpdateVersion = "2026.8.20.7";

        var result = await checker.CheckAsync(settings, honourSkip: false);

        Assert.Equal(UpdateOutcome.UpdateAvailable, result.Outcome);
    }

    [Fact]
    public async Task CheckAsync_WithAnEarlierVersionSkipped_StillOffersTheNextOne()
    {
        var checker = CheckerFor(ReleaseFor("v2026.8.21.1"), out _, out var settings);
        settings.SkippedUpdateVersion = "2026.8.20.7";

        var result = await checker.CheckAsync(settings);

        Assert.Equal(UpdateOutcome.UpdateAvailable, result.Outcome);
    }

    [Fact]
    public async Task CheckAsync_WhenGitHubIsUnreachable_SurfacesTheFailure()
    {
        var source = new FakeUpdateSource { FailWith = new UpdateException(UpdateFailure.Network) };
        var checker = new UpdateChecker(source, Current, new FixedTimeProvider(Now));

        var failure = await Assert.ThrowsAsync<UpdateException>(() => checker.CheckAsync(new AppSettings()));

        Assert.Equal(UpdateFailure.Network, failure.Reason);
    }

    [Fact]
    public void IsDueForAutomaticCheck_HavingNeverRun_IsDue() =>
        Assert.True(CheckerFor(null, out _, out var settings).IsDueForAutomaticCheck(settings));

    [Fact]
    public void IsDueForAutomaticCheck_WithinTheDay_IsNotDue()
    {
        var checker = CheckerFor(null, out _, out var settings);
        settings.LastUpdateCheckUtc = Now.AddHours(-1);

        Assert.False(checker.IsDueForAutomaticCheck(settings));
    }

    [Fact]
    public void IsDueForAutomaticCheck_AfterADay_IsDue()
    {
        var checker = CheckerFor(null, out _, out var settings);
        settings.LastUpdateCheckUtc = Now - UpdateChecker.AutomaticCheckInterval;

        Assert.True(checker.IsDueForAutomaticCheck(settings));
    }

    [Fact]
    public void IsDueForAutomaticCheck_WithATimestampInTheFuture_IsDue()
    {
        var checker = CheckerFor(null, out _, out var settings);
        settings.LastUpdateCheckUtc = Now.AddYears(5);

        Assert.True(checker.IsDueForAutomaticCheck(settings));
    }

    [Fact]
    public void IsDueForAutomaticCheck_WhenTurnedOff_IsNotDue()
    {
        var checker = CheckerFor(null, out _, out var settings);
        settings.CheckForUpdatesOnStartup = false;

        Assert.False(checker.IsDueForAutomaticCheck(settings));
    }

    [Fact]
    public void IsDueForAutomaticCheck_OnADeveloperBuild_IsNotDue()
    {
        var checker = new UpdateChecker(
            new FakeUpdateSource(),
            UpdateChecker.DeveloperVersion,
            new FixedTimeProvider(Now));

        Assert.False(checker.IsDueForAutomaticCheck(new AppSettings()));
    }

    [Theory]
    [InlineData("2026.8.20.7", 2026)]
    [InlineData("v2026.8.20.7", 2026)]
    public void ParseVersion_ReadsAPublishedVersion(string text, int expectedMajor) =>
        Assert.Equal(expectedMajor, UpdateChecker.ParseVersion(text).Major);

    [Theory]
    [InlineData(null)]
    [InlineData("unknown")]
    // A local build. Directory.Build.props stamps today's date with a -dev suffix, and that suffix
    // is the whole reason it lands here rather than being compared against a same-day release.
    [InlineData("2026.8.21.0-dev")]
    public void ParseVersion_WithSomethingUnreadable_FallsBackToSuppressingUpdates(string? text) =>
        Assert.Equal(UpdateChecker.DeveloperVersion, UpdateChecker.ParseVersion(text));

    [Fact]
    public async Task CheckAsync_OnALocalCalVerBuild_NeverOffersASameDayRelease()
    {
        var source = new FakeUpdateSource { Release = ReleaseFor("v2026.8.21.5") };
        var checker = new UpdateChecker(
            source,
            UpdateChecker.ParseVersion("2026.8.21.0-dev"),
            new FixedTimeProvider(Now));

        var result = await checker.CheckAsync(new AppSettings());

        Assert.Equal(UpdateOutcome.UpToDate, result.Outcome);
        Assert.Equal(0, source.LatestReleaseCalls);
    }

    private static UpdateChecker CheckerFor(
        ReleaseInfo? release,
        out FakeUpdateSource source,
        out AppSettings settings)
    {
        source = new FakeUpdateSource { Release = release };
        settings = new AppSettings();

        return new UpdateChecker(source, Current, new FixedTimeProvider(Now));
    }

    private static ReleaseInfo ReleaseFor(string tag)
    {
        Assert.True(ReleaseInfo.TryParseTag(tag, out var version));

        return new ReleaseInfo(
            version,
            tag,
            $"https://github.com/Structed/hyperdrop/releases/tag/{tag}",
            $"HyperDrop-{tag}-win-x64.zip",
            $"https://example.invalid/{tag}.zip",
            $"https://example.invalid/{tag}.zip.sha256",
            1024);
    }
}
