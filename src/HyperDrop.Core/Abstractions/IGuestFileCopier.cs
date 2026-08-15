using HyperDrop.Core.Model;

namespace HyperDrop.Core.Abstractions;

/// <summary>
/// Copies a single file from the host into a running guest.
/// </summary>
/// <remarks>
/// Implementations may hold expensive per-VM state (a PowerShell Direct session, for example), so
/// the transfer queue keeps one instance alive for a whole batch and disposes it at the end.
/// </remarks>
public interface IGuestFileCopier : IAsyncDisposable
{
    /// <summary>Short name shown in the UI, for example "Guest Service Interface".</summary>
    string DisplayName { get; }

    /// <summary>
    /// Copies one file, reporting progress until it completes or throws.
    /// </summary>
    /// <exception cref="HyperDropException">The copy failed for a reason worth showing the user.</exception>
    /// <exception cref="OperationCanceledException">The copy was cancelled.</exception>
    Task CopyAsync(
        GuestCopyRequest request,
        IProgress<CopyProgress> progress,
        CancellationToken cancellationToken);
}
