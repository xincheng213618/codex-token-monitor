using System.Globalization;
using System.Text.Json;

namespace CodexTokenMonitor;

internal sealed record WorkBuddyUsageEntry(
    string Key,
    DateTimeOffset Timestamp,
    long Input,
    long Cached,
    long Output,
    long Total)
{
    public long CompletenessScore => Input + Cached + Output + Total;
}

internal static class WorkBuddyUsageReader
{
    private const string CacheFolder = "WorkBuddyTokenMonitor";

    public static bool ClearCache()
    {
        return UsageCacheStore.Delete(CacheFolder);
    }

    public static bool ClearCachedDay(DateOnly date)
    {
        return UsageCacheStore.DeleteDay(CacheFolder, date);
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
            var scanned = ReadRangeUncached(scanRange.StartLocal, scanRange.EndLocal);
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

                var scannedBucket = scanned.DailyBuckets.FirstOrDefault(item =>
                    DateOnly.FromDateTime(item.StartLocal.DateTime) == date) ?? new TokenUsageBucket
                    {
                        StartLocal = new DateTimeOffset(date.Year, date.Month, date.Day, 0, 0, 0, CodexUsageReader.BeijingOffset)
                    };

                var mergedBucket = new TokenUsageBucket { StartLocal = scannedBucket.StartLocal };
                if (cache.TryGet(date, out var existingBucket))
                {
                    AddBucketValues(mergedBucket, existingBucket);
                }

                AddBucketValues(mergedBucket, scannedBucket);
                var scannedThrough = Min(dayStart.AddDays(1), scanRange.EndLocal).AddTicks(-1);
                var isComplete = scanRange.CacheHistoricalDays && dayStart < todayStart;
                IReadOnlyList<TokenUsageEvent>? detailEvents = null;
                var replaceDetailEvents = true;
                if (cache.HasDetailEvents(date))
                {
                    var detailStart = Max(dayStart, scanRange.StartLocal);
                    var detailEnd = Min(dayStart.AddDays(1), scanRange.EndLocal);
                    var newEvents = ReadEventsUncached(detailStart, detailEnd);
                    detailEvents = newEvents
                        .Where(item => item.Timestamp >= dayStart && item.Timestamp < dayStart.AddDays(1))
                        .ToList();
                    replaceDetailEvents = false;
                }

                cache.Put(mergedBucket, isComplete, scannedThrough, detailEvents, replaceDetailEvents);
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

    private static TokenUsageSummary ReadRangeUncached(DateTimeOffset startLocal, DateTimeOffset endLocal)
    {
        var summary = new TokenUsageSummary
        {
            StartLocal = startLocal,
            EndLocal = endLocal
        };
        var dailyBuckets = new Dictionary<DateOnly, TokenUsageBucket>();

        foreach (var usageEvent in ReadEventsUncached(startLocal, endLocal))
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
        var entries = new Dictionary<string, WorkBuddyUsageEntry>(StringComparer.Ordinal);

        foreach (var root in GetLogRoots())
        {
            foreach (var file in EnumerateJsonlFiles(root, startLocal))
            {
                ReadFile(file, startLocal, endLocal, entries);
            }
        }

        return entries.Values
            .OrderBy(item => item.Timestamp)
            .Select(item => new TokenUsageEvent(item.Timestamp, item.Input, item.Cached, item.Output, 0, item.Total, $"workbuddy:{item.Key}"))
            .ToList();
    }

    private static IEnumerable<string> GetLogRoots()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var projects = Path.Combine(profile, ".workbuddy", "projects");
        if (Directory.Exists(projects))
        {
            yield return projects;
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

        foreach (var file in Directory.EnumerateFiles(root, "*.jsonl", options))
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
        Dictionary<string, WorkBuddyUsageEntry> entries)
    {
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
            {
                if (!line.Contains("\"usage\"", StringComparison.Ordinal) ||
                    !line.Contains("\"input_tokens\"", StringComparison.Ordinal))
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

    private static WorkBuddyUsageEntry? TryReadLine(
        string line,
        string file,
        DateTimeOffset startLocal,
        DateTimeOffset endLocal)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;

            if (!root.TryGetProperty("message", out var message) ||
                !message.TryGetProperty("usage", out var usage))
            {
                return null;
            }

            if (!TryGetTimestamp(root, message, out var timestamp))
            {
                return null;
            }

            timestamp = timestamp.ToOffset(CodexUsageReader.BeijingOffset);
            if (timestamp < startLocal || timestamp >= endLocal)
            {
                return null;
            }

            var input = GetInt64(usage, "input_tokens");
            var cached = GetInt64(usage, "cache_read_input_tokens");
            var output = GetInt64(usage, "output_tokens");
            var total = GetInt64(usage, "total_tokens");
            if (total <= 0)
            {
                total = input + output;
            }

            cached = Math.Min(Math.Max(0, cached), Math.Max(0, input));
            if (input <= 0 && output <= 0 && total <= 0)
            {
                return null;
            }

            var id = message.TryGetProperty("id", out var idElement) ? GetStringValue(idElement) : null;
            var rootId = root.TryGetProperty("id", out var rootIdElement) ? GetStringValue(rootIdElement) : null;
            var uuid = root.TryGetProperty("uuid", out var uuidElement) ? GetStringValue(uuidElement) : null;
            var key = !string.IsNullOrWhiteSpace(id)
                ? id
                : !string.IsNullOrWhiteSpace(rootId)
                    ? rootId
                    : !string.IsNullOrWhiteSpace(uuid)
                        ? uuid
                        : $"{file}|{timestamp.UtcTicks}|{input}|{cached}|{output}|{total}";

            return new WorkBuddyUsageEntry(key, timestamp, input, cached, output, total);
        }
        catch
        {
            return null;
        }
    }

    private static bool TryGetTimestamp(JsonElement root, JsonElement message, out DateTimeOffset timestamp)
    {
        if (root.TryGetProperty("timestamp", out var rootTimestamp) &&
            TryParseTimestamp(rootTimestamp, out timestamp))
        {
            return true;
        }

        if (message.TryGetProperty("timestamp", out var messageTimestamp) &&
            TryParseTimestamp(messageTimestamp, out timestamp))
        {
            return true;
        }

        timestamp = default;
        return false;
    }

    private static bool TryParseTimestamp(JsonElement element, out DateTimeOffset timestamp)
    {
        if (element.ValueKind == JsonValueKind.Number)
        {
            if (element.TryGetInt64(out var numeric))
            {
                timestamp = numeric > 10_000_000_000
                    ? DateTimeOffset.FromUnixTimeMilliseconds(numeric)
                    : DateTimeOffset.FromUnixTimeSeconds(numeric);
                return true;
            }

            if (element.TryGetDouble(out var floating))
            {
                var value = (long)Math.Round(floating);
                timestamp = value > 10_000_000_000
                    ? DateTimeOffset.FromUnixTimeMilliseconds(value)
                    : DateTimeOffset.FromUnixTimeSeconds(value);
                return true;
            }
        }

        if (element.ValueKind == JsonValueKind.String)
        {
            var text = element.GetString();
            if (!string.IsNullOrWhiteSpace(text) &&
                DateTimeOffset.TryParse(
                    text,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out timestamp))
            {
                return true;
            }
        }

        timestamp = default;
        return false;
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
        target.MergeFrom(source);
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

    private static long GetInt64(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return 0;
        }

        return value.ValueKind is JsonValueKind.Number && value.TryGetInt64(out var result)
            ? result
            : 0;
    }

    private static string? GetStringValue(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.GetRawText(),
            _ => null
        };
    }
}
