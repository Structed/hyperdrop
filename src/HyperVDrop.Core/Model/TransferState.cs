namespace HyperVDrop.Core.Model;

/// <summary>
/// Lifecycle of a single queued file transfer.
/// </summary>
public enum TransferState
{
    Queued = 0,

    /// <summary>
    /// Source is being copied to a local staging folder first, because the Hyper-V management
    /// service cannot read the original location (for example a mapped network drive).
    /// </summary>
    Staging,

    Transferring,
    Completed,
    Failed,
    Cancelled,
}

public static class TransferStateExtensions
{
    /// <summary>True once the item will not change state again without an explicit retry.</summary>
    public static bool IsTerminal(this TransferState state) =>
        state is TransferState.Completed or TransferState.Failed or TransferState.Cancelled;

    public static bool IsActive(this TransferState state) =>
        state is TransferState.Staging or TransferState.Transferring;
}
