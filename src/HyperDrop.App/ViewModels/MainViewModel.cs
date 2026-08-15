using System.Collections.ObjectModel;
using System.Windows.Shell;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HyperDrop.App.Notifications;
using HyperDrop.Core;
using HyperDrop.Core.Abstractions;
using HyperDrop.Core.HyperV;
using HyperDrop.Core.Model;
using HyperDrop.Core.Settings;
using HyperDrop.Core.Transfer;

namespace HyperDrop.App.ViewModels;

/// <summary>
/// Drives the main window: virtual machine selection, drop handling, and the transfer list.
/// </summary>
public sealed partial class MainViewModel : ObservableObject, IAsyncDisposable
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(5);

    private readonly IVmProvider _vmProvider;
    private readonly SettingsStore _settingsStore;
    private readonly AppSettings _settings;
    private readonly Notifier _notifier;
    private readonly Dispatcher _dispatcher;
    private readonly Func<string, GuestCredentials?> _credentialPrompt;

    private readonly TransferQueue _queue;
    private readonly Dictionary<string, TransferItemViewModel> _rows = [];
    private readonly Dictionary<string, TransferSnapshot> _snapshots = [];
    private readonly DispatcherTimer _refreshTimer;

    private GuestCredentials? _credentials;
    private string? _credentialsVmId;
    private bool _suppressSettingsSave;

    public MainViewModel(
        IVmProvider vmProvider,
        SettingsStore settingsStore,
        Notifier notifier,
        Dispatcher dispatcher,
        Func<string, GuestCredentials?> credentialPrompt)
    {
        _vmProvider = vmProvider;
        _settingsStore = settingsStore;
        _notifier = notifier;
        _dispatcher = dispatcher;
        _credentialPrompt = credentialPrompt;
        _settings = settingsStore.Load();

        _suppressSettingsSave = true;
        OverwriteExisting = _settings.OverwriteExisting;
        CreateFullPath = _settings.CreateFullPath;
        DestinationPath = _settings.DestinationFor(_settings.LastVmId);
        _suppressSettingsSave = false;

        _queue = new TransferQueue(
            CreateCopier,
            _settings.StageNetworkSources ? new LocalSourceStager() : new NoOpSourceStager());

        _queue.ItemsAdded += OnItemsAdded;
        _queue.ItemUpdated += OnItemUpdated;
        _queue.ItemsRemoved += OnItemsRemoved;
        _queue.BatchCompleted += OnBatchCompleted;

        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = RefreshInterval,
        };

        _refreshTimer.Tick += async (_, _) => await RefreshAsync(quiet: true).ConfigureAwait(true);
    }

    public ObservableCollection<VirtualMachineInfo> VirtualMachines { get; } = [];

    public ObservableCollection<TransferItemViewModel> Transfers { get; } = [];

    /// <summary>Options for the transfer method picker, kept as display pairs so the XAML stays converter-free.</summary>
    public IReadOnlyList<TransferMethodOption> TransferMethodOptions { get; } =
    [
        new(TransferMethod.GuestService, "Guest Service Interface (no sign-in)"),
        new(TransferMethod.PowerShellDirect, "PowerShell Direct (sign in to guest)"),
    ];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedVm))]
    [NotifyPropertyChangedFor(nameof(GuestServiceStatus))]
    [NotifyPropertyChangedFor(nameof(CanEnableGuestService))]
    [NotifyPropertyChangedFor(nameof(IsVmReady))]
    [NotifyPropertyChangedFor(nameof(DropHintText))]
    private string? _selectedVmId;

    [ObservableProperty]
    private string _destinationPath = AppSettings.FallbackDestination;

    [ObservableProperty]
    private bool _overwriteExisting;

    [ObservableProperty]
    private bool _createFullPath = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RequiresCredentials))]
    private TransferMethod _method;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _isStatusError;

    [ObservableProperty]
    private bool _isDragOver;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private double _overallProgress;

    [ObservableProperty]
    private string _overallText = string.Empty;

    [ObservableProperty]
    private bool _hasActiveTransfers;

    [ObservableProperty]
    private bool _hasTransfers;

    [ObservableProperty]
    private TaskbarItemProgressState _taskbarState = TaskbarItemProgressState.None;

    public VirtualMachineInfo? SelectedVm => SelectedVmId is null
        ? null
        : VirtualMachines.FirstOrDefault(vm => vm.Id == SelectedVmId);

    public bool IsVmReady => SelectedVm?.IsRunning == true;

    public bool RequiresCredentials => Method is TransferMethod.PowerShellDirect;

    public bool CanEnableGuestService =>
        SelectedVm is { IsRunning: true, GuestServiceInterface: not IntegrationServiceState.Enabled };

    public string GuestServiceStatus => SelectedVm switch
    {
        null => "No virtual machine selected",
        { IsRunning: false } vm => $"{vm.State} — start the VM to copy files into it",
        { GuestServiceInterface: IntegrationServiceState.Enabled } => "Guest Service Interface enabled",
        { GuestServiceInterface: IntegrationServiceState.Disabled } => "Guest Service Interface disabled",
        _ => "Guest Service Interface state unknown",
    };

    public string DropHintText => SelectedVm is null
        ? "Select a virtual machine, then drop files here"
        : $"Drop files or folders to copy them into {SelectedVm.Name}";

    public async Task InitialiseAsync()    {
        await RefreshAsync(quiet: false).ConfigureAwait(true);

        if (_settings.LastVmId is not null &&
            VirtualMachines.Any(vm => vm.Id == _settings.LastVmId))
        {
            SelectedVmId = _settings.LastVmId;
        }
        else
        {
            SelectedVmId ??= VirtualMachines.FirstOrDefault(vm => vm.IsRunning)?.Id
                ?? VirtualMachines.FirstOrDefault()?.Id;
        }

        _refreshTimer.Start();
    }

    /// <summary>
    /// Turns dropped paths into queued transfers. Called for drag &amp; drop, the browse buttons,
    /// and clipboard paste.
    /// </summary>
    public async Task HandleDropAsync(IReadOnlyList<string> paths)
    {
        IsDragOver = false;

        if (paths.Count == 0)
        {
            return;
        }

        var vm = SelectedVm;

        if (vm is null)
        {
            SetStatus("Select a virtual machine first.", isError: true);
            return;
        }

        if (!vm.IsRunning)
        {
            SetStatus($"\"{vm.Name}\" is not running. Start it and try again.", isError: true);
            return;
        }

        if (string.IsNullOrWhiteSpace(DestinationPath) || !Path.IsPathRooted(DestinationPath))
        {
            SetStatus(
                "Enter an absolute destination path inside the guest, such as C:\\Users\\Public\\Downloads.",
                isError: true);
            return;
        }

        if (!EnsureEngineReady(vm))
        {
            return;
        }

        IsBusy = true;
        DropExpansion expansion;

        try
        {
            expansion = await Task.Run(() => DropExpander.Expand(paths)).ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }

        if (expansion.Items.Count == 0)
        {
            SetStatus(
                expansion.Problems.Count > 0
                    ? $"Nothing to copy. {expansion.Problems[0].Reason}"
                    : "Nothing to copy.",
                isError: true);
            return;
        }

        _queue.Enqueue(
            expansion.Items,
            new TransferTarget
            {
                VmName = vm.Name,
                VmId = vm.Id,
                DestinationRoot = DestinationPath.Trim(),
                OverwriteExisting = OverwriteExisting,
                CreateFullPath = CreateFullPath,
            });

        var skipped = expansion.Problems.Count > 0 ? $" {expansion.Problems.Count} item(s) skipped." : string.Empty;

        SetStatus(
            $"Queued {expansion.Items.Count} file(s), {Humanize.Bytes(expansion.TotalBytes)}, for {vm.Name}.{skipped}",
            isError: false);
    }

    [RelayCommand]
    private async Task RefreshAsync() => await RefreshAsync(quiet: false).ConfigureAwait(true);

    [RelayCommand]
    private async Task EnableGuestServiceAsync()
    {
        var vm = SelectedVm;
        if (vm is null)
        {
            return;
        }

        IsBusy = true;

        try
        {
            await _vmProvider.EnableGuestServiceInterfaceAsync(vm.Id).ConfigureAwait(true);
            SetStatus($"Guest Service Interface enabled for {vm.Name}.", isError: false);
            await RefreshAsync(quiet: true).ConfigureAwait(true);
        }
        catch (HyperDropException ex)
        {
            SetStatus(ex.FullMessage, isError: true);
        }
        catch (Exception ex)
        {
            SetStatus($"Could not enable the Guest Service Interface: {ex.Message}", isError: true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void CancelItem(TransferItemViewModel? item)
    {
        if (item is not null)
        {
            _queue.Cancel(item.Id);
        }
    }

    [RelayCommand]
    private void RetryItem(TransferItemViewModel? item)
    {
        if (item is not null)
        {
            _queue.Retry(item.Id);
        }
    }

    [RelayCommand]
    private void CancelAll() => _queue.CancelAll();

    [RelayCommand]
    private void ClearCompleted() => _queue.ClearCompleted();

    /// <summary>
    /// Confirms the selected engine can actually run, prompting for guest credentials when
    /// PowerShell Direct is in use.
    /// </summary>
    private bool EnsureEngineReady(VirtualMachineInfo vm)
    {
        if (Method is TransferMethod.GuestService)
        {
            if (vm.SupportsGuestServiceCopy)
            {
                return true;
            }

            SetStatus(
                $"The Guest Service Interface is not enabled for \"{vm.Name}\". Use Enable, or switch to PowerShell Direct.",
                isError: true);
            return false;
        }

        if (_credentials is not null && _credentialsVmId == vm.Id)
        {
            return true;
        }

        var credentials = _credentialPrompt(vm.Name);

        if (credentials is null)
        {
            SetStatus("Guest credentials are required for PowerShell Direct.", isError: true);
            return false;
        }

        _credentials?.Dispose();
        _credentials = credentials;
        _credentialsVmId = vm.Id;
        return true;
    }

    /// <summary>
    /// Builds the copy engine for a batch. Runs on the queue's worker thread, so everything it
    /// needs must already be resolved.
    /// </summary>
    private IGuestFileCopier CreateCopier(TransferTarget target)
    {
        if (Method is TransferMethod.PowerShellDirect && _credentials is not null)
        {
            return new PowerShellDirectFileCopier(
                target.VmName,
                _credentials,
                _settings.PowerShellChunkSizeBytes);
        }

        return new GuestServiceFileCopier();
    }

    private async Task RefreshAsync(bool quiet)
    {
        try
        {
            var machines = await _vmProvider.ListAsync().ConfigureAwait(true);
            Reconcile(machines);

            if (!quiet)
            {
                SetStatus(
                    machines.Count == 0
                        ? "No virtual machines found on this host."
                        : $"Found {machines.Count} virtual machine(s).",
                    isError: false);
            }
        }
        catch (HyperDropException ex)
        {
            if (!quiet)
            {
                SetStatus(ex.FullMessage, isError: true);
            }
        }
        catch (Exception ex)
        {
            if (!quiet)
            {
                SetStatus($"Could not read the virtual machine list: {ex.Message}", isError: true);
            }
        }
    }

    /// <summary>
    /// Merges the latest machine list into the bound collection in place, so the combo box keeps
    /// its selection and does not flicker on the background refresh.
    /// </summary>
    private void Reconcile(IReadOnlyList<VirtualMachineInfo> machines)
    {
        for (var index = VirtualMachines.Count - 1; index >= 0; index--)
        {
            if (machines.All(vm => vm.Id != VirtualMachines[index].Id))
            {
                VirtualMachines.RemoveAt(index);
            }
        }

        for (var index = 0; index < machines.Count; index++)
        {
            var incoming = machines[index];
            var existing = VirtualMachines.FirstOrDefault(vm => vm.Id == incoming.Id);

            if (existing is null)
            {
                VirtualMachines.Insert(Math.Min(index, VirtualMachines.Count), incoming);
            }
            else if (existing != incoming)
            {
                VirtualMachines[VirtualMachines.IndexOf(existing)] = incoming;
            }
        }

        // The selected machine's own state may have changed even when the selection did not.
        OnPropertyChanged(nameof(SelectedVm));
        OnPropertyChanged(nameof(GuestServiceStatus));
        OnPropertyChanged(nameof(CanEnableGuestService));
        OnPropertyChanged(nameof(IsVmReady));
        OnPropertyChanged(nameof(DropHintText));
    }

    partial void OnSelectedVmIdChanged(string? value)
    {
        if (value is null)
        {
            return;
        }

        _suppressSettingsSave = true;
        DestinationPath = _settings.DestinationFor(value);
        _suppressSettingsSave = false;

        // Default to whichever engine the machine can actually use right now.
        var vm = SelectedVm;
        if (vm is not null)
        {
            Method = vm.SupportsGuestServiceCopy ? TransferMethod.GuestService : Method;
        }

        _settings.LastVmId = value;
        SaveSettings();
    }

    partial void OnDestinationPathChanged(string value)
    {
        if (SelectedVmId is not null && !string.IsNullOrWhiteSpace(value))
        {
            _settings.SetDestination(SelectedVmId, value.Trim());
        }

        SaveSettings();
    }

    partial void OnOverwriteExistingChanged(bool value)
    {
        _settings.OverwriteExisting = value;
        SaveSettings();
    }

    partial void OnCreateFullPathChanged(bool value)
    {
        _settings.CreateFullPath = value;
        SaveSettings();
    }

    private void SaveSettings()
    {
        if (!_suppressSettingsSave)
        {
            _settingsStore.Save(_settings);
        }
    }

    private void SetStatus(string message, bool isError)
    {
        StatusMessage = message;
        IsStatusError = isError;
    }

    private void OnItemsAdded(object? sender, IReadOnlyList<TransferSnapshot> added) =>
        _dispatcher.InvokeAsync(() =>
        {
            foreach (var snapshot in added)
            {
                var row = new TransferItemViewModel(snapshot);
                _rows[snapshot.Id] = row;
                _snapshots[snapshot.Id] = snapshot;
                Transfers.Add(row);
            }

            UpdateOverall();
        });

    private void OnItemUpdated(object? sender, TransferSnapshot snapshot) =>
        _dispatcher.InvokeAsync(() =>
        {
            if (_rows.TryGetValue(snapshot.Id, out var row))
            {
                row.Apply(snapshot);
            }

            _snapshots[snapshot.Id] = snapshot;
            UpdateOverall();
        });

    private void OnItemsRemoved(object? sender, IReadOnlyList<string> removed) =>
        _dispatcher.InvokeAsync(() =>
        {
            foreach (var id in removed)
            {
                if (_rows.Remove(id, out var row))
                {
                    Transfers.Remove(row);
                }

                _snapshots.Remove(id);
            }

            UpdateOverall();
        });

    private void OnBatchCompleted(object? sender, TransferBatchSummary summary) =>
        _dispatcher.InvokeAsync(() =>
        {
            UpdateOverall();

            SetStatus(
                summary.Failed > 0
                    ? $"Finished with {summary.Failed} failure(s). {summary.Succeeded} file(s) copied to {summary.VmName}."
                    : $"Copied {summary.Succeeded} file(s) ({Humanize.Bytes(summary.BytesTransferred)}) to {summary.VmName}.",
                isError: summary.Failed > 0);

            _notifier.NotifyBatchCompleted(
                summary,
                _settings.NotifyOnCompletion,
                _settings.PlaySoundOnCompletion);
        });

    private void UpdateOverall()
    {
        long total = 0;
        long done = 0;
        var completed = 0;
        var failed = 0;
        var active = 0;

        foreach (var snapshot in _snapshots.Values)
        {
            total += snapshot.TotalBytes;
            done += snapshot.State is TransferState.Completed ? snapshot.TotalBytes : snapshot.BytesTransferred;

            switch (snapshot.State)
            {
                case TransferState.Completed:
                    completed++;
                    break;
                case TransferState.Failed:
                    failed++;
                    break;
                default:
                    if (!snapshot.State.IsTerminal())
                    {
                        active++;
                    }

                    break;
            }
        }

        HasActiveTransfers = active > 0;
        HasTransfers = _snapshots.Count > 0;
        OverallProgress = total > 0 ? Math.Clamp((double)done / total, 0d, 1d) : 0d;

        OverallText = _snapshots.Count == 0
            ? string.Empty
            : $"{completed} of {_snapshots.Count} files  ·  {Humanize.Bytes(done)} of {Humanize.Bytes(total)}";

        TaskbarState = active > 0
            ? TaskbarItemProgressState.Normal
            : failed > 0
                ? TaskbarItemProgressState.Error
                : TaskbarItemProgressState.None;
    }

    /// <summary>
    /// Warns that Windows refused to open the drag &amp; drop message filter, so the browse and
    /// paste paths are the only way in.
    /// </summary>
    public void ReportDragDropUnavailable() => SetStatus(
        "Windows blocked drag & drop into this elevated window. Use Add files… or Ctrl+V instead.",
        isError: true);

    /// <summary>
    /// Stops work and persists settings synchronously, so nothing is lost if the process exits
    /// before the asynchronous teardown finishes.
    /// </summary>
    public void Shutdown()
    {
        _refreshTimer.Stop();
        _queue.CancelAll();
        _settingsStore.Save(_settings);
    }

    public async ValueTask DisposeAsync()
    {
        _refreshTimer.Stop();
        await _queue.DisposeAsync().ConfigureAwait(false);
        _credentials?.Dispose();
        _settingsStore.Save(_settings);
    }
}
