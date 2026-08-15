using System.Net;
using System.Security;

namespace HyperDrop.Core.HyperV;

/// <summary>
/// Credentials for signing in to a guest over PowerShell Direct.
/// </summary>
/// <remarks>
/// The password is held as a <see cref="SecureString"/> so it can come straight from a WPF
/// <c>PasswordBox</c> and is only turned into plain text at the moment it is handed to the
/// PowerShell worker over its standard input.
/// </remarks>
public sealed class GuestCredentials(string userName, SecureString password) : IDisposable
{
    public string UserName { get; } = userName;

    private SecureString Password { get; } = password;

    /// <summary>Materialises the password. Callers should keep the result alive as briefly as possible.</summary>
    internal string RevealPassword() => new NetworkCredential(string.Empty, Password).Password;

    public void Dispose() => Password.Dispose();
}
