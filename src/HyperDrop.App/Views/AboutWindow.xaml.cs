using System.Runtime.InteropServices;
using System.Windows;
using HyperDrop.App.Interop;

namespace HyperDrop.App.Views;

/// <summary>
/// Shows the product version and the host facts that are worth quoting in a bug report.
/// </summary>
public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();

        VersionText.Text = $"Version {AboutInfo.Version}";
        TaglineText.Text = AboutInfo.Tagline;
        RuntimeText.Text = AboutInfo.Runtime;
        OsText.Text = AboutInfo.OsDescription;
        ProcessText.Text = AboutInfo.ProcessDescription;
        CreditText.Text = AboutInfo.Credit;
        ProjectLink.Content = AboutInfo.ProjectDisplayUrl;
    }

    private void OnOpenProject(object sender, RoutedEventArgs e)
    {
        StatusText.Text = ShellLink.Open(AboutInfo.ProjectUrl)
            ? string.Empty
            : $"Could not open a browser. The address is {AboutInfo.ProjectUrl}";
    }

    private void OnCopyDetails(object sender, RoutedEventArgs e)
    {
        try
        {
            // Copy=true keeps the text on the clipboard once this process exits.
            Clipboard.SetDataObject(AboutInfo.Diagnostics(), copy: true);
            StatusText.Text = "Details copied to the clipboard.";
        }
        catch (COMException)
        {
            // Another process can hold the clipboard open, and that is not worth an error dialog.
            StatusText.Text = "The clipboard is in use by another app. Try again.";
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
