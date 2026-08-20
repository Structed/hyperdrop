using System.Windows;
using System.Windows.Input;
using HyperDrop.App.Interop;
using HyperDrop.App.Notifications;
using HyperDrop.App.ViewModels;
using HyperDrop.App.Views;
using HyperDrop.Core.HyperV;
using HyperDrop.Core.Settings;
using Microsoft.Win32;

namespace HyperDrop.App;

public partial class MainWindow : Window
{
    private readonly Notifier _notifier = new();
    private readonly MainViewModel _viewModel;

    public MainWindow()
    {
        InitializeComponent();

        _notifier.Attach(this);

        _viewModel = new MainViewModel(
            new WmiVmProvider(),
            new SettingsStore(),
            _notifier,
            Dispatcher,
            PromptForCredentials);

        DataContext = _viewModel;

        DragEnter += OnDragEnter;
        DragOver += OnDragOver;
        DragLeave += OnDragLeave;
        Drop += OnDrop;

        CommandBindings.Add(new CommandBinding(ApplicationCommands.Paste, OnPaste));
    }

    /// <summary>
    /// Chooses and wires up a drop protocol as soon as the window handle exists.
    /// </summary>
    /// <remarks>
    /// Unelevated, WPF's own OLE drag &amp; drop works and gives full hover feedback. Elevated,
    /// Windows blocks it outright and the legacy path is the only one that can still deliver a
    /// drop, so the window must not advertise an OLE drop target in that case.
    /// </remarks>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var mode = WindowDropTarget.Attach(this, OnLegacyFilesDropped);

        // TextBox opts into drops by default and only understands text, so a file dropped on the
        // destination box would be rejected on the spot instead of bubbling up to the window. It
        // also re-registers an OLE drop target, which would suppress the legacy protocol entirely.
        DestinationBox.AllowDrop = false;

        AboutInfo.DropMode = mode;
        _viewModel.ReportDropMode(mode);
    }

    protected override async void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);

        try
        {
            await _viewModel.InitialiseAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                $"HyperDrop could not read the virtual machine list.\n\n{ex.Message}",
                "HyperDrop",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        // Persist settings and stop work synchronously, then let the queue unwind in the
        // background: the process is going away regardless.
        _viewModel.Shutdown();
        _ = _viewModel.DisposeAsync();
        _notifier.Dispose();

        base.OnClosed(e);
    }

    private GuestCredentials? PromptForCredentials(string vmName)
    {
        var dialog = new CredentialWindow(vmName) { Owner = this };
        return dialog.ShowDialog() == true ? dialog.Credentials : null;
    }

    private void OnAbout(object sender, RoutedEventArgs e)
    {
        var dialog = new AboutWindow { Owner = this };
        dialog.ShowDialog();
    }

    private void OnDragEnter(object sender, DragEventArgs e) => UpdateDragState(e);

    private void OnDragOver(object sender, DragEventArgs e) => UpdateDragState(e);

    private void UpdateDragState(DragEventArgs e)
    {
        var hasFiles = e.Data.GetDataPresent(DataFormats.FileDrop);

        e.Effects = hasFiles ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
        _viewModel.IsDragOver = hasFiles;
    }

    private void OnDragLeave(object sender, DragEventArgs e)
    {
        _viewModel.IsDragOver = false;
        e.Handled = true;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        _viewModel.IsDragOver = false;

        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths)
        {
            await _viewModel.HandleDropAsync(paths);
        }
    }

    /// <summary>Handles a drop that arrived over the legacy protocol, which bypasses WPF entirely.</summary>
    private async void OnLegacyFilesDropped(IReadOnlyList<string> paths) =>
        await _viewModel.HandleDropAsync(paths);

    private async void OnPaste(object sender, ExecutedRoutedEventArgs e)
    {
        if (!Clipboard.ContainsFileDropList())
        {
            return;
        }

        var paths = Clipboard.GetFileDropList()
            .Cast<string?>()
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path!)
            .ToList();

        e.Handled = true;
        await _viewModel.HandleDropAsync(paths);
    }

    private async void OnAddFiles(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose files to copy into the guest",
            Multiselect = true,
            CheckFileExists = true,
        };

        if (dialog.ShowDialog(this) == true)
        {
            await _viewModel.HandleDropAsync(dialog.FileNames);
        }
    }

    private async void OnAddFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choose folders to copy into the guest",
            Multiselect = true,
        };

        if (dialog.ShowDialog(this) == true)
        {
            await _viewModel.HandleDropAsync(dialog.FolderNames);
        }
    }
}
