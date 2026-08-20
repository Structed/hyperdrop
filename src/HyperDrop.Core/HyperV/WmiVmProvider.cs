using System.Globalization;
using System.Management;
using HyperDrop.Core.Abstractions;
using HyperDrop.Core.Model;

namespace HyperDrop.Core.HyperV;

/// <summary>
/// Reads virtual machines and their integration-service state from the Hyper-V WMI provider.
/// </summary>
public sealed class WmiVmProvider : IVmProvider
{
    /// <summary>CIM <c>EnabledState</c> value meaning enabled/running.</summary>
    private const ushort Enabled = 2;

    private static readonly TimeSpan JobPollInterval = TimeSpan.FromMilliseconds(250);

    public Task<IReadOnlyList<VirtualMachineInfo>> ListAsync(CancellationToken cancellationToken = default) =>
        Task.Run<IReadOnlyList<VirtualMachineInfo>>(() => List(cancellationToken), cancellationToken);

    public Task<VirtualMachineInfo?> FindAsync(string vmId, CancellationToken cancellationToken = default) =>
        Task.Run(
            () => List(cancellationToken).FirstOrDefault(vm =>
                string.Equals(vm.Id, vmId, StringComparison.OrdinalIgnoreCase)),
            cancellationToken);

    /// <summary>
    /// Turns on the "Guest Service Interface" integration service by modifying its resource
    /// setting data, which is what <c>Enable-VMIntegrationService</c> does under the covers.
    /// </summary>
    public async Task EnableGuestServiceInterfaceAsync(
        string vmId,
        CancellationToken cancellationToken = default)
    {
        var scope = Wmi.ConnectScope();

        using var vm = Wmi.GetVirtualMachine(scope, vmId);
        using var activeSettings = Wmi.FirstRelated(vm, "Msvm_VirtualSystemSettingData", "Msvm_SettingsDefineState")
            ?? throw new HyperDropException("Hyper-V did not report any settings for this virtual machine.");

        using var guestServiceSettings = Wmi.FirstRelated(
            activeSettings,
            "Msvm_GuestServiceInterfaceComponentSettingData",
            "Msvm_VirtualSystemSettingDataComponent")
            ?? throw new HyperDropException(
                "This virtual machine does not expose a Guest Service Interface.",
                "Its integration services may be too old. Use PowerShell Direct instead.");

        if (ToUInt16(guestServiceSettings["EnabledState"]) == Enabled)
        {
            return;
        }

        guestServiceSettings["EnabledState"] = Enabled;

        using var service = Wmi.GetManagementService(scope);
        using var parameters = service.GetMethodParameters("ModifyResourceSettings");
        parameters["ResourceSettings"] = new[] { guestServiceSettings.GetText(TextFormat.WmiDtd20) };

        using var outParameters = service.InvokeMethod("ModifyResourceSettings", parameters, null);

        await WmiJobRunner
            .RunAsync(outParameters, scope, percentComplete: null, JobPollInterval, cancellationToken)
            .ConfigureAwait(false);
    }

    private static IReadOnlyList<VirtualMachineInfo> List(CancellationToken cancellationToken)
    {
        var scope = Wmi.ConnectScope();

        try
        {
            var guestServiceStates = ReadGuestServiceStates(scope);
            var machines = ReadMachines(scope, guestServiceStates, cancellationToken);

            // Hyper-V does not fail an unauthorised read, it just returns nothing, which would
            // otherwise surface as a misleading "no virtual machines found". Having read a machine
            // already proves access, so the probe is only worth a round trip for an empty list —
            // and it runs here, once the enumeration above has been released, rather than nested
            // inside it.
            if (machines.Count == 0 &&
                HyperVAccess.IsDenied(Wmi.TryCountManagementServices(scope), virtualMachineCount: 0))
            {
                throw HyperVAccess.Denied();
            }

            return machines
                .OrderBy(vm => vm.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (ManagementException ex)
        {
            // Hyper-V's own text is often just "Generic failure", so name the operation: without
            // that the message says nothing at all about what was being attempted.
            throw new HyperDropException(
                $"Hyper-V could not return the virtual machine list ({ex.Message.Trim()}).",
                "Refresh to try again. If it keeps happening, restart the \"Hyper-V Virtual Machine "
                + "Management\" (vmms) service.",
                ex);
        }
    }

    /// <summary>Enumerates the virtual machines, releasing the query before returning.</summary>
    private static List<VirtualMachineInfo> ReadMachines(
        ManagementScope scope,
        Dictionary<string, IntegrationServiceState> guestServiceStates,
        CancellationToken cancellationToken)
    {
        var machines = new List<VirtualMachineInfo>();

        using var searcher = new ManagementObjectSearcher(
            scope,
            new ObjectQuery(
                "SELECT ElementName, Name, EnabledState FROM Msvm_ComputerSystem " +
                "WHERE Caption = 'Virtual Machine'"));

        using var results = searcher.Get();

        foreach (ManagementBaseObject item in results)
        {
            using (item)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (item["Name"] is not string id || item["ElementName"] is not string name)
                {
                    continue;
                }

                machines.Add(new VirtualMachineInfo
                {
                    Name = name,
                    Id = id,
                    State = MapVmState(ToUInt16(item["EnabledState"])),
                    GuestServiceInterface = guestServiceStates.TryGetValue(id, out var state)
                        ? state
                        : IntegrationServiceState.Unknown,
                });
            }
        }

        return machines;
    }

    /// <summary>
    /// Reads every guest service component in one query and indexes it by VM id, rather than
    /// walking associations per machine which would be one round trip per VM.
    /// </summary>
    private static Dictionary<string, IntegrationServiceState> ReadGuestServiceStates(ManagementScope scope)
    {
        var states = new Dictionary<string, IntegrationServiceState>(StringComparer.OrdinalIgnoreCase);

        using var searcher = new ManagementObjectSearcher(
            scope,
            new ObjectQuery("SELECT SystemName, EnabledState FROM Msvm_GuestServiceInterfaceComponent"));

        using var results = searcher.Get();

        foreach (ManagementBaseObject item in results)
        {
            using (item)
            {
                if (item["SystemName"] is not string systemName)
                {
                    continue;
                }

                states[systemName] = ToUInt16(item["EnabledState"]) == Enabled
                    ? IntegrationServiceState.Enabled
                    : IntegrationServiceState.Disabled;
            }
        }

        return states;
    }

    /// <summary>Maps <c>Msvm_ComputerSystem.EnabledState</c> onto <see cref="VmState"/>.</summary>
    internal static VmState MapVmState(ushort enabledState) => enabledState switch
    {
        2 => VmState.Running,
        3 => VmState.Off,
        32768 => VmState.Paused,
        32769 => VmState.Saved,
        32770 => VmState.Starting,
        32774 => VmState.Stopping,
        32776 => VmState.Stopping,
        32777 => VmState.Starting,
        0 => VmState.Unknown,
        _ => VmState.Other,
    };

    private static ushort ToUInt16(object? value) =>
        value is null ? (ushort)0 : Convert.ToUInt16(value, CultureInfo.InvariantCulture);
}
