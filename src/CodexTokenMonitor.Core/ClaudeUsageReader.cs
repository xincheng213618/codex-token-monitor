using System.Globalization;
using System.Text.Json;

namespace CodexTokenMonitor;

internal sealed record ClaudeUsageEntry(
    string Key,
    DateTimeOffset Timestamp,
    long Input,
    long Cached,
    long CacheWrite,
    long Output,
    string? ModelId = null)
{
    public long Total => TokenCountMath.AddNonNegative(Input, Output);
    public decimal CompletenessScore =>
        (decimal)TokenCountMath.NonNegative(Input) +
        TokenCountMath.NonNegative(Cached) +
        TokenCountMath.NonNegative(CacheWrite) +
        TokenCountMath.NonNegative(Output);
}

internal static class ClaudeUsageReader
{
    private const string CacheFolder = "ClaudeCodeTokenMonitor";

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
        DateTimeOffset endInclusive,
        CancellationToken cancellationToken = default)
    {
        return UsageCacheStore.GetIncompleteDays(CacheFolder, startInclusive, endInclusive, cancellationToken);
    }

    public static TokenUsageSummary ReadCachedRange(
        DateTimeOffset startLocal,
        DateTimeOffset endLocal,
        CancellationToken cancellationToken = default)
    {
        return UsageCacheStore.Load(CacheFolder).ReadRange(startLocal, endLocal, cancellationToken);
    }

    public static IReadOnlyList<TokenUsageBucket> ReadCachedDetailRows(
        DateTimeOffset startLocal,
        DateTimeOffset endLocal,
        CancellationToken cancellationToken = default)
    {
        return UsageCacheStore.Load(CacheFolder).ReadDetailRows(startLocal, endLocal, cancellationToken);
    }

    public static void WarmHistoricalDays(
        IEnumerable<DateTimeOffset> daysLocal,
        CancellationToken cancellationToken = default,
        Action<DateTimeOffset>? dayCompleted = null,
        Action<int, int>? fileProgress = null)
    {
        HistoricalUsageBatchWarmer.WarmDays(CacheFolder, daysLocal,
            (start, end, token) => ReadEventsUncached(start, end, token, fileProgress),
            cancellationToken, dayCompleted);
    }

    public static TokenUsageSummary ReadRange(
        DateTimeOffset startLocal,
        DateTimeOffset endLocal,
        bool includeLiveToday = true,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        startLocal = startLocal.ToOffset(CodexUsageReader.BeijingOffset);
        endLocal = endLocal.ToOffset(CodexUsageReader.BeijingOffset);
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
            cancellationToken.ThrowIfCancellationRequested();
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
                if (cache.TryGetRecord(date, out var record) &&
                    record.IsComplete &&
                    record.DetailEventCount == record.Events)
                {
                    continue;
                }

                // Invalidated historical records can retain a watermark at the
                // end of the day. Re-scan the full day to restore completion.
                AddScanRange(scanRanges, dayStart, dayEnd, cacheHistoricalDays: true);
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
            cancellationToken.ThrowIfCancellationRequested();
            var scanned = ReadRangeUncached(scanRange.StartLocal, scanRange.EndLocal, cancellationToken);
            foreach (var bucket in scanned.Summary.DailyBuckets)
            {
                AddBucketToSummary(summary, dailyBuckets, bucket);
            }

            for (var dayStart = StartOfDay(scanRange.StartLocal); dayStart < scanRange.EndLocal; dayStart = dayStart.AddDays(1))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var date = DateOnly.FromDateTime(dayStart.DateTime);
                var isToday = dayStart == todayStart;
                if (!scanRange.CacheHistoricalDays && !isToday)
                {
                    continue;
                }

                var scannedBucket = scanned.Summary.DailyBuckets.FirstOrDefault(item =>
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
                var isComplete = scanRange.CacheHistoricalDays && dayStart < todayStart && scanned.IsComplete;
                IReadOnlyList<TokenUsageEvent>? detailEvents = null;
                var replaceDetailEvents = true;
                var detailScanComplete = true;
                var hasDetailEvents = cache.HasDetailEvents(date, cancellationToken);
                if (hasDetailEvents)
                {
                    var cachedDetailEvents = cache.GetDetailEvents(date, cancellationToken);
                    var detailsAreComplete = !cache.TryGetRecord(date, out var detailRecord) ||
                                             cachedDetailEvents.Count == detailRecord.Events;
                    var detailStart = detailsAreComplete
                        ? Max(dayStart, scanRange.StartLocal)
                        : dayStart;
                    var detailEnd = Min(dayStart.AddDays(1), scanRange.EndLocal);
                    var newEventsResult = ReadEventsUncached(detailStart, detailEnd, cancellationToken);
                    detailScanComplete = newEventsResult.IsComplete;
                    detailEvents = UsageEventMerger.Merge(cachedDetailEvents.Concat(newEventsResult.Events)
                        .Where(item => item.Timestamp >= dayStart && item.Timestamp < dayStart.AddDays(1))
                        .ToList());
                    mergedBucket = CreateBucketFromEvents(dayStart, detailEvents);
                }
                else if (scanRange.CacheHistoricalDays && scannedBucket.Events > 0)
                {
                    mergedBucket = scannedBucket;
                }

                cache.Put(
                    mergedBucket,
                    isComplete && detailScanComplete,
                    scannedThrough,
                    detailEvents,
                    replaceDetailEvents,
                    cancellationToken);
                dailyBuckets[date] = mergedBucket;
                cacheChanged = true;
            }
        }

        if (cacheChanged)
        {
            cancellationToken.ThrowIfCancellationRequested();
            cache.Save();
            summary = new TokenUsageSummary
            {
                StartLocal = startLocal,
                EndLocal = endLocal
            };
            foreach (var bucket in dailyBuckets.Values)
            {
                AddBucketValues(summary, bucket);
            }
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
        bool includeLiveToday = true,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        startLocal = startLocal.ToOffset(CodexUsageReader.BeijingOffset);
        endLocal = endLocal.ToOffset(CodexUsageReader.BeijingOffset);
        var dayStart = StartOfDay(startLocal);
        var date = DateOnly.FromDateTime(dayStart.DateTime);
        var now = DateTimeOffset.UtcNow.ToOffset(CodexUsageReader.BeijingOffset);
        var todayStart = StartOfDay(now);
        var dayEnd = dayStart.AddDays(1);
        var cache = UsageCacheStore.Load(CacheFolder);
        var cachedEvents = cache.GetDetailEvents(date, cancellationToken).ToList();

        if (dayStart == todayStart && !includeLiveToday)
        {
            return ToDetailBuckets(cachedEvents.Where(item => item.Timestamp >= startLocal && item.Timestamp < endLocal));
        }

        if (cache.TryGetRecord(date, out var record) && (cachedEvents.Count > 0 || record.Events == 0))
        {
            var hasCompleteDetails = cachedEvents.Count == record.Events;
            var hasCompleteCoverage = hasCompleteDetails && (record.IsComplete ||
                                      dayStart >= todayStart && record.ScannedThroughLocal is not null &&
                                      record.ScannedThroughLocal.Value >= endLocal.AddTicks(-1));
            if (hasCompleteCoverage)
            {
                return ToDetailBuckets(cachedEvents.Where(item => item.Timestamp >= startLocal && item.Timestamp < endLocal));
            }

            if (dayStart < todayStart || includeLiveToday)
            {
                var replaceDetails = !hasCompleteDetails || dayStart < todayStart && !record.IsComplete;
                var scanStart = replaceDetails || record.ScannedThroughLocal is null
                    ? replaceDetails ? dayStart : startLocal
                    : Max(startLocal, record.ScannedThroughLocal.Value.AddTicks(1));
                var scanEnd = dayStart < todayStart ? dayEnd : endLocal;
                if (scanStart < scanEnd)
                {
                    var newEventsResult = ReadEventsUncached(scanStart, scanEnd, cancellationToken);
                    var mergedEvents = UsageEventMerger.Merge(cachedEvents
                        .Concat(newEventsResult.Events)
                        .Where(item => item.Timestamp >= dayStart && item.Timestamp < dayEnd));
                    var mergedBucket = CreateBucketFromEvents(dayStart, mergedEvents);
                    var isComplete = dayStart < todayStart && scanEnd >= dayEnd && newEventsResult.IsComplete;
                    cache.Put(
                        mergedBucket,
                        isComplete,
                        scanEnd.AddTicks(-1),
                        replaceDetails ? mergedEvents : newEventsResult.Events,
                        replaceDetailEvents: replaceDetails,
                        cancellationToken: cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    cache.Save();
                    cachedEvents = mergedEvents.ToList();
                }
            }

            return ToDetailBuckets(cachedEvents.Where(item => item.Timestamp >= startLocal && item.Timestamp < endLocal));
        }

        var fullEventsResult = ReadEventsUncached(startLocal, endLocal, cancellationToken);
        var fullEvents = fullEventsResult.Events;
        var fullBucket = CreateBucketFromEvents(dayStart, fullEvents);
        var completeHistoricalDay = dayStart < todayStart && startLocal == dayStart && endLocal >= dayEnd && fullEventsResult.IsComplete;
        if (startLocal == dayStart)
        {
            cache.Put(
                fullBucket,
                completeHistoricalDay,
                endLocal.AddTicks(-1),
                fullEvents,
                cancellationToken: cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            cache.Save();
        }

        return ToDetailBuckets(fullEvents);
    }

    public static IReadOnlyList<TokenUsageBucket> ReadTransientDetailRows(
        DateTimeOffset startLocal,
        DateTimeOffset endLocal,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        startLocal = startLocal.ToOffset(CodexUsageReader.BeijingOffset);
        endLocal = endLocal.ToOffset(CodexUsageReader.BeijingOffset);
        return ToDetailBuckets(ReadEventsUncached(startLocal, endLocal, cancellationToken).Events);
    }

    private static UsageRangeScanResult ReadRangeUncached(
        DateTimeOffset startLocal,
        DateTimeOffset endLocal,
        CancellationToken cancellationToken)
    {
        var summary = new TokenUsageSummary
        {
            StartLocal = startLocal,
            EndLocal = endLocal
        };
        var dailyBuckets = new Dictionary<DateOnly, TokenUsageBucket>();

        var eventsResult = ReadEventsUncached(startLocal, endLocal, cancellationToken);
        foreach (var usageEvent in eventsResult.Events)
        {
            cancellationToken.ThrowIfCancellationRequested();
            summary.Add(usageEvent);

            var dayKey = DateOnly.FromDateTime(usageEvent.Timestamp.DateTime);
            if (!dailyBuckets.TryGetValue(dayKey, out var bucket))
            {
                bucket = new TokenUsageBucket
                {
                    StartLocal = new DateTimeOffset(dayKey.Year, dayKey.Month, dayKey.Day, 0, 0, 0, CodexUsageReader.BeijingOffset)
                };
                dailyBuckets[dayKey] = bucket;
            }

            bucket.Add(usageEvent);
        }

        summary.DailyBuckets.AddRange(
            dailyBuckets.Values
                .OrderBy(bucket => bucket.StartLocal)
                .Where(bucket => bucket.Events > 0));

        return new UsageRangeScanResult(summary, eventsResult.IsComplete);
    }

    private static UsageEventScanResult ReadEventsUncached(
        DateTimeOffset startLocal,
        DateTimeOffset endLocal,
        CancellationToken cancellationToken,
        Action<int, int>? fileProgress = null)
    {
        var entries = new Dictionary<string, ClaudeUsageEntry>(StringComparer.Ordinal);
        var isComplete = true;

        var files = new List<string>();
        foreach (var root in GetLogRoots())
        {
            cancellationToken.ThrowIfCancellationRequested();
            files.AddRange(EnumerateJsonlFiles(root, startLocal));
        }
        fileProgress?.Invoke(0, files.Count);
        for (var index = 0; index < files.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!ReadFile(files[index], startLocal, endLocal, entries, cancellationToken))
            {
                isComplete = false;
            }
            fileProgress?.Invoke(index + 1, files.Count);
        }

        var events = entries.Values
            .OrderBy(item => item.Timestamp)
            .Select(item => new TokenUsageEvent(
                item.Timestamp,
                item.Input,
                item.Cached,
                item.Output,
                0,
                item.Total,
                $"claude:{item.Key}",
                item.CacheWrite,
                ModelId: item.ModelId))
            .ToList();
        return new UsageEventScanResult(events, isComplete);
    }

    private static IEnumerable<string> GetLogRoots()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var projects = UsageLogPaths.GetOverrideRoot(UsageSource.ClaudeCode) ??
                       Path.Combine(profile, ".claude", "projects");
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

    private static bool ReadFile(
        string file,
        DateTimeOffset startLocal,
        DateTimeOffset endLocal,
        Dictionary<string, ClaudeUsageEntry> entries,
        CancellationToken cancellationToken)
    {
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!line.Contains("\"usage\"", StringComparison.Ordinal))
                {
                    continue;
                }

                var entry = TryReadLine(line, file, startLocal, endLocal);
                if (entry is null)
                {
                    continue;
                }

                // A batch must retain the same identity on different days,
                // just as separate daily scans do.
                var dayKey = $"{entry.Timestamp:yyyy-MM-dd}|{entry.Key}";
                if (!entries.TryGetValue(dayKey, out var existing) ||
                    entry.CompletenessScore > existing.CompletenessScore)
                {
                    entries[dayKey] = entry;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }

        return true;
    }

    private static ClaudeUsageEntry? TryReadLine(
        string line,
        string file,
        DateTimeOffset startLocal,
        DateTimeOffset endLocal)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;

            if (!StringEquals(root, "type", "assistant") ||
                !root.TryGetProperty("timestamp", out var timestampElement) ||
                !root.TryGetProperty("message", out var message) ||
                !message.TryGetProperty("usage", out var usage))
            {
                return null;
            }

            var timestampText = timestampElement.GetString();
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

            var messageId = message.TryGetProperty("id", out var idElement) ? idElement.GetString() : null;
            var uuid = root.TryGetProperty("uuid", out var uuidElement) ? uuidElement.GetString() : null;
            var key = !string.IsNullOrWhiteSpace(messageId)
                ? messageId
                : $"{file}|{uuid}";

            var nonCachedInput = GetInt64(usage, "input_tokens");
            var cacheCreation = GetInt64(usage, "cache_creation_input_tokens");
            var cacheRead = GetInt64(usage, "cache_read_input_tokens");
            var output = GetInt64(usage, "output_tokens");
            var input = TokenCountMath.AddNonNegative(
                TokenCountMath.AddNonNegative(nonCachedInput, cacheCreation),
                cacheRead);
            var cached = cacheRead;

            var modelId = message.TryGetProperty("model", out var modelElement) &&
                          modelElement.ValueKind == JsonValueKind.String
                ? modelElement.GetString()
                : null;

            return new ClaudeUsageEntry(key, timestamp, input, cached, cacheCreation, output, modelId);
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
                bucket.Add(item);
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
            bucket.Add(item);
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

    private static bool StringEquals(JsonElement element, string propertyName, string expected)
    {
        return element.TryGetProperty(propertyName, out var value) &&
               value.ValueKind is JsonValueKind.String &&
               string.Equals(value.GetString(), expected, StringComparison.Ordinal);
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
}
