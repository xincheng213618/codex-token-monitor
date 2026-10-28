using System.Text.Json;
using Xunit;

namespace CodexTokenMonitor.Tests;

public sealed class CodexCurrentCycleUsageTests
{
    private static readonly TimeSpan Beijing = TimeSpan.FromHours(8);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CurrentDaySliceClipsBothBoundsWhenCacheAlreadyCoversTheRange(bool includeLiveToday)
    {
        using var environment = new TestEnvironment();
        var day = environment.Day;
        var start = environment.At(2);
        var end = environment.At(4);
        var events = new[]
        {
            Event(environment.At(1), "before", 1000),
            Event(start, "at-start", 200),
            Event(environment.At(3), "inside", 300),
            Event(end, "at-end", 4000),
            Event(environment.At(5), "after", 5000)
        };
        environment.Seed(day, events, environment.Now);

        var reader = new CodexUsageReader();
        for (var repeat = 0; repeat < 2; repeat++)
        {
            var actual = reader.ReadRange(start.ToUniversalTime(), end.ToUniversalTime(), includeLiveToday);
            AssertMatches(actual, events[1..3]);
            Assert.Equal(start, actual.StartLocal);
            Assert.Equal(end, actual.EndLocal);
        }
        Assert.Equal(5, environment.Cache.GetDetailEvents(DateOnly.FromDateTime(day.DateTime)).Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LiveRefreshClipsMergedDayAndKeepsImportedEvents(bool startsYesterday)
    {
        using var environment = new TestEnvironment();
        var day = environment.Day;
        var start = environment.At(2);
        var end = environment.At(4);
        var before = Event(environment.At(1), "before", 1000);
        var imported = Event(start, "imported", 200);
        var after = Event(environment.At(5), "after", 5000);
        environment.Seed(day, new[] { before, imported, after }, start);
        var live = Event(environment.At(3), "live", 300) with { Key = "codex:live" };
        environment.WriteLog(live);
        var expected = new List<TokenUsageEvent> { imported, live };
        if (startsYesterday)
        {
            start = start.AddDays(-1);
            var yesterday = Event(start, "yesterday", 400);
            environment.Seed(day.AddDays(-1), new[]
            {
                Event(start.AddTicks(-1), "before-yesterday", 6000), yesterday
            }, day.AddTicks(-1), isComplete: true);
            expected.Add(yesterday);
            expected.Add(before);
        }

        var reader = new CodexUsageReader();
        for (var repeat = 0; repeat < 2; repeat++)
        {
            var actual = reader.ReadRange(start, end, includeLiveToday: true);
            AssertMatches(actual, expected);
            var cached = reader.ReadCachedRange(start, end);
            AssertMatches(cached, expected);
        }
        Assert.Equal(4, environment.Cache.GetDetailEvents(DateOnly.FromDateTime(day.DateTime)).Count);
    }

    private static void AssertMatches(TokenUsageSummary actual, IEnumerable<TokenUsageEvent> events)
    {
        var expected = new TokenUsageBucket();
        foreach (var item in events) expected.Add(item);
        Assert.Equal(expected.Events, actual.Events);
        Assert.Equal(expected.TotalTokens, actual.TotalTokens);
        Assert.Equal(expected.InputTokens, actual.InputTokens);
        Assert.Equal(expected.CachedInputTokens, actual.CachedInputTokens);
        Assert.Equal(expected.OutputTokens, actual.OutputTokens);
        Assert.Equal(expected.ReasoningOutputTokens, actual.ReasoningOutputTokens);
        Assert.Equal(expected.Events, actual.ModelUsage["gpt-6.1-sol"].ServiceTierUsage["priority"].Events);
        Assert.Equal(actual.TotalTokens, actual.DailyBuckets.Sum(bucket => bucket.TotalTokens));
        Assert.Equal(actual.Events, actual.DailyBuckets.Sum(bucket => bucket.Events));
    }

    private static TokenUsageEvent Event(DateTimeOffset timestamp, string key, long input) => new(
        timestamp, input, input / 2, 10, 2, input + 10, key,
        ModelId: "gpt-6.1-sol", ServiceTier: "priority");

    private sealed class TestEnvironment : IDisposable
    {
        private readonly IDisposable cacheScope;
        private readonly IDisposable logScope;
        private readonly string root = Path.Combine(Path.GetTempPath(), "CodexCurrentCycleTests-" + Guid.NewGuid().ToString("N"));
        public DateTimeOffset Now { get; } = DateTimeOffset.UtcNow.ToOffset(Beijing);
        public DateTimeOffset Day { get; }
        public UsageCacheStore Cache { get; }

        public TestEnvironment()
        {
            Day = new DateTimeOffset(Now.Year, Now.Month, Now.Day, 0, 0, 0, Beijing);
            cacheScope = MonitorCachePaths.PushLocalAppDataRoot(root);
            logScope = UsageLogPaths.PushRoot(Path.Combine(root, "logs"));
            Cache = UsageCacheStore.Load("CodexTokenMonitor");
        }

        public DateTimeOffset At(int sixth) => Day.AddTicks((Now - Day).Ticks / 6 * sixth);

        public void Seed(DateTimeOffset day, IReadOnlyList<TokenUsageEvent> events,
            DateTimeOffset scannedThrough, bool isComplete = false)
        {
            var bucket = new TokenUsageBucket { StartLocal = day };
            foreach (var item in events) bucket.Add(item);
            Cache.Put(bucket, isComplete, scannedThrough, events);
        }

        public void WriteLog(TokenUsageEvent item)
        {
            var directory = Path.Combine(root, "logs", "Codex", "sessions");
            Directory.CreateDirectory(directory);
            var context = JsonSerializer.Serialize(new
            {
                type = "turn_context", payload = new { model = item.ModelId, service_tier = item.ServiceTier }
            });
            var line = JsonSerializer.Serialize(new
            {
                timestamp = item.Timestamp, type = "event_msg", payload = new
                {
                    type = "token_count", turn_id = "live", info = new { last_token_usage = new
                    {
                        input_tokens = item.InputTokens, cached_input_tokens = item.CachedInputTokens,
                        output_tokens = item.OutputTokens, reasoning_output_tokens = item.ReasoningOutputTokens,
                        total_tokens = item.TotalTokens
                    } }
                }
            });
            File.WriteAllLines(Path.Combine(directory, "cycle.jsonl"), new[] { context, line });
        }

        public void Dispose()
        {
            UsageCacheStore.Delete("CodexTokenMonitor");
            logScope.Dispose();
            cacheScope.Dispose();
        }
    }
}
