using Microsoft.Data.Sqlite;
using Xunit;

namespace CodexTokenMonitor.Tests;

public sealed class DayTimeZoneTests
{
    private static readonly TimeSpan Beijing = TimeSpan.FromHours(8);

    [Theory]
    [InlineData(-720)]
    [InlineData(0)]
    [InlineData(330)]
    [InlineData(345)]
    [InlineData(480)]
    [InlineData(840)]
    public void DayBoundariesUseConfiguredOffsetWhileOtherModesKeepTheirBoundaries(int minutes)
    {
        var now = new DateTimeOffset(2026, 10, 17, 0, 30, 0, Beijing);
        var offset = TimeSpan.FromMinutes(minutes);
        var localNow = now.ToOffset(offset);
        var range = UsageRangePolicy.ResolveSelectedRange(RangeMode.Day, localNow, null, null, now, offset);
        Assert.Equal(localNow.Date, range.Start.Date);
        Assert.Equal(offset, range.Start.Offset);
        Assert.Equal(TimeSpan.Zero, range.Start.TimeOfDay);
        Assert.Equal(now, range.End);
        Assert.True(range.FollowsCurrent);
        foreach (var mode in new[] { RangeMode.Week, RangeMode.Month, RangeMode.Cycle })
        {
            Assert.Equal(
                UsageRangePolicy.ResolveSelectedRange(mode, now, null, null, now),
                UsageRangePolicy.ResolveSelectedRange(mode, now, null, null, now, offset));
        }
        Assert.Equal(
            UsageRangePolicy.ResolveSelectedRange(RangeMode.Day, now, now.AddHours(-1), null, now),
            UsageRangePolicy.ResolveSelectedRange(RangeMode.Day, now, now.AddHours(-1), null, now, offset));
    }

    [Fact]
    public void SettingsDefaultToEightAndRoundTripWithoutChangingTheMachineZone()
    {
        var root = Path.Combine(Path.GetTempPath(), "token-timezone-test-" + Guid.NewGuid().ToString("N"));
        using var scope = MonitorCachePaths.PushLocalAppDataRoot(root);
        Assert.Equal(Beijing, DayTimeZoneSettings.Load().Offset);
        new DayTimeZoneSettings { OffsetMinutes = 345 }.Save();
        Assert.Equal(345, DayTimeZoneSettings.Load().OffsetMinutes);
        Assert.Equal("UTC+5:45", DayTimeZoneSettings.Format(DayTimeZoneSettings.Load().Offset));
        Assert.Equal("UTC", DayTimeZoneSettings.Format(TimeSpan.Zero));
        Assert.Equal("UTC-3:30", DayTimeZoneSettings.Format(TimeSpan.FromMinutes(-210)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DayTimeZoneSettings { OffsetMinutes = 841 }.Save());
        var path = Path.Combine(root, "CodexTokenMonitor", "day-time-zone-v1.json");
        File.WriteAllText(path, "{\"OffsetMinutes\":900}");
        Assert.Equal(Beijing, DayTimeZoneSettings.Load().Offset);
        File.WriteAllText(path, "broken");
        Assert.Equal(Beijing, DayTimeZoneSettings.Load().Offset);
    }

    [Theory]
    [InlineData(-720)]
    [InlineData(0)]
    [InlineData(330)]
    [InlineData(345)]
    [InlineData(480)]
    [InlineData(840)]
    public void HourlyTotalsMatchExactEventsAcrossCalendarBoundaries(int minutes)
    {
        var folder = "HourlyTests-" + Guid.NewGuid().ToString("N");
        var day = new DateTimeOffset(2026, 12, 31, 0, 0, 0, Beijing);
        try
        {
            var cache = UsageCacheStore.Load(folder);
            var events = Enumerable.Range(0, 96 * 4).Select(index => Event(day.AddMinutes(index * 15), "event-" + index)).ToList();
            foreach (var group in events.GroupBy(item => item.Timestamp.Date))
            {
                var bucket = new TokenUsageBucket { StartLocal = new DateTimeOffset(group.Key, Beijing) };
                foreach (var item in group) bucket.Add(item);
                cache.Put(bucket, detailEvents: group.ToList());
            }
            var start = new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.FromMinutes(minutes));
            // A partial current hour exercises exact clipping after the hourly sum.
            foreach (var end in new[] { start.AddDays(1), start.AddHours(13).AddMinutes(17) })
            {
                var expected = new TokenUsageBucket();
                foreach (var item in events.Where(item => item.Timestamp >= start && item.Timestamp < end)) expected.Add(item);
                for (var repeat = 0; repeat < 2; repeat++)
                {
                    var actual = cache.ReadRange(start, end);
                    Assert.Equal(expected.Events, actual.Events);
                    Assert.Equal(expected.TotalTokens, actual.TotalTokens);
                    Assert.Equal(expected.InputTokens, actual.InputTokens);
                    Assert.Equal(expected.CachedInputTokens, actual.CachedInputTokens);
                    Assert.Equal(expected.CacheWriteInputTokens, actual.CacheWriteInputTokens);
                    Assert.Equal(expected.UncachedInputTokens, actual.UncachedInputTokens);
                    Assert.Equal(expected.ReasoningOutputTokens, actual.ReasoningOutputTokens);
                    Assert.Equal(expected.LongContextInputTokens, actual.LongContextInputTokens);
                    Assert.Equal(expected.PeakInputTokens, actual.PeakInputTokens);
                    Assert.Equal(expected.LastTokenEventLocal, actual.LastTokenEventLocal);
                    foreach (var model in expected.ModelUsage.Keys)
                    {
                        Assert.Equal(expected.ModelUsage[model].TotalTokens, actual.ModelUsage[model].TotalTokens);
                        Assert.Equal(expected.ModelUsage[model].ServiceTierUsage.Keys, actual.ModelUsage[model].ServiceTierUsage.Keys);
                    }
                }
            }
            using var connection = new SqliteConnection("Data Source=" + UsageCacheStore.GetCachePath(folder));
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM usage_hours";
            Assert.True((long)command.ExecuteScalar()! > 0);
        }
        finally { UsageCacheStore.Delete(folder); }
    }

