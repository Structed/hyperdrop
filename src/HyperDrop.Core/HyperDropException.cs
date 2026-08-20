namespace HyperDrop.Core;

/// <summary>
/// A failure that already carries a message suitable for display to the user.
/// </summary>
public sealed class HyperDropException : Exception
{
    public HyperDropException(
        string message,
        string? remedy = null,
        Exception? innerException = null,
        HyperDropFailure failure = HyperDropFailure.General)
        : base(message, innerException)
    {
        Remedy = remedy;
        Failure = failure;
    }

    /// <summary>Optional follow-up suggestion, for example "enable Overwrite existing files".</summary>
    public string? Remedy { get; }

    /// <summary>What went wrong, for the failures the UI can actively help with.</summary>
    public HyperDropFailure Failure { get; }

    /// <summary>Message plus remedy, for tooltips and log lines.</summary>
    public string FullMessage => string.IsNullOrWhiteSpace(Remedy) ? Message : $"{Message} {Remedy}";
}
