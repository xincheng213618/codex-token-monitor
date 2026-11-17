using Xunit;

namespace CodexTokenMonitor.Core.Tests;

public sealed class ClaudeQuotaCycleHistoryTests
{
    private static readonly DateTimeOffset Reset = new(2026, 11, 17, 18, 0, 0, TimeSpan.FromHours(8));

    [Fact]
    public void AssignsBoundaryToNewWeekAndKeepsOfficialSamplesSeparateFromWholeCycleUsage()
    {
        using var isolated = MonitorCachePaths.PushLocalAppDataRoot(
            Path.Combine(Path.GetTempPath(), $"ClaudeCycleHistory-{Guid.NewGuid():N}"));
        var start = Reset.AddDays(-7);
        var now = start.AddHours(8);
        var sample = new ClaudeQuotaReading(ClaudeQuotaWindowKind.Week, start.AddHours(-7), 51,
            start.AddDays(-7), start, 150m, 390_000_000, 100, ClaudeQuotaReadingSource.Official);

        TokenUsageBucket Row(DateTimeOffset stamp, long input, string model)
        {
            var usage = new TokenUsageBucket { StartLocal = stamp };
            usage.Add(new TokenUsageEvent(stamp, input, 0, 0, 0, input, stamp.ToString("O"), ModelId: model));
            return usage;
        }

        var cycles = ClaudeQuotaCycleHistory.Build(now,
            [new(Reset.AddSeconds(-1), now.AddMinutes(-1), 4, 5)], [sample],
            [Row(start.AddSeconds(-1), 2_000_000, "claude-opus-5-5"),
             Row(start, 1_000_000, "claude-fable-5-2"),
             Row(start.AddMinutes(2), 1_000_000, "mimo-v2.5-pro"),
             Row(start.AddDays(-8), 500_000, "claude-opus-5-5")],
            complete: true);

        Assert.Collection(cycles,
            current =>
            {
                Assert.True(current.IsCurrent);
                Assert.Equal(Reset, current.Reset);
                Assert.Equal(1_000_000, current.Tokens);
                Assert.Equal(4, current.Official?.UsedPercent);
                Assert.Equal(5, current.Official?.FableUsedPercent);
            },
            previous =>
            {
                Assert.Equal(start, previous.Reset);
                Assert.Equal(2_000_000, previous.Tokens);
                Assert.True(previous.OfficialIsSample);
                Assert.Equal(51, previous.Official?.UsedPercent);
                Assert.Equal(sample.CapacityUsd, previous.FullReferenceUsd);
            },
            logsOnly =>
            {
                Assert.Equal(start.AddDays(-7), logsOnly.Reset);
                Assert.Equal(500_000, logsOnly.Tokens);
                Assert.Null(logsOnly.Official);
            });
    }
}
