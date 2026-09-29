using System.Buffers;
using System.Text;
using ZstdSharp;

namespace CodexTokenMonitor;

internal sealed record DshUsageEntry(
    string Key,
    DateTimeOffset Timestamp,
    long Input,
    long Cached,
    long CacheWrite,
    long Output,
    long Reasoning,
    string? ModelId)
{
    public long Total => TokenCountMath.AddNonNegative(Input, Output);
    public decimal CompletenessScore =>
        (decimal)TokenCountMath.NonNegative(Input) +
        TokenCountMath.NonNegative(Cached) +
        TokenCountMath.NonNegative(CacheWrite) +
        TokenCountMath.NonNegative(Output) +
        TokenCountMath.NonNegative(Reasoning);
}

/// <summary>
/// Reads token usage from DeepSeek Harness (dsh) session transcripts.
///
/// dsh persists each session as an append-only JSONL log, compressed by
/// default into a concatenation of independent Zstandard frames (one header
/// frame plus one frame per durable append batch), at:
///
///   %USERPROFILE%\.dsh\sessions\--<normalized-cwd>--\<session-id>\session.<vN.>jsonl[.zstd]
///
/// The physical filename carries the session format generation — released
/// v0–v2 wrote `session.jsonl.zstd` (no version segment), v3 writes
/// `session.v3.jsonl.zstd`, v4 (current) writes `session.v4.jsonl.zstd` — and
/// `compression: 'none'` drops the `.zstd` suffix while keeping the raw JSONL
/// lines. Runtime operations select the numerically highest generation in a
/// session directory, and migration deliberately retains the superseded
/// generation beside its successor; reading every matching file would count
/// the same session twice, so the highest generation alone is scanned.
///
/// Every model call reports its token accounting on its assistant settlement:
/// current format in `assistant/message.data.usage`, released v0–v3 in an
/// `assistant/chunk { "type": "usage" }` record. Both carry inputTokens
/// (uncached), cacheReadTokens, cacheWriteTokens, outputTokens and — in the
/// released format only — reasoningTokens. The stable key is (session id,
/// seq), so repeated scans and overlapping imports never double count.
/// </summary>
internal static class DshUsageReader
{
    private const string CacheFolder = "DshTokenMonitor";
    private const string SessionsRootName = ".dsh";
    private const string SessionsDirName = "sessions";
    private const string TranscriptFileNamePrefix = "session";
    private const string TranscriptFileNameSuffix = ".jsonl";
    private const string CompressedTranscriptSuffix = ".jsonl.zstd";
    private const byte ZstdMagic0 = 0x28;
    private const byte ZstdMagic1 = 0xB5;
    private const byte ZstdMagic2 = 0x2F;
    private const byte ZstdMagic3 = 0xFD;

