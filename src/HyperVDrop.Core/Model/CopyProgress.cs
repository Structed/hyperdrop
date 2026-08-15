namespace HyperVDrop.Core.Model;

/// <summary>
/// A progress sample reported by a copy engine.
/// </summary>
/// <remarks>
/// The Guest Service Interface engine only exposes an integer percentage from the Hyper-V job, so
/// byte counts there are derived from the file size. The PowerShell Direct engine reports exact
/// byte counts. <see cref="IsIndeterminate"/> covers the case where an engine genuinely cannot say
/// how far along it is yet.
/// </remarks>
public readonly record struct CopyProgress
{
    public long BytesTransferred { get; init; }

    public long TotalBytes { get; init; }

    /// <summary>True when no meaningful percentage is available and the UI should show a marquee.</summary>
    public bool IsIndeterminate { get; init; }

    /// <summary>Fraction in the range 0..1, or <c>null</c> when indeterminate.</summary>
    public double? Fraction => IsIndeterminate || TotalBytes <= 0
        ? null
        : Math.Clamp((double)BytesTransferred / TotalBytes, 0d, 1d);

    public static CopyProgress Indeterminate(long totalBytes) =>
        new() { TotalBytes = totalBytes, IsIndeterminate = true };

    public static CopyProgress FromBytes(long transferred, long totalBytes) =>
        new() { BytesTransferred = Math.Clamp(transferred, 0, Math.Max(totalBytes, 0)), TotalBytes = totalBytes };

    /// <summary>Builds a sample from the integer percentage reported by an <c>Msvm_ConcreteJob</c>.</summary>
    public static CopyProgress FromPercent(int percent, long totalBytes)
    {
        var clamped = Math.Clamp(percent, 0, 100);
        return new CopyProgress
        {
            BytesTransferred = totalBytes <= 0 ? 0 : (long)(totalBytes * (clamped / 100d)),
            TotalBytes = totalBytes,
        };
    }

    public static CopyProgress Complete(long totalBytes) =>
        new() { BytesTransferred = totalBytes, TotalBytes = totalBytes };
}