    [Fact]
    public void HourlyCacheTracksImportsReplacementAndDeletion()
    {
        var folder = "HourlyInvalidationTests-" + Guid.NewGuid().ToString("N");
        var day = new DateTimeOffset(2026, 10, 16, 0, 0, 0, Beijing);
        try
        {
            var cache = UsageCacheStore.Load(folder);
            var original = Event(day.AddHours(10), "original");
            var bucket = new TokenUsageBucket { StartLocal = day };
            bucket.Add(original);
            cache.Put(bucket, detailEvents: new[] { original });
            Assert.Equal(original.TotalTokens, cache.ReadRange(day.AddHours(8), day.AddHours(15)).TotalTokens);
            // INSERT OR REPLACE may move an existing event to another hour.
            var moved = original with { Timestamp = day.AddHours(12), TotalTokens = 222, InputTokens = 200,
                CachedInputTokens = 100, CacheWriteInputTokens = 0, OutputTokens = 22, ReasoningOutputTokens = 10 };
            cache.Put(bucket, detailEvents: new[] { moved }, replaceDetailEvents: false);
            Assert.Equal(222, cache.ReadRange(day.AddHours(8), day.AddHours(15)).TotalTokens);
            Assert.Equal(0, cache.ReadRange(day.AddHours(10), day.AddHours(11)).Events);
            var imported = Event(day.AddHours(9), "imported");
            cache.MergeImportedDetailEvents(new[] { imported });
            Assert.Equal(222 + imported.TotalTokens, cache.ReadRange(day.AddHours(8), day.AddHours(15)).TotalTokens);
            cache.DeleteDay(DateOnly.FromDateTime(day.DateTime));
            Assert.Equal(imported.TotalTokens, cache.ReadRange(day.AddHours(8), day.AddHours(15)).TotalTokens);
        }
        finally { UsageCacheStore.Delete(folder); }
    }

    private static TokenUsageEvent Event(DateTimeOffset timestamp, string key) => new(
        timestamp, 300_000, 200_000, 100, 30, 300_100, key, 10_000,
        timestamp.Hour % 2 == 0 ? "gpt-6.1-sol" : "gpt-6-sol", timestamp.Minute == 0 ? "fast" : "standard");

    [Fact]
    public void EventAtLastTickOfAnHourInvalidatesThatHourWithoutRoundingUp()
    {
        var folder = "HourlyTickTests-" + Guid.NewGuid().ToString("N");
        var day = new DateTimeOffset(2026, 10, 16, 0, 0, 0, Beijing);
        try
        {
            var cache = UsageCacheStore.Load(folder);
            Assert.Equal(0, cache.ReadRange(day.AddHours(8), day.AddHours(10)).Events);
            var item = Event(day.AddHours(9).AddTicks(-1), "last-tick");
            cache.MergeImportedDetailEvents(new[] { item });
            Assert.Equal(item.TotalTokens, cache.ReadRange(day.AddHours(8), day.AddHours(9)).TotalTokens);
            Assert.Equal(0, cache.ReadRange(day.AddHours(9), day.AddHours(10)).Events);
        }
        finally { UsageCacheStore.Delete(folder); }
    }
}
