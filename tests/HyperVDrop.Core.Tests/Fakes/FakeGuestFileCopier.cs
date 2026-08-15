using HyperVDrop.Core.Abstractions;
using HyperVDrop.Core.Model;

namespace HyperVDrop.Core.Tests.Fakes;

/// <summary>
/// A copy engine that records what it was asked to do, so queue behaviour can be tested without
/// Hyper-V.
/// </summary>
internal sealed class FakeGuestFileCopier : IGuestFileCopier
{
    private readonly List<GuestCopyRequest> _requests = [];

    public string DisplayName => "Fake";

    public int DisposeCount { get; private set; }

    /// <summary>Optional hook to delay, cancel, or throw partway through a copy.</summary>
    public Func<GuestCopyRequest, CancellationToken, Task>? OnCopy { get; set; }

    public IReadOnlyList<GuestCopyRequest> Requests
    {
        get
        {
            lock (_requests)
            {
                return _requests.ToList();
            }
        }
    }

    public async Task CopyAsync(
        GuestCopyRequest request,
        IProgress<CopyProgress> progress,
        CancellationToken cancellationToken)
    {
        lock (_requests)
        {
            _requests.Add(request);
        }

        progress.Report(CopyProgress.FromBytes(request.SizeBytes / 2, request.SizeBytes));

        if (OnCopy is not null)
        {
            await OnCopy(request, cancellationToken).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        progress.Report(CopyProgress.Complete(request.SizeBytes));
    }

    public ValueTask DisposeAsync()
    {
        DisposeCount++;
        return ValueTask.CompletedTask;
    }
}
