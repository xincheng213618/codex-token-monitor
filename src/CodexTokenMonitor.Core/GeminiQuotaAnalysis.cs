namespace CodexTokenMonitor;

/// <summary>
/// Adapts Gemini 5h/7d quota observations and local Antigravity/CLI token usage
/// to the shared Codex cycle-analysis, cost-curve, and manual-interval estimators.
/// </summary>
internal sealed class GeminiQuotaAnalysisSource : IQuotaCycleAnalysisSource
{
    private readonly string? homeOverride;

    public GeminiQuotaAnalysisSource(string? homeOverride = null)
    {
        this.homeOverride = homeOverride;
    }

    public bool ResolvesCurrentPeriod => false;

    public IReadOnlyList<CodexQuotaSnapshot> ReadSnapshots(
        DateTimeOffset start,
        DateTimeOffset end,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return GeminiQuotaCycleHistory.ReadWeekObservations(end)
            .Where(obs => obs.ObservedAt >= start && obs.ObservedAt <= end)
            .Select(obs => new CodexQuotaSnapshot(
                obs.ObservedAt,
                LimitId: "gemini",
                LimitName: "Gemini",
                FiveHourUsedPercent: null,
                FiveHourResetAtLocal: null,
                WeekUsedPercent: (decimal)Math.Clamp(obs.UsedPercent, 0.0, 100.0),
                WeekResetAtLocal: obs.ResetAt))
            .OrderBy(item => item.SnapshotLocal)
            .ToList();
    }

    public QuotaCycleAnalysisResult BuildAnalysis(
        CodexQuotaCycle period,
        CodexQuotaWindowEstimate? currentWeek,
        decimal bandSizePercent,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var now = BeijingClock.Now;
        var start = period.PeriodStart;
        var end = period.IsCurrent && now < period.PeriodEnd ? now : period.PeriodEnd;
        if (end <= start)
        {
            return QuotaCycleAnalysisResult.Empty(period, "周期时间范围无效") with
            {
                BandSizePercent = bandSizePercent
            };
        }

        IReadOnlyList<TokenUsageBucket> rawRows;
        if (homeOverride is null && UsageSourceReaders.For(UsageSource.Gemini) is GeminiUsageReader reader)
        {
            rawRows = reader.ReadDetailRows(start, end.AddMinutes(1), includeLiveToday: true, token);
        }
        else
        {
            rawRows = GeminiUsageReader.ReadTransientDetailRowsWithStatus(start, end.AddMinutes(1), token, homeOverride).Rows;
        }

        var rows = rawRows
            .Where(item => item.StartLocal >= start && item.StartLocal < end)
            .Select(item => GeminiQuotaEstimator.GeminiModelUsage([item], start, end))
            .Where(item => item.Events > 0 || item.TotalTokens > 0)
            .OrderBy(item => item.StartLocal)
            .ToList();
        if (rows.Count == 0)
        {
            return QuotaCycleAnalysisResult.Empty(period, "这个周期没有可用的本机 Gemini Token 记录") with
            {
                BandSizePercent = bandSizePercent
            };
        }

        var points = ReadSnapshots(start, end, token)
            .Where(item => item.WeekUsedPercent is not null)
            .OrderBy(item => item.SnapshotLocal)
            .ToList();
        var samples = BuildMonotonicSamples(start, points, rows, token);
        var result = QuotaCycleAnalysisCalculator.BuildFromSamples(
            period,
            samples,
            bandSizePercent,
            token,
            priceGroup: PricePresetGroups.Gemini);
        return result with
        {
            UsageSamples = rows.Select(QuotaCycleUsageSample.From).ToArray()
        };
    }

    public QuotaModelCapacityReport BuildCapacities(
        CodexQuotaCycle period,
        QuotaCycleAnalysisResult analysis,
        CodexQuotaCycle? previousPeriod,
        CancellationToken token) =>
        new("-", Array.Empty<QuotaModelCapacityEstimate>());

