using Xunit;

namespace CodexTokenMonitor.Tests;

public sealed class QuotaConsumptionTimelineCalculatorTests
{
    private static readonly DateTimeOffset Start = new(2020, 1, 2, 8, 0, 0, TimeSpan.FromHours(8));

    [Fact]
    public void BuildBandsStopsAtFirstQuotaArrivalInsteadOfTheCostAllocationTail()
    {
        var original = Band(0, 0m, 5m, Start, Start.AddHours(10));
        var analysis = Analysis(
            new[] { Point(0, 0m), Point(120, 5m), Point(600, 5m) },
            original);

        var projected = Assert.Single(QuotaConsumptionTimelineCalculator.BuildBands(analysis));

        Assert.Equal(original with { EndLocal = Start.AddHours(2) }, projected);
        Assert.NotSame(original, projected);
        Assert.Same(original, Assert.Single(analysis.Bands));
        Assert.Equal(Start.AddHours(10), original.EndLocal);
        Assert.Same(original.Models, projected.Models);
    }

    [Fact]
    public void BuildBandsInterpolatesPartialBoundariesAndSkipsUnobservedCoverage()
    {
        var beforeCoverage = Band(0, 0m, 5m);
        var partialFirst = Band(1, 2m, 5m);
        var partialLast = Band(2, 5m, 7m);
        var afterCoverage = Band(3, 7m, 10m);
        var analysis = Analysis(
            new[] { Point(0, 2m), Point(50, 7m) },
            beforeCoverage, partialFirst, partialLast, afterCoverage);

        var projected = QuotaConsumptionTimelineCalculator.BuildBands(analysis);

        Assert.Equal(new[] { 1, 2 }, projected.Select(band => band.BandIndex));
        Assert.Equal(partialFirst with { StartLocal = Start, EndLocal = Start.AddMinutes(30) }, projected[0]);
        Assert.Equal(partialLast with { StartLocal = Start.AddMinutes(30), EndLocal = Start.AddMinutes(50) }, projected[1]);
        Assert.Equal(4, analysis.Bands.Count);
    }

    [Fact]
    public void BuildBandsSkipsZeroDurationDropsWithoutLosingLaterMeasurableBands()
    {
        var analysis = Analysis(
            new[] { Point(0, 0m), Point(0, 5m), Point(10, 10m) },
            Band(0, 0m, 5m), Band(1, 5m, 10m));

        var projected = Assert.Single(QuotaConsumptionTimelineCalculator.BuildBands(analysis));

        Assert.Equal(1, projected.BandIndex);
        Assert.Equal(Start, projected.StartLocal);
        Assert.Equal(Start.AddMinutes(10), projected.EndLocal);
    }

    [Fact]
    public void BuildBandsMeasuresFromTheFirstArrivalOnAStartingPlateau()
    {
        var analysis = Analysis(
            new[] { Point(0, 0m), Point(10, 5m), Point(20, 5m), Point(30, 10m) },
            Band(1, 5m, 10m));

        var projected = Assert.Single(QuotaConsumptionTimelineCalculator.BuildBands(analysis));

        Assert.Equal(Start.AddMinutes(10), projected.StartLocal);
        Assert.Equal(Start.AddMinutes(30), projected.EndLocal);
        Assert.Equal(TimeSpan.FromMinutes(20), projected.EndLocal - projected.StartLocal);
    }

    [Fact]
    public void BuildBandsDoesNotFabricateIntervalsForMissingOrSinglePointTimelines()
    {
        var empty = Analysis(Array.Empty<QuotaCycleTimelinePoint>(), Band(0, 0m, 5m));
        var single = Analysis(new[] { Point(10, 5m) }, Band(0, 0m, 5m));
        var noBands = Analysis(new[] { Point(0, 0m), Point(10, 5m) });

        Assert.Empty(QuotaConsumptionTimelineCalculator.BuildBands(empty));
        Assert.Empty(QuotaConsumptionTimelineCalculator.BuildBands(single));
        Assert.Empty(QuotaConsumptionTimelineCalculator.BuildBands(noBands));
    }

    private static QuotaCycleTimelinePoint Point(int minute, decimal used) => new(Start.AddMinutes(minute), used);

    private static QuotaCycleAnalysisBand Band(
        int index,
        decimal from,
        decimal to,
        DateTimeOffset? start = null,
        DateTimeOffset? end = null) => new(
            index,
            from,
            to,
            start ?? Start,
            end ?? Start.AddHours(8),
            to - from,
            50,
            20m,
            400m,
            "timeline-model",
            new[] { new QuotaCycleModelShare("timeline-model", to - from, 100m, 50, 20m, 100m, true) });

    private static QuotaCycleAnalysisResult Analysis(
        IReadOnlyList<QuotaCycleTimelinePoint> timeline,
        params QuotaCycleAnalysisBand[] bands) => new(
            new CodexQuotaCycle(Start, Start.AddDays(7), Start.AddDays(7), 10, 20m, false),
            bands,
            Array.Empty<QuotaCycleModelShare>(),
            timeline.Count,
            0m,
            0,
            0m,
            null,
            0m,
            0m,
            0m,
            "timeline-model",
            "")
        {
            Timeline = timeline
        };
}
