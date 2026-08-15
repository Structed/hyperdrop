namespace HyperDrop.Core.Model;

/// <summary>
/// A Hyper-V virtual machine as shown in the picker.
/// </summary>
public sealed record VirtualMachineInfo
{
    /// <summary>Friendly name (<c>Msvm_ComputerSystem.ElementName</c>).</summary>
    public required string Name { get; init; }

    /// <summary>Stable VM GUID (<c>Msvm_ComputerSystem.Name</c>).</summary>
    public required string Id { get; init; }

    public required VmState State { get; init; }

    /// <summary>
    /// State of the "Guest Service Interface" integration service, which the
    /// <c>Msvm_GuestFileService</c> transfer engine depends on.
    /// </summary>
    public required IntegrationServiceState GuestServiceInterface { get; init; }

    public bool IsRunning => State is VmState.Running;

    /// <summary>
    /// True when files can be copied using the Guest Service Interface without guest credentials.
    /// </summary>
    public bool SupportsGuestServiceCopy =>
        IsRunning && GuestServiceInterface is IntegrationServiceState.Enabled;

    public override string ToString() => Name;
}