    internal static IReadOnlyList<QuotaCycleAnalysisSample> BuildMonotonicSamples(
        DateTimeOffset periodStart,
        IReadOnlyList<CodexQuotaSnapshot> points,
        IReadOnlyList<TokenUsageBucket> rows,
        CancellationToken cancellationToken = default)
    {
        var samples = new List<QuotaCycleAnalysisSample>
        {
            new(periodStart, 0m, new TokenUsageBucket { StartLocal = periodStart })
        };
        var pending = new TokenUsageBucket { StartLocal = periodStart };
        var lastUsedPercent = 0m;
        var previousTime = periodStart;
        var rowIndex = 0;

        foreach (var point in points
                     .GroupBy(item => item.SnapshotLocal)
                     .Select(group => group.OrderByDescending(item => item.WeekUsedPercent ?? -1m).First())
                     .OrderBy(item => item.SnapshotLocal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var anchor = point.SnapshotLocal;
            if (anchor <= previousTime || point.WeekUsedPercent is not { } used)
            {
                continue;
            }

            // Within a single split cycle, ignore minor downward rounding jitter.
            if (used + 0.5m < lastUsedPercent)
            {
                continue;
            }

            while (rowIndex < rows.Count && rows[rowIndex].StartLocal <= anchor)
            {
                pending.MergeFrom(rows[rowIndex]);
                rowIndex++;
            }

            lastUsedPercent = Math.Max(lastUsedPercent, used);
            samples.Add(new QuotaCycleAnalysisSample(anchor, lastUsedPercent, pending));
            pending = new TokenUsageBucket { StartLocal = anchor };
            previousTime = anchor;
        }

        while (rowIndex < rows.Count)
        {
            pending.MergeFrom(rows[rowIndex]);
            rowIndex++;
        }

        if (pending.Events > 0 || pending.TotalTokens > 0)
        {
            var tailStamp = rows[^1].StartLocal > previousTime ? rows[^1].StartLocal : previousTime.AddSeconds(1);
            samples.Add(new QuotaCycleAnalysisSample(tailStamp, lastUsedPercent, pending));
        }

        return samples;
    }
}

internal static class GeminiQuotaCostCurveBuilder
{
    private const string PlanGroupLabel = "Gemini 7d 周期";

    public static QuotaCostCurveResult Build(
        IReadOnlyList<GeminiQuotaCycleUsage>? knownCycles = null,
        DateTimeOffset? nowOverride = null,
        CancellationToken cancellationToken = default,
        string? homeOverride = null)
    {
        var now = nowOverride ?? BeijingClock.Now;
        var cycles = knownCycles is { Count: > 0 }
            ? knownCycles
            : GeminiQuotaCycleHistory.Read(now, cancellationToken, homeOverride);
        if (cycles.Count == 0)
        {
            return new QuotaCostCurveResult([], QuotaCostCurveCalculator.BuildBands([]), PlanGroupLabel);
        }

        var earliest = cycles.Min(c => c.Start);
        IReadOnlyList<TokenUsageBucket> allRows;
        if (homeOverride is null && UsageSourceReaders.For(UsageSource.Gemini) is GeminiUsageReader reader)
        {
            allRows = reader.ReadDetailRows(earliest, now.AddMinutes(1), includeLiveToday: true, cancellationToken);
        }
        else
        {
            allRows = GeminiUsageReader.ReadTransientDetailRowsWithStatus(earliest, now.AddMinutes(1), cancellationToken, homeOverride).Rows;
        }

        var allObservations = GeminiQuotaCycleHistory.ReadWeekObservations(now);
        var curves = new List<QuotaCostCurveSeries>();

        foreach (var cycle in cycles.OrderBy(c => c.Start))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var end = cycle.IsCurrent && now < cycle.End ? now : cycle.End;
            if (end <= cycle.Start) continue;

            var cycleObs = allObservations
                .Where(obs => obs.ObservedAt > cycle.Start && obs.ObservedAt <= end.AddSeconds(1))
                .OrderBy(obs => obs.ObservedAt)
                .ToList();
            if (cycleObs.Count == 0 && cycle.Observation is { } singleObs)
            {
                cycleObs.Add(singleObs);
            }
            if (cycleObs.Count == 0) continue;

            var cycleRows = allRows
                .Where(r => r.StartLocal >= cycle.Start && r.StartLocal <= end)
                .OrderBy(r => r.StartLocal)
                .ToList();
            if (cycleRows.Count == 0) continue;

            var points = new List<QuotaCostCurvePoint>
            {
                new(cycle.Start, 0.0, 0m)
            };
            var runningUsage = new TokenUsageBucket { StartLocal = cycle.Start };
            var rowIndex = 0;
            var highestUsed = 0.0;

            foreach (var obs in cycleObs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (obs.UsedPercent + 0.5 < highestUsed) continue;
                while (rowIndex < cycleRows.Count && cycleRows[rowIndex].StartLocal <= obs.ObservedAt)
                {
                    runningUsage.MergeFrom(cycleRows[rowIndex]);
                    rowIndex++;
                }

                var gemini = GeminiQuotaEstimator.GeminiModelUsage([runningUsage], cycle.Start, end.AddTicks(1));
                var cost = GeminiQuotaEstimator.UsdCost(gemini);
                highestUsed = Math.Max(highestUsed, Math.Clamp(obs.UsedPercent, 0.0, 100.0));
                points.Add(new QuotaCostCurvePoint(obs.ObservedAt, highestUsed, cost));
            }

            if (points.Select(p => Math.Round(p.UsedPercent, 3)).Distinct().Count() < 2)
                continue;

            var label = cycle.IsCurrent
                ? $"当前 {cycle.Start:MM-dd HH:mm}"
                : $"{cycle.Start:MM-dd HH:mm}";
            curves.Add(new QuotaCostCurveSeries(
                cycle.Start,
                label,
                PlanGroupLabel,
                cycle.IsCurrent,
                points));
        }

