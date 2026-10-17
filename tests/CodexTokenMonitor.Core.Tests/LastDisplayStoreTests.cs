using System.Text;
using System.Text.Json;
using Xunit;

namespace CodexTokenMonitor.Tests;

public sealed partial class LastDisplayStoreTests
{
    [Fact]
    public async Task LargeSnapshotPreservesModelsQuotasAndUnicodeWithTheExistingJsonContract()
    {
        using var cacheScope = MonitorCachePaths.PushLocalAppDataRoot(CreateRoot());
        using var hooks = LastDisplayStore.PushWriteTestHooks(
            token => Task.Delay(Timeout.Infinite, token), () => { });
        var start = new DateTimeOffset(2000, 1, 18, 0, 0, 0, TimeSpan.FromHours(8));
        const string model = "模型-α-🧪";
        var range = new SelectedRange(start, start.AddDays(1), "今天 · \"费用\" 🧪", "明细\n缓存", RangeMode.Day);
        var rows = Enumerable.Range(0, 6_430).Select(index =>
        {
            var time = start.AddHours(9).AddSeconds(index);
            var row = new TokenUsageBucket { StartLocal = time };
            row.Add(new TokenUsageEvent(time, 120_000 + index, 90_000, 1_000, 300,
                121_000 + index, CacheWriteInputTokens: 10_000, ModelId: model, ServiceTier: "priority"));
            return row;
        }).ToArray();
        var summary = UsageSummaryBuilder.FromRows(range.Start, range.End, rows);
        var daily = new TokenUsageBucket { StartLocal = start };
        foreach (var row in rows) daily.MergeFrom(row);
        summary.DailyBuckets.Add(daily);
        var reset = start.AddDays(1);
        var quota = new CodexQuotaEstimate(rows[^1].StartLocal, "codex", "额度 · 中文",
            null, new CodexQuotaWindowEstimate("7d", 62m, 10_080, start.AddDays(-6), range.End,
                reset, summary, 627.12m, 1_000m, 1_000_000_000));
        var snapshots = new[]
        {
            new CodexQuotaSnapshot(rows[0].StartLocal, "codex", "额度 · 中文", null, null, 40m, reset),
            new CodexQuotaSnapshot(rows[^1].StartLocal, "codex", "额度 · 中文", null, null, 62m, reset)
        };
        var result = new UsageQueryResult(summary, rows, TimeSpan.FromMinutes(107), quota, snapshots)
        {
            DetailRows = rows
        };

        LastDisplayStore.Save(UsageSource.Codex, range, result);
        await LastDisplayStore.FlushAsync();

        var restored = Assert.IsType<LastDisplaySnapshot>(LastDisplayStore.Load());
        Assert.Equal(range, restored.Range);
        Assert.Equal(result.CodingTime, restored.Result.CodingTime);
        Assert.Equal(summary.TotalTokens, restored.Result.Summary.TotalTokens);
        Assert.Single(restored.Result.Summary.DailyBuckets);
        Assert.Equal(rows.Length, restored.Result.BreakdownRows.Count);
        Assert.Empty(restored.Result.DetailRows);
        for (var index = 0; index < rows.Length; index++)
        {
            var original = rows[index];
            var actual = restored.Result.BreakdownRows[index];
            Assert.Equal(original.StartLocal, actual.StartLocal);
            Assert.Equal(original.InputTokens, actual.InputTokens);
            Assert.Equal(original.CachedInputTokens, actual.CachedInputTokens);
            Assert.Equal(original.CacheWriteInputTokens, actual.CacheWriteInputTokens);
            Assert.Equal(original.TotalTokens, actual.TotalTokens);
            Assert.Equal(original.ReasoningOutputTokens, actual.ReasoningOutputTokens);
            var actualModel = Assert.Single(actual.ModelUsage);
            Assert.Equal(model, actualModel.Key);
            var actualTier = Assert.Single(actualModel.Value.ServiceTierUsage);
            Assert.Equal("priority", actualTier.Key);
            Assert.Equal(original.TotalTokens, actualTier.Value.TotalTokens);
        }
        Assert.Null(restored.Result.Quota!.FiveHour);
        Assert.Equal(quota.LimitName, restored.Result.Quota.LimitName);
        Assert.Equal(quota.Week!.UsedPercent, restored.Result.Quota.Week!.UsedPercent);
        Assert.Equal(quota.Week.UsedGptCost, restored.Result.Quota.Week.UsedGptCost);
        Assert.Equal(summary.TotalTokens, restored.Result.Quota.Week.Usage.TotalTokens);
        Assert.Equal(snapshots, restored.Result.QuotaSnapshots);

        var path = Path.Combine(MonitorCachePaths.LocalAppData, "CodexTokenMonitor", "wpf-last-display-v5.json");
        var bytes = File.ReadAllBytes(path);
        var state = JsonSerializer.Deserialize<LastDisplayState>(File.ReadAllText(path));
        // The former UTF-16 string writer and the stream writer emit the same
        // UTF-8 JSON bytes, so existing v5 readers keep the complete schema.
        Assert.Equal(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(state)), bytes);
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(path)!, "*.tmp"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LoadAcceptsExistingV5JsonIncludingUtf8Bom(bool withBom)
    {
        using var cacheScope = MonitorCachePaths.PushLocalAppDataRoot(CreateRoot());
        var path = Path.Combine(MonitorCachePaths.LocalAppData, "CodexTokenMonitor", "wpf-last-display-v5.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        const string json = """
            {"Source":0,"SavedAtLocal":"2000-01-18T10:00:00+08:00",
             "Range":{"Start":"2000-01-18T00:00:00+08:00","End":"2000-01-19T00:00:00+08:00","Title":"旧版 · 中文 🧪","BreakdownTitle":"明细","Mode":0},
             "Result":{"Summary":{"StartLocal":"2000-01-18T00:00:00+08:00","EndLocal":"2000-01-19T00:00:00+08:00","Events":1,"InputTokens":100,"CachedInputTokens":60,"UncachedInputTokens":40,"OutputTokens":10,"TotalTokens":110},
                       "BreakdownRows":[{"StartLocal":"2000-01-18T09:00:00+08:00","Events":1,"InputTokens":100,"CachedInputTokens":60,"UncachedInputTokens":40,"OutputTokens":10,"TotalTokens":110}],
                       "CodingTimeTicks":3000000000,"Quota":null,"QuotaSnapshots":[]}}
            """;
        File.WriteAllText(path, json, new UTF8Encoding(withBom));

        var restored = Assert.IsType<LastDisplaySnapshot>(LastDisplayStore.Load());

        Assert.Equal("旧版 · 中文 🧪", restored.Range.Title);
        Assert.Equal(110, restored.Result.Summary.TotalTokens);
        Assert.Equal(60, Assert.Single(restored.Result.BreakdownRows).CachedInputTokens);
        Assert.Equal(TimeSpan.FromMinutes(5), restored.Result.CodingTime);
        Assert.Null(restored.Result.Quota);
    }

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
