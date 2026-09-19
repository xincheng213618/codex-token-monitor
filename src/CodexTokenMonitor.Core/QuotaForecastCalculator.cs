using System.Text.RegularExpressions;

namespace CodexTokenMonitor;

// Scalar snapshots deliberately contain no mutable TokenUsageBucket references.
internal sealed record QuotaCycleUsageSample(
    DateTimeOffset TimestampLocal,
    long InputTokens,
    long CachedInputTokens,
    long CacheWriteInputTokens,
    long OutputTokens,
    long TotalTokens,
    long Events)
{
    public static QuotaCycleUsageSample From(TokenUsageBucket usage) => From(usage, usage.StartLocal);

    public static QuotaCycleUsageSample From(TokenUsageBucket usage, DateTimeOffset timestamp) => new(
        timestamp, usage.InputTokens, usage.CachedInputTokens, usage.CacheWriteInputTokens,
        usage.OutputTokens, usage.TotalTokens, usage.Events);
}

internal enum QuotaForecastStatus
{
    Ready,
    HistoricalPeriod,
    TargetInPast,
    NoSamples,
    InsufficientSamples,
    StaleSamples,
    NoQuotaDrop,
    NoUsage,
    IncompleteUsage,
    MissingPrice,
    MissingCalibration,
    UnknownFastMultiplier,
    InvalidData
}

internal sealed record QuotaForecastScenario(
    string ModelId,
    string Mode,
    decimal? BaseRatePerHour,
    decimal? RatePerHour,
    DateTimeOffset? RunoutAtLocal,
    decimal? RemainingAtResetPercent,
    decimal? MaxActivityPercent,
    bool? EnoughUntilReset,
    QuotaForecastStatus Status,
    string Reason,
    string Source,
    int SampleCount,
    decimal? FastMultiplier = null,
    QuotaModelCapacitySource? CalibrationSource = null)
{
    public decimal? UsageCoveragePercent { get; init; }
}

internal sealed record QuotaForecastResult(
    QuotaForecastStatus Status,
    string Reason,
    DateTimeOffset? StartLocal,
    DateTimeOffset? LastSampleLocal,
    TimeSpan SampleDuration,
    TimeSpan? SampleAge,
    int SampleCount,
    decimal ObservedDropPercent,
    decimal? ObservedRatePerHour,
    decimal? CurrentRemainingPercent,
    DateTimeOffset TargetResetLocal,
    decimal ActivityPercent,
    decimal? TokensPerHour,
    QuotaForecastScenario Current,
    IReadOnlyList<QuotaForecastScenario> Models)
{
    public decimal? UsageCoveragePercent { get; init; }
}

internal sealed record QuotaForecastLookbackSelection(
    TimeSpan RequestedLookback,
    TimeSpan EffectiveLookback,
    QuotaForecastResult Result)
{
    public bool WasExpanded => EffectiveLookback > RequestedLookback;
}

/// <summary>
/// A linear, same-workload scenario calculation. The real elapsed sample window
/// includes idle time. It predicts from the last observation, never from a made-up
/// current quota point; it does not model a refill or claim statistical confidence.
/// No log, database, settings or network reads occur here.
/// </summary>
internal static class QuotaForecastCalculator
{
    public static readonly TimeSpan MinimumSampleDuration = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(15);
    public const decimal MinimumUsageCoveragePercent = 95m;
    public const string SameWorkloadAssumption =
        "保持近期 Token 吞吐及输入、缓存、输出比例，按未来工作量比例缩放；包含空闲时间，Fast 仅乘额度倍率，不额外假定吞吐加速。";