        var ordered = curves
            .OrderByDescending(c => c.IsCurrent)
            .ThenByDescending(c => c.PeriodStart)
            .ToList();
        return new QuotaCostCurveResult(ordered, QuotaCostCurveCalculator.BuildBands(ordered), PlanGroupLabel);
    }
}

internal static class GeminiManualWeekEstimator
{
    public static string Build(
        GeminiQuotaWindowUsage? currentWeek,
        IReadOnlyList<GeminiQuotaCycleUsage> cycles,
        decimal firstRemainingInput,
        decimal secondRemainingInput,
        CancellationToken cancellationToken = default,
        string? homeOverride = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var activeCycle = cycles.FirstOrDefault(c => c.IsCurrent);
        var cycleStart = activeCycle?.Start ?? currentWeek?.StartLocal;
        var cycleEnd = activeCycle?.End ?? currentWeek?.ResetLocal;
        if (cycleStart is not { } startLocal || cycleEnd is not { } endLocal)
        {
            return "当前还没有已登记的 7d 额度周期，请先同步桌面额度或手动保存一次 7d 读数。";
        }

        var fromRemaining = Math.Max(firstRemainingInput, secondRemainingInput);
        var toRemaining = Math.Min(firstRemainingInput, secondRemainingInput);
        if (fromRemaining - toRemaining <= 0)
        {
            return "请选择不同的前后剩余百分比（如 100% → 73% 或 90% → 85%）。";
        }

        var startUsedThreshold = (double)(100m - fromRemaining);
        var endUsedThreshold = (double)(100m - toRemaining);
        var now = BeijingClock.Now;
        var effectiveEnd = now < endLocal ? now : endLocal;

        var anchors = new List<(DateTimeOffset At, double UsedPercent)>
        {
            (startLocal, 0.0)
        };
        foreach (var obs in GeminiQuotaCycleHistory.ReadWeekObservations(now)
                     .Where(o => o.ObservedAt > startLocal && o.ObservedAt <= effectiveEnd.AddMinutes(1))
                     .OrderBy(o => o.ObservedAt))
        {
            if (obs.UsedPercent + 0.5 < anchors[^1].UsedPercent) continue;
            anchors.Add((obs.ObservedAt, Math.Clamp(obs.UsedPercent, 0.0, 100.0)));
        }

        if (currentWeek?.ReportedUsedPercent is { } liveUsed &&
            (currentWeek.ObservedLocal ?? now) is { } liveObs &&
            liveObs > startLocal &&
            anchors.All(a => (a.At - liveObs).Duration() > TimeSpan.FromSeconds(2)))
        {
            anchors.Add((liveObs, Math.Clamp(liveUsed, 0.0, 100.0)));
            anchors.Sort((a, b) => a.At.CompareTo(b.At));
        }

        if (anchors.Count < 2)
        {
            return "当前 7d 周期还没有额度读数，请先同步桌面额度或登记一次百分比。";
        }

        var startAnchor = anchors.FirstOrDefault(item => item.UsedPercent + 0.001 >= startUsedThreshold);
        if (startAnchor == default)
        {
            return $"当前周期读数尚未进入剩余 {fromRemaining:0.#}% 附近（最高已用 {anchors.Max(a => a.UsedPercent):0.#}%）。";
        }

        var endAnchor = anchors.FirstOrDefault(item =>
            item.At > startAnchor.At &&
            item.UsedPercent + 0.001 >= endUsedThreshold);
        if (endAnchor == default)
        {
            return $"当前周期还没有到达剩余 {toRemaining:0.#}% 的后续读数（可尝试从 100% 到当前剩余 {(100 - anchors[^1].UsedPercent):0.#}%）。";
        }

        var observedDelta = (decimal)(endAnchor.UsedPercent - startAnchor.UsedPercent);
        if (observedDelta <= 0m)
        {
            return "所选读数区间内没有可用的额度消耗增量。";
        }

        IReadOnlyList<TokenUsageBucket> rows;
        if (homeOverride is null && UsageSourceReaders.For(UsageSource.Gemini) is GeminiUsageReader reader)
        {
            rows = reader.ReadDetailRows(startAnchor.At, endAnchor.At.AddSeconds(1), includeLiveToday: true, cancellationToken);
        }
        else
        {
            rows = GeminiUsageReader.ReadTransientDetailRowsWithStatus(startAnchor.At, endAnchor.At.AddSeconds(1), cancellationToken, homeOverride).Rows;
        }

        var gemini = GeminiQuotaEstimator.GeminiModelUsage(rows, startAnchor.At, endAnchor.At.AddTicks(1));
        var usedCost = GeminiQuotaEstimator.UsdCost(gemini);
        var estimatedLimit = QuotaMath.EstimateLimit(usedCost, observedDelta);
        if (estimatedLimit is null)
        {
            return "区间费用超出可表示范围，暂时不能估算 100% 额度。";
        }

        var estTokens = (long)Math.Min(long.MaxValue, (double)gemini.TotalTokens * 100.0 / (double)observedDelta);
        return $"{100.0 - startAnchor.UsedPercent:0.#}% → {100.0 - endAnchor.UsedPercent:0.#}%（消耗 {observedDelta:0.#}%） · " +
               $"{startAnchor.At:MM-dd HH:mm}–{endAnchor.At:MM-dd HH:mm} · " +
               $"{FormatTokens(gemini.TotalTokens)} Token（API 等价 ${usedCost:N2}） · " +
               $"折算 100% ≈ {FormatUsd(estimatedLimit.Value)}（≈ {FormatTokens(estTokens)} Token）";
    }

    private static string FormatTokens(long tokens) => tokens switch
    {
        >= 1_000_000_000 => $"{tokens / 1_000_000_000d:N3}B",
        >= 1_000_000 => $"{tokens / 1_000_000d:N3}M",
        >= 1_000 => $"{tokens / 1_000d:N1}K",
        _ => $"{tokens:N0}"
    };

    private static string FormatUsd(decimal value) => value switch
    {
        >= 100m => $"${value:N0}",
        >= 10m => $"${value:N2}",
        >= 1m => $"${value:N2}",
        _ => $"${value:N4}"
    };
}
