using CommunityToolkit.Mvvm.ComponentModel;
using HyperVDrop.Core.Model;
using HyperVDrop.Core.Transfer;

namespace HyperVDrop.App.ViewModels;

/// <summary>
/// One row in the transfer list.
/// </summary>
public sealed partial class TransferItemViewModel : ObservableObject
{
    public TransferItemViewModel(TransferSnapshot snapshot)
    {
        Id = snapshot.Id;
        FileName = snapshot.Item.FileName;
        RelativePath = snapshot.Item.RelativePath;
        TotalBytes = snapshot.Item.SizeBytes;
        TotalBytesText = Humanize.Bytes(TotalBytes);
        Apply(snapshot);
    }

    public string Id { get; }

    public string FileName { get; }

    /// <summary>Where the file lands relative to the destination root, shown for folder drops.</summary>
    public string RelativePath { get; }

    public long TotalBytes { get; }

    public string TotalBytesText { get; }

    /// <summary>Only worth showing when the file came from inside a dropped folder.</summary>
    public bool ShowsRelativePath =>
        !string.Equals(RelativePath, FileName, StringComparison.OrdinalIgnoreCase);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCancel))]
    [NotifyPropertyChangedFor(nameof(CanRetry))]
    [NotifyPropertyChangedFor(nameof(IsFailed))]
    [NotifyPropertyChangedFor(nameof(IsCompleted))]
    private TransferState _state;

    [ObservableProperty]
    private double _progressPercent;

    [ObservableProperty]
    private bool _isIndeterminate;

    [ObservableProperty]
    private string _statusText = "Queued";

    [ObservableProperty]
    private string _detailText = string.Empty;

    [ObservableProperty]
    private string? _errorText;

    public bool CanCancel => !State.IsTerminal();

    public bool CanRetry => State is TransferState.Failed or TransferState.Cancelled;

    public bool IsFailed => State is TransferState.Failed;

    public bool IsCompleted => State is TransferState.Completed;

    /// <summary>Applies a snapshot from the transfer queue. Must be called on the UI thread.</summary>
    public void Apply(TransferSnapshot snapshot)
    {
        State = snapshot.State;
        IsIndeterminate = snapshot.IsIndeterminate && snapshot.State.IsActive();
        ProgressPercent = (snapshot.Fraction ?? 0d) * 100d;

        StatusText = snapshot.State switch
        {
            TransferState.Queued => "Waiting",
            TransferState.Staging => "Preparing",
            TransferState.Transferring => IsIndeterminate ? "Starting" : $"{ProgressPercent:0}%",
            TransferState.Completed => "Done",
            TransferState.Failed => "Failed",
            TransferState.Cancelled => "Cancelled",
            _ => string.Empty,
        };

        DetailText = BuildDetail(snapshot);

        ErrorText = snapshot.ErrorMessage is null
            ? null
            : string.IsNullOrWhiteSpace(snapshot.Remedy)
                ? snapshot.ErrorMessage
                : $"{snapshot.ErrorMessage} {snapshot.Remedy}";
    }

    private string BuildDetail(TransferSnapshot snapshot)
    {
        if (snapshot.State is TransferState.Completed)
        {
            return TotalBytesText;
        }

        if (snapshot.State is TransferState.Failed or TransferState.Cancelled)
        {
            return string.Empty;
        }

        if (snapshot.State is TransferState.Queued)
        {
            return TotalBytesText;
        }

        var parts = new List<string>(3)
        {
            $"{Humanize.Bytes(snapshot.BytesTransferred)} of {TotalBytesText}",
        };

        var rate = Humanize.Rate(snapshot.BytesPerSecond);
        if (!string.IsNullOrEmpty(rate))
        {
            parts.Add(rate);
        }

        var remaining = Humanize.Duration(snapshot.EstimatedRemaining);
        if (!string.IsNullOrEmpty(remaining) && snapshot.EstimatedRemaining > TimeSpan.Zero)
        {
            parts.Add($"{remaining} left");
        }

        return string.Join("  ·  ", parts);
    }
}
