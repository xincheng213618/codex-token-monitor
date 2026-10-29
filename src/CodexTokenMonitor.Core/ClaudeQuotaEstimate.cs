using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodexTokenMonitor;

[JsonConverter(typeof(JsonStringEnumConverter))]
internal enum ClaudeQuotaWindowKind
{
    FiveHour,
    Week
}

/// <summary>
/// One manual reading of Claude Code's /usage: the percentage used in a window
/// plus the Claude-model spend the local transcripts show for that window at
/// the moment it was entered. Capacity is spend scaled up to 100%.
/// </summary>
internal sealed record ClaudeQuotaReading(
    ClaudeQuotaWindowKind Kind,
    DateTimeOffset RecordedLocal,
    double UsedPercent,
    DateTimeOffset WindowStartLocal,
    DateTimeOffset ResetLocal,
    decimal CostUsd,
    long Tokens,
    long Events)
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

    private static void Update(Action<List<ClaudeQuotaReading>> change)
    {
        lock (Gate)
        {
            var list = Load().ToList();
            change(list);
            list = list.OrderBy(item => item.RecordedLocal).TakeLast(MaxReadings).ToList();
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(list, Options));
            File.Move(temp, FilePath, overwrite: true);
        }
    }
}
