using System.Globalization;
using System.Text.Json;

namespace CodexTokenMonitor;

internal sealed record ZCodeUsageEntry(
    string Key,
    DateTimeOffset Timestamp,
    long Input,
    long Cached,
    long Output,
    long Reasoning,
    long ReportedTotal)
{
    public long Total => ReportedTotal > 0 ? ReportedTotal : Input + Output;
    public long CompletenessScore => Input + Cached + Output + Reasoning + Total;
}

internal static class ZCodeUsageReader
{
    private const string CacheFolder = "ZCodeTokenMonitor";

    public static bool ClearCache()
    {
        return UsageCacheStore.Delete(CacheFolder);
    }

    public static IReadOnlyList<DateTimeOffset> GetIncompleteHistoricalDays(
        DateTimeOffset startInclusive,
        DateTimeOffset endInclusive)
    {
        return UsageCacheStore.GetIncompleteDays(CacheFolder, startInclusive, endInclusive);
    }

    public static TokenUsageSummary ReadCachedRange(DateTimeOffset startLocal, DateTimeOffset endLocal)
    {
        return UsageCacheStore.Load(CacheFolder).ReadRange(startLocal, endLocal);
    }

    public static IReadOnlyList<TokenUsageBucket> ReadCachedDetailRows(
        DateTimeOffset startLocal,
        DateTimeOffset endLocal)
    {
        return UsageCacheStore.Load(CacheFolder).ReadDetailRows(startLocal, endLocal);
    }

    public static TokenUsageSummary ReadRange(DateTimeOffset startLocal, DateTimeOffset endLocal, bool includeLiveToday = true)
    {
        var summary = new TokenUsageSummary
        {
            StartLocal = startLocal,
            EndLocal = endLocal
        };
        var dailyBuckets = new Dictionary<DateOnly, TokenUsageBucket>();
        var cache = UsageCacheStore.Load(CacheFolder);
        var cacheChanged = false;
        var scanRanges = new List<ScanRange>();

        var now = DateTimeOffset.UtcNow.ToOffset(CodexUsageReader.BeijingOffset);
        var todayStart = StartOfDay(now);

        for (var dayStart = StartOfDay(startLocal); dayStart < endLocal; dayStart = dayStart.AddDays(1))
        {
            var dayEnd = dayStart.AddDays(1);
            var clippedStart = Max(dayStart, startLocal);
            var clippedEnd = Min(dayEnd, endLocal);
            if (clippedStart >= clippedEnd)
            {
                continue;
            }

            var fullHistoricalDay = clippedStart == dayStart && clippedEnd == dayEnd && dayStart < todayStart;
            var liveToday = dayStart == todayStart;
            var fullCachedDay = clippedStart == dayStart && (fullHistoricalDay || liveToday);
            var date = DateOnly.FromDateTime(dayStart.DateTime);

            if (fullCachedDay && cache.TryGet(date, out var cachedBucket))
            {
                AddBucketToSummary(summary, dailyBuckets, cachedBucket);
            }

            if (fullHistoricalDay)
            {
                if (cache.TryGetRecord(date, out var record) && record.IsComplete)
                {
                    continue;
                }

                var scanStart = cache.TryGetRecord(date, out record) && record.ScannedThroughLocal is not null
                    ? record.ScannedThroughLocal.Value.AddTicks(1)
                    : dayStart;
                AddScanRange(scanRanges, Max(scanStart, clippedStart), dayEnd, cacheHistoricalDays: true);
            }
            else if (liveToday)
            {
                if (!includeLiveToday)
                {
                    continue;
                }

                var scanStart = cache.TryGetRecord(date, out var record) && record.ScannedThroughLocal is not null
                    ? record.ScannedThroughLocal.Value.AddTicks(1)
                    : clippedStart;
                AddScanRange(scanRanges, Max(scanStart, clippedStart), clippedEnd, cacheHistoricalDays: false);
            }
            else
            {
                AddScanRange(scanRanges, clippedStart, clippedEnd, cacheHistoricalDays: false);
            }
        }

        foreach (var scanRange in scanRanges)
        {
            var scannedEvents = ReadEventsUncached(scanRange.StartLocal, scanRange.EndLocal);
            var scanned = CreateSummaryFromEvents(scanRange.StartLocal, scanRange.EndLocal, scannedEvents);
            foreach (var bucket in scanned.DailyBuckets)
            {
                AddBucketToSummary(summary, dailyBuckets, bucket);
            }

            for (var dayStart = StartOfDay(scanRange.StartLocal); dayStart < scanRange.EndLocal; dayStart = dayStart.AddDays(1))
            {
                var date = DateOnly.FromDateTime(dayStart.DateTime);
                var isToday = dayStart == todayStart;
                if (!scanRange.CacheHistoricalDays && !isToday)
                {
                    continue;
                }

                var detailStart = Max(dayStart, scanRange.StartLocal);
                var detailEnd = Min(dayStart.AddDays(1), scanRange.EndLocal);
                var existingEvents = cache.GetDetailEvents(date);
                var newEvents = scannedEvents
                    .Where(item => item.Timestamp >= detailStart && item.Timestamp < detailEnd)
                    .ToList();
                var mergedEvents = UsageEventMerger.Merge(existingEvents
                    .Concat(newEvents)
                    .Where(item => item.Timestamp >= dayStart && item.Timestamp < dayStart.AddDays(1)));
                var mergedBucket = CreateBucketFromEvents(dayStart, mergedEvents);
                var isComplete = scanRange.CacheHistoricalDays && dayStart < todayStart;
                var scannedThrough = detailEnd.AddTicks(-1);

                if (mergedEvents.Count == 0 && cache.TryGet(date, out var existingBucket))
                {
                    mergedBucket = existingBucket;
                }

                cache.Put(
                    mergedBucket,
                    isComplete,
                    scannedThrough,
                    newEvents,
                    replaceDetailEvents: existingEvents.Count == 0);
                cacheChanged = true;
            }
        }

        if (cacheChanged)
        {
            cache.Save();
        }

        summary.DailyBuckets.AddRange(
            dailyBuckets.Values
                .OrderBy(bucket => bucket.StartLocal)
                .Where(bucket => bucket.Events > 0));

        return summary;
    }

