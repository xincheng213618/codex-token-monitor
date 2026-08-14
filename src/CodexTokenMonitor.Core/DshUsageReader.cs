using System.Text;
using ZstdSharp;

namespace CodexTokenMonitor;

internal sealed record DshUsageEntry(
    string Key,
    DateTimeOffset Timestamp,
    long Input,
    long Cached,
    long Output,
    long Reasoning)
{
    public long Total => Input + Output;
    public long CompletenessScore => Input + Cached + Output + Reasoning;
}

/// <summary>
/// Reads token usage from DeepSeek Harness (dsh) session transcripts.
///
/// dsh persists each session as an append-only JSONL log compressed into a
/// concatenation of independent Zstandard frames (one header frame plus one
/// frame per durable append batch) at:
///
///   %USERPROFILE%\.dsh\sessions\--<normalized-cwd>--\<session-id>\session.jsonl.zstd
///
/// Every model call reports its token accounting through an
/// `assistant/chunk { "type": "usage" }` record (never packed into chunk
/// rows), carrying inputTokens (uncached), cacheReadTokens, cacheWriteTokens,
/// outputTokens and reasoningTokens. The stable key is (session id, seq), so
/// repeated scans and overlapping imports never double count.
/// </summary>
internal static class DshUsageReader
{
    private const string CacheFolder = "DshTokenMonitor";
    private const string SessionsRootName = ".dsh";
    private const string SessionsDirName = "sessions";
    private const string TranscriptFileName = "session.jsonl.zstd";
    private const byte ZstdMagic0 = 0x28;
    private const byte ZstdMagic1 = 0xB5;
    private const byte ZstdMagic2 = 0x2F;
    private const byte ZstdMagic3 = 0xFD;

    /// <summary>Test seam: overrides the sessions root instead of the user profile.</summary>
    internal static string? OverrideSessionsRoot { get; set; }

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
        var entries = new Dictionary<string, DshUsageEntry>(StringComparer.Ordinal);

        foreach (var file in EnumerateTranscriptFiles(startLocal))
        {
            ReadFile(file, startLocal, endLocal, entries);
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
                $"dsh:{item.Key}"))
            .ToList();
    }

    private static IEnumerable<string> EnumerateTranscriptFiles(DateTimeOffset startLocal)
    {
        var sessionsRoot = OverrideSessionsRoot ??
                           Path.Combine(
                               Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                               SessionsRootName,
                               SessionsDirName);
        if (!Directory.Exists(sessionsRoot))
        {
            yield break;
        }

        var startUtc = startLocal.UtcDateTime;
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true
        };

        foreach (var file in Directory.EnumerateFiles(sessionsRoot, TranscriptFileName, options))
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

            // A transcript whose last write predates the scan start cannot
            // contain records inside the scan range (cached coverage is
            // tracked separately through ScannedThroughLocal).
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
        Dictionary<string, DshUsageEntry> entries)
    {
        string decoded;
        try
        {
            decoded = ReadDecodedTranscript(file);
        }
        catch
        {
            return;
        }

        if (decoded.Length == 0)
        {
            return;
        }

        // The session directory name is the session id, which is part of the
        // stable (session id, seq) key.
        var sessionId = Path.GetFileName(Path.GetDirectoryName(file)) ?? file;

        using var reader = new StringReader(decoded);
        while (reader.ReadLine() is { } line)
        {
            if (!line.Contains("\"usage\"", StringComparison.Ordinal))
            {
                continue;
            }

            var entry = TryReadUsageLine(line, sessionId, startLocal, endLocal);
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

    /// <summary>
    /// Decompresses a concatenation of independent Zstandard frames.
    /// Frames are located by their magic header and unwrapped one by one, so a
    /// truncated tail frame (a live append in progress) is tolerated and the
    /// completed prefix is still returned.
    /// </summary>
    private static string ReadDecodedTranscript(string file)
    {
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var buffer = new byte[stream.Length];
        var read = 0;
        while (read < buffer.Length)
        {
            var count = stream.Read(buffer, read, buffer.Length - read);
            if (count <= 0)
            {
                break;
            }

            read += count;
        }

        if (read == 0)
        {
            return "";
        }

        if (read < buffer.Length)
        {
            Array.Resize(ref buffer, read);
        }

        var builder = new StringBuilder();
        var positions = FindFrameStarts(buffer);
        for (var index = 0; index < positions.Count; index++)
        {
            var frameStart = positions[index];
            var frameEnd = index + 1 < positions.Count ? positions[index + 1] : buffer.Length;
            if (frameEnd <= frameStart)
            {
                continue;
            }

            try
            {
                using var decompressor = new Decompressor();
                var unwrapped = decompressor.Unwrap(buffer.AsSpan(frameStart, frameEnd - frameStart));
                builder.Append(Encoding.UTF8.GetString(unwrapped.ToArray()));
            }
            catch
            {
                // Incomplete or corrupt frame: keep the successfully decoded
                // prefix. Dsh appends frames atomically, so a partial frame
                // only appears while a write is in flight.
                break;
            }
        }

        return builder.ToString();
    }

    private static List<int> FindFrameStarts(byte[] buffer)
    {
        var positions = new List<int>();
        for (var index = 0; index <= buffer.Length - 4; index++)
        {
            if (buffer[index] == ZstdMagic0 &&
                buffer[index + 1] == ZstdMagic1 &&
                buffer[index + 2] == ZstdMagic2 &&
                buffer[index + 3] == ZstdMagic3)
            {
                positions.Add(index);
            }
        }

        return positions;
    }

    private static DshUsageEntry? TryReadUsageLine(
        string line,
        string sessionId,
        DateTimeOffset startLocal,
        DateTimeOffset endLocal)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;

            if (!StringEquals(root, "type", "assistant/chunk") ||
                !root.TryGetProperty("time", out var timeElement) ||
                timeElement.ValueKind != JsonValueKind.Number ||
                !timeElement.TryGetInt64(out var timeMs) ||
                !root.TryGetProperty("seq", out var seqElement) ||
                !seqElement.TryGetInt64(out var seq) ||
                !root.TryGetProperty("data", out var data) ||
                !data.TryGetProperty("chunk", out var chunk) ||
                !StringEquals(chunk, "type", "usage") ||
                !chunk.TryGetProperty("usage", out var usage))
            {
                return null;
            }

            var timestamp = DateTimeOffset.FromUnixTimeMilliseconds(timeMs)
                .ToOffset(CodexUsageReader.BeijingOffset);
            if (timestamp < startLocal || timestamp >= endLocal)
            {
                return null;
            }

            var inputTokens = GetInt64(usage, "inputTokens");
            var cacheRead = GetInt64(usage, "cacheReadTokens");
            var cacheWrite = GetInt64(usage, "cacheWriteTokens");
            var output = GetInt64(usage, "outputTokens");
            var reasoning = GetInt64(usage, "reasoningTokens");

            // The bucket pipeline computes Uncached = input - cached, so the
            // input figure must include every billed input component (same
            // convention as ClaudeUsageReader). cacheWrite is usually 0 for
            // DeepSeek, but keep it inside input for forward compatibility.
            var input = inputTokens + cacheRead + cacheWrite;

            // (session id, seq) is the stable per-session event identity; seq
            // is contiguous within a transcript, so re-scans never duplicate.
            return new DshUsageEntry($"{sessionId}:{seq}", timestamp, input, cacheRead, output, reasoning);
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
