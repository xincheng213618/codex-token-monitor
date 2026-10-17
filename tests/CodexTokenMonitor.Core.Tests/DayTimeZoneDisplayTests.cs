using Xunit;

namespace CodexTokenMonitor.Tests;

public sealed partial class LastDisplayStoreTests
{
    [Fact]
    public async Task RestoreAndDisplayKeepTheSelectedDayOffset()
    {
        using var scope = MonitorCachePaths.PushLocalAppDataRoot(Path.Combine(Path.GetTempPath(), "DayTimeZoneDisplay-" + Guid.NewGuid().ToString("N")));
        var start = new DateTimeOffset(2026, 10, 16, 0, 0, 0, TimeSpan.Zero);
        var range = new SelectedRange(start, start.AddDays(1), "2026-10-16", "明细", RangeMode.Day);
        var row = new TokenUsageBucket { StartLocal = start.AddHours(1).ToOffset(TimeSpan.FromHours(8)), Events = 1, TotalTokens = 110 };
        var rows = new[] { row };
        var result = new UsageQueryResult(UsageSummaryBuilder.FromRows(range.Start, range.End, rows), rows,
            TimeSpan.Zero, null, Array.Empty<CodexQuotaSnapshot>());
        LastDisplayStore.Save(UsageSource.Codex, range, result);
        await LastDisplayStore.FlushAsync();
        var restored = Assert.IsType<LastDisplaySnapshot>(LastDisplayStore.Load());
        Assert.Equal(TimeSpan.Zero, restored.Range.Start.Offset);
        Assert.Equal(TimeSpan.Zero, restored.Range.End.Offset);
        var view = new UsageDisplayViewModel();
        view.ShowResult(restored.Source, "Codex", restored.Range, restored.Result);
        Assert.Equal("2026-10-16 00:00 - 2026-10-17 00:00:00  UTC", view.Snapshot.Period);
    }
}
