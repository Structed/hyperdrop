using System.Threading.Channels;
using HyperDrop.Core.Abstractions;
using HyperDrop.Core.Model;

namespace HyperDrop.Core.Transfer;

/// <summary>
/// Runs queued file transfers on a background worker, one at a time.
/// </summary>
/// <remarks>
/// <para>
/// Transfers are serial by design. The Hyper-V guest file copy service processes one file per VM
/// at a time anyway, so running several in parallel only makes the progress bars less honest.
/// </para>
/// <para>
/// The queue is UI-framework agnostic: it raises events carrying immutable
/// <see cref="TransferSnapshot"/> values from a background thread, and the caller is responsible
/// for marshalling them onto its own thread.
/// </para>
/// </remarks>
public sealed class TransferQueue : IAsyncDisposable
{
    private readonly Func<TransferTarget, IGuestFileCopier> _copierFactory;
    private readonly ISourceStager _stager;
    private readonly TimeProvider _timeProvider;

    private readonly Channel<TransferJob> _pending;
    private readonly List<TransferJob> _jobs = [];
    private readonly Lock _sync = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _worker;

    private IGuestFileCopier? _copier;
    private TransferTarget? _copierTarget;

    /// <summary>
    /// Items written to the channel but not yet processed. Tracked explicitly because the
    /// single-reader channel's reader does not support <c>Count</c>.
    /// </summary>
    private int _pendingCount;

    private int _batchSucceeded;
    private int _batchFailed;
    private int _batchCancelled;
    private long _batchBytes;
    private string _batchVmName = string.Empty;

    public TransferQueue(
        Func<TransferTarget, IGuestFileCopier> copierFactory,
        ISourceStager? stager = null,
        TimeProvider? timeProvider = null)
    {
        _copierFactory = copierFactory ?? throw new ArgumentNullException(nameof(copierFactory));
        _stager = stager ?? new NoOpSourceStager();
        _timeProvider = timeProvider ?? TimeProvider.System;

        _pending = Channel.CreateUnbounded<TransferJob>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

        _worker = Task.Run(RunAsync);
    }

    /// <summary>Raised when new items join the queue.</summary>
    public event EventHandler<IReadOnlyList<TransferSnapshot>>? ItemsAdded;

    /// <summary>Raised on every progress or state change, from a background thread.</summary>
    public event EventHandler<TransferSnapshot>? ItemUpdated;

    /// <summary>Raised once the queue drains, carrying the totals for the batch just finished.</summary>
    public event EventHandler<TransferBatchSummary>? BatchCompleted;

    /// <summary>Raised when items are removed by <see cref="ClearCompleted"/>.</summary>
    public event EventHandler<IReadOnlyList<string>>? ItemsRemoved;

    public IReadOnlyList<TransferSnapshot> Snapshot()
    {
        lock (_sync)
        {
            return _jobs.Select(ToSnapshot).ToList();
        }
    }

    public IReadOnlyList<TransferSnapshot> Enqueue(IEnumerable<TransferItem> items, TransferTarget target)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(target);

        var added = new List<TransferSnapshot>();
        var jobs = new List<TransferJob>();

        lock (_sync)
        {
            foreach (var item in items)
            {
                var job = new TransferJob(item, target);
                _jobs.Add(job);
                jobs.Add(job);
                added.Add(ToSnapshot(job));
            }
        }

        if (added.Count > 0)
        {
            // Announce the rows before queueing the work, so no progress update can arrive for an
            // item the listener has not seen yet.
            ItemsAdded?.Invoke(this, added);
        }

        foreach (var job in jobs)
        {
            Interlocked.Increment(ref _pendingCount);
            _pending.Writer.TryWrite(job);
        }

