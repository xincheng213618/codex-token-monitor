using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodexTokenMonitor;

internal sealed record GeminiRateLimitWindow(
    double UsedPercent,
    DateTimeOffset ResetLocal,
    DateTimeOffset? ObservedLocal = null,
    ClaudeQuotaReadingSource Source = ClaudeQuotaReadingSource.Official,
    int? ResetPrecisionSeconds = null);

internal sealed record GeminiRateLimitSnapshot(
    DateTimeOffset RecordedLocal,
    GeminiRateLimitWindow? FiveHour,
    GeminiRateLimitWindow? SevenDay);

internal sealed record GeminiWeeklyQuotaObservation(
    DateTimeOffset ResetAt,
    DateTimeOffset ObservedAt,
    double UsedPercent,
    ClaudeQuotaReadingSource Source = ClaudeQuotaReadingSource.Official)
{
    public static GeminiWeeklyQuotaObservation? FromSnapshot(GeminiRateLimitSnapshot snapshot)
    {
        if (snapshot.SevenDay is not { } week) return null;
        var reset = GeminiQuotaCycleHistory.NormalizeReset(week.ResetLocal);
        return new(reset, week.ObservedLocal ?? snapshot.RecordedLocal, week.UsedPercent, week.Source);
    }
}

/// <summary>
/// Persists Gemini 5h / 7d quota observations recorded by Scripts/gemini-statusline.ps1
/// (Antigravity CLI statusLine hook) or entered in the Gemini quota detail window.
/// </summary>
internal static class GeminiRateLimitLog
{
    private static readonly object CurrentGate = new();

    public static string CurrentFilePath =>
        Path.Combine(MonitorCachePaths.LocalAppData, GeminiUsageReader.CacheFolder, "official-quota.json");

    public static string FilePath =>
        Path.Combine(MonitorCachePaths.LocalAppData, GeminiUsageReader.CacheFolder, "rate-limits.jsonl");

    public static void SaveCurrent(GeminiRateLimitSnapshot snapshot)
    {
        lock (CurrentGate) SaveCurrentCore(snapshot);
    }

