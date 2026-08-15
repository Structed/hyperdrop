namespace HyperVDrop.Core.Model;

/// <summary>
/// Runtime state of a virtual machine, mapped from <c>Msvm_ComputerSystem.EnabledState</c>.
/// </summary>
public enum VmState
{
    Unknown = 0,
    Running,
    Off,
    Saved,
    Paused,
    Starting,
    Stopping,
    Other,
}

/// <summary>
/// Whether a Hyper-V integration service is switched on for a virtual machine.
/// </summary>
public enum IntegrationServiceState
{
    Unknown = 0,
    Enabled,
    Disabled,
}
