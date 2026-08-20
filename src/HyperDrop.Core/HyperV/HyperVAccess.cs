namespace HyperDrop.Core.HyperV;

/// <summary>
/// Decides whether Hyper-V is refusing this account, and says what to do about it.
/// </summary>
/// <remarks>
/// <para>
/// Hyper-V does not fail an unauthorised read. It quietly returns nothing, which is
/// indistinguishable from "this host has no virtual machines" unless something else is
/// measured alongside it.
/// </para>
/// <para>
/// <c>Msvm_VirtualSystemManagementService</c> is that something else: it is a host singleton that
/// always exists on a Hyper-V host, so an authorised caller always sees exactly one. Seeing none
/// means the caller was filtered out, whatever the virtual machine count says.
/// </para>
/// <para>
/// This deliberately replaces an earlier elevation check. Elevation was never the real gate —
/// membership of the local <c>Hyper-V Administrators</c> group is, and UAC does not strip that
/// group from the filtered token. Measuring the actual capability is correct in both cases.
/// </para>
/// </remarks>
public static class HyperVAccess
{
    /// <summary>The well-known SID of the local <c>Hyper-V Administrators</c> group.</summary>
    /// <remarks>
    /// Used in place of the group name, which is localised — it is "Hyper-V-Administratoren" on a
    /// German Windows, and so on.
    /// </remarks>
    public const string AdministratorsGroupSid = "S-1-5-32-578";

    /// <summary>
    /// Whether Hyper-V is refusing this account, given what a read of the namespace returned.
    /// </summary>
    /// <param name="managementServiceCount">
    /// Instances of <c>Msvm_VirtualSystemManagementService</c> the caller could see.
    /// </param>
    /// <param name="virtualMachineCount">Virtual machines the caller could see.</param>
    public static bool IsDenied(int managementServiceCount, int virtualMachineCount)
    {
        // Seeing a virtual machine proves the read succeeded, so never claim denial then, even if
        // the singleton was somehow missed.
        if (virtualMachineCount > 0)
        {
            return false;
        }

        return managementServiceCount <= 0;
    }

    /// <summary>The failure to raise when <see cref="IsDenied"/> says the account was refused.</summary>
    public static HyperDropException Denied() => new(
        "Hyper-V refused access to this account.",
        "Add your account to the local Hyper-V Administrators group, then sign out and back in. " +
        "You can also restart HyperDrop as an administrator, but drag & drop is limited there.",
        innerException: null,
        failure: HyperDropFailure.HyperVAccessDenied);
}
