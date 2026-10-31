using Xunit;

namespace CodexTokenMonitor.Tests;

public sealed class ClaudeQuotaReferenceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 30, 3, 0, 0, TimeSpan.FromHours(8));

    private static ClaudeQuotaReading Reading(ClaudeQuotaReadingSource source, double percent = 4) => new(
        ClaudeQuotaWindowKind.Week, Now, percent, Now.AddDays(-3), Now.AddDays(4), 12m, 36_000_000, 100, source);

    [Fact]
    public void OneAutomaticReadingIsEnoughForARoughTotal()
    {
        var reference = ClaudeQuotaReferenceCalculator.Estimate(ClaudeQuotaWindowKind.Week, null,
            new[] { Reading(ClaudeQuotaReadingSource.Official) })!;
        Assert.Equal(300m, reference.TotalCostUsd);
        Assert.Equal(900_000_000, reference.TotalTokens);
        Assert.True(reference.IsTentative);
        Assert.False(reference.IsCurrentWindow);
    }

    [Fact]
    public void CurrentWindowUsesOnlyTheUsageAtTheOfficialObservation()
    {
        TokenUsageBucket Usage(long count)
        {
            var bucket = new TokenUsageBucket { StartLocal = Now.AddHours(-2) };
            bucket.Add(new TokenUsageEvent(Now.AddHours(-1), count, 0, 0, 0, count, "sample", ModelId: "claude-opus-5-5"));
            return bucket;
        }
        var window = new ClaudeQuotaWindowUsage(ClaudeQuotaWindowKind.FiveHour, ClaudeWindowSource.Official,
            Now.AddHours(-2), Now.AddHours(3), Usage(10_000_000), 50, true, Now.AddMinutes(-30), Usage(1_000_000));
        var reference = ClaudeQuotaReferenceCalculator.Estimate(ClaudeQuotaWindowKind.FiveHour, window, [])!;
        Assert.Equal(8m, reference.TotalCostUsd);
        Assert.Equal(2_000_000, reference.TotalTokens);
        Assert.True(reference.IsCurrentWindow);
    }

    [Fact]
    public void RemovedManualReadingsCannotPolluteTheReference()
    {
        Assert.Null(ClaudeQuotaReferenceCalculator.Estimate(ClaudeQuotaWindowKind.Week, null,
            new[] { Reading(ClaudeQuotaReadingSource.Manual) }));
        Assert.Null(ClaudeQuotaReferenceCalculator.Estimate(ClaudeQuotaWindowKind.Week, null,
            new[] { Reading(ClaudeQuotaReadingSource.Official, 0) }));
    }

    [Fact]
    public void IncompleteLocalLogsStillProvideAnExplicitlyTentativeReference()
    {
        var usage = new TokenUsageBucket { StartLocal = Now.AddHours(-2) };
        usage.Add(new TokenUsageEvent(Now.AddHours(-1), 1_000_000, 0, 0, 0, 1_000_000, "partial", ModelId: "claude-opus-5-5"));
        var window = new ClaudeQuotaWindowUsage(ClaudeQuotaWindowKind.FiveHour, ClaudeWindowSource.Official,
            Now.AddHours(-2), Now.AddHours(3), usage, 50, false, Now, usage);
        var reference = ClaudeQuotaReferenceCalculator.Estimate(ClaudeQuotaWindowKind.FiveHour, window, [])!;
        Assert.Equal(8m, reference.TotalCostUsd);
        Assert.True(reference.IsTentative);
        Assert.True(reference.IsIncomplete);
    }
}
