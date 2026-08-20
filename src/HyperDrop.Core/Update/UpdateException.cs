namespace HyperDrop.Core.Update;

/// <summary>
/// An update failure that already carries a message suitable for display, plus the reason so the
/// UI can offer the right next step.
/// </summary>
/// <remarks>
/// Separate from <see cref="HyperDropException"/>, which is sealed and models transfer failures.
/// </remarks>
public sealed class UpdateException : Exception
{
    public UpdateException(UpdateFailure reason, Exception? innerException = null)
        : base(UpdateFailures.MessageFor(reason), innerException)
    {
        Reason = reason;
        Remedy = UpdateFailures.RemedyFor(reason);
    }

    public UpdateFailure Reason { get; }

    /// <summary>The suggested next step, when there is an obvious one.</summary>
    public string? Remedy { get; }

    /// <summary>Message plus remedy, for a status line.</summary>
    public string FullMessage => string.IsNullOrWhiteSpace(Remedy) ? Message : $"{Message} {Remedy}";
}
