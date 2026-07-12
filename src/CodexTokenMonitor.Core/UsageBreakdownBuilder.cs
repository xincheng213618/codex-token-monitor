namespace CodexTokenMonitor;

internal static class UsageBreakdownBuilder
{
    public static IReadOnlyList<TokenUsageBucket> Build(
        SelectedRange range,
        TokenUsageSummary summary,
        IReadOnlyList<TokenUsageBucket> detailRows,
        TimeSpan multiDayInterval)
    {
        if (range.Mode == RangeMode.Day)
        {
            return detailRows;
        }

        if (range.Mode is RangeMode.Week or RangeMode.Cycle)
        {
            return BuildIntervalRows(range, summary, detailRows, multiDayInterval);
        }

        return summary.DailyBuckets;
    }

    public static TimeSpan EstimateCodingTimeForRange(
        IUsageSourceReader reader,
        SelectedRange range,
        IReadOnlyList<TokenUsageBucket> breakdownRows,
        IReadOnlyList<TokenUsageBucket> detailRows,
        bool includeLiveToday,
        bool cacheOnly)
    {
        if (range.Mode == RangeMode.Day || range.IsCustomStart)
        {
            return EstimateCodingTime(breakdownRows);
        }

        if (detailRows.Count > 0)
        {
            return EstimateCodingTime(detailRows);
        }

        if (cacheOnly)
        {
            return TimeSpan.Zero;
        }

        var total = TimeSpan.Zero;
        for (var segmentStart = range.Start; segmentStart < range.End;)
        {
            var nextDay = StartOfDay(segmentStart).AddDays(1);
            var segmentEnd = nextDay < range.End ? nextDay : range.End;
            total += EstimateCodingTime(reader.ReadDetailRows(segmentStart, segmentEnd, includeLiveToday));
            segmentStart = segmentEnd;
        }

        return total;
    }

    public static TimeSpan EstimateCodingTime(IReadOnlyList<TokenUsageBucket> rows)
    {
        var ordered = rows
            .Where(row => row.Events > 0)
            .Select(row => row.StartLocal)
            .OrderBy(value => value)
            .ToList();
        if (ordered.Count == 0)
        {
            return TimeSpan.Zero;
        }

        var active = TimeSpan.Zero;
        var sessionStart = ordered[0];
        var previous = ordered[0];
        var maxIdle = TimeSpan.FromMinutes(10);
        for (var i = 1; i < ordered.Count; i++)
        {
            var current = ordered[i];
            if (current - previous > maxIdle)
            {
                active += previous - sessionStart;
                sessionStart = current;
            }

            previous = current;
        }

        active += previous - sessionStart;
        return active;
    }

    private static IReadOnlyList<TokenUsageBucket> BuildIntervalRows(
        SelectedRange range,
        TokenUsageSummary summary,
        IReadOnlyList<TokenUsageBucket> detailRows,
        TimeSpan interval)
    {
        if (detailRows.Count == 0)
        {
            return summary.DailyBuckets;
        }

        var buckets = new Dictionary<DateTimeOffset, TokenUsageBucket>();
        var detailDates = new HashSet<DateOnly>();
        foreach (var row in detailRows)
        {
            var bucketStart = StartOfInterval(row.StartLocal, interval);
            detailDates.Add(DateOnly.FromDateTime(row.StartLocal.DateTime));
            if (!buckets.TryGetValue(bucketStart, out var bucket))
            {
                bucket = new TokenUsageBucket { StartLocal = bucketStart };
                buckets[bucketStart] = bucket;
            }

            bucket.MergeFrom(row);
        }

        var rows = buckets.Values.Where(bucket => bucket.Events > 0).ToList();
        foreach (var dailyBucket in summary.DailyBuckets)
        {
            if (!detailDates.Contains(DateOnly.FromDateTime(dailyBucket.StartLocal.DateTime)))
            {
                rows.Add(dailyBucket);
            }
        }

        return rows.OrderBy(bucket => bucket.StartLocal).ToList();
    }

    private static DateTimeOffset StartOfInterval(DateTimeOffset value, TimeSpan interval)
    {
        var dayStart = StartOfDay(value);
        var ticks = (value - dayStart).Ticks / interval.Ticks * interval.Ticks;
        return dayStart.AddTicks(ticks);
    }

    private static DateTimeOffset StartOfDay(DateTimeOffset value)
    {
        return new DateTimeOffset(value.Year, value.Month, value.Day, 0, 0, 0, value.Offset);
    }
}