    public static IReadOnlyList<TokenUsageBucket> ReadDetailRows(
        DateTimeOffset startLocal,
        DateTimeOffset endLocal,
        bool includeLiveToday = true)
    {
        var dayStart = StartOfDay(startLocal);
        var date = DateOnly.FromDateTime(dayStart.DateTime);
        var now = DateTimeOffset.UtcNow.ToOffset(CodexUsageReader.BeijingOffset);
        var todayStart = StartOfDay(now);
        var dayEnd = dayStart.AddDays(1);
        var cache = UsageCacheStore.Load(CacheFolder);
        var cachedEvents = cache.GetDetailEvents(date).ToList();

        if (dayStart == todayStart && !includeLiveToday)
        {
            return ToDetailBuckets(cachedEvents.Where(item => item.Timestamp >= startLocal && item.Timestamp < endLocal));
        }

        if (cache.TryGetRecord(date, out var record) && (cachedEvents.Count > 0 || record.Events == 0))
        {
            var hasCompleteCoverage = record.IsComplete ||
                                      record.ScannedThroughLocal is not null &&
                                      record.ScannedThroughLocal.Value >= endLocal.AddTicks(-1);
            if (hasCompleteCoverage)
            {
                return ToDetailBuckets(cachedEvents.Where(item => item.Timestamp >= startLocal && item.Timestamp < endLocal));
            }

            if (includeLiveToday)
            {
                var scanStart = record.ScannedThroughLocal is null
                    ? startLocal
                    : Max(startLocal, record.ScannedThroughLocal.Value.AddTicks(1));
                if (scanStart < endLocal)
                {
                    var newEvents = ReadEventsUncached(scanStart, endLocal);
                    var mergedEvents = UsageEventMerger.Merge(cachedEvents
                        .Concat(newEvents)
                        .Where(item => item.Timestamp >= dayStart && item.Timestamp < dayEnd));
                    var mergedBucket = CreateBucketFromEvents(dayStart, mergedEvents);
                    var isComplete = dayStart < todayStart && endLocal >= dayEnd;
                    cache.Put(mergedBucket, isComplete, endLocal.AddTicks(-1), newEvents, replaceDetailEvents: false);
                    cache.Save();
                    cachedEvents = mergedEvents.ToList();
                }
            }

            return ToDetailBuckets(cachedEvents.Where(item => item.Timestamp >= startLocal && item.Timestamp < endLocal));
        }

        var fullEvents = ReadEventsUncached(startLocal, endLocal);
        var fullBucket = CreateBucketFromEvents(dayStart, fullEvents);
        var completeHistoricalDay = dayStart < todayStart && startLocal == dayStart && endLocal >= dayEnd;
        if (startLocal == dayStart)
        {
            cache.Put(fullBucket, completeHistoricalDay, endLocal.AddTicks(-1), fullEvents);
            cache.Save();
        }

        return ToDetailBuckets(fullEvents);
    }

