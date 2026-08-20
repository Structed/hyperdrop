using System.Security.Principal;

namespace HyperDrop.Core.HyperV;

/// <summary>
/// Facts about the host process that change how HyperDrop behaves.
/// </summary>
public static class HostEnvironment
{
    /// <summary>
    /// Whether the process holds an elevated token.
    /// </summary>
    /// <remarks>
    /// This is not a Hyper-V permission check — see <see cref="HyperVAccess"/> for that. It
    /// matters because elevation puts the window at high integrity, where Windows UIPI blocks OLE
    /// drag &amp; drop from Explorer and the app has to fall back to the legacy drop protocol.
    /// </remarks>
    public static bool IsElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SystemException)
        {
            // If the token cannot be inspected, assume not elevated: that keeps the app on the
            // richer drop path, and Hyper-V will report its own error if access is really missing.
            return false;
        }
    }

    /// <summary>The current user's SID, used to name the account in group membership changes.</summary>
    /// <remarks>
    /// A SID rather than a name, because account and group names are localised and may be
    /// qualified by a domain or an Entra tenant.
    /// </remarks>
    public static string? CurrentUserSid()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return identity.User?.Value;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SystemException)
        {
            return null;
        }
    }
}