    public static QuotaForecastLookbackSelection BuildAdaptive(
        QuotaCycleAnalysisResult analysis,
        QuotaModelCapacityReport capacities,
        TimeSpan requestedLookback,
        IEnumerable<TimeSpan> availableLookbacks,
        DateTimeOffset targetReset,
        decimal activityPercent = 100m,
        DateTimeOffset? nowLocal = null,
        IEnumerable<PricePreset>? priceCatalog = null)
    {
        ArgumentNullException.ThrowIfNull(availableLookbacks);
        var now = nowLocal ?? BeijingClock.Now;
        var catalog = priceCatalog?.ToArray();
        var requested = Build(analysis, capacities, requestedLookback, targetReset, activityPercent, now, catalog);
        if (!ShouldExpandLookback(requested.Status))
            return new QuotaForecastLookbackSelection(requestedLookback, requestedLookback, requested);

        foreach (var candidate in availableLookbacks
                     .Where(candidate => candidate > requestedLookback)
                     .Distinct()
                     .OrderBy(candidate => candidate))
        {
            var expanded = Build(analysis, capacities, candidate, targetReset, activityPercent, now, catalog);
            if (expanded.Status == QuotaForecastStatus.Ready)
                return new QuotaForecastLookbackSelection(requestedLookback, candidate, expanded);
        }

        // If no broader interval forms a trustworthy measured slope, keep the
        // user's requested interval and its precise reason instead of silently
        // changing the selection without producing a forecast.
        return new QuotaForecastLookbackSelection(requestedLookback, requestedLookback, requested);
    }

    public static QuotaForecastResult Build(
        QuotaCycleAnalysisResult analysis,
        QuotaModelCapacityReport capacities,
        TimeSpan lookback,
        DateTimeOffset targetReset,
        decimal activityPercent = 100m,
        DateTimeOffset? nowLocal = null,
        IEnumerable<PricePreset>? priceCatalog = null)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        ArgumentNullException.ThrowIfNull(capacities);
        if (lookback <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(lookback));
        if (activityPercent is < 1m or > 100m) throw new ArgumentOutOfRangeException(nameof(activityPercent));

        var now = nowLocal ?? BeijingClock.Now;
        var catalog = BuildCatalog(priceCatalog ?? PricePreset.DefaultsForGroup(PricePresetGroups.Codex));
        var calibrations = capacities.Estimates
            .GroupBy(item => ModelKey(item.ModelId), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key,
                group => group.OrderBy(item => item.Source).First(), StringComparer.OrdinalIgnoreCase);
        var modelIds = catalog.Keys.Concat(calibrations.Keys)
            .Concat(analysis.Models.Select(item => ModelKey(item.ModelId)))
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(item => item, StringComparer.OrdinalIgnoreCase).ToArray();

        // Canonical analysis timelines are already monotonic. Sorting and merging
        // equal instants also makes this boundary safe for imported/test snapshots.
        var points = analysis.Timeline.Where(item => item.TimestampLocal <= now)
            .OrderBy(item => item.TimestampLocal)
            .GroupBy(item => item.TimestampLocal)
            .Select(group => new QuotaCycleTimelinePoint(group.Key, group.Max(item => item.UsedPercent)))
            .ToArray();
        var last = points.LastOrDefault();
        var cutoff = last is null || lookback >= last.TimestampLocal - points[0].TimestampLocal
            ? points.FirstOrDefault()?.TimestampLocal
            : last.TimestampLocal - lookback;
        var selected = points.Where(item => item.TimestampLocal >= cutoff).ToArray();
        var first = selected.FirstOrDefault();
        var duration = last is null || first is null ? TimeSpan.Zero : last.TimestampLocal - first.TimestampLocal;
        var age = last is null ? (TimeSpan?)null : now - last.TimestampLocal;
        var remaining = last is null ? (decimal?)null : 100m - Math.Clamp(last.UsedPercent, 0m, 100m);
        var drop = first is null || last is null ? 0m : Math.Max(0m,
            Math.Clamp(last.UsedPercent, 0m, 100m) - Math.Clamp(first.UsedPercent, 0m, 100m));
        var observedRate = duration > TimeSpan.Zero
            ? drop * TimeSpan.TicksPerHour / duration.Ticks : (decimal?)null;
        var invalid = selected.Any(point => point.UsedPercent is < 0m or > 100m) ||
                      selected.Zip(selected.Skip(1), (a, b) => b.UsedPercent < a.UsedPercent).Any(item => item);

