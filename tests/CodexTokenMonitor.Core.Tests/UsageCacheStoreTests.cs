using Xunit;

namespace CodexTokenMonitor.Tests;

public sealed class UsageCacheStoreTests
{
    [Fact]
    public void Load_ReusesInitializedStoreAndRangeQueryFiltersEvents()
    {
        var folder = $"CodexTokenMonitorTests-{Guid.NewGuid():N}";
        try
        {
            var first = UsageCacheStore.Load(folder);
            var second = UsageCacheStore.Load(folder);
            var dayStart = new DateTimeOffset(2026, 7, 13, 0, 0, 0, TimeSpan.FromHours(8));
            var early = new TokenUsageEvent(dayStart.AddHours(9), 100, 80, 10, 2, 110, "early");
            var late = new TokenUsageEvent(dayStart.AddHours(11), 200, 160, 20, 4, 220, "late");
            var bucket = new TokenUsageBucket { StartLocal = dayStart };
            bucket.Add(early.Timestamp, early.InputTokens, early.CachedInputTokens, early.OutputTokens, early.ReasoningOutputTokens, early.TotalTokens);
            bucket.Add(late.Timestamp, late.InputTokens, late.CachedInputTokens, late.OutputTokens, late.ReasoningOutputTokens, late.TotalTokens);

            first.Put(bucket, detailEvents: new[] { early, late });
            var rows = second.ReadDetailRows(dayStart.AddHours(8), dayStart.AddHours(10));
            var summary = second.ReadRange(dayStart, dayStart.AddDays(1));

            Assert.Same(first, second);
            var row = Assert.Single(rows);
            Assert.Equal(early.Timestamp, row.StartLocal);
            Assert.Equal(100, row.InputTokens);
            Assert.Equal(300, summary.InputTokens);
            Assert.Equal(2, summary.Events);
        }
        finally
        {
            UsageCacheStore.Delete(folder);
        }
    }
}
