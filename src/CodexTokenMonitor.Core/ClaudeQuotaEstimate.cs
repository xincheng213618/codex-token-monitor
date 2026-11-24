using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodexTokenMonitor;

[JsonConverter(typeof(JsonStringEnumConverter))]
internal enum ClaudeQuotaWindowKind
{
    FiveHour,
    Week
}

[JsonConverter(typeof(JsonStringEnumConverter))]
internal enum ClaudeQuotaReadingSource
{
    Manual,
    Official,
    Desktop
}

/// <summary>
/// One reading of a quota window: the percentage used, entered from /usage or
/// taken from the status line's official rate_limits, plus the Claude-model
/// spend the local transcripts show for that window at that moment. Capacity
/// is spend scaled up to 100%.
/// </summary>
internal sealed record ClaudeQuotaReading(
    ClaudeQuotaWindowKind Kind,
    DateTimeOffset RecordedLocal,
    double UsedPercent,
    DateTimeOffset WindowStartLocal,
    DateTimeOffset ResetLocal,
    decimal CostUsd,
    long Tokens,
    long Events,
    ClaudeQuotaReadingSource Source = ClaudeQuotaReadingSource.Manual)
{
    // /usage prints whole percentages, so the true value is within half a point.
    public const double ReportedPercentStep = 0.5;

    public decimal? CapacityUsd => UsedPercent > 0 ? CostUsd / (decimal)(UsedPercent / 100) : null;
    public long? CapacityTokens => UsedPercent > 0 ? (long)Math.Min(long.MaxValue, Tokens / (UsedPercent / 100)) : null;

    /// <summary>Capacity range implied by the half-point rounding of the reported percentage.</summary>
    public (decimal Low, decimal? High)? CapacityUsdRange => UsedPercent > 0
        ? (CostUsd / (decimal)((UsedPercent + ReportedPercentStep) / 100),
            UsedPercent > ReportedPercentStep ? CostUsd / (decimal)((UsedPercent - ReportedPercentStep) / 100) : null)
        : null;
}

/// <summary>Median capacity across windows, each window represented by its most-used reading.</summary>
internal sealed record ClaudeQuotaCapacity(
    ClaudeQuotaWindowKind Kind,
    decimal CapacityUsd,
    long CapacityTokens,
    int Windows,
    ClaudeQuotaReading Latest,
    bool IsTentative);

internal static class ClaudeQuotaEstimator
{
    // Below this, one rounding step moves the estimate by more than 10%.
    public const double ReliablePercent = 5;

    public static TimeSpan Length(ClaudeQuotaWindowKind kind) =>
        kind == ClaudeQuotaWindowKind.FiveHour ? TimeSpan.FromHours(5) : TimeSpan.FromDays(7);

    /// <summary>
    /// Claude-model usage only: gateway models such as MiMo reach Claude Code
    /// through another provider and never draw on the Claude subscription.
    /// </summary>
    public static TokenUsageBucket ClaudeModelUsage(IEnumerable<TokenUsageBucket> rows, DateTimeOffset startLocal, DateTimeOffset endLocal)
    {
        var usage = new TokenUsageBucket { StartLocal = startLocal };
        foreach (var row in rows.Where(row => row.StartLocal >= startLocal && row.StartLocal < endLocal))
        {
            foreach (var (model, modelUsage) in row.ModelUsage)
            {
                if (!IsClaudeModel(model))
                {
                    continue;
                }

                var single = new TokenUsageBucket { StartLocal = startLocal };
                single.ModelUsage[model] = modelUsage;
                single.MergeFrom(modelUsage);
                usage.MergeFrom(single);
            }
        }

        return usage;
    }

    public static bool IsClaudeModel(string modelId) =>
        modelId.StartsWith("claude", StringComparison.OrdinalIgnoreCase);

    public static decimal UsdCost(TokenUsageBucket usage) =>
        CodexModelCost.Estimate(usage, PricePresetGroups.ClaudeCode).CostTotals
            .Where(total => total.CurrencySymbol == "$")
            .Sum(total => total.Amount);

