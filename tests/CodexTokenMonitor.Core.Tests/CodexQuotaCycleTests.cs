using Xunit;

namespace CodexTokenMonitor.Tests;

public sealed class CodexQuotaCycleTests
{
    private static readonly DateTimeOffset Start = new(2026, 8, 31, 10, 26, 40, TimeSpan.FromHours(8));

    [Fact]
    public void BuildActualWeeklyPeriods_RemovesFourMinuteResetJitterWithoutDelayingCurrentCycle()
    {
        var previousStart = Start.AddDays(-1).AddHours(-5).AddMinutes(-1);
        var previousReset = previousStart.AddDays(7);
        var currentReset = Start.AddDays(7);
        var snapshots = new List<CodexQuotaSnapshot>
        {
            Snapshot(previousStart, 0m, previousReset),
            Snapshot(Start.AddSeconds(1), 77m, previousReset)
        };
        snapshots.AddRange(Enumerable.Range(0, 13).Select(index =>
            Snapshot(Start.AddSeconds(3 + index * 17.5), 0m, currentReset)));
        snapshots.Add(Snapshot(Start.AddMinutes(3).AddSeconds(55), 77m, previousReset));
        snapshots.Add(Snapshot(Start.AddMinutes(3).AddSeconds(58), 0m, currentReset));
        var now = Start.AddDays(2);
        snapshots.Add(Snapshot(now, 19m, currentReset));

        var periods = CodexQuotaCycleReader.BuildActualWeeklyPeriods(
            CodexQuotaCycleReader.RemoveTransientResetOutliers(snapshots), now);

        Assert.Equal(2, periods.Count);
        Assert.Equal(previousStart, periods[0].PeriodStart);
        Assert.Equal(Start, periods[0].PeriodEnd);
        Assert.Equal(77m, periods[0].MaxWeekUsedPercent);
        Assert.False(periods[0].IsCurrent);
        Assert.Equal(Start, periods[1].PeriodStart);
        Assert.Equal(now, periods[1].PeriodEnd);
        Assert.Equal(currentReset, periods[1].ResetAt);
        Assert.Equal(19m, periods[1].MaxWeekUsedPercent);
        Assert.True(periods[1].IsCurrent);
    }

    [Theory]
    [InlineData(4 * 60, false)]
    [InlineData(10 * 60, false)]
    [InlineData(10 * 60 + 1, true)]
    public void BuildActualWeeklyPeriods_FiltersCompletedCyclesByDurationRatherThanSnapshotCount(
        int seconds, bool keepHistorical)
    {
        var nextStart = Start.AddSeconds(seconds);
        var snapshots = Enumerable.Range(0, 40)
            .Select(index => Snapshot(Start.AddSeconds(seconds * index / 40d), 77m, Start.AddDays(7)))
            .Append(Snapshot(nextStart, 0m, nextStart.AddDays(7)))
            .ToList();

        var periods = CodexQuotaCycleReader.BuildActualWeeklyPeriods(snapshots, nextStart.AddDays(1));

        Assert.Equal(keepHistorical ? 2 : 1, periods.Count);
        Assert.Equal(nextStart, periods[^1].PeriodStart);
        Assert.True(periods[^1].IsCurrent);
        if (keepHistorical)
        {
            Assert.Equal(Start, periods[0].PeriodStart);
            Assert.Equal(nextStart, periods[0].PeriodEnd);
            Assert.Equal(40, periods[0].SnapshotCount);
        }
    }

    [Fact]
    public void BuildActualWeeklyPeriods_KeepsCurrentCycleThatJustStarted()
    {
        var now = Start.AddMinutes(2);
        var snapshots = new[] { Snapshot(Start, 0m, Start.AddDays(7)) };

        var period = Assert.Single(CodexQuotaCycleReader.BuildActualWeeklyPeriods(snapshots, now));

        Assert.True(period.IsCurrent);
        Assert.Equal(Start, period.PeriodStart);
        Assert.Equal(now, period.PeriodEnd);
    }

    [Fact]
    public void BuildActualWeeklyPeriods_KeepsSparseWeekObservedOnlyInItsLastTwoMinutes()
    {
        var reset = Start.AddDays(7);
        var snapshots = new[]
        {
            Snapshot(reset.AddMinutes(-2), 90m, reset),
            Snapshot(reset, 0m, reset.AddDays(7))
        };

        var periods = CodexQuotaCycleReader.BuildActualWeeklyPeriods(snapshots, reset.AddMinutes(1));

        Assert.Equal(2, periods.Count);
        Assert.Equal(Start, periods[0].PeriodStart);
        Assert.Equal(reset, periods[0].PeriodEnd);
        Assert.False(periods[0].IsCurrent);
        Assert.True(periods[1].IsCurrent);
    }

    [Fact]
    public void FormatCycleDurationRoundsResetJitterToSevenDays()
    {
        var period = new CodexQuotaCycle(
            Start,
            Start.AddDays(7).AddSeconds(-4),
            Start.AddDays(7),
            1,
            99m,
            false);

        Assert.Equal("7天", QuotaEstimateCalculator.FormatCycleDuration(period, period.PeriodEnd));
    }

    private static CodexQuotaSnapshot Snapshot(DateTimeOffset at, decimal usedPercent, DateTimeOffset reset)
    {
        return new CodexQuotaSnapshot(at, "codex", null, null, null, usedPercent, reset);
    }
}
