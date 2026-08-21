using System.Windows;
using System.Windows.Threading;
using HyperDrop.Core.Update;

namespace HyperDrop.App;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandledException;

        // Reaching this point is what proves the build an update installed actually runs, so it
        // is also the right moment to delete the version it replaced.
        UpdateInstaller.CleanupPreviousVersion();
    }

    /// <summary>
    /// Keeps an unexpected failure from silently killing the window mid-transfer.
    /// </summary>
    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(
            MainWindow,
            $"HyperDrop hit an unexpected error.\n\n{e.Exception.Message}",
            "HyperDrop",
            MessageBoxButton.OK,
            MessageBoxImage.Error);

        e.Handled = true;
    }
}
