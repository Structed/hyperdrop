using HyperDrop.Core.Abstractions;
using HyperDrop.Core.Model;
using HyperDrop.Core.Tests.Fakes;
using HyperDrop.Core.Transfer;

namespace HyperDrop.Core.Tests;

public sealed class TransferQueueTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    private static readonly TransferTarget Target = new()
    {
        VmName = "TEST-VM",
        VmId = "11111111-2222-3333-4444-555555555555",
        DestinationRoot = @"C:\Users\Public\Downloads",
        OverwriteExisting = true,
        CreateFullPath = true,
    };

    private static TransferItem Item(string name, long size = 1024) => new()
    {
        SourcePath = $@"C:\host\{name}",
        RelativePath = name,
        SizeBytes = size,
    };

    [Fact]
    public async Task Enqueue_CopiesEveryItemAndReportsSuccess()
    {
        var copier = new FakeGuestFileCopier();
        await using var queue = new TransferQueue(_ => copier);

        var summary = await RunBatchAsync(queue, [Item("a.txt"), Item("b.txt"), Item("c.txt")]);

        Assert.Equal(3, summary.Succeeded);
        Assert.Equal(0, summary.Failed);
        Assert.Equal(0, summary.Cancelled);
        Assert.Equal(3 * 1024, summary.BytesTransferred);
        Assert.Equal("TEST-VM", summary.VmName);
        Assert.Equal(3, copier.Requests.Count);
    }

    [Fact]
    public async Task Enqueue_BuildsDestinationFromRootAndRelativePath()
    {
        var copier = new FakeGuestFileCopier();
        await using var queue = new TransferQueue(_ => copier);

        await RunBatchAsync(queue, [Item(@"docs\readme.md")]);

        var request = Assert.Single(copier.Requests);
        Assert.Equal(@"C:\Users\Public\Downloads\docs\readme.md", request.DestinationPath);
        Assert.True(request.OverwriteExisting);
        Assert.True(request.CreateFullPath);
    }

    [Fact]
    public async Task Enqueue_ProcessesItemsSeriallyInOrder()
    {
        var running = 0;
        var maxConcurrent = 0;

        var copier = new FakeGuestFileCopier
        {
            OnCopy = async (_, _) =>
            {
                var current = Interlocked.Increment(ref running);
                maxConcurrent = Math.Max(maxConcurrent, current);
                await Task.Delay(20);
                Interlocked.Decrement(ref running);
            },
        };

        await using var queue = new TransferQueue(_ => copier);

        await RunBatchAsync(queue, [Item("1"), Item("2"), Item("3"), Item("4")]);

        Assert.Equal(1, maxConcurrent);
        Assert.Equal(["1", "2", "3", "4"], copier.Requests.Select(r => Path.GetFileName(r.SourcePath)));
    }

    [Fact]
    public async Task Failure_IsReportedWithMessageAndRemedy()
    {
        var copier = new FakeGuestFileCopier
        {
            OnCopy = (_, _) => throw new HyperDropException("A file with that name already exists.", "Turn on overwrite."),
        };

        await using var queue = new TransferQueue(_ => copier);

        var updates = CollectUpdates(queue);
        var summary = await RunBatchAsync(queue, [Item("dupe.txt")]);

        Assert.Equal(1, summary.Failed);
        Assert.Equal(0, summary.Succeeded);

        var failure = updates.Last(snapshot => snapshot.State == TransferState.Failed);
        Assert.Equal("A file with that name already exists.", failure.ErrorMessage);
        Assert.Equal("Turn on overwrite.", failure.Remedy);
    }

    [Fact]
    public async Task UnexpectedException_IsSurfacedRatherThanCrashingTheWorker()
    {
        var copier = new FakeGuestFileCopier
        {
            OnCopy = (request, _) => Path.GetFileName(request.SourcePath) == "boom.txt"
                ? throw new InvalidOperationException("kaboom")
                : Task.CompletedTask,
        };

        await using var queue = new TransferQueue(_ => copier);

        var summary = await RunBatchAsync(queue, [Item("boom.txt"), Item("fine.txt")]);

        // The worker must survive the first failure and still process the second item.
        Assert.Equal(1, summary.Failed);
        Assert.Equal(1, summary.Succeeded);
    }

    [Fact]
    public async Task Cancel_StopsAnInFlightItem()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var copier = new FakeGuestFileCopier
        {
            OnCopy = async (_, token) =>
            {
                started.TrySetResult();
                await Task.Delay(System.Threading.Timeout.Infinite, token);
            },
        };

        await using var queue = new TransferQueue(_ => copier);

        var completion = WaitForBatchAsync(queue);
        var added = queue.Enqueue([Item("slow.bin")], Target);

        await started.Task.WaitAsync(Timeout);
        queue.Cancel(added[0].Id);

        var summary = await completion;

        Assert.Equal(1, summary.Cancelled);
        Assert.Equal(0, summary.Succeeded);
    }

    [Fact]
    public async Task CancelAll_StopsQueuedItemsThatNeverStarted()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var copier = new FakeGuestFileCopier
        {
            OnCopy = async (_, token) =>
            {
                started.TrySetResult();
                await release.Task.WaitAsync(token);
            },
        };

        await using var queue = new TransferQueue(_ => copier);

        var completion = WaitForBatchAsync(queue);
        queue.Enqueue([Item("first"), Item("second"), Item("third")], Target);

        await started.Task.WaitAsync(Timeout);
        queue.CancelAll();
        release.TrySetResult();

        var summary = await completion;

        Assert.Equal(3, summary.Cancelled);
        Assert.Equal(0, summary.Succeeded);
    }

    [Fact]
    public async Task Retry_RunsAFailedItemAgain()
    {
        var attempts = 0;

        var copier = new FakeGuestFileCopier
        {
            OnCopy = (_, _) => Interlocked.Increment(ref attempts) == 1
                ? throw new HyperDropException("transient")
                : Task.CompletedTask,
        };

        await using var queue = new TransferQueue(_ => copier);

        var first = await RunBatchAsync(queue, [Item("flaky.txt")]);
        Assert.Equal(1, first.Failed);

        var id = queue.Snapshot().Single().Id;

        var completion = WaitForBatchAsync(queue);
        queue.Retry(id);
        var second = await completion;

        Assert.Equal(1, second.Succeeded);
        Assert.Equal(2, attempts);
        Assert.Equal(TransferState.Completed, queue.Snapshot().Single().State);
    }

    [Fact]
    public async Task Retry_IgnoresItemsThatAlreadySucceeded()
    {
        var copier = new FakeGuestFileCopier();
        await using var queue = new TransferQueue(_ => copier);

        await RunBatchAsync(queue, [Item("done.txt")]);

        queue.Retry(queue.Snapshot().Single().Id);
        await Task.Delay(100);

        Assert.Single(copier.Requests);
    }

    [Fact]
    public async Task ClearCompleted_RemovesOnlyFinishedItems()
    {
        var copier = new FakeGuestFileCopier
        {
            OnCopy = (request, _) => Path.GetFileName(request.SourcePath) == "bad.txt"
                ? throw new HyperDropException("nope")
                : Task.CompletedTask,
        };

        await using var queue = new TransferQueue(_ => copier);

        await RunBatchAsync(queue, [Item("good.txt"), Item("bad.txt")]);

        var removed = new List<string>();
        queue.ItemsRemoved += (_, ids) => removed.AddRange(ids);

        queue.ClearCompleted();

        Assert.Single(removed);
        var remaining = Assert.Single(queue.Snapshot());
        Assert.Equal(TransferState.Failed, remaining.State);
    }

    [Fact]
    public async Task Progress_ReportsBytesAndReachesTheFullSize()
    {
        var copier = new FakeGuestFileCopier();
        await using var queue = new TransferQueue(_ => copier);

        var updates = CollectUpdates(queue);
        await RunBatchAsync(queue, [Item("big.bin", size: 8192)]);

        Assert.Contains(updates, snapshot => snapshot.BytesTransferred == 4096);

        var final = updates.Last();
        Assert.Equal(8192, final.BytesTransferred);
        Assert.Equal(1d, final.Fraction);
    }

    [Fact]
    public async Task Copier_IsReusedAcrossOneBatchAndDisposedWhenTheQueueDrains()
    {
        var created = 0;
        var copier = new FakeGuestFileCopier();

        await using (var queue = new TransferQueue(_ =>
        {
            created++;
            return copier;
        }))
        {
            await RunBatchAsync(queue, [Item("a"), Item("b"), Item("c")]);

            Assert.Equal(1, created);
            Assert.Equal(1, copier.DisposeCount);
        }
    }

    [Fact]
    public async Task Staging_IsUsedWhenTheStagerAsksForIt()
    {
        using var temp = new TempDirectory();
        var source = temp.CreateFile("remote.bin", sizeBytes: 512);
        var stagingRoot = temp.CreateFolder("staging");

        var copier = new FakeGuestFileCopier();
        var stager = new LocalSourceStager(stagingRoot);

        await using var queue = new TransferQueue(_ => copier, new AlwaysStager(stager));

        await RunBatchAsync(
            queue,
            [
                new TransferItem
                {
                    SourcePath = source,
                    RelativePath = "remote.bin",
                    SizeBytes = 512,
                }
            ]);

        var request = Assert.Single(copier.Requests);

        // The engine must be handed the staged copy, not the original path.
        Assert.NotEqual(source, request.SourcePath);
        Assert.StartsWith(stagingRoot, request.SourcePath, StringComparison.OrdinalIgnoreCase);

        // And the staged copy must be cleaned up afterwards.
        Assert.False(File.Exists(request.SourcePath));
    }

    private static async Task<TransferBatchSummary> RunBatchAsync(
        TransferQueue queue,
        IReadOnlyList<TransferItem> items)
    {
        var completion = WaitForBatchAsync(queue);
        queue.Enqueue(items, Target);
        return await completion;
    }

    private static Task<TransferBatchSummary> WaitForBatchAsync(TransferQueue queue)
    {
        var source = new TaskCompletionSource<TransferBatchSummary>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        void Handler(object? sender, TransferBatchSummary summary)
        {
            queue.BatchCompleted -= Handler;
            source.TrySetResult(summary);
        }

        queue.BatchCompleted += Handler;
        return source.Task.WaitAsync(Timeout);
    }

    private static List<TransferSnapshot> CollectUpdates(TransferQueue queue)
    {
        var updates = new List<TransferSnapshot>();

        queue.ItemUpdated += (_, snapshot) =>
        {
            lock (updates)
            {
                updates.Add(snapshot);
            }
        };

        return updates;
    }

    /// <summary>Forces staging regardless of where the source actually lives.</summary>
    private sealed class AlwaysStager(ISourceStager inner) : ISourceStager
    {
        public bool NeedsStaging(string sourcePath) => true;

        public Task<string> StageAsync(
            string sourcePath,
            IProgress<CopyProgress> progress,
            CancellationToken cancellationToken) =>
            inner.StageAsync(sourcePath, progress, cancellationToken);

        public void Cleanup(string stagedPath) => inner.Cleanup(stagedPath);
    }
}