        var windowStatus = !analysis.Period.IsCurrent || analysis.Period.ResetAt <= now
            ? QuotaForecastStatus.HistoricalPeriod
            : targetReset <= now ? QuotaForecastStatus.TargetInPast
            : selected.Length == 0 ? QuotaForecastStatus.NoSamples
            : invalid ? QuotaForecastStatus.InvalidData
            : selected.Length < 2 || duration < MinimumSampleDuration ? QuotaForecastStatus.InsufficientSamples
            : age >= StaleAfter ? QuotaForecastStatus.StaleSamples
            : QuotaForecastStatus.Ready;
        var status = windowStatus == QuotaForecastStatus.Ready && drop <= 0.0001m
            ? QuotaForecastStatus.NoQuotaDrop : windowStatus;

        var usage = new TokenUsageBucket();
        var usageSampleCount = 0;
        if (first is not null && last is not null)
        {
            foreach (var sample in analysis.UsageSamples.Where(item =>
                         item.TimestampLocal > first.TimestampLocal && item.TimestampLocal <= last.TimestampLocal))
            {
                // Normalize only our own copy. Estimating a profile must never
                // mutate a source bucket or its nested model/tier dictionaries.
                var copy = new TokenUsageBucket
                {
                    InputTokens = sample.InputTokens, CachedInputTokens = sample.CachedInputTokens,
                    CacheWriteInputTokens = sample.CacheWriteInputTokens, OutputTokens = sample.OutputTokens,
                    TotalTokens = sample.TotalTokens, Events = sample.Events
                };
                usage.MergeFrom(copy);
                usageSampleCount++;
            }
        }
        var tokensPerHour = duration > TimeSpan.Zero && usage.TotalTokens > 0
            ? usage.TotalTokens / Hours(duration) : (decimal?)null;
        var classifiedTokens = usage.InputTokens + (decimal)usage.OutputTokens;
        var coverage = usage.TotalTokens > 0
            ? Math.Clamp(classifiedTokens / usage.TotalTokens * 100m, 0m, 100m) : (decimal?)null;
        var current = status == QuotaForecastStatus.Ready
            ? Project("近期实测", "实测", observedRate!.Value, remaining!.Value, last!.TimestampLocal,
                targetReset, activityPercent, "真实时间线上额度差 ÷ 经过时间（含空闲）", selected.Length)
            : Unavailable("近期实测", "实测", status, "真实额度观察", selected.Length);

        var models = new List<QuotaForecastScenario>(modelIds.Length * 2);
        foreach (var modelId in modelIds)
        {
            calibrations.TryGetValue(modelId, out var capacity);
            catalog.TryGetValue(modelId, out var preset);
            var source = capacity is null ? "无模型额度标定" : DescribeSource(capacity.Source);
            if (coverage is >= MinimumUsageCoveragePercent and < 100m)
                source += $" · 分类覆盖{coverage.Value.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)}%，未分类按已知比例估算";
            else if (coverage is < MinimumUsageCoveragePercent)
                source += $" · 分类覆盖{coverage.Value.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)}%";
            var normalStatus = windowStatus;
            decimal? baseRate = null;
            decimal estimatedCost = 0m;
            if (normalStatus == QuotaForecastStatus.Ready)
            {
                normalStatus = usage.TotalTokens <= 0 || classifiedTokens <= 0m
                    ? QuotaForecastStatus.NoUsage
                    : coverage < MinimumUsageCoveragePercent
                        ? QuotaForecastStatus.IncompleteUsage
                    : capacity is null || capacity.BandCount <= 0 || capacity.AverageFullQuotaCost <= 0m
                        ? QuotaForecastStatus.MissingCalibration
                        : !TryEstimateCost(modelId, preset, usage, out estimatedCost)
                            ? QuotaForecastStatus.MissingPrice
                            : QuotaForecastStatus.Ready;
                if (normalStatus == QuotaForecastStatus.Ready)
                {
                    // The capacity is already in subscription quota-reference
                    // units, including any observed Fast surcharge in calibration.
                    try
                    {
                        // At least 95% of tokens have measured components. Extend
                        // their observed mix to the small unclassified remainder;
                        // this explicit workload assumption is not a confidence bound.
                        var mixScale = Math.Max(1m, usage.TotalTokens / classifiedTokens);
                        baseRate = estimatedCost * mixScale / capacity!.AverageFullQuotaCost * 100m / Hours(duration);
                    }
                    catch (OverflowException) { normalStatus = QuotaForecastStatus.InvalidData; }
                    if (baseRate is <= 0m) normalStatus = QuotaForecastStatus.MissingPrice;
                }
            }

            var normal = normalStatus == QuotaForecastStatus.Ready
                ? Project(modelId, "普通", baseRate!.Value, remaining!.Value, last!.TimestampLocal,
                    targetReset, activityPercent, source, capacity!.BandCount, 1m, capacity.Source)
                : Unavailable(modelId, "普通", normalStatus, source, capacity?.BandCount ?? usageSampleCount,
                    1m, capacity?.Source);
            models.Add(normal);

            var factor = CodexModelCost.FastQuotaMultiplier(modelId);
            if (normal.Status != QuotaForecastStatus.Ready || factor is null)
            {
                models.Add(Unavailable(modelId, "Fast", normal.Status != QuotaForecastStatus.Ready
                    ? normal.Status : QuotaForecastStatus.UnknownFastMultiplier,
                    source, capacity?.BandCount ?? usageSampleCount, factor, capacity?.Source));
                continue;
            }
            try
            {
                models.Add(Project(modelId, "Fast", baseRate!.Value * factor.Value, remaining!.Value,
                    last!.TimestampLocal, targetReset, activityPercent, source, capacity!.BandCount,
                    factor, capacity.Source));
            }
            catch (OverflowException)
            {
                models.Add(Unavailable(modelId, "Fast", QuotaForecastStatus.InvalidData,
                    source, capacity!.BandCount, factor, capacity.Source));
            }
        }

