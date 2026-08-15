namespace HyperVDrop.App.ViewModels;

/// <summary>
/// Which engine carries files into the guest.
/// </summary>
public enum TransferMethod
{
    /// <summary>Uses the Guest Service Interface. No guest sign-in required.</summary>
    GuestService = 0,

    /// <summary>Uses PowerShell Direct. Requires guest credentials and a Windows guest.</summary>
    PowerShellDirect,
}

/// <summary>A transfer method paired with the label shown in the picker.</summary>
public sealed record TransferMethodOption(TransferMethod Value, string Display);
