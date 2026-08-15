using System.Windows;
using HyperDrop.Core.HyperV;

namespace HyperDrop.App.Views;

/// <summary>
/// Collects guest credentials for a PowerShell Direct session.
/// </summary>
/// <remarks>
/// The password is taken straight from the <c>PasswordBox</c> as a <c>SecureString</c> and handed
/// to the copier without ever existing as a managed string in this layer.
/// </remarks>
public partial class CredentialWindow : Window
{
    public CredentialWindow(string vmName)
    {
        InitializeComponent();
        PromptText.Text = $"Enter the credentials of an account inside \"{vmName}\".";
        Loaded += (_, _) => UserNameBox.Focus();
    }

    public GuestCredentials? Credentials { get; private set; }

    private void OnConnect(object sender, RoutedEventArgs e)
    {
        var userName = UserNameBox.Text.Trim();

        if (string.IsNullOrEmpty(userName))
        {
            UserNameBox.Focus();
            return;
        }

        Credentials = new GuestCredentials(userName, PasswordInput.SecurePassword);
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
