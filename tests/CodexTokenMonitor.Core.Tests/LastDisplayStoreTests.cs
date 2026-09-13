using Xunit;

namespace CodexTokenMonitor.Tests;

public sealed class LastDisplayStoreTests
{
    [Fact]
    public async Task SynchronousFlushCompletesWhileUiContextDoesNotPumpWriterContinuation()
    {
        var root = CreateRoot();
        var context = new HoldingSynchronizationContext();
        var writerEntered = Signal();
        var flushStarted = Signal();
        using var writerEnteredEvent = new ManualResetEventSlim();
        using var writerRelease = new ManualResetEventSlim();
        var writeCount = 0;
        var ui = Task.Run(() =>
        {
            using var cacheScope = MonitorCachePaths.PushLocalAppDataRoot(root);
            using var hooks = LastDisplayStore.PushWriteTestHooks(_ => Task.CompletedTask, () =>
            {
                if (Interlocked.Increment(ref writeCount) != 1) return;
                writerEntered.TrySetResult();
                writerEnteredEvent.Set();
                if (!writerRelease.Wait(TimeSpan.FromSeconds(10)))
                    throw new TimeoutException("The test did not release its paused writer.");
            });
            var previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(context);
            try
            {
                SaveSnapshot(110);
                // The first writer owns WriteGate. Flush must not depend on
                // this blocked caller pumping a posted Release continuation.
                if (!writerEnteredEvent.Wait(TimeSpan.FromSeconds(10)))
                    throw new TimeoutException("The background writer did not start.");
                flushStarted.TrySetResult();
                LastDisplayStore.Flush();
            }
            finally { SynchronizationContext.SetSynchronizationContext(previous); }
        });

        try
        {
            await writerEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await flushStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            writerRelease.Set();
            await ui.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0, context.PostCount);
            using var cacheScope = MonitorCachePaths.PushLocalAppDataRoot(root);
            Assert.Equal(110, LastDisplayStore.Load()!.Result.Summary.TotalTokens);
        }
        finally
        {
            writerRelease.Set();
            // A regression must fail the test instead of leaving a permanently
            // blocked background thread in the test host.
            context.Release();
            await ui.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task FlushWaitingForWriterPersistsNewerSaveAndCancelsItsDebounce()
    {
        using var cacheScope = MonitorCachePaths.PushLocalAppDataRoot(CreateRoot());
        var writerEntered = Signal();
        var newerDelayEntered = Signal();
        var newerDelayCancelled = Signal();
        using var writerRelease = new ManualResetEventSlim();
        var delayCount = 0;
        var writeCount = 0;
        using var hooks = LastDisplayStore.PushWriteTestHooks(token =>
        {
            if (Interlocked.Increment(ref delayCount) == 1) return Task.CompletedTask;
            newerDelayEntered.TrySetResult();
            return WaitForCancellationAsync(token, newerDelayCancelled);
        }, () =>
        {
            if (Interlocked.Increment(ref writeCount) != 1) return;
            writerEntered.TrySetResult();
            if (!writerRelease.Wait(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("The test did not release its paused writer.");
        });

        Task flush = Task.CompletedTask;
        try
        {
            SaveSnapshot(110);
            await writerEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            flush = LastDisplayStore.FlushAsync();
            Assert.False(flush.IsCompleted);
            SaveSnapshot(220);
            await newerDelayEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            writerRelease.Set();
            await flush.WaitAsync(TimeSpan.FromSeconds(10));
            await newerDelayCancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));

            var restored = LastDisplayStore.Load();
            Assert.NotNull(restored);
            Assert.Equal(220, restored.Result.Summary.TotalTokens);
            Assert.Equal(UsageSource.Codex, restored.Source);
            Assert.Equal("fixture-220", restored.Range.Title);
            Assert.Single(restored.Result.BreakdownRows);
            Assert.Empty(restored.Result.DetailRows);
            Assert.Equal(2, Volatile.Read(ref writeCount));
            Assert.True(File.Exists(Path.Combine(MonitorCachePaths.LocalAppData,
                "CodexTokenMonitor", "wpf-last-display-v5.json")));
        }
        finally
        {
            writerRelease.Set();
            await flush.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    private static async Task WaitForCancellationAsync(CancellationToken token, TaskCompletionSource cancelled)
    {
        try { await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false); }
        finally { cancelled.TrySetResult(); }
    }

    private static void SaveSnapshot(long tokens)
    {
        var start = new DateTimeOffset(2000, 1, 18, 0, 0, 0, TimeSpan.FromHours(8));
        var range = new SelectedRange(start, start.AddDays(1), $"fixture-{tokens}", "fixture", RangeMode.Day);
        var row = new TokenUsageBucket
        {
            StartLocal = start.AddHours(9), Events = 1, InputTokens = tokens - 10,
            UncachedInputTokens = tokens - 10, OutputTokens = 10, TotalTokens = tokens
        };
        var rows = new[] { row };
        LastDisplayStore.Save(UsageSource.Codex, range,
            new UsageQueryResult(UsageSummaryBuilder.FromRows(range.Start, range.End, rows), rows,
                TimeSpan.FromMinutes(5), null, Array.Empty<CodexQuotaSnapshot>()) { DetailRows = rows });
    }

    private static string CreateRoot() => Path.Combine(Path.GetTempPath(), "LastDisplayStoreTests-" + Guid.NewGuid().ToString("N"));
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class HoldingSynchronizationContext : SynchronizationContext
    {
        private readonly object sync = new();
        private readonly Queue<(SendOrPostCallback Callback, object? State)> queue = new();
        private bool released;
        private int postCount;
        public int PostCount => Volatile.Read(ref postCount);

        public override void Post(SendOrPostCallback callback, object? state)
        {
            Interlocked.Increment(ref postCount);
            lock (sync)
            {
                if (!released)
                {
                    queue.Enqueue((callback, state));
                    return;
                }
            }
            ThreadPool.QueueUserWorkItem(_ => callback(state));
        }

        public void Release()
        {
            lock (sync)
            {
                released = true;
                while (queue.TryDequeue(out var work))
                    ThreadPool.QueueUserWorkItem(_ => work.Callback(work.State));
            }
        }
    }
}
