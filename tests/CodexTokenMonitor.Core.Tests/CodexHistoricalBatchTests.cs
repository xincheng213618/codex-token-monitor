using System.Text.Json;
using Xunit;

namespace CodexTokenMonitor.Tests;

public sealed class CodexHistoricalBatchTests
{
    private static readonly TimeSpan Beijing = TimeSpan.FromHours(8);
    private static readonly DateTimeOffset FirstDay = new(1999, 1, 18, 0, 0, 0, Beijing);

    [Fact]
    public void BatchPreservesImportsAndCrossDayTurnsWithoutCachingUnrequestedDays()
    {
        using var environment = new TestEnvironment();
        var laterDay = FirstDay.AddDays(2);
        var emptyDay = FirstDay.AddDays(3);
        var first = Event(FirstDay, "shared-turn", 100);
        var later = Event(laterDay, "shared-turn", 200);
        var skipped = Event(FirstDay.AddDays(1), "unrequested", 500);
        environment.WriteLog("sessions", "first.jsonl", first, later, skipped);
        environment.WriteLog("archived_sessions", "duplicate.jsonl", first, later);
        environment.Cache.MergeImportedDetailEvents(new[]
        {
            new TokenUsageEvent(FirstDay.AddHours(10), 50, 0, 5, 0, 55, "imported:retained")
        });
        var completed = new List<DateTimeOffset>();
        var progress = new List<(int Completed, int Total)>();

        UsageSourceReaders.Codex.WarmHistoricalDays(
            new[] { FirstDay.ToUniversalTime(), laterDay, emptyDay, FirstDay },
            dayCompleted: completed.Add,
            fileProgress: (count, total) => progress.Add((count, total)));

        Assert.Equal(new[] { emptyDay, laterDay, FirstDay }, completed);
        Assert.Equal(new[] { (0, 2), (1, 2), (2, 2) }, progress);
        AssertDay(environment.Cache, FirstDay, 2, 165);
        AssertDay(environment.Cache, laterDay, 1, 210);
        AssertDay(environment.Cache, emptyDay, 0, 0);
        Assert.False(environment.Cache.TryGetRecord(DateOnly.FromDateTime(FirstDay.AddDays(1).DateTime), out _));
        Assert.Contains(environment.Cache.GetDetailEvents(DateOnly.FromDateTime(FirstDay.DateTime)),
            item => item.Key == "imported:retained");

        // Completed days are cache hits: repeating a batch must not scan the
        // source again or add either local duplicates or imported data twice.
        var repeatedProgress = new List<(int Completed, int Total)>();
        var repeatedCompleted = new List<DateTimeOffset>();
        UsageSourceReaders.Codex.WarmHistoricalDays(new[] { FirstDay, laterDay, emptyDay },
            dayCompleted: repeatedCompleted.Add,
            fileProgress: (count, total) => repeatedProgress.Add((count, total)));
        Assert.Empty(repeatedProgress);
        Assert.Equal(new[] { emptyDay, laterDay, FirstDay }, repeatedCompleted);
        AssertDay(environment.Cache, FirstDay, 2, 165);
        AssertDay(environment.Cache, laterDay, 1, 210);
    }

    [Fact]
    public void UnreadableSourceKeepsEveryRequestedDayIncomplete()
    {
        using var environment = new TestEnvironment();
        environment.WriteLog("sessions", "available.jsonl", Event(FirstDay, "available", 100));
        var lockedPath = environment.WriteLog("sessions", "locked.jsonl", Event(FirstDay, "locked", 200));
        using var locked = new FileStream(lockedPath, FileMode.Open, FileAccess.Read, FileShare.None);
        var completed = new List<DateTimeOffset>();

        UsageSourceReaders.Codex.WarmHistoricalDays(new[] { FirstDay, FirstDay.AddDays(1) },
            dayCompleted: completed.Add);

        Assert.Empty(completed);
        Assert.Equal(2, UsageSourceReaders.Codex.GetIncompleteHistoricalDays(FirstDay, FirstDay.AddDays(1)).Count);
        var retained = environment.Cache.GetDetailEvents(DateOnly.FromDateTime(FirstDay.DateTime));
        Assert.Equal("codex:available", Assert.Single(retained).Key);
    }

    [Fact]
    public void CancellationDuringScanDoesNotPublishPartialDays()
    {
        using var environment = new TestEnvironment();
        environment.WriteLog("sessions", "first.jsonl", Event(FirstDay, "first", 100));
        environment.WriteLog("sessions", "second.jsonl", Event(FirstDay.AddDays(1), "second", 200));
        using var cancellation = new CancellationTokenSource();
        var completed = new List<DateTimeOffset>();

        Assert.Throws<OperationCanceledException>(() => UsageSourceReaders.Codex.WarmHistoricalDays(
            new[] { FirstDay, FirstDay.AddDays(1) }, cancellation.Token, completed.Add,
            (count, _) =>
            {
                if (count == 1)
                {
                    cancellation.Cancel();
                }
            }));

        Assert.Empty(completed);
        Assert.False(environment.Cache.TryGetRecord(DateOnly.FromDateTime(FirstDay.DateTime), out _));
        Assert.False(environment.Cache.TryGetRecord(DateOnly.FromDateTime(FirstDay.AddDays(1).DateTime), out _));
    }