    public static IReadOnlyList<TokenUsageBucket> ReadTransientDetailRows(
        DateTimeOffset startLocal,
        DateTimeOffset endLocal)
    {
        return ToDetailBuckets(ReadEventsUncached(startLocal, endLocal));
    }

    private static TokenUsageSummary CreateSummaryFromEvents(
        DateTimeOffset startLocal,
        DateTimeOffset endLocal,
        IEnumerable<TokenUsageEvent> events)
    {
        var summary = new TokenUsageSummary
        {
            StartLocal = startLocal,
            EndLocal = endLocal
        };
        var dailyBuckets = new Dictionary<DateOnly, TokenUsageBucket>();

        foreach (var usageEvent in UsageEventMerger.Merge(events))
        {
            summary.Add(
                usageEvent.Timestamp,
                usageEvent.InputTokens,
                usageEvent.CachedInputTokens,
                usageEvent.OutputTokens,
                usageEvent.ReasoningOutputTokens,
                usageEvent.TotalTokens);

            var dayKey = DateOnly.FromDateTime(usageEvent.Timestamp.DateTime);
            if (!dailyBuckets.TryGetValue(dayKey, out var bucket))
            {
                bucket = new TokenUsageBucket
                {
                    StartLocal = new DateTimeOffset(dayKey.Year, dayKey.Month, dayKey.Day, 0, 0, 0, CodexUsageReader.BeijingOffset)
                };
                dailyBuckets[dayKey] = bucket;
            }

            bucket.Add(
                usageEvent.Timestamp,
                usageEvent.InputTokens,
                usageEvent.CachedInputTokens,
                usageEvent.OutputTokens,
                usageEvent.ReasoningOutputTokens,
                usageEvent.TotalTokens);
        }

        summary.DailyBuckets.AddRange(
            dailyBuckets.Values
                .OrderBy(bucket => bucket.StartLocal)
                .Where(bucket => bucket.Events > 0));

        return summary;
    }

    private static List<TokenUsageEvent> ReadEventsUncached(DateTimeOffset startLocal, DateTimeOffset endLocal)
    {
        var entries = new Dictionary<string, ZCodeUsageEntry>(StringComparer.Ordinal);

        foreach (var root in GetLogRoots())
        {
            foreach (var file in EnumerateJsonlFiles(root, startLocal))
            {
                ReadFile(file, startLocal, endLocal, entries);
            }
        }

        return entries.Values
            .OrderBy(item => item.Timestamp)
            .Select(item => new TokenUsageEvent(
                item.Timestamp,
                item.Input,
                item.Cached,
                item.Output,
                item.Reasoning,
                item.Total,
                $"zcode:{item.Key}"))
            .ToList();
    }

