namespace CodexTokenMonitor;

internal enum PriceSchedule
{
    Flat,
    DeepSeekBeijingPeakDouble
}

internal sealed record PriceProfile(
    string Name,
    string CurrencySymbol,
    decimal UncachedInputPerMillion,
    decimal CachedInputPerMillion,
    decimal OutputPerMillion,
    decimal Divisor,
    PriceSchedule Schedule = PriceSchedule.Flat,
    decimal? CacheWriteInputPerMillion = null);

internal static class DeepSeekPricingSchedule
{
    public static bool IsPeak(DateTimeOffset timestamp)
    {
        var beijing = timestamp.ToOffset(CodexUsageReader.BeijingOffset);
        var time = beijing.TimeOfDay;
        return time >= TimeSpan.FromHours(9) && time < TimeSpan.FromHours(12) ||
               time >= TimeSpan.FromHours(14) && time < TimeSpan.FromHours(18);
    }
}

internal static class UsageTelemetryRules
{
    public const long OpenAiLongContextThresholdTokens = 272_000;
}

internal static class TokenCountMath
{
    public static long NonNegative(long value)
    {
        return Math.Max(0L, value);
    }

    public static long AddNonNegative(long left, long right)
    {
        left = NonNegative(left);
        right = NonNegative(right);
        return left > long.MaxValue - right ? long.MaxValue : left + right;
    }

    public static long SubtractNonNegative(long left, long right)
    {
        left = NonNegative(left);
        right = NonNegative(right);
        return right >= left ? 0L : left - right;
    }
}

internal static class QuotaPercentRules
{
    public static bool IsValid(decimal? value)
    {
        return value is null or >= 0m and <= 100m;
    }