    [Fact]
    public void CancellationDuringPersistenceKeepsCommittedDayAndCanResume()
    {
        using var environment = new TestEnvironment();
        var laterDay = FirstDay.AddDays(1);
        environment.WriteLog("sessions", "both.jsonl",
            Event(FirstDay, "first", 100), Event(laterDay, "second", 200));
        using var cancellation = new CancellationTokenSource();

        Assert.Throws<OperationCanceledException>(() => UsageSourceReaders.Codex.WarmHistoricalDays(
            new[] { FirstDay, laterDay }, cancellation.Token, _ => cancellation.Cancel()));

        AssertDay(environment.Cache, laterDay, 1, 210);
        Assert.False(environment.Cache.TryGetRecord(DateOnly.FromDateTime(FirstDay.DateTime), out _));
        UsageSourceReaders.Codex.WarmHistoricalDays(UsageSourceReaders.Codex.GetIncompleteHistoricalDays(FirstDay, laterDay));
        AssertDay(environment.Cache, FirstDay, 1, 110);
        AssertDay(environment.Cache, laterDay, 1, 210);
    }

    [Fact]
    public void MissingLegacySourcePreservesAggregateWithoutClaimingCompleteDetails()
    {
        using var environment = new TestEnvironment();
        var aggregate = new TokenUsageBucket { StartLocal = FirstDay };
        aggregate.Add(FirstDay.AddHours(9), 100, 40, 10, 0, 110);
        environment.Cache.Put(aggregate, isComplete: false,
            scannedThroughLocal: FirstDay.AddDays(1).AddTicks(-1));
        var completed = new List<DateTimeOffset>();

        UsageSourceReaders.Codex.WarmHistoricalDays(new[] { FirstDay }, dayCompleted: completed.Add);

        Assert.Empty(completed);
        Assert.True(environment.Cache.TryGetRecord(DateOnly.FromDateTime(FirstDay.DateTime), out var record));
        Assert.Equal(110, record.TotalTokens);
        Assert.Equal(1, record.Events);
        Assert.False(record.IsComplete);
        Assert.Single(UsageSourceReaders.Codex.GetIncompleteHistoricalDays(FirstDay, FirstDay));
    }

    [Fact]
    public void CurrentAndFutureDaysAreNotSealedByHistoricalWarmup()
    {
        using var environment = new TestEnvironment();
        var now = DateTimeOffset.UtcNow.ToOffset(Beijing);
        var today = new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, Beijing);
        var completed = new List<DateTimeOffset>();

        UsageSourceReaders.Codex.WarmHistoricalDays(new[] { today, today.AddDays(1) }, dayCompleted: completed.Add);

        Assert.Empty(completed);
        Assert.False(environment.Cache.TryGetRecord(DateOnly.FromDateTime(today.DateTime), out _));
        Assert.False(environment.Cache.TryGetRecord(DateOnly.FromDateTime(today.AddDays(1).DateTime), out _));
    }

    private static TokenUsageEvent Event(DateTimeOffset day, string key, long input) =>
        new(day.AddHours(9), input, 40, 10, 0, input + 10, $"codex:{key}", 5);

    private static void AssertDay(UsageCacheStore cache, DateTimeOffset day, int events, long totalTokens)
    {
        Assert.True(cache.TryGetRecord(DateOnly.FromDateTime(day.DateTime), out var record));
        Assert.True(record.IsComplete);
        Assert.Equal(events, record.Events);
        Assert.Equal(events, record.DetailEventCount);
        Assert.Equal(totalTokens, record.TotalTokens);
        Assert.Equal(day.AddDays(1).AddTicks(-1), record.ScannedThroughLocal);
    }

    private sealed class TestEnvironment : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), $"CodexHistoricalBatchTests-{Guid.NewGuid():N}");
        private readonly IDisposable cacheScope;
        private readonly IDisposable logScope;

        public TestEnvironment()
        {
            cacheScope = MonitorCachePaths.PushLocalAppDataRoot(root);
            logScope = UsageLogPaths.PushRoot(Path.Combine(root, "logs"));
            Cache = UsageCacheStore.Load("CodexTokenMonitor");
        }

        public UsageCacheStore Cache { get; }

        public string WriteLog(string logFolder, string name, params TokenUsageEvent[] events)
        {
            var folder = Path.Combine(root, "logs", "Codex", logFolder);
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, name);
            File.WriteAllLines(path, events.Select(item => JsonSerializer.Serialize(new
            {
                timestamp = item.Timestamp,
                type = "event_msg",
                payload = new
                {
                    type = "token_count",
                    turn_id = item.Key!["codex:".Length..],
                    info = new
                    {
                        last_token_usage = new
                        {
                            input_tokens = item.InputTokens,
                            cached_input_tokens = item.CachedInputTokens,
                            cache_write_input_tokens = item.CacheWriteInputTokens,
                            output_tokens = item.OutputTokens,
                            reasoning_output_tokens = item.ReasoningOutputTokens,
                            total_tokens = item.TotalTokens
                        }
                    }
                }
            })));
            return path;
        }

        public void Dispose()
        {
            UsageCacheStore.Delete("CodexTokenMonitor");
            logScope.Dispose();
            cacheScope.Dispose();
            var fullPath = Path.GetFullPath(root);
            var expectedPrefix = Path.Combine(Path.GetTempPath(), "CodexHistoricalBatchTests-");
            if (!fullPath.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Unexpected test cleanup path.");
            }
            if (Directory.Exists(fullPath))
            {
                Directory.Delete(fullPath, recursive: true);
            }
        }
    }
}