    private static IEnumerable<string> GetLogRoots()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var cliRoot = Path.Combine(profile, ".zcode", "cli");
        foreach (var name in new[] { "rollout", "debug" })
        {
            var root = Path.Combine(cliRoot, name);
            if (Directory.Exists(root))
            {
                yield return root;
            }
        }
    }

    private static IEnumerable<string> EnumerateJsonlFiles(string root, DateTimeOffset startLocal)
    {
        var startUtc = startLocal.UtcDateTime;
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true
        };

        foreach (var file in Directory.EnumerateFiles(root, "model-io-*.jsonl", options))
        {
            FileInfo info;
            try
            {
                info = new FileInfo(file);
            }
            catch
            {
                continue;
            }

            if (info.LastWriteTimeUtc >= startUtc)
            {
                yield return file;
            }
        }
    }

    private static void ReadFile(
        string file,
        DateTimeOffset startLocal,
        DateTimeOffset endLocal,
        Dictionary<string, ZCodeUsageEntry> entries)
    {
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
            {
                if (!line.Contains("\"usage\"", StringComparison.Ordinal))
                {
                    continue;
                }

                var entry = TryReadLine(line, file, startLocal, endLocal);
                if (entry is null)
                {
                    continue;
                }

                if (!entries.TryGetValue(entry.Key, out var existing) ||
                    entry.CompletenessScore > existing.CompletenessScore)
                {
                    entries[entry.Key] = entry;
                }
            }
        }
        catch
        {
            return;
        }
    }

    private static ZCodeUsageEntry? TryReadLine(
        string line,
        string file,
        DateTimeOffset startLocal,
        DateTimeOffset endLocal)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;

            if (!StringEquals(root, "type", "model_io") ||
                !root.TryGetProperty("response", out var response) ||
                !response.TryGetProperty("usage", out var usage))
            {
                return null;
            }

            var timestampText = GetString(root, "completedAt") ?? GetString(root, "startedAt");
            if (string.IsNullOrWhiteSpace(timestampText))
            {
                return null;
            }

            var timestamp = DateTimeOffset.Parse(
                timestampText,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal).ToOffset(CodexUsageReader.BeijingOffset);

            if (timestamp < startLocal || timestamp >= endLocal)
            {
                return null;
            }

            var requestId = GetString(root, "requestId");
            var turnId = GetString(root, "turnId");
            var sessionId = GetString(root, "sessionId");
            var key = !string.IsNullOrWhiteSpace(requestId)
                ? requestId
                : !string.IsNullOrWhiteSpace(turnId)
                    ? $"{sessionId}|{turnId}|{timestamp:O}"
                    : $"{file}|{timestamp:O}|{GetInt64(usage, "totalTokens")}";

            var input = GetInt64(usage, "inputTokens");
            var cacheRead = GetInt64(usage, "cacheReadTokens");
            var output = GetInt64(usage, "outputTokens");
            var reasoning = GetInt64(usage, "reasoningTokens");
            var total = GetInt64(usage, "totalTokens");
            var cached = Math.Min(input, cacheRead);

            if (input == 0 && output == 0 && total == 0)
            {
                return null;
            }

            return new ZCodeUsageEntry(key, timestamp, input, cached, output, reasoning, total);
        }
        catch
        {
            return null;
        }
    }

    private static IReadOnlyList<TokenUsageBucket> ToDetailBuckets(IEnumerable<TokenUsageEvent> events)
    {
        return UsageEventMerger.Merge(events)
            .Select(item =>
            {
                var bucket = new TokenUsageBucket { StartLocal = item.Timestamp };
                bucket.Add(
                    item.Timestamp,
                    item.InputTokens,
                    item.CachedInputTokens,
                    item.OutputTokens,
                    item.ReasoningOutputTokens,
                    item.TotalTokens);
                return bucket;
            })
            .ToList();
    }

    private static TokenUsageBucket CreateBucketFromEvents(
        DateTimeOffset bucketStart,
        IEnumerable<TokenUsageEvent> events)
    {
        var bucket = new TokenUsageBucket { StartLocal = bucketStart };
        foreach (var item in UsageEventMerger.Merge(events))
        {
            bucket.Add(
                item.Timestamp,
                item.InputTokens,
                item.CachedInputTokens,
                item.OutputTokens,
                item.ReasoningOutputTokens,
                item.TotalTokens);
        }

        return bucket;
    }

    private static void AddScanRange(
        List<ScanRange> scanRanges,
        DateTimeOffset startLocal,
        DateTimeOffset endLocal,
        bool cacheHistoricalDays)
    {
        if (startLocal >= endLocal)
        {
            return;
        }

        if (scanRanges.Count > 0)
        {
            var previous = scanRanges[^1];
            if (previous.EndLocal == startLocal && previous.CacheHistoricalDays == cacheHistoricalDays)
            {
                scanRanges[^1] = previous with { EndLocal = endLocal };
                return;
            }
        }

        scanRanges.Add(new ScanRange(startLocal, endLocal, cacheHistoricalDays));
    }

    private static void AddBucketToSummary(
        TokenUsageSummary summary,
        Dictionary<DateOnly, TokenUsageBucket> dailyBuckets,
        TokenUsageBucket bucket)
    {
        if (bucket.Events == 0)
        {
            return;
        }

        AddBucketValues(summary, bucket);
        var dayKey = DateOnly.FromDateTime(bucket.StartLocal.DateTime);
        if (!dailyBuckets.TryGetValue(dayKey, out var dailyBucket))
        {
            dailyBucket = new TokenUsageBucket { StartLocal = bucket.StartLocal };
            dailyBuckets[dayKey] = dailyBucket;
        }

        AddBucketValues(dailyBucket, bucket);
    }

    private static void AddBucketValues(TokenUsageBucket target, TokenUsageBucket source)
    {
        target.Events += source.Events;
        target.InputTokens += source.InputTokens;
        target.CachedInputTokens += source.CachedInputTokens;
        target.UncachedInputTokens += source.UncachedInputTokens;
        target.OutputTokens += source.OutputTokens;
        target.ReasoningOutputTokens += source.ReasoningOutputTokens;
        target.TotalTokens += source.TotalTokens;

        if (target.LastTokenEventLocal is null ||
            (source.LastTokenEventLocal is not null && source.LastTokenEventLocal > target.LastTokenEventLocal))
        {
            target.LastTokenEventLocal = source.LastTokenEventLocal;
        }
    }

    private static DateTimeOffset StartOfDay(DateTimeOffset value)
    {
        var local = value.ToOffset(CodexUsageReader.BeijingOffset);
        return new DateTimeOffset(local.Year, local.Month, local.Day, 0, 0, 0, CodexUsageReader.BeijingOffset);
    }

    private static DateTimeOffset Max(DateTimeOffset first, DateTimeOffset second)
    {
        return first >= second ? first : second;
    }

    private static DateTimeOffset Min(DateTimeOffset first, DateTimeOffset second)
    {
        return first <= second ? first : second;
    }

    private static bool StringEquals(JsonElement element, string propertyName, string expected)
    {
        return element.TryGetProperty(propertyName, out var value) &&
               value.ValueKind is JsonValueKind.String &&
               string.Equals(value.GetString(), expected, StringComparison.Ordinal);
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) && value.ValueKind is JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static long GetInt64(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return 0;
        }

        if (value.ValueKind is JsonValueKind.Number && value.TryGetInt64(out var result))
        {
            return result;
        }

        if (value.ValueKind is JsonValueKind.String &&
            long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out result))
        {
            return result;
        }

        return 0;
    }
}