        return added;
    }

    /// <summary>Cancels one item, whether it is running or still waiting.</summary>
    public void Cancel(string id)
    {
        TransferJob? job;
        lock (_sync)
        {
            job = _jobs.FirstOrDefault(candidate => candidate.Id == id);
        }

        job?.Cancellation.Cancel();
    }

    public void CancelAll()
    {
        TransferJob[] jobs;
        lock (_sync)
        {
            jobs = [.. _jobs.Where(job => !job.State.IsTerminal())];
        }

        foreach (var job in jobs)
        {
            job.Cancellation.Cancel();
        }
    }

    /// <summary>Puts a failed or cancelled item back on the queue, keeping its place in the list.</summary>
    public void Retry(string id)
    {
        TransferSnapshot? snapshot = null;
        TransferJob? job = null;

        lock (_sync)
        {
            var candidate = _jobs.FirstOrDefault(entry => entry.Id == id);
            if (candidate is null || !candidate.State.IsTerminal() || candidate.State == TransferState.Completed)
            {
                return;
            }

            candidate.Reset();
            snapshot = ToSnapshot(candidate);
            job = candidate;
        }

        if (snapshot is not null)
        {
            ItemUpdated?.Invoke(this, snapshot);
        }

        if (job is not null)
        {
            Interlocked.Increment(ref _pendingCount);
            _pending.Writer.TryWrite(job);
        }
    }

    public void ClearCompleted()
    {
        List<string> removed;

        lock (_sync)
        {
            removed = _jobs
                .Where(job => job.State == TransferState.Completed)
                .Select(job => job.Id)
                .ToList();

            _jobs.RemoveAll(job => job.State == TransferState.Completed);
        }

        if (removed.Count > 0)
        {
            ItemsRemoved?.Invoke(this, removed);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _pending.Writer.TryComplete();
        await _shutdown.CancelAsync().ConfigureAwait(false);

        try
        {
            await _worker.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown.
        }

        await ReleaseCopierAsync().ConfigureAwait(false);
        _shutdown.Dispose();
    }

    private async Task RunAsync()
    {
        try
        {
            while (await _pending.Reader.WaitToReadAsync(_shutdown.Token).ConfigureAwait(false))
            {
                while (_pending.Reader.TryRead(out var job))
                {
                    await ProcessAsync(job).ConfigureAwait(false);

                    if (Interlocked.Decrement(ref _pendingCount) == 0)
                    {
                        await CompleteBatchAsync().ConfigureAwait(false);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown requested.
        }
        catch (ChannelClosedException)
        {
            // Queue disposed.
        }
    }

    private async Task ProcessAsync(TransferJob job)
    {
        if (job.Cancellation.IsCancellationRequested)
        {
            Finish(job, TransferState.Cancelled);
            return;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            job.Cancellation.Token, _shutdown.Token);

        var estimator = new RateEstimator(_timeProvider);
        var progress = CreateProgress(job, estimator);
        string? stagedPath = null;

        try
        {
            var sourcePath = job.Item.SourcePath;

            if (_stager.NeedsStaging(sourcePath))
            {
                SetState(job, TransferState.Staging);
                stagedPath = await _stager
                    .StageAsync(sourcePath, progress, linked.Token)
                    .ConfigureAwait(false);
                sourcePath = stagedPath;
            }

            SetState(job, TransferState.Transferring);
            estimator.Reset();

            var copier = await GetCopierAsync(job.Target).ConfigureAwait(false);

            await copier.CopyAsync(
                new GuestCopyRequest
                {
                    VmName = job.Target.VmName,
                    VmId = job.Target.VmId,
                    SourcePath = sourcePath,
                    DestinationPath = job.Item.ResolveDestination(job.Target.DestinationRoot),
                    SizeBytes = job.Item.SizeBytes,
                    OverwriteExisting = job.Target.OverwriteExisting,
                    CreateFullPath = job.Target.CreateFullPath,
                },
                progress,
                linked.Token).ConfigureAwait(false);

            lock (_sync)
            {
                job.BytesTransferred = job.Item.SizeBytes;
                job.IsIndeterminate = false;
                job.EstimatedRemaining = TimeSpan.Zero;
            }

            Finish(job, TransferState.Completed);
        }
        catch (OperationCanceledException)
        {
            Finish(job, TransferState.Cancelled);
        }
        catch (HyperDropException ex)
        {
            Finish(job, TransferState.Failed, ex.Message, ex.Remedy);
        }
        catch (Exception ex)
        {
            Finish(job, TransferState.Failed, ex.Message);
        }
        finally
        {
            if (stagedPath is not null)
            {
                _stager.Cleanup(stagedPath);
            }
        }
    }

    /// <summary>
    /// Reuses one copier for as long as the target does not change, so engines that hold an
    /// expensive session (PowerShell Direct) pay the setup cost once per batch.
    /// </summary>
    private async ValueTask<IGuestFileCopier> GetCopierAsync(TransferTarget target)
    {
        if (_copier is not null && _copierTarget == target)
        {
            return _copier;
        }

        await ReleaseCopierAsync().ConfigureAwait(false);

        _copier = _copierFactory(target);
        _copierTarget = target;
        return _copier;
    }

    private async ValueTask ReleaseCopierAsync()
    {
        if (_copier is null)
        {
            return;
        }

        try
        {
            await _copier.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // A copier that fails to shut down cleanly must not break the queue.
        }
        finally
        {
            _copier = null;
            _copierTarget = null;
        }
    }

    private IProgress<CopyProgress> CreateProgress(TransferJob job, RateEstimator estimator) =>
        new CallbackProgress<CopyProgress>(report =>
        {
            TransferSnapshot snapshot;

            lock (_sync)
            {
                job.BytesTransferred = report.BytesTransferred;
                job.IsIndeterminate = report.IsIndeterminate;

                estimator.Update(report.BytesTransferred);
                job.BytesPerSecond = estimator.BytesPerSecond;
                job.EstimatedRemaining = estimator.EstimateRemaining(
                    job.Item.SizeBytes - report.BytesTransferred);

                snapshot = ToSnapshot(job);
            }

            ItemUpdated?.Invoke(this, snapshot);
        });

    private void SetState(TransferJob job, TransferState state)
    {
        TransferSnapshot snapshot;

        lock (_sync)
        {
            job.State = state;
            snapshot = ToSnapshot(job);
        }

        ItemUpdated?.Invoke(this, snapshot);
    }

    private void Finish(TransferJob job, TransferState state, string? error = null, string? remedy = null)
    {
        TransferSnapshot snapshot;

        lock (_sync)
        {
            job.State = state;
            job.ErrorMessage = error;
            job.Remedy = remedy;
            job.BytesPerSecond = null;
            job.IsIndeterminate = false;

            if (state != TransferState.Completed)
            {
                job.EstimatedRemaining = null;
            }

            switch (state)
            {
                case TransferState.Completed:
                    _batchSucceeded++;
                    _batchBytes += job.Item.SizeBytes;
                    break;
                case TransferState.Failed:
                    _batchFailed++;
                    break;
                case TransferState.Cancelled:
                    _batchCancelled++;
                    break;
            }

            _batchVmName = job.Target.VmName;
            snapshot = ToSnapshot(job);
        }

        ItemUpdated?.Invoke(this, snapshot);
    }

    private async Task CompleteBatchAsync()
    {
        TransferBatchSummary summary;

        lock (_sync)
        {
            if (_batchSucceeded + _batchFailed + _batchCancelled == 0)
            {
                return;
            }

            summary = new TransferBatchSummary
            {
                Succeeded = _batchSucceeded,
                Failed = _batchFailed,
                Cancelled = _batchCancelled,
                BytesTransferred = _batchBytes,
                VmName = _batchVmName,
            };

            _batchSucceeded = 0;
            _batchFailed = 0;
            _batchCancelled = 0;
            _batchBytes = 0;
        }

        // Drop any session held by the copier now that the queue is idle.
        await ReleaseCopierAsync().ConfigureAwait(false);

        BatchCompleted?.Invoke(this, summary);
    }

    private static TransferSnapshot ToSnapshot(TransferJob job) => new()
    {
        Id = job.Id,
        Item = job.Item,
        State = job.State,
        BytesTransferred = job.BytesTransferred,
        IsIndeterminate = job.IsIndeterminate,
        BytesPerSecond = job.BytesPerSecond,
        EstimatedRemaining = job.EstimatedRemaining,
        ErrorMessage = job.ErrorMessage,
        Remedy = job.Remedy,
    };

    /// <summary>Mutable per-item state, only ever touched under <see cref="_sync"/>.</summary>
    private sealed class TransferJob(TransferItem item, TransferTarget target)
    {
        public TransferItem Item { get; } = item;

        public TransferTarget Target { get; } = target;

        public string Id => Item.Id;

        public TransferState State { get; set; } = TransferState.Queued;

        public long BytesTransferred { get; set; }

        public bool IsIndeterminate { get; set; }

        public double? BytesPerSecond { get; set; }

        public TimeSpan? EstimatedRemaining { get; set; }

        public string? ErrorMessage { get; set; }

        public string? Remedy { get; set; }

        public CancellationTokenSource Cancellation { get; private set; } = new();

        public void Reset()
        {
            State = TransferState.Queued;
            BytesTransferred = 0;
            IsIndeterminate = false;
            BytesPerSecond = null;
            EstimatedRemaining = null;
            ErrorMessage = null;
            Remedy = null;

            Cancellation.Dispose();
            Cancellation = new CancellationTokenSource();
        }
    }
}
