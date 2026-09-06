namespace CodexTokenMonitor;

/// <summary>
/// Attributes observed weekly-quota movement to the model usage recorded between
/// quota samples. Quota values can be interpolated between server snapshots, so
/// the result is an analytical estimate rather than an account billing record.
/// </summary>
internal static class QuotaCycleAnalysisCalculator
{
    internal const decimal DefaultBandSizePercent = 5m;
    private const decimal MinimumQuotaMovement = 0.0001m;

    public static QuotaCycleAnalysisResult Build(
        CodexQuotaCycle period,
        CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow.ToOffset(CodexUsageReader.BeijingOffset);
        var end = period.IsCurrent && now < period.PeriodEnd ? now : period.PeriodEnd;
        if (end <= period.PeriodStart)
        {
            return QuotaCycleAnalysisResult.Empty(period, "周期时间范围无效");
        }

        var usageRows = CodexUsageReader.ReadCachedDetailRows(
                period.PeriodStart,
                end,
                cancellationToken)
            .Where(item => item.StartLocal >= period.PeriodStart && item.StartLocal < end)
            .OrderBy(item => item.StartLocal)
            .ToList();
        if (usageRows.Count == 0)
        {
            return QuotaCycleAnalysisResult.Empty(period, "这个周期没有可用的逐条 Token 记录");
        }

        var timeline = CodexUsageReader.ReadMaterializedQuotaTimeline(
                usageRows.Select(item => item.StartLocal),
                cancellationToken: cancellationToken)
            .Where(item =>
                item.WeekUsedPercent is not null &&
                !item.IsAnomaly &&
                CodexQuotaCycleReader.IsSameQuotaReset(item.WeekResetAtLocal, period.ResetAt))
            .GroupBy(item => item.SnapshotLocal)
            .ToDictionary(group => group.Key, group => group.Last());

        var samples = usageRows
            .Where(row => timeline.ContainsKey(row.StartLocal))
            .Select(row => new QuotaCycleAnalysisSample(
                row.StartLocal,
                timeline[row.StartLocal].WeekUsedPercent!.Value,
                row))
            .ToList();
        return BuildFromSamples(period, samples, DefaultBandSizePercent, cancellationToken);
    }

    internal static QuotaCycleAnalysisResult BuildFromSamples(
        CodexQuotaCycle period,
        IReadOnlyList<QuotaCycleAnalysisSample> source,
        decimal bandSizePercent = DefaultBandSizePercent,
        CancellationToken cancellationToken = default,
        IEnumerable<PricePreset>? priceCatalog = null)
    {
        if (bandSizePercent <= 0m || bandSizePercent > 100m)
        {
            throw new ArgumentOutOfRangeException(nameof(bandSizePercent));
        }

        var samples = source
            .Where(item => item.Usage is not null)
            .OrderBy(item => item.TimestampLocal)
            .ToList();
        if (samples.Count < 2)
        {
            return QuotaCycleAnalysisResult.Empty(period, "额度锚点不足，至少需要两个可对齐的时间点");
        }

        var catalog = priceCatalog?.ToList();
        var intervals = BuildIntervals(samples, cancellationToken, catalog);
        if (intervals.Count == 0)
        {
            return QuotaCycleAnalysisResult.Empty(period, "这个周期没有可归因的额度下降区间");
        }

        var builders = new SortedDictionary<int, BandBuilder>();
        foreach (var interval in intervals)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AddIntervalToBands(interval, bandSizePercent, builders);
        }

        var bands = builders.Values
            .Select(item => item.Build())
            .Where(item => item.QuotaDropPercent > 0m && item.EstimatedFullQuotaCost is not null)
            .OrderBy(item => item.UsedFromPercent)
            .ToList();
        if (bands.Count == 0)
        {
            return QuotaCycleAnalysisResult.Empty(period, "额度变化过小，暂时无法形成稳定分段");
        }

        var totalDrop = bands.Sum(item => item.QuotaDropPercent);
        var totalCost = bands.Sum(item => item.EquivalentCost);
        var totalTokens = bands.Aggregate(0L, (total, item) =>
            TokenCountMath.AddNonNegative(total, item.Tokens));
        var estimatedFullCost = QuotaMath.EstimateLimit(totalCost, totalDrop);
        var weightedMean = estimatedFullCost ?? 0m;
        var variance = weightedMean <= 0m || totalDrop <= 0m
            ? 0m
            : bands.Sum(item =>
                item.QuotaDropPercent * Square((item.EstimatedFullQuotaCost ?? weightedMean) - weightedMean)) / totalDrop;
        var standardDeviation = DecimalSqrt(variance);
        var volatilityPercent = weightedMean <= 0m ? 0m : standardDeviation / weightedMean * 100m;

