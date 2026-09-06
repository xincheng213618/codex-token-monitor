namespace CodexTokenMonitor;

internal static class HistoricalUsageBatchWarmer
{
    public static void WarmDays(
        string cacheFolder,
        IEnumerable<DateTimeOffset> daysLocal,
        Func<DateTimeOffset, DateTimeOffset, CancellationToken, UsageEventScanResult> scan,
        CancellationToken cancellationToken = default,
        Action<DateTimeOffset>? dayCompleted = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var today = StartOfDay(DateTimeOffset.UtcNow);
        var days = daysLocal.Select(StartOfDay)
            .Where(day => day < today)
            .Distinct()
            .OrderByDescending(day => day)
            .ToList();
        if (days.Count == 0)
        {
            return;
        }

        var cache = UsageCacheStore.Load(cacheFolder);
        var incomplete = UsageCacheStore.GetIncompleteDays(
            cacheFolder, days[^1], days[0], cancellationToken).ToHashSet();
        var pending = days.Where(incomplete.Contains).ToList();
        foreach (var day in days.Where(day => !incomplete.Contains(day)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            dayCompleted?.Invoke(day);
        }
        if (pending.Count == 0)
        {
            return;
        }

        // Parse each source file once for the requested history. Group before
        // merging: event identities are deduplicated within each cached day.
        var scanned = scan(pending[^1], pending[0].AddDays(1), cancellationToken);
        var eventsByDay = scanned.Events
            .GroupBy(item => StartOfDay(item.Timestamp))
            .ToDictionary(group => group.Key, group => group.ToList());
        foreach (var day in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var date = DateOnly.FromDateTime(day.DateTime);
            var existingEvents = cache.GetDetailEvents(date, cancellationToken);
            eventsByDay.TryGetValue(day, out var newEvents);
            var merged = UsageEventMerger.Merge(existingEvents.Concat(
                newEvents ?? Enumerable.Empty<TokenUsageEvent>()));
            var bucket = new TokenUsageBucket { StartLocal = day };
            foreach (var item in merged)
            {
                bucket.Add(item.Timestamp, item.InputTokens, item.CachedInputTokens,
                    item.CacheWriteInputTokens, item.OutputTokens,
                    item.ReasoningOutputTokens, item.TotalTokens);
            }

            // Preserve summary-only history when its original source is gone.
            // It remains incomplete until its matching details are available.
            if (merged.Count == 0 && cache.TryGet(date, out var existingBucket))
            {
                bucket = existingBucket;
            }
            cache.Put(bucket, scanned.IsComplete, day.AddDays(1).AddTicks(-1),
                merged, replaceDetailEvents: true,
                cancellationToken: cancellationToken, propagateErrors: true);

            if (scanned.IsComplete && cache.TryGetRecord(date, out var record) &&
                record.IsComplete && record.IsValid && record.Events == record.DetailEventCount)
            {
                dayCompleted?.Invoke(day);
            }
        }
    }

    private static DateTimeOffset StartOfDay(DateTimeOffset value)
    {
        value = value.ToOffset(CodexUsageReader.BeijingOffset);
        return new DateTimeOffset(value.Year, value.Month, value.Day,
            0, 0, 0, CodexUsageReader.BeijingOffset);
    }
}