        return new QuotaForecastResult(status, ReasonFor(status), first?.TimestampLocal, last?.TimestampLocal,
            duration, age, selected.Length, drop, observedRate, remaining, targetReset, activityPercent,
            tokensPerHour, current with { UsageCoveragePercent = coverage },
            models.Select(item => item with { UsageCoveragePercent = coverage }).ToArray())
        {
            UsageCoveragePercent = coverage
        };
    }

    private static QuotaForecastScenario Project(
        string modelId, string mode, decimal baseRate, decimal remaining, DateTimeOffset start,
        DateTimeOffset target, decimal activity, string source, int samples,
        decimal? fastMultiplier = null, QuotaModelCapacitySource? calibrationSource = null)
    {
        try
        {
            var rate = baseRate * (activity / 100m);
            var horizonTicks = (target - start).Ticks;
            var fullWorkloadDemand = baseRate * horizonTicks / TimeSpan.TicksPerHour;
            var demand = rate * horizonTicks / TimeSpan.TicksPerHour;
            var budget = fullWorkloadDemand <= remaining ? 100m : remaining / fullWorkloadDemand * 100m;
            var projectedRemaining = Math.Clamp(remaining - demand, 0m, 100m);
            var hoursToEmpty = rate > 0m ? remaining / rate : 0m;
            DateTimeOffset? runout = null;
            // Overflowing the representable date is explicitly reported rather
            // than wrapping to an earlier or negative time.
            var availableTicks = DateTimeOffset.MaxValue.UtcTicks - start.UtcTicks;
            if (hoursToEmpty <= availableTicks / (decimal)TimeSpan.TicksPerHour)
            {
                runout = start.AddTicks((long)Math.Round(hoursToEmpty * TimeSpan.TicksPerHour,
                    MidpointRounding.AwayFromZero));
            }
            return new QuotaForecastScenario(modelId, mode, baseRate, rate, runout, projectedRemaining,
                Math.Clamp(budget, 0m, 100m), demand <= remaining, QuotaForecastStatus.Ready,
                runout is null ? "预计耗尽时间超出可表示日期范围。" : "", source, samples,
                fastMultiplier, calibrationSource);
        }
        catch (OverflowException)
        {
            return Unavailable(modelId, mode, QuotaForecastStatus.InvalidData, source, samples,
                fastMultiplier, calibrationSource);
        }
        catch (ArgumentOutOfRangeException)
        {
            return Unavailable(modelId, mode, QuotaForecastStatus.InvalidData, source, samples,
                fastMultiplier, calibrationSource);
        }
    }

    private static bool TryEstimateCost(string modelId, PricePreset? preset, TokenUsageBucket usage, out decimal cost)
    {
        cost = 0m;
        if (preset is null || CodexModelCost.HasNoPublicPrice(modelId) || preset.Divisor <= 0m ||
            preset.UncachedInput < 0m || preset.CachedInput < 0m || preset.Output < 0m ||
            preset.CacheWriteInput < 0m || preset.Schedule != PriceSchedule.Flat ||
            preset.UncachedInput == 0m && preset.CachedInput == 0m && preset.Output == 0m &&
            (preset.CacheWriteInput ?? 0m) == 0m)
        {
            return false;
        }
        cost = usage.EstimateCost(CodexSubscriptionPricing.GetProfile(modelId, preset.ToProfile()));
        return cost is > 0m and < decimal.MaxValue;
    }

    private static Dictionary<string, PricePreset> BuildCatalog(IEnumerable<PricePreset> presets) => presets
        .Where(item => string.Equals(item.Provider, "OpenAI", StringComparison.OrdinalIgnoreCase) &&
                       item.CurrencySymbol == "$")
        .GroupBy(item => ModelKey(string.IsNullOrWhiteSpace(item.ModelId) ? item.Model : item.ModelId),
            StringComparer.OrdinalIgnoreCase)
        .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

    private static string ModelKey(string value) => Regex.Replace(
        CodexModelCost.NormalizeModelId(value), @"-\d{4}-\d{2}-\d{2}$", "");

    private static decimal Hours(TimeSpan duration) => duration.Ticks / (decimal)TimeSpan.TicksPerHour;

    private static bool ShouldExpandLookback(QuotaForecastStatus status) => status is
        QuotaForecastStatus.NoSamples or
        QuotaForecastStatus.InsufficientSamples or
        QuotaForecastStatus.NoQuotaDrop;

    private static QuotaForecastScenario Unavailable(string model, string mode, QuotaForecastStatus status,
        string source, int samples, decimal? multiplier = null, QuotaModelCapacitySource? calibrationSource = null) => new(
            model, mode, null, null, null, null, null, null, status, ReasonFor(status), source,
            samples, multiplier, calibrationSource);

    private static string ReasonFor(QuotaForecastStatus status) => status switch
    {
        QuotaForecastStatus.Ready => "按所选窗口的平均消耗线性外推，空闲计入经过时间；不是置信区间或官方额度承诺。",
        QuotaForecastStatus.HistoricalPeriod => "历史或已结束周期仅供回看，不用于当前额度预测。",
        QuotaForecastStatus.TargetInPast => "目标重置时间必须晚于现在。",
        QuotaForecastStatus.NoSamples => "没有当前时间之前的额度观察点。",
        QuotaForecastStatus.InsufficientSamples => "所选窗口至少需要两个真实额度点，且实际覆盖不少于 5 分钟。",
        QuotaForecastStatus.StaleSamples => "最后额度点已超过或达到 15 分钟，需刷新后再预测；没有补算为当前额度。",
        QuotaForecastStatus.NoQuotaDrop => "所选窗口没有可测额度下降，无法据此判断耗尽时间或保证足够。",
        QuotaForecastStatus.NoUsage => "所选时间段缺少可计价 Token 用量，无法比较同工作量方案。",
        QuotaForecastStatus.IncompleteUsage => "近期 Token 的输入/输出分项覆盖不足 95%，无法比较同工作量方案；近期额度实测仍有效。",
        QuotaForecastStatus.MissingPrice => "缺少有效模型参考价格，无法估算该方案。",
        QuotaForecastStatus.MissingCalibration => "没有该模型的有效额度容量标定，不能从价格直接推算额度。",
        QuotaForecastStatus.UnknownFastMultiplier => "该模型的 Fast 额度倍率尚未核实。",
        _ => "样本或数值超出有效范围，暂不预测。"
    };

    private static string DescribeSource(QuotaModelCapacitySource source) => source switch
    {
        QuotaModelCapacitySource.CurrentPeriodApproved => "本期单模型分段标定",
        QuotaModelCapacitySource.CurrentPeriodBlended => "本期混合分段校准",
        QuotaModelCapacitySource.PreviousPeriodApproved => "上期单模型分段标定",
        _ => "本期混合分段反推（非单模型实测）"
    };
}
