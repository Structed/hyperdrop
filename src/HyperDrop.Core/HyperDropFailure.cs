namespace HyperDrop.Core;

/// <summary>
/// The kind of failure a <see cref="HyperDropException"/> represents, for the cases where the UI
/// can offer more than a message.
/// </summary>
public enum HyperDropFailure
{
    /// <summary>Nothing special. Show the message and the remedy.</summary>
    General,

    /// <summary>
    /// Hyper-V refused this account, so the UI can offer to fix the group membership or restart
    /// elevated instead of just describing the problem.
    /// </summary>
    HyperVAccessDenied,
}
