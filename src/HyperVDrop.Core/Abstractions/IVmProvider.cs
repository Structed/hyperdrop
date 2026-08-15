using HyperVDrop.Core.Model;

namespace HyperVDrop.Core.Abstractions;

/// <summary>
/// Enumerates Hyper-V virtual machines and manages the integration services the app depends on.
/// </summary>
public interface IVmProvider
{
    /// <summary>Lists all virtual machines on the local host, running or not.</summary>
    Task<IReadOnlyList<VirtualMachineInfo>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>Re-reads a single virtual machine, or returns <c>null</c> if it no longer exists.</summary>
    Task<VirtualMachineInfo?> FindAsync(string vmId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Switches on the "Guest Service Interface" integration service for a virtual machine.
    /// </summary>
    Task EnableGuestServiceInterfaceAsync(string vmId, CancellationToken cancellationToken = default);
}
