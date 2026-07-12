namespace CodexTokenMonitor;

internal sealed record PriceProfile(
    string Name,
    string CurrencySymbol,
    decimal UncachedInputPerMillion,
    decimal CachedInputPerMillion,
    decimal OutputPerMillion,
    decimal Divisor);

internal static class UsageTelemetryRules
{
    public const long OpenAiLongContextThresholdTokens = 272_000;
}

internal static class PriceProfiles
{
    public static PriceProfile PrimaryCodex =>
        PriceSettingsStore.Current.CodexPresets.FirstOrDefault()?.ToProfile() ??
        PriceSettingsStore.Current.ToGptProfile();

    // Compatibility name retained for older callers; quota accounting follows
    // the first Codex comparison profile selected in the price library.
    public static PriceProfile Gpt55StandardLong => PrimaryCodex;

    public static PriceProfile DeepSeekV4Pro => PriceSettingsStore.Current.ToDeepSeekProfile();

    public static PriceProfile XiaomiMimoV25Pro => PriceSettingsStore.Current.ToXiaomiProfile();
}

internal static class QuotaFreshness
{
    public static readonly TimeSpan CurrentEstimateMaxAge = TimeSpan.FromHours(6);

    public static bool IsFresh(DateTimeOffset snapshotLocal, DateTimeOffset nowLocal)
    {
        return snapshotLocal <= nowLocal.AddMinutes(5) &&
               nowLocal - snapshotLocal <= CurrentEstimateMaxAge;
    }
}

internal class TokenUsageBucket
{
    public DateTimeOffset StartLocal { get; init; }
    public long Events { get; set; }
    public long InputTokens { get; set; }
    public long CachedInputTokens { get; set; }
    public long UncachedInputTokens { get; set; }
    public long OutputTokens { get; set; }
    public long ReasoningOutputTokens { get; set; }
    public long TotalTokens { get; set; }
    public long LongContextEvents { get; set; }
    public long LongContextInputTokens { get; set; }
    public long LongContextCachedInputTokens { get; set; }
    public long LongContextOutputTokens { get; set; }
    public DateTimeOffset? LastTokenEventLocal { get; set; }
    public double CacheRatioPercent => InputTokens > 0 ? CachedInputTokens / (double)InputTokens * 100 : 0;

    public decimal EstimateCost(PriceProfile profile)
    {
        return (UncachedInputTokens / profile.Divisor * profile.UncachedInputPerMillion) +
               (CachedInputTokens / profile.Divisor * profile.CachedInputPerMillion) +
               (OutputTokens / profile.Divisor * profile.OutputPerMillion);
    }

    public void Add(DateTimeOffset timestamp, long input, long cached, long output, long reasoning, long total)
    {
        Events++;
        InputTokens += input;
        CachedInputTokens += cached;
        UncachedInputTokens += input - cached;
        OutputTokens += output;
        ReasoningOutputTokens += reasoning;
        TotalTokens += total;

        if (input > UsageTelemetryRules.OpenAiLongContextThresholdTokens)
        {
            LongContextEvents++;
            LongContextInputTokens += input;
            LongContextCachedInputTokens += cached;
            LongContextOutputTokens += output;
        }

        if (LastTokenEventLocal is null || timestamp > LastTokenEventLocal)
        {
            LastTokenEventLocal = timestamp;
        }
    }

    public void MergeFrom(TokenUsageBucket source)
    {
        Events += source.Events;
        InputTokens += source.InputTokens;
        CachedInputTokens += source.CachedInputTokens;
        UncachedInputTokens += source.UncachedInputTokens;
        OutputTokens += source.OutputTokens;
        ReasoningOutputTokens += source.ReasoningOutputTokens;
        TotalTokens += source.TotalTokens;
        LongContextEvents += source.LongContextEvents;
        LongContextInputTokens += source.LongContextInputTokens;
        LongContextCachedInputTokens += source.LongContextCachedInputTokens;
        LongContextOutputTokens += source.LongContextOutputTokens;
        if (source.LastTokenEventLocal is not null &&
            (LastTokenEventLocal is null || source.LastTokenEventLocal > LastTokenEventLocal))
        {
            LastTokenEventLocal = source.LastTokenEventLocal;
        }
    }
}

