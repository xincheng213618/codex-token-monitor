using CodexTokenMonitor;
using Xunit;

namespace CodexTokenMonitor.Core.Tests;

public sealed class AgentSpeedLiveSnapshotTests
{
    private static readonly DateTimeOffset Day = new(2026, 10, 26, 0, 0, 0, TimeSpan.FromHours(8));
    private static DateTimeOffset At(double seconds) => Day.AddSeconds(seconds);

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void FooterAndDetailedSnapshotsAgreeAcrossEstimatesFinalizationAndExpiry(bool total, bool filter)
    {
        var engine = new AgentSpeedEngine();
        engine.Add(new("prior", "a", "m", At(4), At(8), 400, 800));
        engine.Add(new("recent", "a", "m", At(8), At(11), 300, 900));
        engine.Add(new("partial", "p", "m", At(11), At(12), 2, 100, Final: false));
        engine.Update(new("a", "parent", "m", false, AgentSpeedPhase.Generating, At(13), At(11)));
        engine.Update(new("p", "child", "m", true, AgentSpeedPhase.Generating, At(13), At(12), ParentSessionId: "parent"));
        engine.Add(new("claude", "c", "m", At(9), At(13), 200, 2000, UsageSource.ClaudeCode));
        var metric = total ? AgentSpeedMetric.TotalThroughput : AgentSpeedMetric.Output;
        UsageSource? source = filter ? UsageSource.Codex : null;
        Compare(At(13), expectDetails: true);
        engine.Add(new("partial", "p", "m", At(11), At(14), 150, 500));
        engine.Update(new("p", "child", "m", true, AgentSpeedPhase.Idle, At(14), null, ParentSessionId: "parent"));
        Compare(At(14), expectDetails: true);
        Compare(At(400), expectDetails: true);
        Compare(Day.AddDays(1).AddHours(1), expectDetails: false);

        void Compare(DateTimeOffset now, bool expectDetails)
        {
            var full = engine.Snapshot(now, now >= Day.AddDays(1) ? Day.AddDays(1) : Day, metric, source);
            var live = engine.Snapshot(now, full.DayStart, metric, source, AgentSpeedSnapshotOptions.Live);
            Assert.Equal(full.Live, live.Live, 8);
            Assert.Equal(full.EstimatedShare, live.EstimatedShare, 8);
            Assert.Equal(full.Requests, live.Requests);
            Assert.Equal(full.MeasuredRequests, live.MeasuredRequests);
            Assert.Equal(full.UnfinalizedRequests, live.UnfinalizedRequests);
            Assert.Equal(metric, live.Metric);
            Assert.False(live.HasDetails);
            Assert.Null(live.Last); Assert.Null(live.Average); Assert.Null(live.MeasuredPeak);
            Assert.Empty(live.Models); Assert.Empty(live.Agents); Assert.Empty(live.History);
            Assert.True(full.HasDetails);
            Assert.Equal(300, full.History.Count);
            Assert.Equal(expectDetails, full.Models.Count > 0);
        }
    }

    [Fact]
    public void BackgroundPeakPersistenceDoesNotRequireChartOrSessionSnapshots()
    {
        var engine = new AgentSpeedEngine();
        engine.Add(new("r", "a", "m", At(1), At(4), 300, 600));
        var full = engine.Snapshot(At(4), Day);
        var peak = engine.Snapshot(At(4), Day, options: AgentSpeedSnapshotOptions.Peak);
        Assert.Equal(100, peak.Live);
        Assert.Equal(full.MeasuredPeak, peak.MeasuredPeak);
        Assert.False(peak.HasDetails);
        Assert.Empty(peak.History); Assert.Empty(peak.Agents); Assert.Empty(peak.Models);
        engine.Add(new("r2", "b", "m", At(1), At(4), 300, 0));
        Assert.Equal(200, engine.Snapshot(At(4), Day, options: AgentSpeedSnapshotOptions.Peak).MeasuredPeak);
        Assert.Equal(200, engine.Snapshot(At(4), Day).MeasuredPeak);
    }
}
