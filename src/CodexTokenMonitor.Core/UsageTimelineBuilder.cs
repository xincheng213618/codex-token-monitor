namespace CodexTokenMonitor;

internal static class UsageTimelineBuilder
{
    public static IReadOnlyList<TokenUsageBucket> Build(
        DateTimeOffset startLocal,
        DateTimeOffset endLocal,
        IEnumerable<TokenUsageBucket> sourceRows,
        TimeSpan? interval)
    {
        ArgumentNullException.ThrowIfNull(sourceRows);

        startLocal = startLocal.ToOffset(CodexUsageReader.BeijingOffset);
        endLocal = endLocal.ToOffset(CodexUsageReader.BeijingOffset);

        var rows = sourceRows
            .Where(row => row.Events > 0)
            .OrderBy(row => row.StartLocal)
            .ToList();
        if (rows.Count == 0 || interval is not { } bucketInterval || bucketInterval <= TimeSpan.Zero || endLocal <= startLocal)
        {
            return rows;
        }

        var intervalTicks = bucketInterval.Ticks;
        var spanTicks = Math.Max(intervalTicks, (endLocal - startLocal).Ticks);
        var bucketCount = Math.Max(
            1,
            (int)Math.Min(int.MaxValue, Math.Ceiling(spanTicks / (double)intervalTicks)));
        var buckets = new TokenUsageBucket[bucketCount];
        for (var index = 0; index < bucketCount; index++)
        {
            buckets[index] = new TokenUsageBucket
            {
                StartLocal = startLocal.AddTicks(intervalTicks * index)
            };
        }

        foreach (var row in rows)
        {
            var offsetTicks = (row.StartLocal - startLocal).Ticks;
            var clampedOffset = Math.Clamp(offsetTicks, 0L, spanTicks - 1);
            var index = Math.Clamp((int)(clampedOffset / intervalTicks), 0, bucketCount - 1);
            buckets[index].MergeFrom(row);
        }

        return buckets
            .Where(bucket => bucket.Events > 0)
            .ToList();
    }
}
