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
    long Reasoning)
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
            summary.Add(
                usageEvent.Timestamp,
                usageEvent.InputTokens,
                usageEvent.CachedInputTokens,
                usageEvent.CacheWriteInputTokens,
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
                usageEvent.CacheWriteInputTokens,
                usageEvent.OutputTokens,
                usageEvent.ReasoningOutputTokens,
                usageEvent.TotalTokens);
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
                item.CacheWrite))
            .ToList();
        return new UsageEventScanResult(events, isComplete);
    }

    private static IEnumerable<string> EnumerateTranscriptFiles(DateTimeOffset startLocal)
    {
        var sessionsRoot = UsageLogPaths.GetOverrideRoot(UsageSource.Dsh) ?? OverrideSessionsRoot ??
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
            var decodedCompletely = ReadDecodedTranscript(file, decoded =>
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
                    cancellationToken);
                pendingLine = text[(lastNewLine + 1)..];
            }, cancellationToken);

            if (pendingLine.Length > 0)
            {
                ConsumeLine(pendingLine, sessionId, startLocal, endLocal, entries, cancellationToken);
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
        CancellationToken cancellationToken)
    {
        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } line)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ConsumeLine(line, sessionId, startLocal, endLocal, entries, cancellationToken);
        }
    }

    private static void ConsumeLine(
        string line,
        string sessionId,
        DateTimeOffset startLocal,
        DateTimeOffset endLocal,
        Dictionary<string, DshUsageEntry> entries,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!line.Contains("\"usage\"", StringComparison.Ordinal))
        {
            return;
        }

        var entry = TryReadUsageLine(line, sessionId, startLocal, endLocal);
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

            // The bucket pipeline computes Uncached = input - cached - cacheWrite, so the
            // input figure must include every billed input component (same
            // convention as ClaudeUsageReader). cacheWrite is usually 0 for
            // DeepSeek, but keep it inside input for forward compatibility.
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
                reasoning);
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
                    item.CacheWriteInputTokens,
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
                item.CacheWriteInputTokens,
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
