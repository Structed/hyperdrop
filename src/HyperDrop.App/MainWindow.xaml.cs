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
    /// Opens the UIPI hole for drag &amp; drop as soon as the window handle exists.
    /// </summary>
    /// <remarks>
    /// This app runs elevated, and without this Windows would silently discard every drop coming
    /// from Explorer. If the filter cannot be applied we say so, because the alternative is a
    /// window that looks fine and ignores the user.
    /// </remarks>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        if (!UipiDragDrop.Enable(this))
        {
            _viewModel.ReportDragDropUnavailable();
        }
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
