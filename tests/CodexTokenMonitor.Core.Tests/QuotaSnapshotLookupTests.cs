using System.Diagnostics;
using Xunit;

namespace CodexTokenMonitor.Tests;

public sealed class QuotaSnapshotLookupTests
{
    private static readonly TimeSpan Beijing = TimeSpan.FromHours(8);

    [Fact]
    public void Select_ReturnsExactEventSnapshot()
    {
        var start = new DateTimeOffset(2026, 7, 11, 0, 0, 0, Beijing);
        var exact = Snapshot(start.AddMinutes(5), 20m, 40m);
        var lookup = new QuotaSnapshotLookup(new[] { exact });

        var selected = lookup.Select(DayRange(start), Bucket(start.AddMinutes(5)), eventBreakdown: true);

        Assert.Same(exact, selected);
    }

    [Fact]
    public void Select_InterpolatesMissingEventAnchor()
    {
        var start = new DateTimeOffset(2026, 7, 11, 0, 0, 0, Beijing);
        var reset = start.AddHours(5);
        var lookup = new QuotaSnapshotLookup(new[]
        {
            Snapshot(start, 10m, 30m, reset),
            Snapshot(start.AddMinutes(10), 20m, 40m, reset)
        });

        var selected = lookup.Select(DayRange(start), Bucket(start.AddMinutes(5)), eventBreakdown: true);

        Assert.NotNull(selected);
        Assert.Equal(15m, selected!.FiveHourUsedPercent);
        Assert.Equal(35m, selected.WeekUsedPercent);
    }

    [Fact]
    public void Select_UsesLastSnapshotInsideDailyBucket()
    {
        var start = new DateTimeOffset(2026, 7, 11, 0, 0, 0, Beijing);
        var evening = Snapshot(start.AddHours(23), 75m, 80m);
        var lookup = new QuotaSnapshotLookup(new[]
        {
            Snapshot(start.AddHours(1), 10m, 20m),
            evening,
            Snapshot(start.AddDays(1), 1m, 2m)
        });

        var selected = lookup.Select(DayRange(start), Bucket(start), eventBreakdown: false);

        Assert.Same(evening, selected);
    }

    [Fact]
    public void Select_HandlesLargeDayWithoutQuadraticRescans()
    {
        var start = new DateTimeOffset(2026, 7, 11, 0, 0, 0, Beijing);
        var snapshots = Enumerable.Range(0, 12_000)
            .Select(index => Snapshot(start.AddSeconds(index * 7), index % 100, index % 100))
            .ToArray();
        var lookup = new QuotaSnapshotLookup(snapshots);
        var range = DayRange(start);
        var stopwatch = Stopwatch.StartNew();

        for (var index = 0; index < 12_000; index++)
        {
            _ = lookup.Select(range, Bucket(start.AddSeconds(index * 7)), eventBreakdown: true);
        }

        stopwatch.Stop();
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1), $"Lookup took {stopwatch.Elapsed}.");
    }

    [Fact]
    public void QuotaWindowClassification_RejectsThirtyDayResetCardWindow()
    {
        Assert.True(CodexUsageReader.IsFiveHourWindow(300));
        Assert.True(CodexUsageReader.IsWeeklyWindow(10_080));
        Assert.False(CodexUsageReader.IsWeeklyWindow(43_200));
    }

    [Fact]
    public void NormalizeQuotaSnapshotWindows_DropsPersistedThirtyDayWindow()
    {
        var snapshotTime = new DateTimeOffset(2026, 7, 2, 14, 13, 0, Beijing);
        var snapshot = new CodexQuotaSnapshot(
            snapshotTime,
            "codex",
            null,
            null,
            null,
            15m,
            snapshotTime.AddDays(30));

        var normalized = CodexUsageReader.NormalizeQuotaSnapshotWindows(snapshot);

        Assert.Null(normalized.WeekUsedPercent);
        Assert.Null(normalized.WeekResetAtLocal);
    }

    private static SelectedRange DayRange(DateTimeOffset start)
    {
        return new SelectedRange(start, start.AddDays(1), "", "", RangeMode.Day);
    }

    private static TokenUsageBucket Bucket(DateTimeOffset time)
    {
        return new TokenUsageBucket { StartLocal = time };
    }

    private static CodexQuotaSnapshot Snapshot(
        DateTimeOffset time,
        decimal fiveHour,
        decimal week,
        DateTimeOffset? reset = null)
    {
        return new CodexQuotaSnapshot(time, "codex", "Codex", fiveHour, reset, week, reset);
    }
}