    public static ClaudeQuotaReading CreateReading(
        ClaudeQuotaWindowKind kind,
        double usedPercent,
        DateTimeOffset resetLocal,
        DateTimeOffset nowLocal,
        IEnumerable<TokenUsageBucket> rows)
    {
        var start = resetLocal - Length(kind);
        var usage = ClaudeModelUsage(rows, start, nowLocal < resetLocal ? nowLocal.AddTicks(1) : resetLocal);
        return new ClaudeQuotaReading(kind, nowLocal, usedPercent, start, resetLocal,
            UsdCost(usage), usage.TotalTokens, usage.Events);
    }

    /// <summary>Reads the window's transcripts and prices the Claude-model part of it.</summary>
    public static ClaudeQuotaReading ReadWindow(
        ClaudeQuotaWindowKind kind,
        double usedPercent,
        DateTimeOffset resetLocal,
        DateTimeOffset nowLocal,
        CancellationToken cancellationToken = default)
    {
        var start = resetLocal - Length(kind);
        var rows = ClaudeUsageReader.ReadTransientDetailRows(start, nowLocal.AddMinutes(1), cancellationToken);
        return CreateReading(kind, usedPercent, resetLocal, nowLocal, rows);
    }

    /// <summary>
    /// Turns official windows into readings so capacity calibrates itself.
    /// Each window keeps one official reading, replaced as its percentage
    /// grows; returns true when the store changed.
    /// </summary>
    public static bool RecordOfficial(ClaudeCodeActivitySnapshot snapshot, IReadOnlyList<ClaudeQuotaReading> readings)
    {
        var changed = false;
        foreach (var window in new[] { snapshot.FiveHourWindow, snapshot.WeekWindow })
        {
            if (window is not { Source: ClaudeWindowSource.Official, OfficialPercent: { } percent, IsComplete: true } || percent < 1)
            {
                continue;
            }

            var claude = window.UsageAtObservation is { } captured
                ? ClaudeModelUsage(new[] { captured }, window.StartLocal, DateTimeOffset.MaxValue)
                : window.ClaudeUsage;
            var cost = UsdCost(claude);
            if (cost <= 0)
            {
                continue;
            }

            var existing = readings.FirstOrDefault(item =>
                item.Source == ClaudeQuotaReadingSource.Official && item.Kind == window.Kind && SameWindow(item.ResetLocal, window.ResetLocal));
            if (existing is not null && existing.UsedPercent >= percent)
            {
                continue;
            }

            var reading = new ClaudeQuotaReading(window.Kind, window.ObservedLocal ?? snapshot.NowLocal, percent, window.StartLocal, window.ResetLocal,
                cost, claude.TotalTokens, claude.Events, ClaudeQuotaReadingSource.Official);
            ClaudeQuotaReadingStore.Replace(existing, reading);
            changed = true;
        }

        return changed;
    }

    private static bool SameWindow(DateTimeOffset first, DateTimeOffset second) =>
        Math.Abs((first - second).TotalMinutes) < 1;

    public static ClaudeQuotaCapacity? Summarize(IEnumerable<ClaudeQuotaReading> readings, ClaudeQuotaWindowKind kind)
    {
        var ofKind = readings.Where(item => item.Kind == kind && item.UsedPercent > 0 && item.CostUsd > 0).ToList();
        if (ofKind.Count == 0)
        {
            return null;
        }

        // Later readings in one window cover more of it, so they round less.
        var perWindow = ofKind
            .GroupBy(item => item.ResetLocal.UtcDateTime.Ticks / TimeSpan.TicksPerMinute)
            .Select(group => group.OrderByDescending(item => item.UsedPercent).ThenByDescending(item => item.RecordedLocal).First())
            .ToList();
        var reliable = perWindow.Where(item => item.UsedPercent >= ReliablePercent).ToList();
        var basis = reliable.Count > 0 ? reliable : perWindow;
        var latest = ofKind.MaxBy(item => item.RecordedLocal)!;
        return new ClaudeQuotaCapacity(
            kind,
            Median(basis.Select(item => item.CapacityUsd!.Value)),
            (long)Median(basis.Select(item => (decimal)item.CapacityTokens!.Value)),
            basis.Count,
            latest,
            reliable.Count == 0);
    }

