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

        if (interval is not { } bucketInterval || bucketInterval <= TimeSpan.Zero || endLocal <= startLocal)
        {
            return sourceRows
                .Where(row => row.Events > 0)
                .OrderBy(row => row.StartLocal)
                .ToList();
        }

        var intervalTicks = bucketInterval.Ticks;
        var spanTicks = Math.Max(intervalTicks, (endLocal - startLocal).Ticks);
        var bucketCount = Math.Max(
            1,
            (int)Math.Min(int.MaxValue, Math.Ceiling(spanTicks / (double)intervalTicks)));
        TokenUsageBucket?[]? buckets = null;
        // Fixed buckets already define chronological output order. Merge the
        // incoming rows directly, creating only buckets that contain events.
        foreach (var row in sourceRows)
        {
            if (row.Events <= 0)
            {
                continue;
            }

            buckets ??= new TokenUsageBucket?[bucketCount];
            var offsetTicks = (row.StartLocal - startLocal).Ticks;
            var clampedOffset = Math.Clamp(offsetTicks, 0L, spanTicks - 1);
            var index = Math.Clamp((int)(clampedOffset / intervalTicks), 0, bucketCount - 1);
            var bucket = buckets[index] ??= new TokenUsageBucket
            {
                StartLocal = startLocal.AddTicks(intervalTicks * index)
            };
            bucket.MergeFrom(row);
        }

        if (buckets is null)
        {
            return Array.Empty<TokenUsageBucket>();
        }

        var result = new List<TokenUsageBucket>();
        foreach (var bucket in buckets)
        {
            if (bucket is { Events: > 0 })
            {
                result.Add(bucket);
            }
        }

        return result;
    }
}
