using System.Management;

namespace HyperDrop.Core.HyperV;

/// <summary>
/// Shared plumbing for talking to the Hyper-V WMI provider.
/// </summary>
internal static class Wmi
{
    internal const string VirtualizationNamespace = @"root\virtualization\v2";

    /// <summary>
    /// Connects to the Hyper-V namespace, converting the two failures users actually hit
    /// (no Hyper-V, no permission) into messages that say what to do about it.
    /// </summary>
    internal static ManagementScope ConnectScope()
    {
        var scope = new ManagementScope(
            VirtualizationNamespace,
            new ConnectionOptions
            {
                Impersonation = ImpersonationLevel.Impersonate,
                EnablePrivileges = true,
            });

        try
        {
            scope.Connect();
        }
        catch (ManagementException ex) when (ex.ErrorCode is ManagementStatus.InvalidNamespace)
        {
            throw new HyperDropException(
                "The Hyper-V management interface was not found on this machine.",
                "Enable the Hyper-V role in Windows Features, then restart.",
                ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            var denied = HyperVAccess.Denied();
            throw new HyperDropException(
                denied.Message,
                denied.Remedy,
                ex,
                HyperDropFailure.HyperVAccessDenied);
        }

        return scope;
    }

    /// <summary>
    /// Counts the host's <c>Msvm_VirtualSystemManagementService</c> singleton, which is how we
    /// tell "no virtual machines" apart from "Hyper-V will not talk to this account".
    /// </summary>
    internal static int CountManagementServices(ManagementScope scope)
    {
        using var searcher = new ManagementObjectSearcher(
            scope,
            new ObjectQuery("SELECT __PATH FROM Msvm_VirtualSystemManagementService"));

        using var results = searcher.Get();

        var count = 0;

        foreach (ManagementBaseObject item in results)
        {
            using (item)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>Escapes a value for safe embedding in a WQL string literal.</summary>
    internal static string EscapeWql(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
             .Replace("'", "\\'", StringComparison.Ordinal);

    /// <summary>Returns the first object matching a query, or <c>null</c>.</summary>
    internal static ManagementObject? QueryFirst(ManagementScope scope, string wql)
    {
        using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery(wql));
        using var results = searcher.Get();

        foreach (ManagementBaseObject item in results)
        {
            if (item is ManagementObject managementObject)
            {
                return managementObject;
            }

            item.Dispose();
        }

        return null;
    }

    /// <summary>Returns the first related object, or <c>null</c>.</summary>
    internal static ManagementObject? FirstRelated(
        ManagementObject source,
        string relatedClass,
        string relationshipClass)
    {
        using var results = source.GetRelated(
            relatedClass,
            relationshipClass,
            relationshipQualifier: null,
            relatedQualifier: null,
            relatedRole: null,
            thisRole: null,
            classDefinitionsOnly: false,
            options: null);

        foreach (ManagementBaseObject item in results)
        {
            if (item is ManagementObject managementObject)
            {
                return managementObject;
            }

            item.Dispose();
        }

        return null;
    }

    /// <summary>Locates the host's <c>Msvm_VirtualSystemManagementService</c> singleton.</summary>
    /// <remarks>
    /// The singleton always exists on a Hyper-V host, so not finding it means the caller was
    /// filtered out rather than that the host is misconfigured.
    /// </remarks>
    internal static ManagementObject GetManagementService(ManagementScope scope) =>
        QueryFirst(scope, "SELECT * FROM Msvm_VirtualSystemManagementService")
        ?? throw HyperVAccess.Denied();

    /// <summary>Locates a virtual machine by its stable GUID.</summary>
    internal static ManagementObject GetVirtualMachine(ManagementScope scope, string vmId) =>
        QueryFirst(scope, $"SELECT * FROM Msvm_ComputerSystem WHERE Name = '{EscapeWql(vmId)}'")
        ?? throw new HyperDropException(
            "The virtual machine could not be found. It may have been deleted.",
            "Refresh the virtual machine list.");
}
