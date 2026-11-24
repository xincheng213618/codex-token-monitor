using System.Globalization;
using System.Text.Json;

namespace CodexTokenMonitor;

/// <summary>
/// Reads local Gemini usage across Google Antigravity (AGY) conversation transcripts
/// (~/.gemini/antigravity/brain/&lt;id&gt;/.system_generated/logs/transcript.jsonl) and
/// Gemini CLI session logs (~/.gemini/tmp/&lt;hash&gt;/chats/session-*.json[l]).
/// </summary>
internal sealed class GeminiUsageReader : IUsageSourceReader
{
    internal const string CacheFolder = "GeminiTokenMonitor";

    public UsageSource Source => UsageSource.Gemini;
    public string Title => UsageSourceRegistry.For(Source).Title;
    public bool SupportsQuota => false;

    public bool ClearCache() => UsageCacheStore.Delete(CacheFolder);

    public bool RefreshCachedDay(DateOnly date, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var deleted = UsageCacheStore.DeleteDay(CacheFolder, date);
        var start = new DateTimeOffset(date.Year, date.Month, date.Day, 0, 0, 0, CodexUsageReader.BeijingOffset);
        var now = DateTimeOffset.UtcNow.ToOffset(CodexUsageReader.BeijingOffset);
        _ = ReadDetailRows(start, start.AddDays(1) < now ? start.AddDays(1) : now, true, cancellationToken);
        return deleted;
    }

    public IReadOnlyList<DateTimeOffset> GetIncompleteHistoricalDays(
        DateTimeOffset startInclusive,
        DateTimeOffset endInclusive,
        CancellationToken cancellationToken = default) =>
        UsageCacheStore.GetIncompleteDays(CacheFolder, startInclusive, endInclusive, cancellationToken);

    public TokenUsageSummary ReadCachedRange(
        DateTimeOffset startLocal,
        DateTimeOffset endLocal,
        CancellationToken cancellationToken = default) =>
        UsageCacheStore.Load(CacheFolder).ReadRange(startLocal, endLocal, cancellationToken);

    public IReadOnlyList<TokenUsageBucket> ReadCachedDetailRows(
        DateTimeOffset startLocal,
        DateTimeOffset endLocal,
        CancellationToken cancellationToken = default) =>
        UsageCacheStore.Load(CacheFolder).ReadDetailRows(startLocal, endLocal, cancellationToken);

    internal CodexRhythmTokens ReadRhythmTokens(
        CodexUsageRhythm rhythm, TimeSpan reportingOffset, CancellationToken token) =>
        UsageCacheStore.Load(CacheFolder).ReadRhythmTokens(rhythm, reportingOffset, token);

    internal long? ReadRhythmTodayTotal(DateTimeOffset cutoff, TimeSpan reportingOffset, CancellationToken token)
    {
        var localCutoff = cutoff.ToOffset(reportingOffset);
        var start = new DateTimeOffset(localCutoff.Date, reportingOffset).ToOffset(CodexUsageReader.BeijingOffset);
        var end = cutoff.ToOffset(CodexUsageReader.BeijingOffset);
        if (start >= end) return null;

        for (var day = StartOfDay(start); day < end; day = day.AddDays(1))
        {
            var dayEnd = day.AddDays(1) < end ? day.AddDays(1) : end;
            _ = ReadDetailRows(day, dayEnd, includeLiveToday: true, cancellationToken: token);
        }

        return UsageCacheStore.Load(CacheFolder).ReadRhythmTokenTotal(start, end, token);
    }

    public TokenUsageSummary ReadRange(
        DateTimeOffset startLocal,
        DateTimeOffset endLocal,
        bool includeLiveToday,
        CancellationToken cancellationToken = default)
    {
        var rows = ReadDetailRows(startLocal, endLocal, includeLiveToday, cancellationToken);
        var summary = UsageSummaryBuilder.FromRows(startLocal, endLocal, rows);
        foreach (var day in rows.GroupBy(row => StartOfDay(row.StartLocal)))
        {
            var bucket = new TokenUsageBucket { StartLocal = day.Key };
            foreach (var row in day) bucket.MergeFrom(row);
            summary.DailyBuckets.Add(bucket);
        }
        return summary;
    }