internal sealed class TokenUsageSummary : TokenUsageBucket
{
    public DateTimeOffset EndLocal { get; init; }
    public List<TokenUsageBucket> DailyBuckets { get; } = new();
}

internal sealed record TokenUsageEvent(
    DateTimeOffset Timestamp,
    long InputTokens,
    long CachedInputTokens,
    long OutputTokens,
    long ReasoningOutputTokens,
    long TotalTokens,
    string? Key = null);

internal sealed record CodexQuotaWindowEstimate(
    string Label,
    decimal UsedPercent,
    int WindowMinutes,
    DateTimeOffset WindowStartLocal,
    DateTimeOffset WindowEndLocal,
    DateTimeOffset? ResetAtLocal,
    TokenUsageSummary Usage,
    decimal UsedGptCost,
    decimal? EstimatedGptLimit,
    long? EstimatedTokenLimit);

internal sealed record CodexQuotaEstimate(
    DateTimeOffset SnapshotLocal,
    string? LimitId,
    string? LimitName,
    CodexQuotaWindowEstimate? FiveHour,
    CodexQuotaWindowEstimate? Week);

internal sealed record CodexQuotaSnapshot(
    DateTimeOffset SnapshotLocal,
    string? LimitId,
    string? LimitName,
    decimal? FiveHourUsedPercent,
    DateTimeOffset? FiveHourResetAtLocal,
    decimal? WeekUsedPercent,
    DateTimeOffset? WeekResetAtLocal,
    bool IsAnomaly = false);

internal static class UsageEventMerger
{
    public static IReadOnlyList<TokenUsageEvent> Merge(IEnumerable<TokenUsageEvent> events)
    {
        return events
            .GroupBy(GetStableKey, StringComparer.Ordinal)
            .Select(group => group
                .OrderByDescending(CompletenessScore)
                .ThenByDescending(item => item.Timestamp)
                .First())
            .OrderBy(item => item.Timestamp)
            .ToList();
    }

    private static string GetStableKey(TokenUsageEvent item)
    {
        return !string.IsNullOrWhiteSpace(item.Key)
            ? item.Key
            : $"{item.Timestamp:O}|{item.InputTokens}|{item.CachedInputTokens}|{item.OutputTokens}|{item.ReasoningOutputTokens}|{item.TotalTokens}";
    }

    private static long CompletenessScore(TokenUsageEvent item)
    {
        return item.InputTokens + item.CachedInputTokens + item.OutputTokens + item.ReasoningOutputTokens + item.TotalTokens;
    }
}

internal sealed class CachedUsageEvent
{
    public string? Key { get; set; }
    public DateTimeOffset Timestamp { get; set; }
    public long InputTokens { get; set; }
    public long CachedInputTokens { get; set; }
    public long OutputTokens { get; set; }
    public long ReasoningOutputTokens { get; set; }
    public long TotalTokens { get; set; }
}

internal sealed class CachedDayRecord
{
    public string Date { get; set; } = "";
    public bool IsComplete { get; set; }
    public DateTimeOffset? ScannedThroughLocal { get; set; }
    public long Events { get; set; }
    public long InputTokens { get; set; }
    public long CachedInputTokens { get; set; }
    public long UncachedInputTokens { get; set; }
    public long OutputTokens { get; set; }
    public long ReasoningOutputTokens { get; set; }
    public long TotalTokens { get; set; }
    public long LongContextEvents { get; set; }
    public long LongContextInputTokens { get; set; }
    public long LongContextCachedInputTokens { get; set; }
    public long LongContextOutputTokens { get; set; }
    public DateTimeOffset? LastTokenEventLocal { get; set; }
    public int DetailEventCount { get; set; }
    public List<CachedUsageEvent> DetailEvents { get; set; } = new();
}
