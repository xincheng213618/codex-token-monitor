namespace CodexTokenMonitor;

/// <summary>
/// Projects the existing cost-band identities onto first quota-boundary arrival
/// times. Only the consumption timeline uses these copies; cost attribution and
/// the original analysis bands keep their existing interval semantics.
/// </summary>
internal static class QuotaConsumptionTimelineCalculator
{
    public static IReadOnlyList<QuotaCycleAnalysisBand> BuildBands(QuotaCycleAnalysisResult analysis)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        if (analysis.Timeline.Count < 2 || analysis.Bands.Count == 0)
        {
            return Array.Empty<QuotaCycleAnalysisBand>();
        }

        var result = new List<QuotaCycleAnalysisBand>(analysis.Bands.Count);
        foreach (var band in analysis.Bands)
        {
            if (band.UsedFromPercent < 0m || band.UsedToPercent > 100m ||
                band.UsedToPercent <= band.UsedFromPercent)
            {
                continue;
            }

            var start = FindFirstArrival(analysis.Timeline, band.UsedFromPercent);
            var end = FindFirstArrival(analysis.Timeline, band.UsedToPercent);
            if (start is null || end is null || end <= start)
            {
                // Missing boundaries are not extrapolated. A quota jump whose
                // endpoints share one timestamp cannot provide a measured rate.
                continue;
            }

            result.Add(band with { StartLocal = start.Value, EndLocal = end.Value });
        }
        return result.ToArray();
    }

    private static DateTimeOffset? FindFirstArrival(
        IReadOnlyList<QuotaCycleTimelinePoint> timeline,
        decimal targetUsedPercent)
    {
        // Timeline is chronological and monotonic as emitted by the analysis
        // calculator. A partial first observation cannot establish earlier
        // boundaries, and a flat tail cannot establish a later boundary.
        if (targetUsedPercent < timeline[0].UsedPercent || targetUsedPercent > timeline[^1].UsedPercent)
        {
            return null;
        }

        var low = 0;
        var high = timeline.Count;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (timeline[middle].UsedPercent < targetUsedPercent)
                low = middle + 1;
            else
                high = middle;
        }

        if (low >= timeline.Count)
        {
            return null;
        }
        var after = timeline[low];
        if (after.UsedPercent == targetUsedPercent)
        {
            // Lower-bound search deliberately chooses the first point on a
            // plateau, rather than its last point or the cost-band tail.
            return after.TimestampLocal;
        }
        if (low == 0)
        {
            return null;
        }

        var before = timeline[low - 1];
        var quotaSpan = after.UsedPercent - before.UsedPercent;
        var elapsed = after.TimestampLocal - before.TimestampLocal;
        if (quotaSpan <= 0m || elapsed < TimeSpan.Zero)
        {
            return null;
        }

        var ratio = (targetUsedPercent - before.UsedPercent) / quotaSpan;
        var ticks = (long)Math.Round(elapsed.Ticks * ratio, MidpointRounding.AwayFromZero);
        return before.TimestampLocal.AddTicks(ticks);
    }
}