    private static void SaveCurrentCore(GeminiRateLimitSnapshot snapshot)
    {
        object? Window(GeminiRateLimitWindow? window) => window is null ? null : new
        {
            used_percentage = window.UsedPercent,
            resets_at = window.ResetLocal.ToUnixTimeSeconds(),
            observed_at = (window.ObservedLocal ?? snapshot.RecordedLocal).ToUnixTimeSeconds(),
            source = window.Source.ToString(),
            reset_precision_seconds = window.ResetPrecisionSeconds
        };

        var temp = CurrentFilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CurrentFilePath)!);
            var history = ReadWeeklyHistory();
            if (GeminiWeeklyQuotaObservation.FromSnapshot(snapshot) is { } current)
            {
                history = MergeWeeklyObservations(history.Append(current));
            }

            var existing = ReadLatestFromCurrentFile();
            var fiveHour = Newer(snapshot.FiveHour, existing?.FiveHour);
            var sevenDay = Newer(snapshot.SevenDay, existing?.SevenDay);

            File.WriteAllText(temp, JsonSerializer.Serialize(new
            {
                recorded_at = snapshot.RecordedLocal.ToUnixTimeSeconds(),
                five_hour = Window(fiveHour),
                seven_day = Window(sevenDay),
                weekly_history = history
            }));
            File.Move(temp, CurrentFilePath, overwrite: true);
            AppendHistorySnapshot(snapshot, fiveHour, sevenDay);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static GeminiWeeklyQuotaObservation[] MergeWeeklyObservations(IEnumerable<GeminiWeeklyQuotaObservation> source)
    {
        var ordered = source
            .Where(reading => reading is not null && reading.UsedPercent is >= 0 and <= 100 &&
                              reading.ResetAt > DateTimeOffset.UnixEpoch.AddDays(7))
            .OrderBy(reading => reading.ObservedAt)
            .ToList();
        var merged = new List<GeminiWeeklyQuotaObservation>(ordered.Count);
        foreach (var item in ordered)
        {
            var normalized = item with { ResetAt = GeminiQuotaCycleHistory.NormalizeReset(item.ResetAt) };
            if (merged.Count > 0)
            {
                var last = merged[^1];
                if (last.ResetAt == normalized.ResetAt &&
                    Math.Abs(last.UsedPercent - normalized.UsedPercent) < 0.01 &&
                    (normalized.ObservedAt - last.ObservedAt).Duration() <= TimeSpan.FromMinutes(1))
                {
                    merged[^1] = normalized.ObservedAt >= last.ObservedAt ? normalized : last;
                    continue;
                }
            }

            merged.Add(normalized);
        }

        return merged.Count > 500 ? merged.TakeLast(500).ToArray() : merged.ToArray();
    }

    private static void AppendHistorySnapshot(
        GeminiRateLimitSnapshot incoming,
        GeminiRateLimitWindow? mergedFiveHour,
        GeminiRateLimitWindow? mergedSevenDay)
    {
        object? Window(GeminiRateLimitWindow? window) => window is null ? null : new
        {
            used_percentage = window.UsedPercent,
            resets_at = window.ResetLocal.ToUnixTimeSeconds(),
            observed_at = (window.ObservedLocal ?? incoming.RecordedLocal).ToUnixTimeSeconds(),
            source = window.Source.ToString(),
            reset_precision_seconds = window.ResetPrecisionSeconds
        };

        try
        {
            if (File.Exists(FilePath))
            {
                var lastLine = File.ReadLines(FilePath).LastOrDefault(line => !string.IsNullOrWhiteSpace(line));
                if (lastLine is not null && Parse([lastLine]).LastOrDefault() is { } prev)
                {
                    var sameFive = SameWindowState(prev.FiveHour, incoming.FiveHour ?? mergedFiveHour);
                    var sameSeven = SameWindowState(prev.SevenDay, incoming.SevenDay ?? mergedSevenDay);
                    if (sameFive && sameSeven &&
                        (incoming.RecordedLocal - prev.RecordedLocal).Duration() <= TimeSpan.FromSeconds(5))
                    {
                        return;
                    }
                }
            }

            var line = JsonSerializer.Serialize(new
            {
                recorded_at = incoming.RecordedLocal.ToUnixTimeSeconds(),
                five_hour = Window(incoming.FiveHour ?? mergedFiveHour),
                seven_day = Window(incoming.SevenDay ?? mergedSevenDay)
            });
            File.AppendAllText(FilePath, line + "\n");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static bool SameWindowState(GeminiRateLimitWindow? first, GeminiRateLimitWindow? second)
    {
        if (first is null || second is null) return first is null && second is null;
        return Math.Abs(first.UsedPercent - second.UsedPercent) < 0.01 &&
               (first.ResetLocal - second.ResetLocal).Duration() <= TimeSpan.FromSeconds(60);
    }

    private static GeminiRateLimitWindow? Newer(GeminiRateLimitWindow? incoming, GeminiRateLimitWindow? existing) =>
        incoming is null || existing?.ObservedLocal > incoming.ObservedLocal ? existing : incoming;

    private static GeminiRateLimitSnapshot? ReadLatestFromCurrentFile()
    {
        try
        {
            if (!File.Exists(CurrentFilePath)) return null;
            return Parse([File.ReadAllText(CurrentFilePath)]).LastOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static IReadOnlyList<GeminiWeeklyQuotaObservation> ReadWeeklyHistory()
    {
        try
        {
            if (!File.Exists(CurrentFilePath)) return [];
            var json = File.ReadAllText(CurrentFilePath);
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return [];
            if (document.RootElement.TryGetProperty("weekly_history", out var history))
            {
                return (history.Deserialize<GeminiWeeklyQuotaObservation[]>() ?? [])
                    .Where(reading => reading is not null && reading.UsedPercent is >= 0 and <= 100 &&
                        reading.ResetAt > DateTimeOffset.UnixEpoch.AddDays(7) && reading.ObservedAt < reading.ResetAt &&
                        reading.ObservedAt >= reading.ResetAt.AddDays(-7)).ToArray();
            }

            return Parse([json]).FirstOrDefault() is { } snapshot &&
                GeminiWeeklyQuotaObservation.FromSnapshot(snapshot) is { } current ? [current] : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentOutOfRangeException)
        {
            return [];
        }
    }

    public static GeminiRateLimitSnapshot? ReadLatest(DateTimeOffset nowLocal) =>
        Latest(ReadAllSnapshots(), nowLocal);

    public static IReadOnlyList<GeminiRateLimitSnapshot> ReadAllSnapshots()
    {
        var lines = new List<string>();
        try
        {
            if (File.Exists(CurrentFilePath)) lines.Add(File.ReadAllText(CurrentFilePath));
            if (File.Exists(FilePath))
            {
                using var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                lines.AddRange(reader.ReadToEnd().Split('\n'));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        return Parse(lines);
    }

    public static GeminiRateLimitSnapshot? Latest(IEnumerable<GeminiRateLimitSnapshot> snapshots, DateTimeOffset nowLocal)
    {
        var list = snapshots.ToList();
        GeminiRateLimitWindow? LatestWindow(Func<GeminiRateLimitSnapshot, GeminiRateLimitWindow?> select) => list
            .Select(item => select(item) is { } window ? window with { ObservedLocal = window.ObservedLocal ?? item.RecordedLocal } : null)
            .OfType<GeminiRateLimitWindow>()
            .Where(window => window.ResetLocal > nowLocal)
            .OrderByDescending(window => window.ObservedLocal)
            .ThenBy(window => window.ResetPrecisionSeconds ?? 0)
            .FirstOrDefault();
        var fiveHour = LatestWindow(item => item.FiveHour);
        var sevenDay = LatestWindow(item => item.SevenDay);
        if (fiveHour is null && sevenDay is null)
            return null;

        return new GeminiRateLimitSnapshot(new[] { fiveHour?.ObservedLocal, sevenDay?.ObservedLocal }.Max()!.Value,
            fiveHour, sevenDay);
    }

    public static IReadOnlyList<GeminiRateLimitSnapshot> Parse(IEnumerable<string> lines)
    {
        var result = new List<GeminiRateLimitSnapshot>();
        foreach (var line in lines)
        {
            var text = line.Trim().TrimStart('\uFEFF');
            if (text.Length == 0) continue;

            try
            {
                using var document = JsonDocument.Parse(text);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object) continue;

                var recordedLocal = BeijingClock.Now;
                if (root.TryGetProperty("recorded_at", out var recorded))
                {
                    if (recorded.ValueKind == JsonValueKind.Number && recorded.TryGetInt64(out var recordedSeconds))
                        recordedLocal = ToLocal(recordedSeconds);
                    else if (recorded.ValueKind == JsonValueKind.String &&
                             DateTimeOffset.TryParse(recorded.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsedRec))
                        recordedLocal = parsedRec.ToOffset(CodexUsageReader.BeijingOffset);
                }

                var five = ReadWindow(root, "five_hour", recordedLocal);
                var week = ReadWindow(root, "seven_day", recordedLocal);

                // Also accept raw Antigravity CLI statusLine payload ("quota": { "gemini_5h": { "remaining_fraction": 0.78, "reset_time": "..." } }).
                if (five is null && week is null &&
                    root.TryGetProperty("quota", out var quotaObj) && quotaObj.ValueKind == JsonValueKind.Object)
                {
                    ParseAntigravityQuotaBuckets(quotaObj, recordedLocal, out five, out week);
                }

                if (five is not null || week is not null)
                    result.Add(new GeminiRateLimitSnapshot(recordedLocal, five, week));
            }
            catch (JsonException) { }
        }

        return result;
    }

    private static void ParseAntigravityQuotaBuckets(
        JsonElement quotaObj,
        DateTimeOffset recordedLocal,
        out GeminiRateLimitWindow? fiveHour,
        out GeminiRateLimitWindow? sevenDay)
    {
        fiveHour = null;
        sevenDay = null;
        foreach (var prop in quotaObj.EnumerateObject())
        {
            var kind = prop.Name.ToLowerInvariant().Replace('_', '-') switch
            {
                "gemini-5h" or "gemini-5-hour" or "gemini-five-hour" => ClaudeQuotaWindowKind.FiveHour,
                "gemini-7d" or "gemini-weekly" or "gemini-seven-day" => ClaudeQuotaWindowKind.Week,
                _ => (ClaudeQuotaWindowKind?)null
            };
            if (kind is null) continue;
            if (prop.Value.ValueKind != JsonValueKind.Object)
                continue;
            if (prop.Value.TryGetProperty("disabled", out var dis) && dis.ValueKind == JsonValueKind.True)
                continue;

            double? usedPercent = null;
            if (prop.Value.TryGetProperty("remaining_fraction", out var rem) && rem.ValueKind == JsonValueKind.Number && rem.TryGetDouble(out var frac))
                usedPercent = Math.Clamp((1.0 - frac) * 100.0, 0.0, 100.0);
            else if (prop.Value.TryGetProperty("remainingFraction", out var rem2) && rem2.ValueKind == JsonValueKind.Number && rem2.TryGetDouble(out var frac2))
                usedPercent = Math.Clamp((1.0 - frac2) * 100.0, 0.0, 100.0);
            else if (prop.Value.TryGetProperty("used_percentage", out var usedProp) && usedProp.ValueKind == JsonValueKind.Number && usedProp.TryGetDouble(out var u))
                usedPercent = Math.Clamp(u, 0.0, 100.0);

            if (usedPercent is null) continue;

            DateTimeOffset? resetLocal = null;
            if ((prop.Value.TryGetProperty("reset_time", out var rt) || prop.Value.TryGetProperty("resetTime", out rt)) &&
                rt.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(rt.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsedReset))
            {
                resetLocal = parsedReset.ToOffset(CodexUsageReader.BeijingOffset);
            }
            else if (prop.Value.TryGetProperty("reset_in_seconds", out var ris) && ris.ValueKind == JsonValueKind.Number && ris.TryGetInt64(out var secs) && secs > 0)
            {
                resetLocal = recordedLocal.AddSeconds(secs);
            }

            if (resetLocal is null) continue;
            var candidate = new GeminiRateLimitWindow(usedPercent.Value, resetLocal.Value, recordedLocal, ClaudeQuotaReadingSource.Official);
            if (kind == ClaudeQuotaWindowKind.FiveHour) fiveHour ??= candidate;
            else sevenDay ??= candidate;
        }
    }

    private static GeminiRateLimitWindow? ReadWindow(JsonElement root, string name, DateTimeOffset defaultObserved)
    {
        if (!root.TryGetProperty(name, out var window) || window.ValueKind != JsonValueKind.Object)
            return null;

        double usedPercent;
        if (window.TryGetProperty("used_percentage", out var used) && used.ValueKind == JsonValueKind.Number && used.TryGetDouble(out var u))
            usedPercent = Math.Clamp(u, 0, 100);
        else if (window.TryGetProperty("remaining_fraction", out var rem) && rem.ValueKind == JsonValueKind.Number && rem.TryGetDouble(out var f))
            usedPercent = Math.Clamp((1.0 - f) * 100.0, 0, 100);
        else
            return null;

        DateTimeOffset resetLocal;
        if (window.TryGetProperty("resets_at", out var reset) && reset.ValueKind == JsonValueKind.Number && reset.TryGetInt64(out var resetSeconds))
            resetLocal = ToLocal(resetSeconds);
        else if ((window.TryGetProperty("reset_time", out var resetStr) || window.TryGetProperty("resets_at", out resetStr)) &&
                 resetStr.ValueKind == JsonValueKind.String &&
                 DateTimeOffset.TryParse(resetStr.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsedReset))
            resetLocal = parsedReset.ToOffset(CodexUsageReader.BeijingOffset);
        else
            return null;

        var observed = defaultObserved;
        if (window.TryGetProperty("observed_at", out var obs) && obs.ValueKind == JsonValueKind.Number && obs.TryGetInt64(out var obsSec))
            observed = ToLocal(obsSec);

        var source = ClaudeQuotaReadingSource.Official;
        if (window.TryGetProperty("source", out var srcProp) && srcProp.ValueKind == JsonValueKind.String &&
            Enum.TryParse<ClaudeQuotaReadingSource>(srcProp.GetString(), ignoreCase: true, out var parsedSource))
        {
            source = parsedSource;
        }

        int? precision = window.TryGetProperty("reset_precision_seconds", out var precisionProp) &&
            precisionProp.ValueKind == JsonValueKind.Number && precisionProp.TryGetInt32(out var seconds) && seconds > 0
            ? seconds : null;
        return new GeminiRateLimitWindow(usedPercent, resetLocal, observed, source, precision);
    }

    private static DateTimeOffset ToLocal(long epochSeconds) =>
        DateTimeOffset.FromUnixTimeSeconds(epochSeconds).ToOffset(CodexUsageReader.BeijingOffset);
}

internal sealed record GeminiQuotaWindowUsage(
    ClaudeQuotaWindowKind Kind,
    ClaudeWindowSource Source,
    DateTimeOffset StartLocal,
    DateTimeOffset ResetLocal,
    TokenUsageBucket Usage,
    double? ReportedUsedPercent,
    bool IsComplete,
    DateTimeOffset? ObservedLocal = null,
    TokenUsageBucket? UsageAtObservation = null,
    int? ResetPrecisionSeconds = null,
    ClaudeQuotaReadingSource ReadingSource = ClaudeQuotaReadingSource.Official)
{
    public TokenUsageBucket GeminiUsage =>
        GeminiQuotaEstimator.GeminiModelUsage(new[] { Usage }, StartLocal, DateTimeOffset.MaxValue);

    public string ReadingLabel => ReadingSource switch
    {
        ClaudeQuotaReadingSource.Desktop => "桌面读数",
        ClaudeQuotaReadingSource.Manual => "手动登记",
        _ => "CLI 同步"
    };

    public string ResetLabel => ResetPrecisionSeconds is null
        ? ResetLocal.ToString("MM-dd HH:mm")
        : $"约 {ResetLocal.AddSeconds(30):MM-dd HH:mm}";
}

internal static class GeminiQuotaEstimator
{
    public const double ReliablePercent = 5;

    public static TimeSpan Length(ClaudeQuotaWindowKind kind) =>
        kind == ClaudeQuotaWindowKind.FiveHour ? TimeSpan.FromHours(5) : TimeSpan.FromDays(7);

    public static bool IsGeminiModel(string modelId) =>
        modelId.StartsWith("gemini", StringComparison.OrdinalIgnoreCase) ||
        modelId.StartsWith("google/", StringComparison.OrdinalIgnoreCase);

    public static TokenUsageBucket GeminiModelUsage(
        IEnumerable<TokenUsageBucket> rows,
        DateTimeOffset startLocal,
        DateTimeOffset endLocal)
    {
        var usage = new TokenUsageBucket { StartLocal = startLocal };
        foreach (var row in rows.Where(row => row.StartLocal >= startLocal && row.StartLocal < endLocal))
        {
            if (row.ModelUsage.Count == 0)
            {
                usage.MergeFrom(row);
                continue;
            }

            foreach (var (model, modelUsage) in row.ModelUsage)
            {
                if (!IsGeminiModel(model)) continue;
                var single = new TokenUsageBucket { StartLocal = startLocal };
                single.ModelUsage[model] = modelUsage;
                single.MergeFrom(modelUsage);
                usage.MergeFrom(single);
            }
        }

        return usage;
    }

    public static decimal UsdCost(TokenUsageBucket usage) =>
        CodexModelCost.Estimate(usage, PricePresetGroups.Gemini).CostTotals
            .Where(total => total.CurrencySymbol == "$")
            .Sum(total => total.Amount);

    public static ClaudeQuotaReading CreateReading(
        ClaudeQuotaWindowKind kind,
        double usedPercent,
        DateTimeOffset resetLocal,
        DateTimeOffset nowLocal,
        IEnumerable<TokenUsageBucket> rows,
        ClaudeQuotaReadingSource source = ClaudeQuotaReadingSource.Manual)
    {
        var start = resetLocal - Length(kind);
        var end = nowLocal < resetLocal ? nowLocal.AddTicks(1) : resetLocal;
        var usage = GeminiModelUsage(rows, start, end);
        return new ClaudeQuotaReading(
            kind,
            nowLocal,
            Math.Clamp(usedPercent, 0, 100),
            start,
            resetLocal,
            UsdCost(usage),
            usage.TotalTokens,
            usage.Events,
            source);
    }

    public static ClaudeQuotaReading ReadWindow(
        ClaudeQuotaWindowKind kind,
        double usedPercent,
        DateTimeOffset resetLocal,
        DateTimeOffset nowLocal,
        ClaudeQuotaReadingSource source = ClaudeQuotaReadingSource.Manual,
        CancellationToken cancellationToken = default,
        string? homeOverride = null)
    {
        var start = resetLocal - Length(kind);
        var (rows, _) = GeminiUsageReader.ReadTransientDetailRowsWithStatus(start, nowLocal.AddMinutes(1), cancellationToken, homeOverride);
        return CreateReading(kind, usedPercent, resetLocal, nowLocal, rows, source);
    }

    public static bool RecordSnapshot(GeminiActivitySnapshot snapshot, IReadOnlyList<ClaudeQuotaReading> readings)
    {
        var changed = false;
        foreach (var window in new[] { snapshot.FiveHourWindow, snapshot.WeekWindow })
        {
            if (window is not { ReportedUsedPercent: { } percent, IsComplete: true } || percent < 1)
                continue;

            var gemini = window.UsageAtObservation is { } captured
                ? GeminiModelUsage(new[] { captured }, window.StartLocal, DateTimeOffset.MaxValue)
                : window.GeminiUsage;
            var cost = UsdCost(gemini);
            if (gemini.TotalTokens <= 0) continue;
            var existing = readings.LastOrDefault(item =>
                item.Kind == window.Kind &&
                SameWindow(item.ResetLocal, window.ResetLocal) &&
                SameWindow(item.WindowStartLocal, window.StartLocal));
            if (existing is not null && existing.UsedPercent >= percent &&
                (existing.RecordedLocal > (window.ObservedLocal ?? snapshot.NowLocal) ||
                 existing.RecordedLocal == (window.ObservedLocal ?? snapshot.NowLocal) && existing.Source == window.ReadingSource))
                continue;

            var reading = new ClaudeQuotaReading(
                window.Kind,
                window.ObservedLocal ?? snapshot.NowLocal,
                percent,
                window.StartLocal,
                window.ResetLocal,
                cost,
                gemini.TotalTokens,
                gemini.Events,
                window.ReadingSource);
            GeminiQuotaReadingStore.Replace(existing, reading);
            changed = true;
        }

        return changed;
    }

    private static bool SameWindow(DateTimeOffset first, DateTimeOffset second) =>
        Math.Abs((first - second).TotalMinutes) < 2;
}

internal static class GeminiQuotaReferenceCalculator
{
    public static ClaudeQuotaReference? Estimate(
        ClaudeQuotaWindowKind kind,
        GeminiQuotaWindowUsage? window,
        IReadOnlyList<ClaudeQuotaReading> readings)
    {
        if (window is { ReportedUsedPercent: >= 1 and <= 100 } &&
            window.ObservedLocal is { } observed)
        {
            var sample = window.UsageAtObservation ?? window.Usage;
            var usage = GeminiQuotaEstimator.GeminiModelUsage(new[] { sample }, window.StartLocal, DateTimeOffset.MaxValue);
            var cost = GeminiQuotaEstimator.UsdCost(usage);
            if (usage.TotalTokens > 0)
            {
                return Calculate(cost, usage.TotalTokens, window.ReportedUsedPercent.Value, observed,
                    isCurrentWindow: true, isIncomplete: !window.IsComplete || window.ResetPrecisionSeconds is not null);
            }
        }

        // Fall back to historical calibrated readings (prefer >= 5% used, then most recent).
        var valid = readings
            .Where(item => item.Kind == kind && item.UsedPercent >= 1 && item.UsedPercent <= 100 && item.Tokens > 0)
            .ToList();
        if (valid.Count == 0) return null;

        var reliable = valid.Where(item => item.UsedPercent >= GeminiQuotaEstimator.ReliablePercent).ToList();
        var chosen = (reliable.Count > 0 ? reliable : valid).MaxBy(item => item.RecordedLocal)!;
        return Calculate(chosen.CostUsd, chosen.Tokens, chosen.UsedPercent, chosen.RecordedLocal,
            isCurrentWindow: false, isIncomplete: false);
    }

    private static ClaudeQuotaReference Calculate(
        decimal cost, long tokens, double percent, DateTimeOffset at, bool isCurrentWindow, bool isIncomplete)
    {
        var fraction = (decimal)percent / 100m;
        return new ClaudeQuotaReference(
            cost / fraction,
            (long)Math.Min(long.MaxValue, tokens / fraction),
            cost,
            tokens,
            percent,
            at,
            isCurrentWindow,
            isIncomplete);
    }
}

internal static class GeminiQuotaReadingStore
{
    private const int MaxReadings = 500;
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private static string FilePath =>
        Path.Combine(MonitorCachePaths.LocalAppData, GeminiUsageReader.CacheFolder, "quota-readings.json");

    public static IReadOnlyList<ClaudeQuotaReading> Load()
    {
        lock (Gate)
        {
            try
            {
                return File.Exists(FilePath)
                    ? JsonSerializer.Deserialize<List<ClaudeQuotaReading>>(File.ReadAllText(FilePath), Options) ?? []
                    : [];
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                return [];
            }
        }
    }

    public static void Add(ClaudeQuotaReading reading) => Update(list => list.Add(reading));

    public static void Replace(ClaudeQuotaReading? previous, ClaudeQuotaReading reading) => Update(list =>
    {
        if (previous is not null) list.Remove(previous);
        list.Add(reading);
    });

    private static void Update(Action<List<ClaudeQuotaReading>> change)
    {
        lock (Gate)
        {
            List<ClaudeQuotaReading> list;
            try
            {
                list = File.Exists(FilePath)
                    ? JsonSerializer.Deserialize<List<ClaudeQuotaReading>>(File.ReadAllText(FilePath), Options) ?? []
                    : [];
            }
            catch (JsonException)
            {
                File.Move(FilePath, FilePath + $".corrupt-{DateTime.Now:yyyyMMddHHmmss}", overwrite: true);
                list = [];
            }

            change(list);
            list = list.OrderBy(item => item.RecordedLocal).TakeLast(MaxReadings).ToList();
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var temp = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(list, Options));
            File.Move(temp, FilePath, overwrite: true);
        }
    }
}

internal sealed record GeminiQuotaCycleUsage(
    DateTimeOffset Start,
    DateTimeOffset End,
    DateTimeOffset Reset,
    bool IsCurrent,
    int SnapshotCount,
    double? MaxUsedPercent,
    long? Tokens,
    long? Requests,
    decimal? ApiCostUsd,
    long UnpricedTokens,
    bool UsageIsPartial,
    DateTimeOffset? UsageThrough,
    GeminiWeeklyQuotaObservation? Observation,
    decimal? FullReferenceUsd)
{
    public CodexQuotaCycle ToCodexCycle() => new(
        Start,
        End,
        Reset,
        SnapshotCount,
        MaxUsedPercent is { } u ? (decimal)Math.Clamp(u, 0.0, 100.0) : null,
        IsCurrent);
}

internal static class GeminiQuotaCycleHistory
{
    private static readonly TimeSpan Week = TimeSpan.FromDays(7);
    private static readonly TimeSpan ResetShiftSplitThreshold = TimeSpan.FromHours(2);
    private const double ResetDropSplitThresholdPercent = 2.0;

    public static DateTimeOffset NormalizeReset(DateTimeOffset reset)
    {
        var local = reset.ToOffset(CodexUsageReader.BeijingOffset);
        var seconds = local.TimeOfDay.Ticks % TimeSpan.TicksPerMinute;
        if (seconds <= TimeSpan.TicksPerSecond) return local.AddTicks(-seconds);
        var toMinute = TimeSpan.TicksPerMinute - seconds;
        return toMinute <= TimeSpan.TicksPerSecond ? local.AddTicks(toMinute) : local;
    }

    public static IReadOnlyList<GeminiWeeklyQuotaObservation> ReadWeekObservations(DateTimeOffset upToLocal)
    {
        var candidates = new List<GeminiWeeklyQuotaObservation>();

        foreach (var snap in GeminiRateLimitLog.ReadAllSnapshots())
        {
            if (GeminiWeeklyQuotaObservation.FromSnapshot(snap) is { } obs &&
                obs.UsedPercent is >= 0 and <= 100 &&
                obs.ObservedAt <= upToLocal.AddMinutes(1))
            {
                candidates.Add(obs);
            }
        }

        foreach (var obs in GeminiRateLimitLog.ReadWeeklyHistory())
        {
            if (obs.ObservedAt <= upToLocal.AddMinutes(1))
            {
                candidates.Add(obs with { ResetAt = NormalizeReset(obs.ResetAt) });
            }
        }

        foreach (var sample in GeminiQuotaReadingStore.Load())
        {
            if (IsWeekReading(sample) && sample.RecordedLocal <= upToLocal.AddMinutes(1))
            {
                candidates.Add(new GeminiWeeklyQuotaObservation(
                    NormalizeReset(sample.ResetLocal),
                    sample.RecordedLocal,
                    sample.UsedPercent,
                    sample.Source));
            }
        }

        var ordered = candidates
            .Where(o => o.ResetAt > DateTimeOffset.UnixEpoch.AddDays(7) &&
                        o.ObservedAt < o.ResetAt.AddMinutes(5) &&
                        o.ObservedAt >= o.ResetAt - Week - TimeSpan.FromHours(2))
            .OrderBy(o => o.ObservedAt)
            .ToList();

        var deduped = new List<GeminiWeeklyQuotaObservation>(ordered.Count);
        foreach (var item in ordered)
        {
            if (deduped.Count > 0)
            {
                var prev = deduped[^1];
                if ((item.ObservedAt - prev.ObservedAt).Duration() <= TimeSpan.FromSeconds(5) &&
                    Math.Abs(item.UsedPercent - prev.UsedPercent) < 0.01 &&
                    (item.ResetAt - prev.ResetAt).Duration() <= TimeSpan.FromMinutes(2))
                {
                    if (prev.Source != ClaudeQuotaReadingSource.Official && item.Source == ClaudeQuotaReadingSource.Official)
                        deduped[^1] = item;
                    continue;
                }
            }

            deduped.Add(item);
        }

        return deduped;
    }

    public static DateTimeOffset ResolveActiveWeekStart(
        DateTimeOffset resetLocal,
        double? usedPercent,
        DateTimeOffset? observedLocal,
        DateTimeOffset nowLocal)
    {
        var nominalStart = resetLocal - Week;
        if (usedPercent is null) return nominalStart;
        var observations = ReadWeekObservations(nowLocal)
            .Where(o => o.ObservedAt >= nominalStart && o.ObservedAt <= (observedLocal ?? nowLocal).AddSeconds(5))
            .ToList();
        if (observations.Count < 2) return nominalStart;

        var splitStart = nominalStart;
        for (var i = 1; i < observations.Count; i++)
        {
            if (StartsNewCycle(observations[i - 1], observations[i]))
            {
                var nextNominal = observations[i].ResetAt - Week;
                splitStart = nextNominal > observations[i - 1].ObservedAt && nextNominal <= observations[i].ObservedAt
                    ? nextNominal
                    : observations[i].ObservedAt;
            }
        }

        return splitStart > nominalStart && splitStart < resetLocal ? splitStart : nominalStart;
    }

    public static IReadOnlyList<GeminiQuotaCycleUsage> Read(
        DateTimeOffset now,
        CancellationToken cancellationToken = default,
        string? homeOverride = null)
    {
        var observations = ReadWeekObservations(now);
        var readings = GeminiQuotaReadingStore.Load();
        if (observations.Count == 0 && !readings.Any(IsWeekReading))
            return [];

        var (rows, complete) = GeminiUsageReader.ReadTransientDetailRowsWithStatus(
            DateTimeOffset.UnixEpoch, now, cancellationToken, homeOverride);
        return Build(now, observations, readings, rows, complete, cancellationToken);
    }

    internal static IReadOnlyList<GeminiQuotaCycleUsage> Build(
        DateTimeOffset now,
        IReadOnlyList<GeminiWeeklyQuotaObservation> observations,
        IReadOnlyList<ClaudeQuotaReading> readings,
        IReadOnlyList<TokenUsageBucket> rows,
        bool complete,
        CancellationToken cancellationToken = default)
    {
        var mergedObs = observations
            .Concat(readings.Where(r => IsWeekReading(r) && r.RecordedLocal <= now)
                .Select(r => new GeminiWeeklyQuotaObservation(NormalizeReset(r.ResetLocal), r.RecordedLocal, r.UsedPercent, r.Source)))
            .Where(o => o.ObservedAt <= now.AddMinutes(1))
            .OrderBy(o => o.ObservedAt)
            .ToList();

        var timeline = new List<GeminiWeeklyQuotaObservation>(mergedObs.Count);
        foreach (var item in mergedObs)
        {
            var normalized = item with { ResetAt = NormalizeReset(item.ResetAt) };
            if (timeline.Count > 0)
            {
                var prev = timeline[^1];
                if ((normalized.ObservedAt - prev.ObservedAt).Duration() <= TimeSpan.FromSeconds(5) &&
                    Math.Abs(normalized.UsedPercent - prev.UsedPercent) < 0.01 &&
                    (normalized.ResetAt - prev.ResetAt).Duration() <= TimeSpan.FromMinutes(2))
                {
                    continue;
                }
            }
            timeline.Add(normalized);
        }

        if (timeline.Count == 0) return [];

        // Split timeline into actual cycles (supporting early/mid-week resets and natural resets).
        var clusters = new List<List<GeminiWeeklyQuotaObservation>> { new() { timeline[0] } };
        for (var i = 1; i < timeline.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (StartsNewCycle(timeline[i - 1], timeline[i]))
                clusters.Add([timeline[i]]);
            else
                clusters[^1].Add(timeline[i]);
        }

        var sortedRows = rows.OrderBy(r => r.StartLocal).ToList();
        var specs = new List<(DateTimeOffset Start, DateTimeOffset End, DateTimeOffset Reset, bool IsCurrent, List<GeminiWeeklyQuotaObservation> Obs)>();

        // 1. Pre-observation historical 7d windows (only when local Gemini usage exists).
        var firstClusterReset = clusters[0][^1].ResetAt;
        var firstNominalStart = firstClusterReset - Week;
        var firstStart = firstNominalStart < clusters[0][0].ObservedAt ? firstNominalStart : clusters[0][0].ObservedAt;
        if (sortedRows.Count > 0 && sortedRows[0].StartLocal < firstStart)
        {
            foreach (var group in sortedRows.Where(r => r.StartLocal < firstStart).GroupBy(r => EndOfWeek(r.StartLocal, firstStart)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var wEnd = group.Key <= firstStart ? group.Key : firstStart;
                var wStart = wEnd - Week;
                var u = GeminiQuotaEstimator.GeminiModelUsage(group, wStart, wEnd);
                if (u.Events > 0)
                {
                    specs.Add((wStart, wEnd, wEnd, false, []));
                }
            }
        }

        // 2. Observed clusters (including early resets).
        var nextStart = firstStart;
        for (var c = 0; c < clusters.Count; c++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var cluster = clusters[c];
            var nominalReset = cluster[^1].ResetAt;
            var start = nextStart;

            if (c < clusters.Count - 1)
            {
                var nextCluster = clusters[c + 1];
                var nextFirst = nextCluster[0];
                var nextNominalStart = nextCluster[^1].ResetAt - Week;
                DateTimeOffset end;

                if (nominalReset <= nextFirst.ObservedAt.AddMinutes(2))
                {
                    // Natural reset boundary reached before next observation.
                    end = nominalReset;
                    specs.Add((start, end, nominalReset, false, cluster));

                    // Backfill any intermediate unrecorded 7d weeks that had local usage.
                    var gapEnd = nextNominalStart > end ? nextNominalStart : end;
                    if (gapEnd > end)
                    {
                        foreach (var group in sortedRows
                                     .Where(r => r.StartLocal >= end && r.StartLocal < gapEnd)
                                     .GroupBy(r => EndOfWeek(r.StartLocal, end)))
                        {
                            var gEnd = group.Key <= gapEnd ? group.Key : gapEnd;
                            var gStart = gEnd - Week < end ? end : gEnd - Week;
                            var u = GeminiQuotaEstimator.GeminiModelUsage(group, gStart, gEnd);
                            if (u.Events > 0)
                                specs.Add((gStart, gEnd, gEnd, false, []));
                        }
                    }

                    nextStart = nextNominalStart > end ? nextNominalStart : end;
                }
                else
                {
                    // Early / mid-week reset! Cut the previous cycle cleanly without losing it.
                    end = nextNominalStart > cluster[^1].ObservedAt && nextNominalStart <= nextFirst.ObservedAt
                        ? nextNominalStart
                        : nextFirst.ObservedAt;
                    if (end <= start) end = start.AddSeconds(1);
                    specs.Add((start, end, nominalReset, false, cluster));
                    nextStart = end;
                }
            }
            else
            {
                // Final observed cluster: either still active, or already expired naturally.
                if (nominalReset > now)
                {
                    specs.Add((start, nominalReset, nominalReset, true, cluster));
                }
                else
                {
                    specs.Add((start, nominalReset, nominalReset, false, cluster));
                    var projectedCurrentReset = EndOfWeek(now, nominalReset);
                    foreach (var group in sortedRows
                                 .Where(r => r.StartLocal >= nominalReset && r.StartLocal < projectedCurrentReset - Week)
                                 .GroupBy(r => EndOfWeek(r.StartLocal, nominalReset)))
                    {
                        var u = GeminiQuotaEstimator.GeminiModelUsage(group, group.Key - Week, group.Key);
                        if (u.Events > 0)
                            specs.Add((group.Key - Week, group.Key, group.Key, false, []));
                    }

                    specs.Add((projectedCurrentReset - Week, projectedCurrentReset, projectedCurrentReset, true, []));
                }
            }
        }

        var byResetSample = readings
            .Where(r => IsWeekReading(r) && r.RecordedLocal <= now)
            .ToList();

        var result = new List<GeminiQuotaCycleUsage>(specs.Count);
        foreach (var spec in specs.OrderByDescending(s => s.Start))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var effectiveEnd = spec.IsCurrent && now < spec.End ? now.AddTicks(1) : spec.End;
            var usage = GeminiQuotaEstimator.GeminiModelUsage(sortedRows, spec.Start, effectiveEnd);
            var hasUsage = usage.Events > 0 || usage.TotalTokens > 0;

            var latestObs = spec.Obs.Count > 0 ? spec.Obs[^1] : null;
            var maxUsed = spec.Obs.Count > 0 ? spec.Obs.Max(o => o.UsedPercent) : (double?)null;
            var matchingSample = byResetSample
                .Where(s => s.RecordedLocal >= spec.Start.AddMinutes(-2) && s.RecordedLocal <= spec.End.AddMinutes(2))
                .OrderByDescending(s => s.UsedPercent)
                .ThenByDescending(s => s.RecordedLocal)
                .FirstOrDefault();

            var priced = hasUsage ? CodexModelCost.Estimate(usage, PricePresetGroups.Gemini) : null;
            var cost = priced?.CostTotals.Where(t => t.CurrencySymbol == "$").Sum(t => t.Amount)
                       ?? (hasUsage ? 0m : matchingSample?.CostUsd);

            decimal? reference = null;
            var peakObs = spec.Obs
                .Where(o => o.UsedPercent >= 1.0)
                .OrderByDescending(o => o.UsedPercent >= GeminiQuotaEstimator.ReliablePercent ? 1 : 0)
                .ThenByDescending(o => o.UsedPercent)
                .ThenByDescending(o => o.ObservedAt)
                .FirstOrDefault();
            if (peakObs is not null)
            {
                var atObsUsage = GeminiQuotaEstimator.GeminiModelUsage(
                    sortedRows.Where(r => r.StartLocal >= spec.Start && r.StartLocal <= peakObs.ObservedAt),
                    spec.Start,
                    peakObs.ObservedAt.AddTicks(1));
                var atObsCost = GeminiQuotaEstimator.UsdCost(atObsUsage);
                if (atObsCost > 0m)
                {
                    reference = atObsCost * 100m / (decimal)peakObs.UsedPercent;
                }
                else if (cost is > 0m)
                {
                    reference = cost.Value * 100m / (decimal)peakObs.UsedPercent;
                }
            }
            reference ??= matchingSample?.CapacityUsd;

            if (!spec.IsCurrent && !hasUsage && spec.Obs.Count == 0 && matchingSample is null)
                continue;

            result.Add(new GeminiQuotaCycleUsage(
                spec.Start,
                spec.End,
                spec.Reset,
                spec.IsCurrent,
                spec.Obs.Count,
                maxUsed,
                hasUsage ? usage.TotalTokens : matchingSample?.Tokens,
                hasUsage ? usage.Events : matchingSample?.Events,
                cost,
                priced?.UnpricedTokens ?? 0,
                !complete || (!hasUsage && matchingSample is not null),
                !hasUsage ? matchingSample?.RecordedLocal : spec.IsCurrent ? now : spec.End,
                latestObs,
                reference));
        }

        return result;
    }

    internal static bool StartsNewCycle(GeminiWeeklyQuotaObservation previous, GeminiWeeklyQuotaObservation current)
    {
        if (current.ObservedAt >= previous.ResetAt.AddMinutes(-2))
            return true;
        if (current.UsedPercent + ResetDropSplitThresholdPercent < previous.UsedPercent)
            return true;
        if (current.ResetAt > previous.ResetAt + ResetShiftSplitThreshold)
            return true;
        return false;
    }

    private static bool IsWeekReading(ClaudeQuotaReading reading) =>
        reading.Kind == ClaudeQuotaWindowKind.Week &&
        reading.RecordedLocal < reading.ResetLocal.AddMinutes(5) &&
        reading.RecordedLocal >= reading.ResetLocal - Week - TimeSpan.FromHours(2);

    private static DateTimeOffset EndOfWeek(DateTimeOffset at, DateTimeOffset anchor)
    {
        var weeks = Math.DivRem((at - anchor).Ticks, Week.Ticks, out var remainder);
        return anchor.AddDays((remainder < 0 ? weeks : weeks + 1) * 7);
    }
}

internal sealed record GeminiSessionActivityRecord(
    string SessionKey,
    bool IsAntigravity,
    bool IsT3,
    bool IsSubagent,
    DateTimeOffset FirstTimestamp,
    DateTimeOffset LastTimestamp,
    IReadOnlyList<DateTimeOffset> EventTimestamps,
    int ToolCallsCount);

internal sealed record GeminiRangeActivityStats(
    int MainSessions,
    int SubagentSessions,
    int AntigravitySessions,
    int CliSessions,
    long AntigravityEvents,
    long CliEvents,
    long ToolCalls,
    long SevenDayTotalTokens,
    long SevenDayEvents,
    int SevenDayActiveDays,
    int T3Sessions = 0,
    long T3Events = 0);

internal sealed record GeminiActivitySnapshot(
    DateTimeOffset NowLocal,
    IReadOnlyList<ClaudeCodeBlock> RecentBlocks,
    TokenUsageBucket Today,
    TokenUsageBucket Week,
    int WeekActiveDays,
    GeminiRangeActivityStats RangeStats,
    bool WeekIsComplete = true,
    GeminiQuotaWindowUsage? FiveHourWindow = null,
    GeminiQuotaWindowUsage? WeekWindow = null)
{
    public ClaudeCodeBlock? CurrentBlock => RecentBlocks.LastOrDefault(block => block.IsActiveAt(NowLocal));
}

/// <summary>
/// Computes Gemini 5h/7d quota windows, capacity inputs, today's model mix, and
/// session/subagent breakdown for the Gemini header panel.
/// </summary>
internal static class GeminiActivityReader
{
    private static readonly TimeSpan Lookback = TimeSpan.FromHours(24);
    private static readonly TimeSpan AnchorMaxAge = TimeSpan.FromHours(72);
    private static readonly TimeSpan HistoryMemoTtl = TimeSpan.FromMinutes(2);

    private sealed record CachedFileEntry(
        CodexLogFileIndex.Signature Signature,
        GeminiSessionActivityRecord? Record);

    private sealed record HistorySegment(
        TokenUsageBucket Usage,
        int ActiveDays,
        bool IsComplete,
        DateTimeOffset ReadAt);

    private static readonly ConcurrentDictionary<string, CachedFileEntry> FileCache =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly object HistoryGate = new();
    private static readonly Dictionary<(string Scope, DateTimeOffset Start, DateTimeOffset End), HistorySegment> HistoryMemo = new();

    internal static void ResetHistoryMemo()
    {
        lock (HistoryGate)
        {
            HistoryMemo.Clear();
        }
    }

    internal static void ResetCaches()
    {
        FileCache.Clear();
        ResetHistoryMemo();
    }

    private static string AnchorPath =>
        Path.Combine(MonitorCachePaths.LocalAppData, GeminiUsageReader.CacheFolder, "block-anchor.txt");

    public static GeminiActivitySnapshot ReadCurrent(
        DateTimeOffset rangeStart,
        DateTimeOffset rangeEnd,
        DateTimeOffset nowLocal,
        IReadOnlyList<ClaudeQuotaReading>? readings = null,
        CancellationToken cancellationToken = default,
        string? homeOverride = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        nowLocal = nowLocal.ToOffset(CodexUsageReader.BeijingOffset);
        var todayStart = new DateTimeOffset(nowLocal.Year, nowLocal.Month, nowLocal.Day, 0, 0, 0, CodexUsageReader.BeijingOffset);
        var latestQuota = GeminiRateLimitLog.ReadLatest(nowLocal);

        var anchor = latestQuota?.FiveHour is { } qFive
            ? qFive.ResetLocal - ClaudeCodeBlockCalculator.BlockLength
            : LoadAnchor(nowLocal);
        var lookbackStart = anchor is { } a && a < nowLocal - Lookback ? a : nowLocal - Lookback;

        var (rows, rowsComplete) = GeminiUsageReader.ReadTransientDetailRowsWithStatus(
            lookbackStart, nowLocal.AddMinutes(1), cancellationToken, homeOverride);
        var blocks = ClaudeCodeBlockCalculator.Build(rows, anchor);
        if (blocks.Count > 0)
            SaveAnchor(blocks[^1].StartLocal);

        var today = Merge(rows.Where(row => row.StartLocal >= todayStart), todayStart);
        var weekStart = todayStart.AddDays(-6);
        var (weekUsage, weekActiveDays, weekComplete) = UsageInWindow(
            weekStart, nowLocal, lookbackStart, todayStart, rows, rowsComplete, cancellationToken, homeOverride);
        var rangeStats = ReadStats(rangeStart, rangeEnd, nowLocal, cancellationToken, homeOverride, weekUsage, weekActiveDays);

        var snapshot = new GeminiActivitySnapshot(
            nowLocal,
            blocks,
            today,
            weekUsage,
            weekActiveDays,
            rangeStats,
            weekComplete);

        GeminiQuotaWindowUsage BuildWindow(
            ClaudeQuotaWindowKind kind,
            ClaudeWindowSource source,
            DateTimeOffset reset,
            double? usedPercent,
            DateTimeOffset? observed = null,
            int? resetPrecisionSeconds = null,
            ClaudeQuotaReadingSource readingSource = ClaudeQuotaReadingSource.Official)
        {
            var start = kind == ClaudeQuotaWindowKind.Week
                ? GeminiQuotaCycleHistory.ResolveActiveWeekStart(reset, usedPercent, observed, nowLocal)
                : reset - GeminiQuotaEstimator.Length(kind);
            var (usage, _, complete) = UsageInWindow(
                start, nowLocal, lookbackStart, todayStart, rows, rowsComplete, cancellationToken, homeOverride);
            TokenUsageBucket? atObservation = null;
            if (observed is { } at)
            {
                var clamped = at < nowLocal ? at : nowLocal;
                var captured = UsageInWindow(
                    start, clamped, lookbackStart, todayStart, rows, rowsComplete, cancellationToken, homeOverride);
                atObservation = captured.Usage;
                complete &= captured.IsComplete;
            }

            return new GeminiQuotaWindowUsage(kind, source, start, reset, usage, usedPercent, complete, observed, atObservation,
                resetPrecisionSeconds, readingSource);
        }

        GeminiQuotaWindowUsage? Resolve(ClaudeQuotaWindowKind kind, GeminiRateLimitWindow? loggedWindow)
        {
            if (loggedWindow is not null)
            {
                var src = loggedWindow.Source == ClaudeQuotaReadingSource.Manual
                    ? ClaudeWindowSource.Manual
                    : ClaudeWindowSource.Official;
                return BuildWindow(kind, src, loggedWindow.ResetLocal, loggedWindow.UsedPercent,
                    loggedWindow.ObservedLocal ?? latestQuota!.RecordedLocal,
                    loggedWindow.ResetPrecisionSeconds, loggedWindow.Source);
            }

            var savedReading = readings?
                .Where(item => item.Kind == kind && item.ResetLocal > nowLocal)
                .OrderByDescending(item => item.RecordedLocal)
                .FirstOrDefault();
            if (savedReading is not null)
            {
                var src = savedReading.Source == ClaudeQuotaReadingSource.Manual
                    ? ClaudeWindowSource.Manual
                    : ClaudeWindowSource.Official;
                return BuildWindow(kind, src, savedReading.ResetLocal, savedReading.UsedPercent, savedReading.RecordedLocal,
                    readingSource: savedReading.Source);
            }

            return kind == ClaudeQuotaWindowKind.FiveHour && snapshot.CurrentBlock is { } block
                ? BuildWindow(kind, ClaudeWindowSource.Inferred, block.EndLocal, null)
                : null;
        }

        return snapshot with
        {
            FiveHourWindow = Resolve(ClaudeQuotaWindowKind.FiveHour, latestQuota?.FiveHour),
            WeekWindow = Resolve(ClaudeQuotaWindowKind.Week, latestQuota?.SevenDay)
        };
    }

    private static (TokenUsageBucket Usage, int ActiveDays, bool IsComplete) UsageInWindow(
        DateTimeOffset start,
        DateTimeOffset end,
        DateTimeOffset lookbackStart,
        DateTimeOffset todayStart,
        IReadOnlyList<TokenUsageBucket> recentRows,
        bool recentComplete,
        CancellationToken cancellationToken,
        string? homeOverride)
    {
        var bucket = new TokenUsageBucket { StartLocal = start };
        if (start >= end) return (bucket, 0, true);

        // Reuse the already-loaded live lookback slice whenever it covers [start, end].
        if (start >= lookbackStart)
        {
            var inWindow = recentRows.Where(r => r.StartLocal >= start && r.StartLocal <= end).ToList();
            foreach (var r in inWindow) bucket.MergeFrom(r);
            var activeDays = inWindow.Select(r => r.StartLocal.ToOffset(CodexUsageReader.BeijingOffset).Date).Distinct().Count();
            return (bucket, activeDays, recentComplete);
        }

        if (end <= todayStart)
        {
            var histOnly = ReadHistoricalSegment(start, end.AddTicks(1), cancellationToken, homeOverride);
            return (histOnly.Usage, histOnly.ActiveDays, histOnly.IsComplete);
        }

        var history = ReadHistoricalSegment(start, todayStart, cancellationToken, homeOverride);
        bucket.MergeFrom(history.Usage);
        var todayRows = recentRows.Where(r => r.StartLocal >= todayStart && r.StartLocal <= end).ToList();
        var todayPart = Merge(todayRows, todayStart);
        var days = history.ActiveDays + (todayPart.Events > 0 ? 1 : 0);
        if (todayPart.Events > 0) bucket.MergeFrom(todayPart);
        return (bucket, days, history.IsComplete && recentComplete);
    }

    private static HistorySegment ReadHistoricalSegment(
        DateTimeOffset start,
        DateTimeOffset end,
        CancellationToken cancellationToken,
        string? homeOverride)
    {
        if (start >= end)
            return new HistorySegment(new TokenUsageBucket { StartLocal = start }, 0, true, BeijingClock.Now);

        var scope = $"{MonitorCachePaths.LocalAppData}|{homeOverride ?? ""}|{UsageLogPaths.GetOverrideRoot(UsageSource.Gemini) ?? ""}";
        var key = (scope, start, end);
        lock (HistoryGate)
        {
            if (HistoryMemo.TryGetValue(key, out var cached) &&
                BeijingClock.Now - cached.ReadAt < HistoryMemoTtl)
            {
                return cached;
            }
        }

        var bucket = new TokenUsageBucket { StartLocal = start };
        var complete = true;
        var activeDays = 0;

        if (homeOverride is null && UsageSourceReaders.For(UsageSource.Gemini) is GeminiUsageReader reader)
        {
            var firstDay = new DateTimeOffset(start.Date, CodexUsageReader.BeijingOffset);
            var incomplete = reader.GetIncompleteHistoricalDays(firstDay, end.AddTicks(-1), cancellationToken).ToHashSet();

            for (var day = firstDay; day < end; day = day.AddDays(1))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var segStart = day < start ? start : day;
                var segEnd = day.AddDays(1) > end ? end : day.AddDays(1);
                TokenUsageBucket dayBucket;
                if (segStart == day && segEnd == day.AddDays(1) && !incomplete.Contains(day))
                {
                    dayBucket = reader.ReadCachedRange(day, segEnd, cancellationToken);
                }
                else
                {
                    var (segRows, segComplete) = GeminiUsageReader.ReadTransientDetailRowsWithStatus(segStart, segEnd, cancellationToken);
                    complete &= segComplete;
                    dayBucket = Merge(segRows, segStart);
                }

                if (dayBucket.Events > 0)
                {
                    activeDays++;
                    bucket.MergeFrom(dayBucket);
                }
            }
        }
        else
        {
            var (scanRows, scanComplete) = GeminiUsageReader.ReadTransientDetailRowsWithStatus(start, end, cancellationToken, homeOverride);
            complete = scanComplete;
            foreach (var r in scanRows) bucket.MergeFrom(r);
            activeDays = scanRows.Select(r => r.StartLocal.ToOffset(CodexUsageReader.BeijingOffset).Date).Distinct().Count();
        }

        var segment = new HistorySegment(bucket, activeDays, complete, BeijingClock.Now);
        lock (HistoryGate)
        {
            if (HistoryMemo.Count > 64)
            {
                var now = BeijingClock.Now;
                foreach (var stale in HistoryMemo.Where(p => now - p.Value.ReadAt >= HistoryMemoTtl).Select(p => p.Key).ToList())
                    HistoryMemo.Remove(stale);
            }

            HistoryMemo[key] = segment;
        }

        return segment;
    }

    public static GeminiRangeActivityStats ReadStats(
        DateTimeOffset rangeStart,
        DateTimeOffset rangeEnd,
        DateTimeOffset nowLocal,
        CancellationToken cancellationToken = default,
        string? homeOverride = null,
        TokenUsageBucket? preloadedWeek = null,
        int? preloadedWeekActiveDays = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        rangeStart = rangeStart.ToOffset(CodexUsageReader.BeijingOffset);
        rangeEnd = rangeEnd.ToOffset(CodexUsageReader.BeijingOffset);
        nowLocal = nowLocal.ToOffset(CodexUsageReader.BeijingOffset);

        var roots = GeminiUsageReader.GetLogRoots(homeOverride);
        var (files, _) = GeminiUsageReader.DiscoverLogFiles(roots, cancellationToken, reportDiagnostics: false);

        int mainSessions = 0, subagentSessions = 0, agySessions = 0, cliSessions = 0, t3Sessions = 0;
        long agyEvents = 0, cliEvents = 0, toolCalls = 0, t3Events = 0;

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var record = ReadFileCached(file, cancellationToken);
            if (record is null || record.EventTimestamps.Count == 0) continue;
            if (record.LastTimestamp < rangeStart || record.FirstTimestamp >= rangeEnd) continue;

            var inRangeCount = 0L;
            foreach (var stamp in record.EventTimestamps)
            {
                if (stamp >= rangeStart && stamp < rangeEnd)
                    inRangeCount++;
            }

            if (inRangeCount == 0) continue;

            if (record.IsSubagent) subagentSessions++;
            else mainSessions++;

            if (record.IsAntigravity)
            {
                agySessions++;
                agyEvents += inRangeCount;
                if (record.IsT3)
                {
                    t3Sessions++;
                    t3Events += inRangeCount;
                }
            }
            else
            {
                cliSessions++;
                cliEvents += inRangeCount;
            }

            if (record.FirstTimestamp >= rangeStart && record.LastTimestamp < rangeEnd)
                toolCalls += record.ToolCallsCount;
            else if (record.EventTimestamps.Count > 0)
                toolCalls += (long)Math.Round(record.ToolCallsCount * (inRangeCount / (double)record.EventTimestamps.Count));
        }

        long sevenDayTokens;
        long sevenDayEvents;
        int sevenDayActiveDays;
        if (preloadedWeek is not null && preloadedWeekActiveDays is { } preActive)
        {
            sevenDayTokens = preloadedWeek.TotalTokens;
            sevenDayEvents = preloadedWeek.Events;
            sevenDayActiveDays = preActive;
        }
        else
        {
            var weekStart = new DateTimeOffset(nowLocal.Date.AddDays(-6), CodexUsageReader.BeijingOffset);
            var scan = GeminiUsageReader.ReadEvents(weekStart, nowLocal, cancellationToken, homeOverride: homeOverride);
            sevenDayTokens = 0;
            sevenDayEvents = 0;
            foreach (var ev in scan.Events)
            {
                sevenDayTokens = TokenCountMath.AddNonNegative(sevenDayTokens, ev.TotalTokens);
                sevenDayEvents++;
            }
            sevenDayActiveDays = scan.Events
                .Select(ev => ev.Timestamp.ToOffset(CodexUsageReader.BeijingOffset).Date)
                .Distinct()
                .Count();
        }

        return new GeminiRangeActivityStats(
            mainSessions,
            subagentSessions,
            agySessions,
            cliSessions,
            agyEvents,
            cliEvents,
            toolCalls,
            sevenDayTokens,
            sevenDayEvents,
            sevenDayActiveDays,
            t3Sessions,
            t3Events);
    }

    public static decimal EstimateCacheSavingsUsd(TokenUsageBucket summary)
    {
        var presets = PriceSettingsStore.Current.PresetsForGroup(PricePresetGroups.Gemini);
        decimal saved = 0m;
        foreach (var (modelId, bucket) in summary.ModelUsage)
        {
            if (bucket.CachedInputTokens <= 0) continue;
            var norm = CodexModelCost.NormalizeModelId(modelId);
            var family = CodexModelCost.FamilyKey(norm);
            var preset = presets.FirstOrDefault(p =>
            {
                var pKey = CodexModelCost.NormalizeModelId(string.IsNullOrWhiteSpace(p.ModelId) ? p.Model : p.ModelId);
                return string.Equals(pKey, norm, StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(CodexModelCost.FamilyKey(pKey), family, StringComparison.OrdinalIgnoreCase);
            });
            if (preset is null || CodexModelCost.IsPending(preset) || preset.CurrencySymbol != "$" || preset.Divisor <= 0)
                continue;

            var deltaRate = Math.Max(0m, preset.UncachedInput - preset.CachedInput);
            if (deltaRate <= 0m) continue;
            saved = CodexModelCost.AddCosts(saved, (bucket.CachedInputTokens / preset.Divisor) * deltaRate);
        }

        return saved;
    }

    private static TokenUsageBucket Merge(IEnumerable<TokenUsageBucket> rows, DateTimeOffset start)
    {
        var bucket = new TokenUsageBucket { StartLocal = start };
        foreach (var row in rows) bucket.MergeFrom(row);
        return bucket;
    }

    private static DateTimeOffset? LoadAnchor(DateTimeOffset nowLocal)
    {
        try
        {
            if (File.Exists(AnchorPath) &&
                DateTimeOffset.TryParse(File.ReadAllText(AnchorPath).Trim(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var anchor) &&
                anchor <= nowLocal && nowLocal - anchor <= AnchorMaxAge)
            {
                return anchor.ToOffset(CodexUsageReader.BeijingOffset);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return null;
    }

    private static void SaveAnchor(DateTimeOffset anchor)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(AnchorPath)!);
            File.WriteAllText(AnchorPath, anchor.ToString("O", CultureInfo.InvariantCulture));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static GeminiSessionActivityRecord? ReadFileCached(string file, CancellationToken token)
    {
        try
        {
            using var stream = CodexLogFileIndex.Open(file);
            var sig = CodexLogFileIndex.Identify(file, stream);
            if (FileCache.TryGetValue(file, out var cached) && cached.Signature == sig)
                return cached.Record;

            stream.Position = 0;
            var record = ParseSessionFile(file, stream, token);
            using var verify = CodexLogFileIndex.Open(file);
            if (sig == CodexLogFileIndex.Identify(file, verify))
                FileCache[file] = new CachedFileEntry(sig, record);
            return record;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static GeminiSessionActivityRecord? ParseSessionFile(string file, FileStream stream, CancellationToken token)
    {
        var name = Path.GetFileName(file);
        if (name.StartsWith("session-", StringComparison.OrdinalIgnoreCase) &&
            name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            return ParseCliJson(file, stream, token);
        }

        if (name.StartsWith("session-", StringComparison.OrdinalIgnoreCase) &&
            name.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase))
        {
            return ParseCliJsonl(file, stream, token);
        }

        return ParseAntigravityTranscript(file, stream, token);
    }

    private static GeminiSessionActivityRecord? ParseAntigravityTranscript(
        string file, FileStream stream, CancellationToken token)
    {
        using var reader = new StreamReader(stream, leaveOpen: true);
        var convId = GeminiUsageReader.ExtractAntigravityConversationId(file);
        var isT3 = GeminiUsageReader.IsT3AntigravityPath(file);
        var timestamps = new List<DateTimeOffset>();
        var isSubagent = false;
        var seenPlanner = false;
        bool? explicitHuman = null;
        var toolCalls = 0;

        while (reader.ReadLine() is { } line)
        {
            token.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (!line.Contains("PLANNER_RESPONSE", StringComparison.Ordinal) &&
                !line.Contains("CHECKPOINT", StringComparison.Ordinal) &&
                (seenPlanner || !line.Contains("USER_INPUT", StringComparison.Ordinal) &&
                                !line.Contains("SYSTEM_MESSAGE", StringComparison.Ordinal)))
            {
                continue;
            }

            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) continue;
                var type = AgentSpeedJson.Text(root, "type");
                if (!seenPlanner && type is "USER_INPUT" or "SYSTEM_MESSAGE")
                {
                    var stepIdx = AgentSpeedJson.Number(root, "step_index");
                    var src = AgentSpeedJson.Text(root, "source");
                    var content = AgentSpeedJson.Text(root, "content");
                    if (type == "USER_INPUT" && GeminiUsageReader.IsExplicitHumanPrompt(src, content))
                    {
                        explicitHuman = true;
                    }
                    else if (explicitHuman is null && GeminiUsageReader.IsSubagentInitialStep(type, stepIdx, src, content))
                    {
                        explicitHuman = false;
                    }
                }
                else if (type is "PLANNER_RESPONSE" or "CHECKPOINT")
                {
                    if (!seenPlanner)
                    {
                        seenPlanner = true;
                        isSubagent = explicitHuman == false;
                    }

                    if (GeminiUsageReader.TryParseTimestamp(root, "created_at", out var ts))
                        timestamps.Add(ts);
                    if (type == "PLANNER_RESPONSE" && root.TryGetProperty("tool_calls", out var tc) && tc.ValueKind == JsonValueKind.Array)
                        toolCalls += tc.GetArrayLength();
                }
            }
            catch (JsonException) { }
        }

        if (timestamps.Count == 0) return null;
        timestamps.Sort();
        return new GeminiSessionActivityRecord(
            "agy:" + convId,
            IsAntigravity: true,
            IsT3: isT3,
            IsSubagent: isSubagent,
            FirstTimestamp: timestamps[0],
            LastTimestamp: timestamps[^1],
            EventTimestamps: timestamps,
            ToolCallsCount: toolCalls);
    }

    private static GeminiSessionActivityRecord? ParseCliJson(string file, FileStream stream, CancellationToken token)
    {
        var timestamps = new List<DateTimeOffset>();
        var toolCalls = 0;
        var sessionId = Path.GetFileNameWithoutExtension(file);
        try
        {
            using var doc = JsonDocument.Parse(stream);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            sessionId = AgentSpeedJson.Text(root, "sessionId") ?? sessionId;
            DateTimeOffset? fallback = null;
            if (GeminiUsageReader.TryParseTimestamp(root, "lastUpdated", out var lu)) fallback = lu;
            else if (GeminiUsageReader.TryParseTimestamp(root, "startTime", out var st)) fallback = st;

            if (root.TryGetProperty("messages", out var msgs) && msgs.ValueKind == JsonValueKind.Array)
            {
                foreach (var msg in msgs.EnumerateArray())
                {
                    token.ThrowIfCancellationRequested();
                    if (msg.ValueKind != JsonValueKind.Object) continue;
                    if (msg.TryGetProperty("toolCalls", out var tc) && tc.ValueKind == JsonValueKind.Array)
                        toolCalls += tc.GetArrayLength();
                    if (!GeminiUsageReader.TryExtractGeminiCliTokens(msg, out _, out _, out _, out _, out _, out _))
                        continue;

                    if (GeminiUsageReader.TryParseTimestamp(msg, "timestamp", out var ts) ||
                        GeminiUsageReader.TryParseTimestamp(msg, "createdAt", out ts) ||
                        GeminiUsageReader.TryParseTimestamp(msg, "created_at", out ts))
                    {
                        timestamps.Add(ts);
                    }
                    else if (fallback is { } fb)
                    {
                        timestamps.Add(fb);
                    }
                }
            }
        }
        catch (JsonException) { return null; }

        if (timestamps.Count == 0) return null;
        timestamps.Sort();
        return new GeminiSessionActivityRecord(
            "gcli:" + sessionId,
            IsAntigravity: false,
            IsT3: false,
            IsSubagent: false,
            FirstTimestamp: timestamps[0],
            LastTimestamp: timestamps[^1],
            EventTimestamps: timestamps,
            ToolCallsCount: toolCalls);
    }

    private static GeminiSessionActivityRecord? ParseCliJsonl(string file, FileStream stream, CancellationToken token)
    {
        using var reader = new StreamReader(stream, leaveOpen: true);
        var timestamps = new List<DateTimeOffset>();
        var toolCalls = 0;
        var sessionId = Path.GetFileNameWithoutExtension(file);

        while (reader.ReadLine() is { } line)
        {
            token.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) continue;
                sessionId = AgentSpeedJson.Text(root, "sessionId") ?? sessionId;
                if (root.TryGetProperty("toolCalls", out var tc) && tc.ValueKind == JsonValueKind.Array)
                    toolCalls += tc.GetArrayLength();
                if (!GeminiUsageReader.TryExtractGeminiCliTokens(root, out _, out _, out _, out _, out _, out _))
                    continue;
                if (GeminiUsageReader.TryParseTimestamp(root, "timestamp", out var ts) ||
                    GeminiUsageReader.TryParseTimestamp(root, "createdAt", out ts) ||
                    GeminiUsageReader.TryParseTimestamp(root, "created_at", out ts))
                {
                    timestamps.Add(ts);
                }
            }
            catch (JsonException) { }
        }

        if (timestamps.Count == 0) return null;
        timestamps.Sort();
        return new GeminiSessionActivityRecord(
            "gcli:" + sessionId,
            IsAntigravity: false,
            IsT3: false,
            IsSubagent: false,
            FirstTimestamp: timestamps[0],
            LastTimestamp: timestamps[^1],
            EventTimestamps: timestamps,
            ToolCallsCount: toolCalls);
    }
}
