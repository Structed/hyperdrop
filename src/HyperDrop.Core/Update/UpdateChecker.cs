using HyperDrop.Core.Settings;

namespace HyperDrop.Core.Update;

/// <summary>What a check concluded.</summary>
public enum UpdateOutcome
{
    /// <summary>Nothing newer is published, or this build is not eligible for updates.</summary>
    UpToDate,

    /// <summary>A newer release exists and should be offered.</summary>
    UpdateAvailable,

    /// <summary>A newer release exists but the user asked to skip that exact version.</summary>
    Skipped,
}

/// <param name="Outcome">What was concluded.</param>
/// <param name="Release">The release the outcome refers to, when there is one.</param>
public sealed record UpdateCheckResult(UpdateOutcome Outcome, ReleaseInfo? Release)
{
    public static UpdateCheckResult UpToDate { get; } = new(UpdateOutcome.UpToDate, null);

    public static UpdateCheckResult Available(ReleaseInfo release) =>
        new(UpdateOutcome.UpdateAvailable, release);

    public static UpdateCheckResult Skipped(ReleaseInfo release) =>
        new(UpdateOutcome.Skipped, release);
}

/// <summary>
/// Decides whether a newer release should be offered. All of the policy lives here so it can be
/// tested without a network: version comparison, developer builds, the daily throttle and skipping.
/// </summary>
public sealed class UpdateChecker
{
    /// <summary>
    /// The placeholder version in <c>HyperDrop.App.csproj</c>. CI overrides it with the real
    /// CalVer, so a build still carrying it was produced locally.
    /// </summary>
    public static readonly Version DeveloperVersion = new(1, 0, 0);

    /// <summary>
    /// How long an automatic check waits before running again. GitHub allows 60 unauthenticated
    /// requests an hour, and nothing about a portable app justifies checking more often than this.
    /// </summary>
    public static readonly TimeSpan AutomaticCheckInterval = TimeSpan.FromHours(24);

    private readonly IUpdateSource _source;
    private readonly TimeProvider _time;

    public UpdateChecker(IUpdateSource source, Version currentVersion, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(currentVersion);

        _source = source;
        _time = timeProvider ?? TimeProvider.System;
        CurrentVersion = currentVersion;
    }

    public Version CurrentVersion { get; }

    /// <summary>
    /// Whether this build came off a developer machine rather than CI. Those are never offered an
    /// update: every real release is newer than the placeholder, so the banner would show on every
    /// <c>dotnet run</c> and offer to overwrite the build under test with a published one.
    /// </summary>
    public bool IsDeveloperBuild =>
        CurrentVersion.Major == DeveloperVersion.Major &&
        CurrentVersion.Minor == DeveloperVersion.Minor &&
        Math.Max(CurrentVersion.Build, 0) == 0 &&
        Math.Max(CurrentVersion.Revision, 0) == 0;

    /// <summary>
    /// Whether the startup check should run, given the preference and when it last ran. A manual
    /// check from the About dialog does not consult this.
    /// </summary>
    public bool IsDueForAutomaticCheck(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (!settings.CheckForUpdatesOnStartup || IsDeveloperBuild)
        {
            return false;
        }

        if (settings.LastUpdateCheckUtc is not { } last)
        {
            return true;
        }

        var elapsed = _time.GetUtcNow() - last;

        // A clock that has moved backwards, or a hand-edited settings file, would otherwise park
        // the next check arbitrarily far into the future.
        return elapsed >= AutomaticCheckInterval || elapsed < TimeSpan.Zero;
    }

    /// <summary>
    /// Asks the source for the latest release and compares it with the running version, recording
    /// the time so <see cref="IsDueForAutomaticCheck"/> can throttle the next one.
    /// </summary>
    /// <param name="settings">Preferences, updated with the check time and read for the skip.</param>
    /// <param name="honourSkip">
    /// Whether a previously skipped version stays hidden. A manual check passes <c>false</c>, so
    /// asking explicitly is also how a skip is undone.
    /// </param>
    /// <param name="cancellationToken">Cancels the check.</param>
    /// <exception cref="UpdateException">GitHub could not be reached.</exception>
    public async Task<UpdateCheckResult> CheckAsync(
        AppSettings settings,
        bool honourSkip = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (IsDeveloperBuild)
        {
            return UpdateCheckResult.UpToDate;
        }

        var release = await _source.GetLatestReleaseAsync(cancellationToken).ConfigureAwait(false);

        settings.LastUpdateCheckUtc = _time.GetUtcNow();

        if (release is null || release.Version <= CurrentVersion)
        {
            return UpdateCheckResult.UpToDate;
        }

        if (honourSkip &&
            ReleaseInfo.TryParseTag(settings.SkippedUpdateVersion, out var skipped) &&
            skipped == release.Version)
        {
            return UpdateCheckResult.Skipped(release);
        }

        return UpdateCheckResult.Available(release);
    }

    /// <summary>
    /// Reads the running version out of the string the About dialog shows.
    /// </summary>
    /// <remarks>
    /// Anything unrecognisable falls back to the developer version, which suppresses updates. A
    /// build whose version cannot be read is the last thing that should be replacing itself.
    /// </remarks>
    public static Version ParseVersion(string? version) =>
        ReleaseInfo.TryParseTag(version, out var parsed) ? parsed : DeveloperVersion;
}