    public static decimal? Normalize(decimal? value)
    {
        return IsValid(value) ? value : null;
    }
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
    // Only identified events enter this map. Legacy/unidentified usage remains
    // in the counters below, so it can never silently acquire a default price.
    public Dictionary<string, TokenUsageBucket> ModelUsage { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    // Subsets inside each model bucket. Missing historical tier stays unknown.
    public Dictionary<string, TokenUsageBucket> ServiceTierUsage { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public DateTimeOffset StartLocal { get; init; }
    public long Events { get; set; }
    public long InputTokens { get; set; }
    public long CachedInputTokens { get; set; }
    public long CacheWriteInputTokens { get; set; }
    public long UncachedInputTokens { get; set; }
    public long OutputTokens { get; set; }
    public long ReasoningOutputTokens { get; set; }
    public long TotalTokens { get; set; }
    public long LongContextEvents { get; set; }
    public long LongContextInputTokens { get; set; }
    public long LongContextCachedInputTokens { get; set; }
    public long LongContextCacheWriteInputTokens { get; set; }
    public long LongContextOutputTokens { get; set; }
    public long PeakInputTokens { get; set; }
    public long PeakCachedInputTokens { get; set; }
    public long PeakCacheWriteInputTokens { get; set; }
    public long PeakOutputTokens { get; set; }
    public DateTimeOffset? LastTokenEventLocal { get; set; }
    public double CacheRatioPercent => InputTokens > 0 ? CachedInputTokens / (double)InputTokens * 100 : 0;

    public decimal EstimateCost(PriceProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        NormalizeInPlace();

        // Price settings are user-editable and old settings files can contain
        // zero/negative values.  Keep malformed pricing from producing a
        // negative bill or a divide-by-zero/decimal-overflow crash in a view.
        var divisor = profile.Divisor > 0m ? profile.Divisor : 1_000_000m;
        var cacheWriteRate = profile.CacheWriteInputPerMillion ?? profile.UncachedInputPerMillion;
        var baseCost = AddCost(
            AddCost(
                AddCost(
                    AddCost(0m, UncachedInputTokens, profile.UncachedInputPerMillion, divisor),
                    CachedInputTokens,
                    profile.CachedInputPerMillion,
                    divisor),
                CacheWriteInputTokens,
                cacheWriteRate,
                divisor),
            OutputTokens,
            profile.OutputPerMillion,
            divisor);
        if (profile.Schedule != PriceSchedule.DeepSeekBeijingPeakDouble)
        {
            return baseCost;
        }

        // DeepSeek presets store the official off-peak prices. Peak prices are
        // exactly double, so adding the peak subset once produces the billable total.
        var peakUncachedInput = TokenCountMath.SubtractNonNegative(
            TokenCountMath.SubtractNonNegative(PeakInputTokens, PeakCachedInputTokens),
            PeakCacheWriteInputTokens);
        var peakCost = AddCost(
            AddCost(
                AddCost(
                    AddCost(0m, peakUncachedInput, profile.UncachedInputPerMillion, divisor),
                    PeakCachedInputTokens,
                    profile.CachedInputPerMillion,
                    divisor),
                PeakCacheWriteInputTokens,
                cacheWriteRate,
                divisor),
            PeakOutputTokens,
            profile.OutputPerMillion,
            divisor);
        return AddCost(baseCost, peakCost);
    }

    public void Add(TokenUsageEvent item)
    {
        item = item.Normalize();
        Add(item.Timestamp, item.InputTokens, item.CachedInputTokens, item.CacheWriteInputTokens,
            item.OutputTokens, item.ReasoningOutputTokens, item.TotalTokens);
        if (!string.IsNullOrWhiteSpace(item.ModelId))
        {
            if (!ModelUsage.TryGetValue(item.ModelId, out var modelUsage))
            {
                ModelUsage[item.ModelId] = modelUsage = new TokenUsageBucket { StartLocal = StartLocal };
            }
            modelUsage.Add(item.Timestamp, item.InputTokens, item.CachedInputTokens, item.CacheWriteInputTokens,
                item.OutputTokens, item.ReasoningOutputTokens, item.TotalTokens);
            if (item.ServiceTier is { } tier)
            {
                if (!modelUsage.ServiceTierUsage.TryGetValue(tier, out var tierUsage))
                    modelUsage.ServiceTierUsage[tier] = tierUsage = new TokenUsageBucket { StartLocal = StartLocal };
                tierUsage.Add(item.Timestamp, item.InputTokens, item.CachedInputTokens, item.CacheWriteInputTokens,
                    item.OutputTokens, item.ReasoningOutputTokens, item.TotalTokens);
            }
        }
    }

    private static decimal AddCost(decimal total, long tokens, decimal rate, decimal divisor)
    {
        if (total >= decimal.MaxValue || tokens <= 0 || rate <= 0m)
        {
            return total;
        }

        try
        {
            var amount = tokens / divisor * rate;
            return total > decimal.MaxValue - amount
                ? decimal.MaxValue
                : total + amount;
        }
        catch (OverflowException)
        {
            return decimal.MaxValue;
        }
    }

    private static decimal AddCost(decimal total, decimal amount)
    {
        if (total >= decimal.MaxValue || amount <= 0m)
        {
            return total;
        }

        try
        {
            return total > decimal.MaxValue - amount
                ? decimal.MaxValue
                : total + amount;
        }
        catch (OverflowException)
        {
            return decimal.MaxValue;
        }
    }

    public void Add(DateTimeOffset timestamp, long input, long cached, long output, long reasoning, long total)
    {
        Add(timestamp, input, cached, cacheWrite: 0, output, reasoning, total);
    }

    public void Add(
        DateTimeOffset timestamp,
        long input,
        long cached,
        long cacheWrite,
        long output,
        long reasoning,
        long total)
    {
        NormalizeInPlace();

        input = TokenCountMath.NonNegative(input);
        cached = Math.Clamp(TokenCountMath.NonNegative(cached), 0L, input);
        cacheWrite = Math.Clamp(
            TokenCountMath.NonNegative(cacheWrite),
            0L,
            TokenCountMath.SubtractNonNegative(input, cached));
        output = TokenCountMath.NonNegative(output);
        reasoning = TokenCountMath.NonNegative(reasoning);
        total = Math.Max(
            TokenCountMath.NonNegative(total),
            TokenCountMath.AddNonNegative(input, output));

        Events = Events == long.MaxValue ? long.MaxValue : Events + 1;
        InputTokens = TokenCountMath.AddNonNegative(InputTokens, input);
        CachedInputTokens = TokenCountMath.AddNonNegative(CachedInputTokens, cached);
        CacheWriteInputTokens = TokenCountMath.AddNonNegative(CacheWriteInputTokens, cacheWrite);
        UncachedInputTokens = TokenCountMath.AddNonNegative(
            UncachedInputTokens,
            TokenCountMath.SubtractNonNegative(
                TokenCountMath.SubtractNonNegative(input, cached),
                cacheWrite));
        OutputTokens = TokenCountMath.AddNonNegative(OutputTokens, output);
        ReasoningOutputTokens = TokenCountMath.AddNonNegative(ReasoningOutputTokens, reasoning);
        TotalTokens = TokenCountMath.AddNonNegative(TotalTokens, total);

        if (input > UsageTelemetryRules.OpenAiLongContextThresholdTokens)
        {
            LongContextEvents = LongContextEvents == long.MaxValue ? long.MaxValue : LongContextEvents + 1;
            LongContextInputTokens = TokenCountMath.AddNonNegative(LongContextInputTokens, input);
            LongContextCachedInputTokens = TokenCountMath.AddNonNegative(LongContextCachedInputTokens, cached);
            LongContextCacheWriteInputTokens = TokenCountMath.AddNonNegative(
                LongContextCacheWriteInputTokens,
                cacheWrite);
            LongContextOutputTokens = TokenCountMath.AddNonNegative(LongContextOutputTokens, output);
        }

        if (DeepSeekPricingSchedule.IsPeak(timestamp))
        {
            PeakInputTokens = TokenCountMath.AddNonNegative(PeakInputTokens, input);
            PeakCachedInputTokens = TokenCountMath.AddNonNegative(PeakCachedInputTokens, cached);
            PeakCacheWriteInputTokens = TokenCountMath.AddNonNegative(PeakCacheWriteInputTokens, cacheWrite);
            PeakOutputTokens = TokenCountMath.AddNonNegative(PeakOutputTokens, output);
        }

        if (LastTokenEventLocal is null || timestamp > LastTokenEventLocal)
        {
            LastTokenEventLocal = timestamp;
        }
    }

    public void NormalizeInPlace()
    {
        Events = TokenCountMath.NonNegative(Events);
        InputTokens = TokenCountMath.NonNegative(InputTokens);
        CachedInputTokens = Math.Clamp(TokenCountMath.NonNegative(CachedInputTokens), 0L, InputTokens);
        CacheWriteInputTokens = Math.Clamp(
            TokenCountMath.NonNegative(CacheWriteInputTokens),
            0L,
            TokenCountMath.SubtractNonNegative(InputTokens, CachedInputTokens));
        UncachedInputTokens = TokenCountMath.SubtractNonNegative(
            TokenCountMath.SubtractNonNegative(InputTokens, CachedInputTokens),
            CacheWriteInputTokens);
        OutputTokens = TokenCountMath.NonNegative(OutputTokens);
        ReasoningOutputTokens = TokenCountMath.NonNegative(ReasoningOutputTokens);
        TotalTokens = Math.Max(
            TokenCountMath.NonNegative(TotalTokens),
            TokenCountMath.AddNonNegative(InputTokens, OutputTokens));

        LongContextEvents = Math.Clamp(LongContextEvents, 0L, Events);
        LongContextInputTokens = Math.Clamp(
            TokenCountMath.NonNegative(LongContextInputTokens),
            0L,
            InputTokens);
        LongContextCachedInputTokens = Math.Clamp(
            TokenCountMath.NonNegative(LongContextCachedInputTokens),
            0L,
            LongContextInputTokens);
        LongContextCacheWriteInputTokens = Math.Clamp(
            TokenCountMath.NonNegative(LongContextCacheWriteInputTokens),
            0L,
            TokenCountMath.SubtractNonNegative(LongContextInputTokens, LongContextCachedInputTokens));
        LongContextOutputTokens = Math.Clamp(
            TokenCountMath.NonNegative(LongContextOutputTokens),
            0L,
            OutputTokens);
        PeakInputTokens = Math.Clamp(TokenCountMath.NonNegative(PeakInputTokens), 0L, InputTokens);
        PeakCachedInputTokens = Math.Clamp(
            TokenCountMath.NonNegative(PeakCachedInputTokens),
            0L,
            PeakInputTokens);
        PeakCacheWriteInputTokens = Math.Clamp(
            TokenCountMath.NonNegative(PeakCacheWriteInputTokens),
            0L,
            TokenCountMath.SubtractNonNegative(PeakInputTokens, PeakCachedInputTokens));
        PeakOutputTokens = Math.Clamp(TokenCountMath.NonNegative(PeakOutputTokens), 0L, OutputTokens);
    }

    public void MergeFrom(TokenUsageBucket source)
    {
        foreach (var (tier, usage) in source.ServiceTierUsage)
        {
            if (!ServiceTierUsage.TryGetValue(tier, out var target))
                ServiceTierUsage[tier] = target = new TokenUsageBucket { StartLocal = StartLocal };
            target.MergeFrom(usage);
        }
        foreach (var (model, usage) in source.ModelUsage)
        {
            if (!ModelUsage.TryGetValue(model, out var target))
            {
                ModelUsage[model] = target = new TokenUsageBucket { StartLocal = StartLocal };
            }
            target.MergeFrom(usage);
        }
        NormalizeInPlace();
        source.NormalizeInPlace();
        Events = TokenCountMath.AddNonNegative(Events, source.Events);
        InputTokens = TokenCountMath.AddNonNegative(InputTokens, source.InputTokens);
        CachedInputTokens = TokenCountMath.AddNonNegative(CachedInputTokens, source.CachedInputTokens);
        CacheWriteInputTokens = TokenCountMath.AddNonNegative(CacheWriteInputTokens, source.CacheWriteInputTokens);
        UncachedInputTokens = TokenCountMath.AddNonNegative(UncachedInputTokens, source.UncachedInputTokens);
        OutputTokens = TokenCountMath.AddNonNegative(OutputTokens, source.OutputTokens);
        ReasoningOutputTokens = TokenCountMath.AddNonNegative(ReasoningOutputTokens, source.ReasoningOutputTokens);
        TotalTokens = TokenCountMath.AddNonNegative(TotalTokens, source.TotalTokens);
        LongContextEvents = TokenCountMath.AddNonNegative(LongContextEvents, source.LongContextEvents);
        LongContextInputTokens = TokenCountMath.AddNonNegative(LongContextInputTokens, source.LongContextInputTokens);
        LongContextCachedInputTokens = TokenCountMath.AddNonNegative(LongContextCachedInputTokens, source.LongContextCachedInputTokens);
        LongContextCacheWriteInputTokens = TokenCountMath.AddNonNegative(
            LongContextCacheWriteInputTokens,
            source.LongContextCacheWriteInputTokens);
        LongContextOutputTokens = TokenCountMath.AddNonNegative(LongContextOutputTokens, source.LongContextOutputTokens);
        PeakInputTokens = TokenCountMath.AddNonNegative(PeakInputTokens, source.PeakInputTokens);
        PeakCachedInputTokens = TokenCountMath.AddNonNegative(PeakCachedInputTokens, source.PeakCachedInputTokens);
        PeakCacheWriteInputTokens = TokenCountMath.AddNonNegative(
            PeakCacheWriteInputTokens,
            source.PeakCacheWriteInputTokens);
        PeakOutputTokens = TokenCountMath.AddNonNegative(PeakOutputTokens, source.PeakOutputTokens);
        if (source.LastTokenEventLocal is not null &&
            (LastTokenEventLocal is null || source.LastTokenEventLocal > LastTokenEventLocal))
        {
            LastTokenEventLocal = source.LastTokenEventLocal;
        }

        // Recompute all derived fields after saturating independent counters.
        // Without this pass, an input overflow could leave UncachedInputTokens
        // larger than InputTokens - CachedInputTokens, which then distorts cost
        // and cache-ratio calculations for extreme imported or aggregated data.
        NormalizeInPlace();
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
    string? Key = null,
    long CacheWriteInputTokens = 0,
    string? ModelId = null,
    string? ServiceTier = null)
{
    public TokenUsageEvent Normalize()
    {
        var input = TokenCountMath.NonNegative(InputTokens);
        var cached = Math.Clamp(TokenCountMath.NonNegative(CachedInputTokens), 0L, input);
        var cacheWrite = Math.Clamp(
            TokenCountMath.NonNegative(CacheWriteInputTokens),
            0L,
            TokenCountMath.SubtractNonNegative(input, cached));
        var output = TokenCountMath.NonNegative(OutputTokens);
        var reasoning = TokenCountMath.NonNegative(ReasoningOutputTokens);
        var total = Math.Max(
            TokenCountMath.NonNegative(TotalTokens),
            TokenCountMath.AddNonNegative(input, output));

        return this with
        {
            ModelId = string.IsNullOrWhiteSpace(ModelId) ? null : ModelId.Trim(),
            ServiceTier = string.IsNullOrWhiteSpace(ServiceTier) ? null : ServiceTier.Trim().ToLowerInvariant(),
            InputTokens = input,
            CachedInputTokens = cached,
            CacheWriteInputTokens = cacheWrite,
            OutputTokens = output,
            ReasoningOutputTokens = reasoning,
            TotalTokens = total
        };
    }
}

// A source scan may still return useful events after one file is temporarily
// unreadable. Keep that health bit alongside the events so callers can show
// the partial result without marking the day as permanently complete.
internal sealed record UsageEventScanResult(
    IReadOnlyList<TokenUsageEvent> Events,
    bool IsComplete);

internal sealed record UsageRangeScanResult(
    TokenUsageSummary Summary,
    bool IsComplete);

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
        ArgumentNullException.ThrowIfNull(events);

        // Keep only the preferred event for each stable key while consuming
        // the input.  GroupBy creates a grouping object and a backing list for
        // every key, which is a noticeable short-lived allocation during
        // large historical scans and multi-package imports.
        var merged = new Dictionary<string, TokenUsageEvent>(StringComparer.Ordinal);
        foreach (var candidate in events)
        {
            var normalized = candidate.Normalize();
            var key = GetStableKeyNormalized(normalized);
            if (!merged.TryGetValue(key, out var existing) || IsPreferred(normalized, existing))
            {
                merged[key] = existing is null ? normalized : EnrichMetadata(normalized, existing);
            }
            else merged[key] = EnrichMetadata(existing, normalized);
        }

        return merged.Values
            .OrderBy(item => item.Timestamp)
            .ToList();
    }

    private static TokenUsageEvent EnrichMetadata(TokenUsageEvent preferred, TokenUsageEvent other) => preferred with
    {
        ModelId = preferred.ModelId ?? other.ModelId,
        ServiceTier = preferred.ServiceTier ?? (preferred.ModelId is null || other.ModelId is null ||
            string.Equals(preferred.ModelId, other.ModelId, StringComparison.OrdinalIgnoreCase) ? other.ServiceTier : null)
    };

    internal static string GetStableKey(TokenUsageEvent item)
    {
        item = item.Normalize();
        return GetStableKeyNormalized(item);
    }

    private static string GetStableKeyNormalized(TokenUsageEvent item)
    {
        // Keep the fallback compatible with persisted event keys. Cache-write
        // telemetry enriches an existing event after a parser/schema upgrade;
        // it must not create a second identity for that event during a rescan.
        return !string.IsNullOrWhiteSpace(item.Key)
            ? item.Key
            : $"{item.Timestamp:O}|{item.InputTokens}|{item.CachedInputTokens}|{item.OutputTokens}|{item.ReasoningOutputTokens}|{item.TotalTokens}";
    }

    private static bool IsPreferred(TokenUsageEvent candidate, TokenUsageEvent existing)
    {
        var completenessComparison = CompletenessScore(candidate).CompareTo(CompletenessScore(existing));
        if (completenessComparison == 0 && string.IsNullOrWhiteSpace(candidate.ModelId) != string.IsNullOrWhiteSpace(existing.ModelId))
        {
            return !string.IsNullOrWhiteSpace(candidate.ModelId);
        }
        return completenessComparison > 0 ||
               completenessComparison == 0 && candidate.Timestamp > existing.Timestamp;
    }

    private static decimal CompletenessScore(TokenUsageEvent item)
    {
        // Five long counters can exceed Int64 even after each individual
        // counter has been normalized. Decimal keeps duplicate selection
        // monotonic for extreme-but-valid imported values.
        return (decimal)item.InputTokens +
               item.CachedInputTokens +
               item.CacheWriteInputTokens +
               item.OutputTokens +
               item.ReasoningOutputTokens +
               item.TotalTokens;
    }
}

internal sealed class CachedUsageEvent
{
    public string? Key { get; set; }
    public DateTimeOffset Timestamp { get; set; }
    public long InputTokens { get; set; }
    public long CachedInputTokens { get; set; }
    public long CacheWriteInputTokens { get; set; }
    public long OutputTokens { get; set; }
    public long ReasoningOutputTokens { get; set; }
    public long TotalTokens { get; set; }
}

internal sealed class CachedDayRecord
{
    public Dictionary<string, TokenUsageBucket> ModelUsage { get; set; } = new();
    public string Date { get; set; } = "";
    // False means the persisted counters were malformed.  The record can
    // still be used for scan metadata, but must not be treated as a cache hit.
    public bool IsValid { get; set; } = true;
    public bool IsComplete { get; set; }
    public DateTimeOffset? ScannedThroughLocal { get; set; }
    public long Events { get; set; }
    public long InputTokens { get; set; }
    public long CachedInputTokens { get; set; }
    public long CacheWriteInputTokens { get; set; }
    public long UncachedInputTokens { get; set; }
    public long OutputTokens { get; set; }
    public long ReasoningOutputTokens { get; set; }
    public long TotalTokens { get; set; }
    public long LongContextEvents { get; set; }
    public long LongContextInputTokens { get; set; }
    public long LongContextCachedInputTokens { get; set; }
    public long LongContextCacheWriteInputTokens { get; set; }
    public long LongContextOutputTokens { get; set; }
    public long PeakInputTokens { get; set; }
    public long PeakCachedInputTokens { get; set; }
    public long PeakCacheWriteInputTokens { get; set; }
    public long PeakOutputTokens { get; set; }
    public DateTimeOffset? LastTokenEventLocal { get; set; }
    public int DetailEventCount { get; set; }
    public List<CachedUsageEvent> DetailEvents { get; set; } = new();
}
