namespace CodexTokenMonitor;

/// <summary>
/// Reads Kimi desktop/Code wire.jsonl usage.record entries. In protocol 1.4,
/// usageScope=turn records are per-call deltas, including calls in failed turns.
/// inputOther, inputCacheRead and inputCacheCreation are disjoint input counts.
/// Context occupancy and desktop segment totals are not additional usage.
/// </summary>
internal sealed class KimiUsageReader : IUsageSourceReader
{
    internal const string CacheFolder = "KimiTokenMonitor";
    public UsageSource Source => UsageSource.Kimi;
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

    public IReadOnlyList<DateTimeOffset> GetIncompleteHistoricalDays(DateTimeOffset startInclusive,
        DateTimeOffset endInclusive, CancellationToken cancellationToken = default) =>
        UsageCacheStore.GetIncompleteDays(CacheFolder, startInclusive, endInclusive, cancellationToken);

    public TokenUsageSummary ReadCachedRange(DateTimeOffset startLocal, DateTimeOffset endLocal,
        CancellationToken cancellationToken = default) =>
        UsageCacheStore.Load(CacheFolder).ReadRange(startLocal, endLocal, cancellationToken);

    public IReadOnlyList<TokenUsageBucket> ReadCachedDetailRows(DateTimeOffset startLocal, DateTimeOffset endLocal,
        CancellationToken cancellationToken = default) =>
        UsageCacheStore.Load(CacheFolder).ReadDetailRows(startLocal, endLocal, cancellationToken);

    public TokenUsageSummary ReadRange(DateTimeOffset startLocal, DateTimeOffset endLocal, bool includeLiveToday,
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

    public DailyUsageSnapshot ReadDay(DateTimeOffset startLocal, DateTimeOffset endLocal, bool includeLiveToday,
        CancellationToken cancellationToken = default)
    {
        var rows = includeLiveToday
            ? ReadDetailRows(startLocal, endLocal, true, cancellationToken)
            : ReadCachedDetailRows(startLocal, endLocal, cancellationToken);
        return new(UsageSummaryBuilder.FromRows(startLocal, endLocal, rows), rows);
    }

    public IReadOnlyList<TokenUsageBucket> ReadDetailRows(DateTimeOffset startLocal, DateTimeOffset endLocal,
        bool includeLiveToday, CancellationToken cancellationToken = default)
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
                events.AddRange(cached);
            else
                pending.Add(day);
        }

        if (pending.Count > 0)
        {
            // Scan each file once per range. Re-read live days from the start:
            // writers can flush records timestamped before the last refresh.
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

    public IReadOnlyList<TokenUsageBucket> ReadTransientDetailRows(DateTimeOffset startLocal,
        DateTimeOffset endLocal, CancellationToken cancellationToken = default) =>
        ToRows(ReadEvents(startLocal, endLocal, cancellationToken).Events);

    public void WarmHistoricalDay(DateTimeOffset dayStart, CancellationToken cancellationToken = default) =>
        WarmHistoricalDays(new[] { dayStart }, cancellationToken);

    public void WarmHistoricalDays(IEnumerable<DateTimeOffset> daysLocal,
        CancellationToken cancellationToken = default, Action<DateTimeOffset>? dayCompleted = null,
        Action<int, int>? fileProgress = null) =>
        HistoricalUsageBatchWarmer.WarmDays(CacheFolder, daysLocal,
            (start, end, token) => ReadEvents(start, end, token, fileProgress), cancellationToken, dayCompleted);

    internal static UsageEventScanResult ReadEvents(DateTimeOffset start, DateTimeOffset end,
        CancellationToken cancellationToken = default, Action<int, int>? fileProgress = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var events = new List<TokenUsageEvent>();
        if (start >= end) return new(events, true);
        var complete = true;
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in GetLogRoots())
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                foreach (var file in Directory.EnumerateFiles(root, "wire.jsonl", new EnumerationOptions
                {
                    RecurseSubdirectories = true, IgnoreInaccessible = false,
                    AttributesToSkip = FileAttributes.ReparsePoint
                }))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    files.Add(file);
                }
            }
            catch (DirectoryNotFoundException) { }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                complete = false;
                CacheOperationDiagnostics.Report(root, "Read Kimi sessions", ex);
            }
        }
        fileProgress?.Invoke(0, files.Count);
        var completed = 0;
        foreach (var file in files)
        {
            complete &= ReadFile(file, start, end, events, cancellationToken);
            fileProgress?.Invoke(++completed, files.Count);
        }
        return new(UsageEventMerger.Merge(events), complete);
    }

    private static IEnumerable<string> GetLogRoots()
    {
        if (UsageLogPaths.GetOverrideRoot(UsageSource.Kimi) is { } isolated)
        {
            yield return isolated;
            yield break;
        }
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".kimi-code", "sessions");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "kimi-desktop", "daimon-share", "daimon", "runtime", "kimi-code", "home", "sessions");
    }

    private static bool ReadFile(string file, DateTimeOffset start, DateTimeOffset end,
        List<TokenUsageEvent> events, CancellationToken cancellationToken)
    {
        var complete = true;
        var agentDirectory = Directory.GetParent(file);
        var identity = $"{agentDirectory?.Parent?.Parent?.Name}/{agentDirectory?.Name}";
        string? requestIdentity = null;
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!line.Contains("usage.record", StringComparison.Ordinal) &&
                    !line.Contains("llm.request", StringComparison.Ordinal)) continue;
                try
                {
                    using var document = JsonDocument.Parse(line);
                    var root = document.RootElement;
                    if (root.ValueKind != JsonValueKind.Object) continue;
                    var type = String(root, "type");
                    if (type == "llm.request")
                    {
                        requestIdentity = Number(root, "time", out var requestTime)
                            ? $"{String(root, "turnStep")}/{requestTime}" : null;
                        continue;
                    }
                    // Exclude usage copied inside tool output and prompts.
                    if (type != "usage.record") continue;
                    if (String(root, "usageScope") != "turn" || !Number(root, "time", out var time) ||
                        !root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object ||
                        !Number(usage, "inputOther", out var other) || !Number(usage, "output", out var output) ||
                        !OptionalNumber(usage, "inputCacheRead", out var cached) ||
                        !OptionalNumber(usage, "inputCacheCreation", out var written))
                    {
                        complete = false;
                        continue;
                    }
                    var timestamp = DateTimeOffset.FromUnixTimeMilliseconds(time).ToOffset(CodexUsageReader.BeijingOffset);
                    if (timestamp < start || timestamp >= end) continue;
                    var input = TokenCountMath.AddNonNegative(TokenCountMath.AddNonNegative(other, cached), written);
                    if (input == 0 && output == 0) continue;
                    events.Add(new(timestamp, input, cached, output, 0,
                        TokenCountMath.AddNonNegative(input, output),
                        $"kimi:{identity}:{requestIdentity}:{time}", written, ModelId: String(root, "model")));
                }
                catch (Exception ex) when (ex is JsonException or ArgumentOutOfRangeException)
                {
                    // Retry a partially flushed tail instead of sealing history.
                    complete = false;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            CacheOperationDiagnostics.Report(file, "Read Kimi usage", ex);
            return false;
        }
        return complete;
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
        return !element.TryGetProperty(name, out _) || Number(element, name, out number);
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