        var modelBuilders = new Dictionary<string, ModelAccumulator>(StringComparer.OrdinalIgnoreCase);
        foreach (var band in bands)
        {
            foreach (var model in band.Models)
            {
                GetModel(modelBuilders, model.ModelId).Add(
                    model.QuotaDropPercent,
                    model.Tokens,
                    model.EquivalentCost,
                    model.IsPriced);
            }
        }

        var models = modelBuilders.Values
            .Select(item => item.Build(totalDrop, totalCost))
            .OrderByDescending(item => item.QuotaDropPercent)
            .ThenByDescending(item => item.Tokens)
            .ToList();
        var min = bands.Min(item => item.EstimatedFullQuotaCost!.Value);
        var max = bands.Max(item => item.EstimatedFullQuotaCost!.Value);
        var dominant = models.FirstOrDefault()?.ModelId ?? "未识别";

        return new QuotaCycleAnalysisResult(
            period,
            bands,
            models,
            samples.Count,
            totalDrop,
            totalTokens,
            totalCost,
            estimatedFullCost,
            min,
            max,
            volatilityPercent,
            dominant,
            "");
    }

    private static List<RawInterval> BuildIntervals(
        IReadOnlyList<QuotaCycleAnalysisSample> samples,
        CancellationToken cancellationToken,
        IReadOnlyList<PricePreset>? priceCatalog)
    {
        var result = new List<RawInterval>();
        var previousTime = samples[0].TimestampLocal;
        var previousUsed = ClampPercent(samples[0].UsedPercent);
        var pending = new TokenUsageBucket { StartLocal = previousTime };

        for (var index = 1; index < samples.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sample = samples[index];
            var currentUsed = ClampPercent(sample.UsedPercent);
            pending.MergeFrom(sample.Usage);

            // Small backwards movement is snapshot/interpolation noise. Keep the
            // accumulated usage for the next monotonic quota observation.
            if (currentUsed <= previousUsed + MinimumQuotaMovement)
            {
                continue;
            }

            var estimate = CodexModelCost.Estimate(pending, priceCatalog);
            var modelSlices = BuildModelSlices(pending, priceCatalog);
            result.Add(new RawInterval(
                previousTime,
                sample.TimestampLocal,
                previousUsed,
                currentUsed,
                pending.TotalTokens,
                estimate.QuotaEquivalentCost,
                modelSlices));

            previousTime = sample.TimestampLocal;
            previousUsed = currentUsed;
            pending = new TokenUsageBucket { StartLocal = sample.TimestampLocal };
        }

        return result;
    }

    private static IReadOnlyList<ModelSlice> BuildModelSlices(
        TokenUsageBucket usage,
        IReadOnlyList<PricePreset>? priceCatalog)
    {
        var result = new List<ModelSlice>();
        long identifiedTokens = 0;
        long identifiedEvents = 0;
        foreach (var (modelId, modelUsage) in usage.ModelUsage)
        {
            var wrapper = new TokenUsageBucket { StartLocal = usage.StartLocal };
            wrapper.MergeFrom(modelUsage);
            wrapper.ModelUsage[modelId] = modelUsage;
            var estimate = CodexModelCost.Estimate(wrapper, priceCatalog);
            result.Add(new ModelSlice(
                modelId,
                modelUsage.TotalTokens,
                modelUsage.Events,
                estimate.QuotaEquivalentCost,
                estimate.IsComplete));
            identifiedTokens = TokenCountMath.AddNonNegative(identifiedTokens, modelUsage.TotalTokens);
            identifiedEvents = TokenCountMath.AddNonNegative(identifiedEvents, modelUsage.Events);
        }

        var missingTokens = TokenCountMath.SubtractNonNegative(usage.TotalTokens, identifiedTokens);
        var missingEvents = TokenCountMath.SubtractNonNegative(usage.Events, identifiedEvents);
        if (missingTokens > 0 || missingEvents > 0 || result.Count == 0)
        {
            result.Add(new ModelSlice("未识别", missingTokens, missingEvents, 0m, false));
        }

        return result;
    }

    private static void AddIntervalToBands(
        RawInterval interval,
        decimal bandSizePercent,
        IDictionary<int, BandBuilder> builders)
    {
        var delta = interval.UsedToPercent - interval.UsedFromPercent;
        if (delta <= MinimumQuotaMovement)
        {
            return;
        }

        var cursor = interval.UsedFromPercent;
        while (cursor < interval.UsedToPercent - MinimumQuotaMovement)
        {
            var bandIndex = Math.Min(
                (int)Math.Floor(cursor / bandSizePercent),
                Math.Max(0, (int)Math.Ceiling(100m / bandSizePercent) - 1));
            var boundary = Math.Min(100m, (bandIndex + 1) * bandSizePercent);
            var overlapEnd = Math.Min(interval.UsedToPercent, boundary);
            var overlap = overlapEnd - cursor;
            if (overlap <= MinimumQuotaMovement)
            {
                cursor = Math.Min(interval.UsedToPercent, cursor + MinimumQuotaMovement);
                continue;
            }

            if (!builders.TryGetValue(bandIndex, out var builder))
            {
                builders[bandIndex] = builder = new BandBuilder(bandIndex);
            }

            var intervalRatio = overlap / delta;
            var startRatio = (cursor - interval.UsedFromPercent) / delta;
            var endRatio = (overlapEnd - interval.UsedFromPercent) / delta;
            builder.Add(
                cursor,
                overlapEnd,
                InterpolateTime(interval.StartLocal, interval.EndLocal, startRatio),
                InterpolateTime(interval.StartLocal, interval.EndLocal, endRatio),
                Allocate(interval.Tokens, intervalRatio),
                interval.EquivalentCost * intervalRatio,
                interval.Models,
                intervalRatio,
                overlap);
            cursor = overlapEnd;
        }
    }

    private static DateTimeOffset InterpolateTime(
        DateTimeOffset start,
        DateTimeOffset end,
        decimal ratio)
    {
        var ticks = (long)Math.Round((end - start).Ticks * (double)Math.Clamp(ratio, 0m, 1m));
        return start.AddTicks(ticks);
    }

    private static long Allocate(long value, decimal ratio)
    {
        if (value <= 0 || ratio <= 0m)
        {
            return 0;
        }

        var allocated = Math.Round(value * ratio, MidpointRounding.AwayFromZero);
        return allocated >= long.MaxValue ? long.MaxValue : (long)allocated;
    }

    private static ModelAccumulator GetModel(
        IDictionary<string, ModelAccumulator> models,
        string modelId)
    {
        if (!models.TryGetValue(modelId, out var model))
        {
            models[modelId] = model = new ModelAccumulator(modelId);
        }

        return model;
    }

    private static decimal ClampPercent(decimal value) => Math.Clamp(value, 0m, 100m);

    private static decimal Square(decimal value) => value * value;

    private static decimal DecimalSqrt(decimal value)
    {
        if (value <= 0m)
        {
            return 0m;
        }

        return (decimal)Math.Sqrt((double)value);
    }

    private sealed record RawInterval(
        DateTimeOffset StartLocal,
        DateTimeOffset EndLocal,
        decimal UsedFromPercent,
        decimal UsedToPercent,
        long Tokens,
        decimal EquivalentCost,
        IReadOnlyList<ModelSlice> Models);

    private sealed record ModelSlice(
        string ModelId,
        long Tokens,
        long Events,
        decimal EquivalentCost,
        bool IsPriced);

    private sealed class BandBuilder(int bandIndex)
    {
        private readonly Dictionary<string, ModelAccumulator> models = new(StringComparer.OrdinalIgnoreCase);
        private decimal usedFrom = 100m;
        private decimal usedTo;
        private DateTimeOffset? start;
        private DateTimeOffset? end;
        private long tokens;
        private decimal equivalentCost;

        public void Add(
            decimal intervalUsedFrom,
            decimal intervalUsedTo,
            DateTimeOffset intervalStart,
            DateTimeOffset intervalEnd,
            long intervalTokens,
            decimal intervalCost,
            IReadOnlyList<ModelSlice> intervalModels,
            decimal intervalRatio,
            decimal quotaDrop)
        {
            usedFrom = Math.Min(usedFrom, intervalUsedFrom);
            usedTo = Math.Max(usedTo, intervalUsedTo);
            start = start is null || intervalStart < start ? intervalStart : start;
            end = end is null || intervalEnd > end ? intervalEnd : end;
            tokens = TokenCountMath.AddNonNegative(tokens, intervalTokens);
            equivalentCost += intervalCost;

            var useCostWeight = intervalModels.All(item => item.IsPriced) &&
                                intervalModels.Sum(item => item.EquivalentCost) > 0m;
            var weightTotal = useCostWeight
                ? intervalModels.Sum(item => item.EquivalentCost)
                : intervalModels.Sum(item => (decimal)Math.Max(0, item.Tokens));
            foreach (var model in intervalModels)
            {
                var weight = useCostWeight ? model.EquivalentCost : Math.Max(0, model.Tokens);
                var attributedDrop = weightTotal > 0m ? quotaDrop * weight / weightTotal : 0m;
                GetModel(models, model.ModelId).Add(
                    attributedDrop,
                    Allocate(model.Tokens, intervalRatio),
                    model.EquivalentCost * intervalRatio,
                    model.IsPriced);
            }
        }

        public QuotaCycleAnalysisBand Build()
        {
            var drop = Math.Max(0m, usedTo - usedFrom);
            var modelRows = models.Values
                .Select(item => item.Build(drop, equivalentCost))
                .OrderByDescending(item => item.QuotaDropPercent)
                .ThenByDescending(item => item.Tokens)
                .ToList();
            return new QuotaCycleAnalysisBand(
                bandIndex,
                usedFrom,
                usedTo,
                start ?? default,
                end ?? start ?? default,
                drop,
                tokens,
                equivalentCost,
                QuotaMath.EstimateLimit(equivalentCost, drop),
                modelRows.FirstOrDefault()?.ModelId ?? "未识别",
                modelRows);
        }
    }

    private sealed class ModelAccumulator(string modelId)
    {
        private decimal quotaDrop;
        private long tokens;
        private decimal cost;
        private bool isPriced = true;

        public void Add(decimal drop, long tokenCount, decimal equivalentCost, bool priced)
        {
            quotaDrop += Math.Max(0m, drop);
            tokens = TokenCountMath.AddNonNegative(tokens, tokenCount);
            cost += Math.Max(0m, equivalentCost);
            isPriced &= priced;
        }

        public QuotaCycleModelShare Build(decimal totalDrop, decimal totalCost) => new(
            modelId,
            quotaDrop,
            totalDrop > 0m ? quotaDrop / totalDrop * 100m : 0m,
            tokens,
            cost,
            totalCost > 0m ? cost / totalCost * 100m : 0m,
            isPriced);
    }
}

