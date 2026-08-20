using HyperDrop.Core.HyperV;

namespace HyperDrop.Core.Tests;

public sealed class HyperVAccessTests
{
    [Fact]
    public void IsDenied_NoManagementServiceAndNoMachines_IsDenial()
    {
        // The management service is a host singleton, so an authorised caller always sees one.
        // Seeing neither it nor a machine is the shape of a filtered-out caller.
        Assert.True(HyperVAccess.IsDenied(managementServiceCount: 0, virtualMachineCount: 0));
    }

    [Fact]
    public void IsDenied_ManagementServiceVisibleButNoMachines_IsNotDenial()
    {
        // A Hyper-V host with no virtual machines on it yet. Previously this was reported as a
        // permission problem, which sent people off to run the app as an administrator for nothing.
        Assert.False(HyperVAccess.IsDenied(managementServiceCount: 1, virtualMachineCount: 0));
    }

    [Fact]
    public void IsDenied_MachinesVisible_IsNeverDenial()
    {
        // Reading a machine proves the read succeeded, whatever the singleton probe did.
        Assert.False(HyperVAccess.IsDenied(managementServiceCount: 0, virtualMachineCount: 3));
    }

    [Fact]
    public void Denied_IsTaggedSoTheUiCanOfferToFixIt()
    {
        Assert.Equal(HyperDropFailure.HyperVAccessDenied, HyperVAccess.Denied().Failure);
    }

    [Fact]
    public void Denied_PointsAtTheGroupMembershipRatherThanJustElevation()
    {
        var remedy = HyperVAccess.Denied().Remedy;

        Assert.NotNull(remedy);
        Assert.Contains("Hyper-V Administrators", remedy, StringComparison.Ordinal);
    }

    [Fact]
    public void AdministratorsGroupSid_IsTheWellKnownHyperVAdministratorsSid()
    {
        // Hard-coded on purpose: the group name is localised, the SID is not.
        Assert.Equal("S-1-5-32-578", HyperVAccess.AdministratorsGroupSid);
    }
}
