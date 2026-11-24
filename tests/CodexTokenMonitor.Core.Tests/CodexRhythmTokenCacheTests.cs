using Xunit;

namespace CodexTokenMonitor.Tests;

public sealed class CodexRhythmTokenCacheTests
{
    [Fact]
    public void ReportingDayRequiresBothCacheDaysWhileTodayUsesTheRecordedSlice()
    {
        var folder = "RhythmTokenCacheTests-" + Guid.NewGuid().ToString("N");
        using var scope = MonitorCachePaths.PushLocalAppDataRoot(
            Path.Combine(Path.GetTempPath(), folder));
        var offset = TimeSpan.FromMinutes(345);
        var day = new DateTimeOffset(2026, 11, 9, 0, 0, 0, offset);
        var next = day.AddDays(1);
        var now = next.AddHours(1);
        var history = new CodexMessageHistory(
            [day.AddSeconds(-1), day.AddHours(8), next.AddSeconds(1)], [],
            now, "test", 0);
        var rhythm = CodexUsageRhythm.Calculate(history, offset);
        var date = DateOnly.FromDateTime(day.DateTime);
        var cache = UsageCacheStore.Load(folder);
        var firstDay = new[]
        {
            Event(day.AddTicks(-1), 100), Event(day, 10), Event(day.AddHours(16), 20)
        };
        var secondDay = new[]
        {
            Event(next.AddTicks(-1), 30), Event(next, 40), Event(now.AddTicks(-1), 50), Event(now, 60)
        };

        Save(cache, firstDay, complete: true);
        Save(cache, secondDay, complete: true);
        var recorded = cache.ReadRhythmTokens(rhythm, offset, CancellationToken.None);
        Assert.Equal(60, recorded.DailyTotals[date]);
        Assert.Equal(new long[] { 10, 20, 30 }, recorded.Events.Select(item => item.TotalTokens));
        Assert.Equal(90, cache.ReadRhythmTokenTotal(next, now, CancellationToken.None));

        Save(cache, secondDay, complete: false);
        var incomplete = cache.ReadRhythmTokens(rhythm, offset, CancellationToken.None);
        Assert.DoesNotContain(date, incomplete.DailyTotals.Keys);
        Assert.Empty(incomplete.Events);
        Assert.Equal(90, cache.ReadRhythmTokenTotal(next, now, CancellationToken.None));
    }

    private static TokenUsageEvent Event(DateTimeOffset timestamp, long total) =>
        new(timestamp.ToOffset(CodexUsageReader.BeijingOffset), total, 0, 0, 0, total);

    private static void Save(UsageCacheStore cache, TokenUsageEvent[] events, bool complete)
    {
        var start = new DateTimeOffset(events[0].Timestamp.Date, CodexUsageReader.BeijingOffset);
        var bucket = new TokenUsageBucket { StartLocal = start };
        foreach (var item in events) bucket.Add(item);
        cache.Put(bucket, isComplete: complete, detailEvents: events, propagateErrors: true);
    }
}