    private static decimal Median(IEnumerable<decimal> values)
    {
        var sorted = values.Order().ToList();
        var middle = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
    }
}

/// <summary>A rough 100% reference from one automatic reading and its matching local usage.</summary>
internal sealed record ClaudeQuotaReference(
    decimal TotalCostUsd,
    long TotalTokens,
    decimal RecordedCostUsd,
    long RecordedTokens,
    double UsedPercent,
    DateTimeOffset RecordedLocal,
    bool IsCurrentWindow,
    bool IsIncomplete)
{
    public bool IsTentative => UsedPercent < ClaudeQuotaEstimator.ReliablePercent || IsIncomplete;
}

internal static class ClaudeQuotaReferenceCalculator
{
    public static ClaudeQuotaReference? Estimate(ClaudeQuotaWindowKind kind, ClaudeQuotaWindowUsage? window,
        IReadOnlyList<ClaudeQuotaReading> readings)
    {
        if (window is { Source: ClaudeWindowSource.Official, OfficialPercent: >= 1 and <= 100 } &&
            window.ObservedLocal is { } observed)
        {
            var sample = window.UsageAtObservation ?? window.Usage;
            var usage = ClaudeQuotaEstimator.ClaudeModelUsage(new[] { sample }, window.StartLocal, DateTimeOffset.MaxValue);
            var cost = ClaudeQuotaEstimator.UsdCost(usage);
            if (usage.TotalTokens > 0 && cost > 0)
                return Calculate(cost, usage.TotalTokens, window.OfficialPercent.Value, observed, true, !window.IsComplete);
        }

        // Keep a reference during a fresh/empty window without reviving old manual entries.
        var previous = readings.Where(item => item.Kind == kind && item.Source == ClaudeQuotaReadingSource.Official &&
                item.UsedPercent >= 1 && item.UsedPercent <= 100 && item.CostUsd > 0 && item.Tokens > 0)
            .MaxBy(item => item.RecordedLocal);
        return previous is null ? null : Calculate(previous.CostUsd, previous.Tokens, previous.UsedPercent,
            previous.RecordedLocal, false, false);
    }

    private static ClaudeQuotaReference Calculate(decimal cost, long tokens, double percent, DateTimeOffset at,
        bool current, bool incomplete)
    {
        var fraction = (decimal)percent / 100m;
        return new ClaudeQuotaReference(cost / fraction, (long)Math.Min(long.MaxValue, tokens / fraction),
            cost, tokens, percent, at, current, incomplete);
    }
}

internal static class ClaudeQuotaReadingStore
{
    private const int MaxReadings = 500;
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private static string FilePath =>
        Path.Combine(MonitorCachePaths.LocalAppData, "ClaudeCodeTokenMonitor", "quota-readings.json");

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

    public static void Remove(ClaudeQuotaReading reading) => Update(list => list.Remove(reading));

    public static void Replace(ClaudeQuotaReading? previous, ClaudeQuotaReading reading) => Update(list =>
    {
        if (previous is not null)
        {
            list.Remove(previous);
        }

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
                // Never overwrite readings that merely failed to parse; keep
                // them beside the fresh file for recovery.
                File.Move(FilePath, FilePath + $".corrupt-{DateTime.Now:yyyyMMddHHmmss}", overwrite: true);
                list = [];
            }

            change(list);
            list = list.OrderBy(item => item.RecordedLocal).TakeLast(MaxReadings).ToList();
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(list, Options));
            File.Move(temp, FilePath, overwrite: true);
        }
    }
}