internal sealed record QuotaCycleAnalysisSample(
    DateTimeOffset TimestampLocal,
    decimal UsedPercent,
    TokenUsageBucket Usage);

internal sealed record QuotaCycleAnalysisResult(
    CodexQuotaCycle Period,
    IReadOnlyList<QuotaCycleAnalysisBand> Bands,
    IReadOnlyList<QuotaCycleModelShare> Models,
    int AlignedSampleCount,
    decimal ObservedQuotaDropPercent,
    long Tokens,
    decimal EquivalentCost,
    decimal? EstimatedFullQuotaCost,
    decimal MinimumBandEstimate,
    decimal MaximumBandEstimate,
    decimal VolatilityPercent,
    string DominantModel,
    string EmptyReason)
{
    public bool HasData => Bands.Count > 0;

    public static QuotaCycleAnalysisResult Empty(CodexQuotaCycle period, string reason) => new(
        period,
        Array.Empty<QuotaCycleAnalysisBand>(),
        Array.Empty<QuotaCycleModelShare>(),
        0,
        0m,
        0,
        0m,
        null,
        0m,
        0m,
        0m,
        "未识别",
        reason);
}

internal sealed record QuotaCycleAnalysisBand(
    int BandIndex,
    decimal UsedFromPercent,
    decimal UsedToPercent,
    DateTimeOffset StartLocal,
    DateTimeOffset EndLocal,
    decimal QuotaDropPercent,
    long Tokens,
    decimal EquivalentCost,
    decimal? EstimatedFullQuotaCost,
    string DominantModel,
    IReadOnlyList<QuotaCycleModelShare> Models)
{
    public decimal RemainingFromPercent => 100m - UsedFromPercent;
    public decimal RemainingToPercent => 100m - UsedToPercent;
}

internal sealed record QuotaCycleModelShare(
    string ModelId,
    decimal QuotaDropPercent,
    decimal QuotaSharePercent,
    long Tokens,
    decimal EquivalentCost,
    decimal CostSharePercent,
    bool IsPriced);