    public DailyUsageSnapshot ReadDay(
        DateTimeOffset startLocal,
        DateTimeOffset endLocal,
        bool includeLiveToday,
        CancellationToken cancellationToken = default)
    {
        var rows = includeLiveToday
            ? ReadDetailRows(startLocal, endLocal, true, cancellationToken)
            : ReadCachedDetailRows(startLocal, endLocal, cancellationToken);
        return new(UsageSummaryBuilder.FromRows(startLocal, endLocal, rows), rows);
    }

    public IReadOnlyList<TokenUsageBucket> ReadDetailRows(
        DateTimeOffset startLocal,
        DateTimeOffset endLocal,
        bool includeLiveToday,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (startLocal >= endLocal) return Array.Empty<TokenUsageBucket>();
        startLocal = startLocal.ToOffset(CodexUsageReader.BeijingOffset);
        endLocal = endLocal.ToOffset(CodexUsageReader.BeijingOffset);
        var today = StartOfDay(DateTimeOffset.UtcNow);
        var cache = UsageCacheStore.Load(CacheFolder);
        var pending = new List<DateTimeOffset>();
        var events = new List<TokenUsageEvent>();
        for (var day = StartOfDay(startLocal); day < endLocal; day = day.AddDays(1))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var date = DateOnly.FromDateTime(day.DateTime);
            var cached = cache.GetDetailEvents(date, cancellationToken);
            if (day >= today && !includeLiveToday ||
                day < today && cache.TryGetRecord(date, out var record) && record.IsComplete &&
                record.IsValid && record.Events == cached.Count)
            {
                events.AddRange(cached);
            }
            else
            {
                pending.Add(day);
            }
        }

