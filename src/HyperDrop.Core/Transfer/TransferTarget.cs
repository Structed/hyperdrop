using HyperDrop.Core.Model;

namespace HyperDrop.Core.Transfer;

/// <summary>
/// Where a batch of files is going, and the options that apply to the whole batch.
/// </summary>
public sealed record TransferTarget
{
    public required string VmName { get; init; }

    public required string VmId { get; init; }

    /// <summary>Absolute folder inside the guest, for example <c>C:\Users\Public\Downloads</c>.</summary>
    public required string DestinationRoot { get; init; }

    public bool OverwriteExisting { get; init; }

    public bool CreateFullPath { get; init; } = true;
}

/// <summary>
/// An immutable view of one queued transfer, safe to hand to the UI thread.
/// </summary>
public sealed record TransferSnapshot
{
    public required string Id { get; init; }

    public required TransferItem Item { get; init; }

    public required TransferState State { get; init; }

    public required long BytesTransferred { get; init; }

    /// <summary>True when the engine cannot currently say how far along it is.</summary>
    public bool IsIndeterminate { get; init; }

    public double? BytesPerSecond { get; init; }

    public TimeSpan? EstimatedRemaining { get; init; }

    public string? ErrorMessage { get; init; }

    /// <summary>Suggested fix for <see cref="ErrorMessage"/>, when one is known.</summary>
    public string? Remedy { get; init; }

    public long TotalBytes => Item.SizeBytes;

    public double? Fraction => IsIndeterminate || TotalBytes <= 0
        ? null
        : Math.Clamp((double)BytesTransferred / TotalBytes, 0d, 1d);
}

/// <summary>
/// Totals for a finished batch, used to build the completion notification.
/// </summary>
public sealed record TransferBatchSummary
{
    public required int Succeeded { get; init; }

    public required int Failed { get; init; }

    public required int Cancelled { get; init; }

    public required long BytesTransferred { get; init; }

    public required string VmName { get; init; }

    public int Total => Succeeded + Failed + Cancelled;
}
