using System.Security.Principal;

namespace HyperVDrop.Core.HyperV;

/// <summary>
/// Facts about the host process that change how Hyper-V behaves.
/// </summary>
public static class HostEnvironment
{
    /// <summary>
    /// Whether the process holds an elevated token.
    /// </summary>
    /// <remarks>
    /// This matters because the Hyper-V WMI provider does not deny access to an unelevated caller.
    /// It quietly returns an empty result set, which would otherwise be reported to the user as
    /// "no virtual machines found" rather than "you need to run as administrator".
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
            // If the token cannot be inspected, assume elevated and let the real call fail
            // with whatever Hyper-V actually reports.
            return true;
        }
    }
}