        if (pending.Count > 0)
        {
            var scanEnd = pending[^1] < today ? pending[^1].AddDays(1) : endLocal;
            var scan = ReadEvents(pending[0], scanEnd, cancellationToken);
            var byDay = scan.Events.GroupBy(item => StartOfDay(item.Timestamp))
                .ToDictionary(group => group.Key, group => group.ToList());
            foreach (var day in pending)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var date = DateOnly.FromDateTime(day.DateTime);
                byDay.TryGetValue(day, out var found);
                var merged = UsageEventMerger.Merge(cache.GetDetailEvents(date, cancellationToken)
                    .Concat(found ?? Enumerable.Empty<TokenUsageEvent>()));
                var bucket = new TokenUsageBucket { StartLocal = day };
                foreach (var item in merged) bucket.Add(item);
                var through = (day.AddDays(1) < scanEnd ? day.AddDays(1) : scanEnd).AddTicks(-1);
                cache.Put(bucket, day < today && scan.IsComplete, through, merged,
                    replaceDetailEvents: true, cancellationToken: cancellationToken);
                events.AddRange(merged);
            }
        }

        return ToRows(events.Where(item => item.Timestamp >= startLocal && item.Timestamp < endLocal));
    }

    public IReadOnlyList<TokenUsageBucket> ReadTransientDetailRows(
        DateTimeOffset startLocal,
        DateTimeOffset endLocal,
        CancellationToken cancellationToken = default) =>
        ToRows(ReadEvents(startLocal, endLocal, cancellationToken).Events);

    internal static (IReadOnlyList<TokenUsageBucket> Rows, bool IsComplete) ReadTransientDetailRowsWithStatus(
        DateTimeOffset startLocal,
        DateTimeOffset endLocal,
        CancellationToken cancellationToken = default,
        string? homeOverride = null)
    {
        var scan = ReadEvents(startLocal, endLocal, cancellationToken, homeOverride: homeOverride);
        return (ToRows(scan.Events), scan.IsComplete);
    }

    public void WarmHistoricalDay(DateTimeOffset dayStart, CancellationToken cancellationToken = default) =>
        WarmHistoricalDays(new[] { dayStart }, cancellationToken);

    public void WarmHistoricalDays(
        IEnumerable<DateTimeOffset> daysLocal,
        CancellationToken cancellationToken = default,
        Action<DateTimeOffset>? dayCompleted = null,
        Action<int, int>? fileProgress = null) =>
        HistoricalUsageBatchWarmer.WarmDays(
            CacheFolder,
            daysLocal,
            (start, end, token) => ReadEvents(start, end, token, fileProgress),
            cancellationToken,
            dayCompleted);

    internal static string GetGeminiHome(string? homeOverride = null)
    {
        if (!string.IsNullOrWhiteSpace(homeOverride))
            return Path.GetFullPath(homeOverride);
        if (UsageLogPaths.GetOverrideRoot(UsageSource.Gemini) is { } isolated)
            return isolated;
        if (Environment.GetEnvironmentVariable("GEMINI_HOME") is { Length: > 0 } envHome)
            return envHome;
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gemini");
    }

    internal static IReadOnlyList<string> GetLogRoots(string? homeOverride = null)
    {
        if (!string.IsNullOrWhiteSpace(homeOverride))
            return [Path.GetFullPath(homeOverride)];
        if (UsageLogPaths.GetOverrideRoot(UsageSource.Gemini) is { } isolated)
            return [isolated];

        var geminiHome = GetGeminiHome();
        var roots = new List<string>(2);
        var agyHome = Environment.GetEnvironmentVariable("ANTIGRAVITY_HOME");
        roots.Add(!string.IsNullOrWhiteSpace(agyHome)
            ? Path.Combine(agyHome, "brain")
            : Path.Combine(geminiHome, "antigravity", "brain"));
        roots.Add(Path.Combine(geminiHome, "tmp"));
        return roots;
    }

    internal static (IReadOnlyList<string> Files, bool Complete) DiscoverLogFiles(
        IEnumerable<string> roots,
        CancellationToken cancellationToken = default,
        bool reportDiagnostics = true)
    {
        var rawFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var complete = true;
        foreach (var root in roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DiscoverSubtree(root, 0, rawFiles, ref complete, cancellationToken, reportDiagnostics);
        }

        // Antigravity writes both transcript.jsonl (compact) and transcript_full.jsonl
        // in the same .system_generated/logs directory with identical token headers.
        // Prefer transcript.jsonl so we never double-count or parse multi-megabyte tool payloads.
        var transcriptDirsWithCompact = rawFiles
            .Where(path => Path.GetFileName(path).Equals("transcript.jsonl", StringComparison.OrdinalIgnoreCase))
            .Select(path => Path.GetDirectoryName(path) ?? "")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var selected = rawFiles
            .Where(path =>
                !Path.GetFileName(path).Equals("transcript_full.jsonl", StringComparison.OrdinalIgnoreCase) ||
                !transcriptDirsWithCompact.Contains(Path.GetDirectoryName(path) ?? ""))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return (selected, complete);
    }

    internal static bool IsCandidateLogFile(string path)
    {
        var name = Path.GetFileName(path);
        if (name.Equals("transcript.jsonl", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("transcript_full.jsonl", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return name.StartsWith("session-", StringComparison.OrdinalIgnoreCase) &&
               (name.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase));
    }

    private static void DiscoverSubtree(
        string directory,
        int depth,
        HashSet<string> files,
        ref bool complete,
        CancellationToken cancellationToken,
        bool reportDiagnostics)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (depth > 10 || !Directory.Exists(directory)) return;

        try
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) return;

            foreach (var file in Directory.EnumerateFiles(directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (IsCandidateLogFile(file))
                {
                    files.Add(file);
                }
            }

            foreach (var sub in Directory.EnumerateDirectories(directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var dirName = Path.GetFileName(sub);
                // Skip non-log directories under Antigravity conversations (steps, artifacts, browser_sandbox, node_modules).
                if (dirName.Equals("steps", StringComparison.OrdinalIgnoreCase) ||
                    dirName.Equals("browser_sandbox", StringComparison.OrdinalIgnoreCase) ||
                    dirName.Equals("scratch", StringComparison.OrdinalIgnoreCase) ||
                    dirName.Equals("node_modules", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                DiscoverSubtree(sub, depth + 1, files, ref complete, cancellationToken, reportDiagnostics);
            }
        }
        catch (DirectoryNotFoundException) { }
        catch (FileNotFoundException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Keep scanning sibling conversations; do not freeze historical days when one folder is locked/inaccessible.
            complete = false;
            if (reportDiagnostics)
            {
                // Inaccessible sibling directories (such as OS-protected profiles) should not fail the whole query,
                // only prevent premature IsComplete sealing on historical days.
            }
        }
    }

    internal static UsageEventScanResult ReadEvents(
        DateTimeOffset start,
        DateTimeOffset end,
        CancellationToken cancellationToken = default,
        Action<int, int>? fileProgress = null,
        string? homeOverride = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var events = new List<TokenUsageEvent>();
        if (start >= end) return new(events, true);

        start = start.ToOffset(CodexUsageReader.BeijingOffset);
        end = end.ToOffset(CodexUsageReader.BeijingOffset);

        var (files, complete) = DiscoverLogFiles(GetLogRoots(homeOverride), cancellationToken);
        fileProgress?.Invoke(0, files.Count);
        var completed = 0;
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            complete &= ReadFile(file, start, end, events, cancellationToken);
            fileProgress?.Invoke(++completed, files.Count);
        }

        return new(UsageEventMerger.Merge(events), complete);
    }

    private static bool ReadFile(
        string file,
        DateTimeOffset start,
        DateTimeOffset end,
        List<TokenUsageEvent> events,
        CancellationToken cancellationToken)
    {
        var name = Path.GetFileName(file);
        if (name.StartsWith("session-", StringComparison.OrdinalIgnoreCase) &&
            name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            return ReadGeminiCliJsonFile(file, start, end, events, cancellationToken);
        }

        if (name.StartsWith("session-", StringComparison.OrdinalIgnoreCase) &&
            name.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase))
        {
            return ReadGeminiCliJsonlFile(file, start, end, events, cancellationToken);
        }

        return ReadAntigravityTranscriptFile(file, start, end, events, cancellationToken);
    }

    internal static string ExtractAntigravityConversationId(string file)
    {
        // Expected layout: .../<conversation-id>/.system_generated/logs/transcript.jsonl
        var logsDir = Directory.GetParent(file);
        if (logsDir?.Name.Equals("logs", StringComparison.OrdinalIgnoreCase) == true &&
            logsDir.Parent?.Name.Equals(".system_generated", StringComparison.OrdinalIgnoreCase) == true &&
            logsDir.Parent.Parent is { Name.Length: > 0 } convDir)
        {
            return convDir.Name;
        }

        return logsDir?.Name ?? Path.GetFileNameWithoutExtension(file);
    }

    private static bool ReadAntigravityTranscriptFile(
        string file,
        DateTimeOffset start,
        DateTimeOffset end,
        List<TokenUsageEvent> events,
        CancellationToken cancellationToken)
    {
        var complete = true;
        var conversationId = ExtractAntigravityConversationId(file);
        var lineIndex = 0;
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
            {
                cancellationToken.ThrowIfCancellationRequested();
                lineIndex++;
                if (string.IsNullOrWhiteSpace(line) ||
                    !line.Contains("PLANNER_RESPONSE", StringComparison.Ordinal) &&
                    !line.Contains("CHECKPOINT", StringComparison.Ordinal))
                {
                    continue;
                }

                try
                {
                    using var document = JsonDocument.Parse(line);
                    var root = document.RootElement;
                    if (root.ValueKind != JsonValueKind.Object) continue;
                    // Antigravity records context compaction as a separate token-bearing step.
                    if (String(root, "type") is not ("PLANNER_RESPONSE" or "CHECKPOINT")) continue;

                    if (!TryParseTimestamp(root, "created_at", out var timestamp))
                    {
                        complete = false;
                        continue;
                    }

                    if (!OptionalNumber(root, "input_tokens", out var uncached) ||
                        !OptionalNumber(root, "cache_read_tokens", out var cachedRead) ||
                        !OptionalNumber(root, "cached_tokens", out var altCached) ||
                        !OptionalNumber(root, "cache_write_tokens", out var cacheWrite) ||
                        !OptionalNumber(root, "output_tokens", out var rawOutput) ||
                        !OptionalNumber(root, "thoughts_token_count", out var thoughts1) ||
                        !OptionalNumber(root, "reasoning_tokens", out var thoughts2) ||
                        !OptionalNumber(root, "thinking_tokens", out var thoughts3))
                    {
                        complete = false;
                        continue;
                    }

                    if (timestamp < start || timestamp >= end) continue;

                    var cached = Math.Max(cachedRead, altCached);
                    var input = TokenCountMath.AddNonNegative(TokenCountMath.AddNonNegative(uncached, cached), cacheWrite);
                    var reasoning = Math.Max(thoughts1, Math.Max(thoughts2, thoughts3));
                    var output = Math.Max(rawOutput, reasoning);
                    if (input == 0 && output == 0) continue;

                    var stepKey = Number(root, "step_index", out var stepIndex)
                        ? stepIndex.ToString(CultureInfo.InvariantCulture)
                        : $"{lineIndex}:{timestamp.UtcTicks}";

                    events.Add(new TokenUsageEvent(
                        timestamp,
                        input,
                        cached,
                        output,
                        reasoning,
                        TokenCountMath.AddNonNegative(input, output),
                        $"agy:{conversationId}:{stepKey}",
                        cacheWrite,
                        ModelId: String(root, "model")));
                }
                catch (Exception ex) when (ex is JsonException or ArgumentOutOfRangeException)
                {
                    // Partially flushed tail line; retry on subsequent scan.
                    complete = false;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        return complete;
    }

    private static bool ReadGeminiCliJsonFile(
        string file,
        DateTimeOffset start,
        DateTimeOffset end,
        List<TokenUsageEvent> events,
        CancellationToken cancellationToken)
    {
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var document = JsonDocument.Parse(stream);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return true;

            var sessionId = String(root, "sessionId") ?? Path.GetFileNameWithoutExtension(file);
            var fallbackModel = String(root, "model");
            DateTimeOffset? sessionFallbackTime = null;
            if (TryParseTimestamp(root, "lastUpdated", out var updated)) sessionFallbackTime = updated;
            else if (TryParseTimestamp(root, "startTime", out var started)) sessionFallbackTime = started;

            if (!root.TryGetProperty("messages", out var messages) || messages.ValueKind != JsonValueKind.Array)
                return true;

            var index = 0;
            foreach (var message in messages.EnumerateArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var currentIndex = index++;
                if (message.ValueKind != JsonValueKind.Object) continue;
                TryAddGeminiCliMessage(message, sessionId, currentIndex, fallbackModel, sessionFallbackTime, start, end, events);
            }

            return true;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentOutOfRangeException)
        {
            // A session-*.json rewrite in progress may be momentarily incomplete.
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool ReadGeminiCliJsonlFile(
        string file,
        DateTimeOffset start,
        DateTimeOffset end,
        List<TokenUsageEvent> events,
        CancellationToken cancellationToken)
    {
        var complete = true;
        var sessionId = Path.GetFileNameWithoutExtension(file);
        string? activeModel = null;
        var lineIndex = 0;
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var currentIndex = lineIndex++;
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    using var document = JsonDocument.Parse(line);
                    var root = document.RootElement;
                    if (root.ValueKind != JsonValueKind.Object) continue;
                    sessionId = String(root, "sessionId") ?? sessionId;
                    activeModel = String(root, "model") ?? activeModel;
                    TryAddGeminiCliMessage(root, sessionId, currentIndex, activeModel, null, start, end, events);
                }
                catch (Exception ex) when (ex is JsonException or ArgumentOutOfRangeException)
                {
                    complete = false;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        return complete;
    }

    private static void TryAddGeminiCliMessage(
        JsonElement message,
        string sessionId,
        int index,
        string? fallbackModel,
        DateTimeOffset? fallbackTime,
        DateTimeOffset start,
        DateTimeOffset end,
        List<TokenUsageEvent> events)
    {
        var type = String(message, "type") ?? String(message, "role");
        if (type is not null &&
            !type.Equals("gemini", StringComparison.OrdinalIgnoreCase) &&
            !type.Equals("model", StringComparison.OrdinalIgnoreCase) &&
            !type.Equals("assistant", StringComparison.OrdinalIgnoreCase) &&
            !type.Equals("usage", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!TryExtractGeminiCliTokens(message, out var input, out var cached, out var cacheWrite, out var output, out var reasoning, out var total))
            return;

        DateTimeOffset timestamp;
        if (TryParseTimestamp(message, "timestamp", out var ts) ||
            TryParseTimestamp(message, "createdAt", out ts) ||
            TryParseTimestamp(message, "created_at", out ts))
        {
            timestamp = ts;
        }
        else if (fallbackTime is { } fallback)
        {
            timestamp = fallback;
        }
        else
        {
            return;
        }

        if (timestamp < start || timestamp >= end) return;
        if (input == 0 && output == 0) return;

        var model = String(message, "model") ?? fallbackModel;
        var messageId = String(message, "id") ?? String(message, "uuid") ?? index.ToString(CultureInfo.InvariantCulture);
        events.Add(new TokenUsageEvent(
            timestamp,
            input,
            cached,
            output,
            reasoning,
            total,
            $"gcli:{sessionId}:{messageId}",
            cacheWrite,
            ModelId: model));
    }

    internal static bool TryExtractGeminiCliTokens(
        JsonElement message,
        out long input,
        out long cached,
        out long cacheWrite,
        out long output,
        out long reasoning,
        out long total)
    {
        input = cached = cacheWrite = output = reasoning = total = 0;

        if (message.TryGetProperty("tokens", out var tokens) && tokens.ValueKind == JsonValueKind.Object)
        {
            if (!OptionalNumber(tokens, "input", out var promptInput) ||
                !OptionalNumber(tokens, "output", out var candidatesOutput) ||
                !OptionalNumber(tokens, "cached", out cached) ||
                !OptionalNumber(tokens, "thoughts", out reasoning) ||
                !OptionalNumber(tokens, "tool", out var toolInput) ||
                !OptionalNumber(tokens, "total", out var rawTotal))
            {
                return false;
            }

            var baseInput = promptInput >= cached
                ? promptInput
                : TokenCountMath.AddNonNegative(promptInput, cached);
            input = TokenCountMath.AddNonNegative(baseInput, toolInput);
            output = ComputeGeminiCliOutput(input, candidatesOutput, reasoning, rawTotal);
            total = Math.Max(rawTotal, TokenCountMath.AddNonNegative(input, output));
            return input > 0 || output > 0 || rawTotal > 0;
        }

        if (message.TryGetProperty("usageMetadata", out var meta) && meta.ValueKind == JsonValueKind.Object)
        {
            if (!OptionalNumber(meta, "promptTokenCount", out var promptInput) ||
                !OptionalNumber(meta, "candidatesTokenCount", out var candidatesOutput) ||
                !OptionalNumber(meta, "cachedContentTokenCount", out cached) ||
                !OptionalNumber(meta, "thoughtsTokenCount", out reasoning) ||
                !OptionalNumber(meta, "toolUsePromptTokenCount", out var toolInput) ||
                !OptionalNumber(meta, "totalTokenCount", out var rawTotal))
            {
                return false;
            }

            var baseInput = promptInput >= cached
                ? promptInput
                : TokenCountMath.AddNonNegative(promptInput, cached);
            input = TokenCountMath.AddNonNegative(baseInput, toolInput);
            output = ComputeGeminiCliOutput(input, candidatesOutput, reasoning, rawTotal);
            total = Math.Max(rawTotal, TokenCountMath.AddNonNegative(input, output));
            return input > 0 || output > 0 || rawTotal > 0;
        }

        return false;
    }

    private static long ComputeGeminiCliOutput(long input, long candidatesOutput, long thoughts, long rawTotal)
    {
        if (thoughts <= 0) return candidatesOutput;
        // Gemini API reports candidatesTokenCount separately from thoughtsTokenCount
        // while billing both as output tokens (total = prompt + candidates + thoughts + tool).
        // If a custom wrapper already folded thoughts into output, avoid double-counting.
        if (candidatesOutput >= thoughts && rawTotal > 0 &&
            rawTotal <= TokenCountMath.AddNonNegative(input, candidatesOutput))
        {
            return candidatesOutput;
        }

        return TokenCountMath.AddNonNegative(candidatesOutput, thoughts);
    }

    internal static bool TryParseTimestamp(JsonElement element, string propertyName, out DateTimeOffset timestamp)
    {
        timestamp = default;
        if (!element.TryGetProperty(propertyName, out var prop)) return false;
        if (prop.ValueKind == JsonValueKind.String && prop.GetString() is { Length: > 0 } text &&
            DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
        {
            timestamp = parsed.ToOffset(CodexUsageReader.BeijingOffset);
            return true;
        }

        if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt64(out var raw) && raw > 0)
        {
            timestamp = (raw > 10_000_000_000L
                    ? DateTimeOffset.FromUnixTimeMilliseconds(raw)
                    : DateTimeOffset.FromUnixTimeSeconds(raw))
                .ToOffset(CodexUsageReader.BeijingOffset);
            return true;
        }

        return false;
    }

    private static string? String(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool Number(JsonElement element, string name, out long number)
    {
        number = 0;
        return element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number &&
            value.TryGetInt64(out number) && number >= 0;
    }

    private static bool OptionalNumber(JsonElement element, string name, out long number)
    {
        number = 0;
        if (!element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
            return true;
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out number) && number >= 0;
    }

    private static DateTimeOffset StartOfDay(DateTimeOffset value)
    {
        value = value.ToOffset(CodexUsageReader.BeijingOffset);
        return new(value.Year, value.Month, value.Day, 0, 0, 0, CodexUsageReader.BeijingOffset);
    }

    private static IReadOnlyList<TokenUsageBucket> ToRows(IEnumerable<TokenUsageEvent> events) =>
        UsageEventMerger.Merge(events).Select(item =>
        {
            var bucket = new TokenUsageBucket { StartLocal = item.Timestamp };
            bucket.Add(item);
            return bucket;
        }).ToList();
}