    /// <summary>Test seam: overrides the sessions root instead of the user profile.</summary>
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
                    detailEvents = UsageEventMerger.Merge(cachedDetailEvents.Concat(newEventsResult.Events)
                        .Where(item => item.Timestamp >= dayStart && item.Timestamp < dayStart.AddDays(1))
                        .ToList());
                    mergedBucket = CreateBucketFromEvents(dayStart, detailEvents);
                    isComplete &= newEventsResult.IsComplete;
                }
                else if (scanRange.CacheHistoricalDays && scannedBucket.Events > 0)
                {
                    mergedBucket = scannedBucket;
                }

                cache.Put(
                    mergedBucket,
                    isComplete,
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
        var entries = new Dictionary<string, DshUsageEntry>(StringComparer.Ordinal);
        var isComplete = true;

        var files = EnumerateTranscriptFiles(startLocal).ToList();
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
                item.Reasoning,
                item.Total,
                $"dsh:{item.Key}",
                item.CacheWrite,
                ModelId: item.ModelId))
            .ToList();
        return new UsageEventScanResult(events, isComplete);
    }

    private static IEnumerable<string> EnumerateTranscriptFiles(DateTimeOffset startLocal)
    {
        var sessionsRoot = UsageLogPaths.GetOverrideRoot(UsageSource.Dsh) ??
                           Path.Combine(
                               Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                               SessionsRootName,
                               SessionsDirName);
        if (!Directory.Exists(sessionsRoot))
        {
            yield break;
        }

        var startUtc = startLocal.UtcDateTime;
        foreach (var file in SelectHighestGenerations(sessionsRoot))
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

    /// <summary>
    /// Resolves one transcript per session directory: the numerically highest
    /// format generation present. Migration keeps the superseded generation on
    /// disk, so scanning every match would report one session twice.
    /// </summary>
    private static List<string> SelectHighestGenerations(string sessionsRoot)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            MatchType = MatchType.Simple
        };

        var selected = new Dictionary<string, (int Version, string Path)>(StringComparer.OrdinalIgnoreCase);
        IEnumerable<string> candidates;
        try
        {
            candidates = Directory.EnumerateFiles(sessionsRoot, "session*.jsonl*", options);
        }
        catch
        {
            return [];
        }

        foreach (var file in candidates)
        {
            if (!TryGetTranscriptGeneration(Path.GetFileName(file), out var version))
            {
                continue;
            }

            var directory = Path.GetDirectoryName(file) ?? file;
            if (!selected.TryGetValue(directory, out var current) || version > current.Version)
            {
                selected[directory] = (version, file);
            }
        }

        return selected.Values
            .Select(item => item.Path)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Accepts the released/current transcript names — `session.jsonl`,
    /// `session.jsonl.zstd`, `session.v3.jsonl.zstd`, `session.v4.jsonl` —
    /// and reports their format generation (a missing version segment is 0).
    /// </summary>
    private static bool TryGetTranscriptGeneration(string fileName, out int version)
    {
        version = 0;
        if (!fileName.StartsWith(TranscriptFileNamePrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var remainder = fileName[TranscriptFileNamePrefix.Length..];
        var compressed = remainder.EndsWith(CompressedTranscriptSuffix, StringComparison.OrdinalIgnoreCase);
        if (!compressed && !remainder.EndsWith(TranscriptFileNameSuffix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // `.jsonl.zstd` and `.jsonl` include their separating dot, so the
        // remaining segment is `.v4` or empty.
        var suffixLength = compressed ? CompressedTranscriptSuffix.Length : TranscriptFileNameSuffix.Length;
        var versionSegment = remainder[..^suffixLength];
        if (versionSegment.StartsWith('.'))
        {
            versionSegment = versionSegment[1..];
        }

        if (versionSegment.Length == 0)
        {
            return true;
        }

        if (versionSegment.Length < 2 ||
            versionSegment[0] is not ('v' or 'V') ||
            !int.TryParse(versionSegment[1..], out version) ||
            version < 0)
        {
            version = 0;
            return false;
        }

        return true;
    }

    private static bool ReadFile(
        string file,
        DateTimeOffset startLocal,
        DateTimeOffset endLocal,
        Dictionary<string, DshUsageEntry> entries,
        CancellationToken cancellationToken)
    {
        try
        {
            // Keep only one decoded frame and the current compressed frame in
            // memory.  A long-lived session can grow very large; materializing
            // the complete compressed file and complete decoded transcript at
            // once creates two avoidable large-object allocations.
            var sessionId = Path.GetFileName(Path.GetDirectoryName(file)) ?? file;
            var pendingLine = "";
            var context = new DshTranscriptContext();
            void ConsumeDecoded(string decoded)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (decoded.Length == 0)
                {
                    return;
                }

                var text = pendingLine + decoded;
                var lastNewLine = text.LastIndexOf('\n');
                if (lastNewLine < 0)
                {
                    pendingLine = text;
                    return;
                }

                ConsumeLines(
                    text[..(lastNewLine + 1)],
                    sessionId,
                    startLocal,
                    endLocal,
                    entries,
                    context,
                    cancellationToken);
                pendingLine = text[(lastNewLine + 1)..];
            }

            var decodedCompletely = IsZstandardTranscript(file)
                ? ReadDecodedTranscript(file, ConsumeDecoded, cancellationToken)
                : ReadRawTranscript(file, ConsumeDecoded, cancellationToken);

            if (pendingLine.Length > 0)
            {
                ConsumeLine(pendingLine, sessionId, startLocal, endLocal, entries, context, cancellationToken);
            }

            return decodedCompletely;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    private static void ConsumeLines(
        string text,
        string sessionId,
        DateTimeOffset startLocal,
        DateTimeOffset endLocal,
        Dictionary<string, DshUsageEntry> entries,
        DshTranscriptContext context,
        CancellationToken cancellationToken)
    {
        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } line)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ConsumeLine(line, sessionId, startLocal, endLocal, entries, context, cancellationToken);
        }
    }

    private static void ConsumeLine(
        string line,
        string sessionId,
        DateTimeOffset startLocal,
        DateTimeOffset endLocal,
        Dictionary<string, DshUsageEntry> entries,
        DshTranscriptContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!line.Contains("\"usage\"", StringComparison.Ordinal) &&
            !line.Contains("\"assistant/message\"", StringComparison.Ordinal) &&
            !line.Contains("\"request/context\"", StringComparison.Ordinal) &&
            !line.Contains("\"request/header\"", StringComparison.Ordinal))
        {
            return;
        }

        var entry = TryReadLine(line, sessionId, startLocal, endLocal, context);
        if (entry is null)
        {
            return;
        }

        // Keep event deduplication local to a day during historical batches.
        var dayKey = $"{entry.Timestamp:yyyy-MM-dd}|{entry.Key}";
        if (!entries.TryGetValue(dayKey, out var existing) ||
            entry.CompletenessScore > existing.CompletenessScore)
        {
            entries[dayKey] = entry;
        }
    }

    /// <summary>
    /// Distinguishes the compressed transcript from a `compression: 'none'`
    /// root by its Zstandard magic, so either encoding is read correctly and a
    /// mislabeled file still degrades to a corrupt-frame report rather than
    /// silently yielding nothing.
    /// </summary>
    private static bool IsZstandardTranscript(string file)
    {
        try
        {
            Span<byte> magic = stackalloc byte[4];
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var read = stream.ReadAtLeast(magic, magic.Length, throwOnEndOfStream: false);
            return read == magic.Length &&
                   magic[0] == ZstdMagic0 &&
                   magic[1] == ZstdMagic1 &&
                   magic[2] == ZstdMagic2 &&
                   magic[3] == ZstdMagic3;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Streams newline-delimited transcript text, one bounded block at a time,
    /// for a log written with compression disabled. A trailing partial line (a
    /// live append in progress) stays buffered until the next append completes
    /// it, mirroring the truncated-tail tolerance of the compressed reader.
    /// </summary>
    private static bool ReadRawTranscript(
        string file,
        Action<string> consumeDecoded,
        CancellationToken cancellationToken)
    {
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var bytes = ArrayPool<byte>.Shared.Rent(64 * 1024);
            var chars = ArrayPool<char>.Shared.Rent(64 * 1024);
            var decoder = Encoding.UTF8.GetDecoder();
            try
            {
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var bytesRead = stream.Read(bytes, 0, bytes.Length);
                    if (bytesRead <= 0)
                    {
                        break;
                    }

                    var flush = stream.Position >= stream.Length;
                    decoder.Convert(
                        bytes, 0, bytesRead,
                        chars, 0, chars.Length,
                        flush,
                        out _,
                        out var charsUsed,
                        out _);
                    if (charsUsed > 0)
                    {
                        consumeDecoded(new string(chars, 0, charsUsed));
                    }
                }

                return true;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(bytes);
                ArrayPool<char>.Shared.Return(chars);
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
    }

    /// <summary>
    /// Decompresses a concatenation of independent Zstandard frames.
    /// Frames are located by their magic header and unwrapped one by one, so a
    /// truncated tail frame (a live append in progress) is tolerated and the
    /// completed prefix is still returned.
    /// </summary>
    private static bool ReadDecodedTranscript(
        string file,
        Action<string> consumeDecoded,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(consumeDecoded);
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        var frame = new MemoryStream();
        var magicCandidate = new byte[4];
        var magicCandidateLength = 0;
        var frameStarted = false;
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var bytesRead = stream.Read(buffer, 0, buffer.Length);
                if (bytesRead <= 0)
                {
                    break;
                }

                for (var index = 0; index < bytesRead; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var value = buffer[index];
                    if (!frameStarted)
                    {
                        if (value == ZstdMagicAt(magicCandidateLength))
                        {
                            magicCandidate[magicCandidateLength++] = value;
                        }
                        else
                        {
                            magicCandidateLength = value == ZstdMagic0 ? 1 : 0;
                            if (magicCandidateLength == 1)
                            {
                                magicCandidate[0] = value;
                            }
                        }

                        if (magicCandidateLength == 4)
                        {
                            frame.Write(magicCandidate, 0, magicCandidateLength);
                            magicCandidateLength = 0;
                            frameStarted = true;
                        }

                        continue;
                    }

                    if (value == ZstdMagicAt(magicCandidateLength))
                    {
                        magicCandidate[magicCandidateLength++] = value;
                        if (magicCandidateLength == 4)
                        {
                            if (!TryConsumeFrame(frame, consumeDecoded, cancellationToken))
                            {
                                return false;
                            }

                            frame.Dispose();
                            frame = new MemoryStream();
                            frame.Write(magicCandidate, 0, magicCandidateLength);
                            magicCandidateLength = 0;
                        }

                        continue;
                    }

                    if (magicCandidateLength > 0)
                    {
                        frame.Write(magicCandidate, 0, magicCandidateLength);
                        magicCandidateLength = 0;
                    }

                    if (value == ZstdMagic0)
                    {
                        magicCandidate[0] = value;
                        magicCandidateLength = 1;
                    }
                    else
                    {
                        frame.WriteByte(value);
                    }
                }
            }

            if (!frameStarted)
            {
                return false;
            }

            if (magicCandidateLength > 0)
            {
                frame.Write(magicCandidate, 0, magicCandidateLength);
            }

            // A truncated final frame throws here; all previous complete
            // frames have already been consumed and remain usable.
            return TryConsumeFrame(frame, consumeDecoded, cancellationToken);
        }
        finally
        {
            frame.Dispose();
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static bool TryConsumeFrame(
        MemoryStream frame,
        Action<string> consumeDecoded,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var decompressor = new Decompressor();
            var unwrapped = decompressor.Unwrap(
                frame.GetBuffer().AsSpan(0, checked((int)frame.Length)));
            if (unwrapped.Length > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                consumeDecoded(Encoding.UTF8.GetString(unwrapped.ToArray()));
            }

            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Incomplete or corrupt frame: keep the successfully decoded
            // prefix. Dsh appends frames atomically, so a partial frame
            // only appears while a write is in flight.
            return false;
        }
    }

    private static byte ZstdMagicAt(int index)
    {
        return index switch
        {
            0 => ZstdMagic0,
            1 => ZstdMagic1,
            2 => ZstdMagic2,
            3 => ZstdMagic3,
            _ => 0
        };
    }

    private static DshUsageEntry? TryReadLine(
        string line,
        string sessionId,
        DateTimeOffset startLocal,
        DateTimeOffset endLocal,
        DshTranscriptContext context)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;

            if (StringEquals(root, "type", "request/context"))
            {
                context.ModelId = root.TryGetProperty("data", out var requestContext)
                    ? GetString(requestContext, "model") : null;
                return null;
            }

            if (StringEquals(root, "type", "request/header"))
            {
                context.ModelId = root.TryGetProperty("data", out var headerData) &&
                                  headerData.TryGetProperty("header", out var header) &&
                                  header.TryGetProperty("config", out var config)
                    ? GetString(config, "model") : null;
                return null;
            }

            if (!TryGetUsageSettlement(root, context, out var usage, out var timeMs, out var seq))
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

            // The bucket pipeline computes Uncached = input - cached - cacheWrite, so the
            // input figure must include every billed input component (same
            // convention as ClaudeUsageReader). Both transcript generations
            // report inputTokens as the uncached remainder; cacheWrite is
            // usually 0 for DeepSeek, but stays inside input for forward
            // compatibility.
            var input = TokenCountMath.AddNonNegative(
                TokenCountMath.AddNonNegative(inputTokens, cacheRead),
                cacheWrite);

            // (session id, seq) is the stable per-session event identity; seq
            // is contiguous within a transcript, so re-scans never duplicate.
            return new DshUsageEntry(
                $"{sessionId}:{seq}",
                timestamp,
                input,
                cacheRead,
                cacheWrite,
                output,
                reasoning,
                context.ModelId);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Extracts the token accounting of one assistant settlement, accepting
    /// both transcript generations: current-format `assistant/message`
    /// (`data.usage`, the same sample the harness folds into its own
    /// tokenUsage projection) and released v0–v3
    /// `assistant/chunk { "type": "usage" }`. A settlement in the current
    /// format also carries the route that produced it, which keeps model
    /// attribution correct even when no request record precedes it.
    /// </summary>
    private static bool TryGetUsageSettlement(
        JsonElement root,
        DshTranscriptContext context,
        out JsonElement usage,
        out long timeMs,
        out long seq)
    {
        usage = default;
        timeMs = 0;
        seq = 0;

        if (!root.TryGetProperty("time", out var timeElement) ||
            timeElement.ValueKind != JsonValueKind.Number ||
            !timeElement.TryGetInt64(out timeMs) ||
            !root.TryGetProperty("seq", out var seqElement) ||
            !seqElement.TryGetInt64(out seq) ||
            !root.TryGetProperty("data", out var data))
        {
            return false;
        }

        if (StringEquals(root, "type", "assistant/message"))
        {
            if (!data.TryGetProperty("usage", out usage) || usage.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var modelId = data.TryGetProperty("message", out var message) &&
                          message.TryGetProperty("source", out var source)
                ? GetString(source, "model")
                : null;
            if (modelId is not null)
            {
                context.ModelId = modelId;
            }

            return true;
        }

        return StringEquals(root, "type", "assistant/chunk") &&
               data.TryGetProperty("chunk", out var chunk) &&
               StringEquals(chunk, "type", "usage") &&
               chunk.TryGetProperty("usage", out usage) &&
               usage.ValueKind == JsonValueKind.Object;
    }

    private static string? GetString(JsonElement element, string property)
    {
        return element.ValueKind == JsonValueKind.Object &&
               element.TryGetProperty(property, out var value) &&
               value.ValueKind == JsonValueKind.String &&
               !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!.Trim()
            : null;
    }

    private sealed class DshTranscriptContext
    {
        public string? ModelId { get; set; }
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
