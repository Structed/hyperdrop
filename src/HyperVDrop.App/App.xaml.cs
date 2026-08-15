using System.Windows;
using System.Windows.Threading;

namespace HyperVDrop.App;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandledException;
    }

    /// <summary>
    /// Keeps an unexpected failure from silently killing the window mid-transfer.
    /// </summary>
    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(
            MainWindow,
            $"HyperVDrop hit an unexpected error.\n\n{e.Exception.Message}",
            "HyperVDrop",
            MessageBoxButton.OK,
            MessageBoxImage.Error);

        e.Handled = true;
    }
}
