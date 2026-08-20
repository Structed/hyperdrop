using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HyperDrop.App.Interop;
using HyperDrop.Core.Settings;
using HyperDrop.Core.Update;

namespace HyperDrop.App.ViewModels;

/// <summary>Where the update flow currently is.</summary>
public enum UpdateStage
{
    /// <summary>Nothing to say.</summary>
    Idle,

    /// <summary>Asking GitHub.</summary>
    Checking,

    /// <summary>A newer release is published and is being offered.</summary>
    Available,

    /// <summary>Downloading and applying it.</summary>
    Installing,

    /// <summary>Something went wrong, and <see cref="UpdateViewModel.Detail"/> says what.</summary>
    Failed,
}

/// <summary>
/// Drives the update banner and the About dialog's update controls.
/// </summary>
public sealed partial class UpdateViewModel : ObservableObject, IDisposable
{
    private readonly AppSettings _settings;
    private readonly Action _saveSettings;
    private readonly Action _shutdown;
    private readonly UpdateChecker _checker;
    private readonly UpdateDownloader _downloader;
    private readonly UpdateInstaller _installer;
    private readonly IDisposable? _ownedSource;

    private ReleaseInfo? _release;

    public UpdateViewModel(
        AppSettings settings,
        Action saveSettings,
        Version currentVersion,
        IUpdateSource? source = null,
        UpdateInstaller? installer = null,
        Action? shutdown = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(saveSettings);
        ArgumentNullException.ThrowIfNull(currentVersion);

        _settings = settings;
        _saveSettings = saveSettings;
        _shutdown = shutdown ?? (() => System.Windows.Application.Current?.Shutdown());

        var releaseSource = source ?? new GitHubReleaseSource(productVersion: currentVersion.ToString());
        _ownedSource = source is null ? releaseSource as IDisposable : null;

        _checker = new UpdateChecker(releaseSource, currentVersion);
        _downloader = new UpdateDownloader(releaseSource);
        _installer = installer ?? new UpdateInstaller();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBannerVisible))]
    [NotifyPropertyChangedFor(nameof(IsWorking))]
    [NotifyPropertyChangedFor(nameof(ShowProgress))]
    [NotifyPropertyChangedFor(nameof(IsFailed))]
    [NotifyCanExecuteChangedFor(nameof(CheckCommand))]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand))]
    private UpdateStage _stage;

    [ObservableProperty]
    private string _headline = string.Empty;

    /// <summary>The line under the headline, and the status text in the About dialog.</summary>
    [ObservableProperty]
    private string _detail = string.Empty;

    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Detail))]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand))]
    private bool _areTransfersRunning;

    public bool IsBannerVisible => Stage is UpdateStage.Available or UpdateStage.Installing ||
        (Stage is UpdateStage.Failed && _release is not null);

    public bool IsWorking => Stage is UpdateStage.Checking or UpdateStage.Installing;

    public bool ShowProgress => Stage is UpdateStage.Installing;

    public bool IsFailed => Stage is UpdateStage.Failed;

    /// <summary>Whether the release page can be opened, which needs a release to point at.</summary>
    public bool HasRelease => _release is not null;

    /// <summary>
    /// Whether automatic checks are on. Bound to the About dialog's checkbox rather than the main
    /// window: it is set once and then forgotten, unlike the transfer options.
    /// </summary>
    public bool CheckAutomatically
    {
        get => _settings.CheckForUpdatesOnStartup;
        set
        {
            if (_settings.CheckForUpdatesOnStartup == value)
            {
                return;
            }

            _settings.CheckForUpdatesOnStartup = value;
            _saveSettings();
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Runs the once-a-day check. Failures are swallowed: a check nobody asked for has no business
    /// reporting that GitHub was unreachable.
    /// </summary>
    public async Task CheckOnStartupAsync()
    {
        if (!_checker.IsDueForAutomaticCheck(_settings))
        {
            return;
        }

        try
        {
            await RunCheckAsync(honourSkip: true, quiet: true).ConfigureAwait(true);
        }
        catch (UpdateException)
        {
            Stage = UpdateStage.Idle;
        }
        finally
        {
            _saveSettings();
        }
    }

    /// <summary>Keeps the restart blocked while files are still moving.</summary>
    public void ReportTransfersRunning(bool running) => AreTransfersRunning = running;

    [RelayCommand(CanExecute = nameof(CanCheck))]
    private async Task CheckAsync()
    {
        try
        {
            // Asking explicitly also undoes a skip: the user is looking for the answer, so hiding
            // a version they once dismissed would just be confusing.
            await RunCheckAsync(honourSkip: false, quiet: false).ConfigureAwait(true);
        }
        catch (UpdateException ex)
        {
            Fail(ex);
        }
        finally
        {
            _saveSettings();
        }
    }

    private bool CanCheck() => !IsWorking;

    [RelayCommand(CanExecute = nameof(CanInstall))]
    private async Task InstallAsync()
    {
        if (_release is null)
        {
            return;
        }

        Stage = UpdateStage.Installing;
        Progress = 0;
        Detail = "Downloading…";

        try
        {
            var package = await _downloader
                .DownloadAsync(_release, new Progress<double>(value => Progress = value))
                .ConfigureAwait(true);

            Detail = "Installing…";
            await Task.Run(() => _installer.Apply(package)).ConfigureAwait(true);

            _downloader.Cleanup();

            // Persist before handing over, so the incoming instance reads current preferences.
            _saveSettings();

            _installer.Relaunch();
            _shutdown();
        }
        catch (UpdateException ex)
        {
            Fail(ex);
        }
        catch (Exception ex)
        {
            Stage = UpdateStage.Failed;
            Detail = $"The update failed. {ex.Message}";
        }
    }

    private bool CanInstall() =>
        !IsWorking &&
        !AreTransfersRunning &&
        _release is { IsInstallable: true } &&
        _installer.CanInstall();

    [RelayCommand(CanExecute = nameof(HasRelease))]
    private void Skip()
    {
        if (_release is null)
        {
            return;
        }

        _settings.SkippedUpdateVersion = _release.Version.ToString();
        _saveSettings();

        _release = null;
        Stage = UpdateStage.Idle;
        Detail = string.Empty;
        SkipCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(HasRelease))]
    private void OpenRelease()
    {
        if (_release is not null && !ShellLink.Open(_release.ReleaseUrl))
        {
            Detail = $"Could not open a browser. The address is {_release.ReleaseUrl}";
        }
    }

    private async Task RunCheckAsync(bool honourSkip, bool quiet)
    {
        Stage = UpdateStage.Checking;

        if (!quiet)
        {
            Detail = "Checking for updates…";
        }

        var result = await _checker.CheckAsync(_settings, honourSkip).ConfigureAwait(true);

        _release = result.Release;
        SkipCommand.NotifyCanExecuteChanged();
        OpenReleaseCommand.NotifyCanExecuteChanged();

        if (result.Outcome is UpdateOutcome.UpdateAvailable && result.Release is not null)
        {
            Stage = UpdateStage.Available;
            Headline = $"HyperDrop {result.Release.Version} is available";
            Detail = DescribeOffer(result.Release);
            return;
        }

        _release = null;
        Stage = UpdateStage.Idle;

        Detail = quiet
            ? string.Empty
            : _checker.IsDeveloperBuild
                ? "This is a local build, so there is nothing to update to."
                : "HyperDrop is up to date.";
    }

    /// <summary>
    /// Says plainly what clicking will do, or why it cannot right now.
    /// </summary>
    private string DescribeOffer(ReleaseInfo release)
    {
        if (AreTransfersRunning)
        {
            return "Transfers are still running. HyperDrop will restart once they finish.";
        }

        if (!release.IsInstallable)
        {
            return "This release has no download HyperDrop can install. Open the release page to get it.";
        }

        if (!_installer.CanInstall())
        {
            return UpdateFailures.RemedyFor(UpdateFailure.DestinationNotWritable)!;
        }

        return release.PackageSizeBytes > 0
            ? $"{Humanize.Bytes(release.PackageSizeBytes)}. HyperDrop will restart to finish."
            : "HyperDrop will restart to finish.";
    }

    private void Fail(UpdateException ex)
    {
        Stage = UpdateStage.Failed;
        Detail = ex.FullMessage;
    }

    partial void OnAreTransfersRunningChanged(bool value)
    {
        if (Stage is UpdateStage.Available && _release is not null)
        {
            Detail = DescribeOffer(_release);
        }
    }

    public void Dispose() => _ownedSource?.Dispose();
}
