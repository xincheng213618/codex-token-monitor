namespace CodexTokenMonitor;

internal static class QuotaCostCurveCalculator
{
    private const int MaxPlotPointsPerCycle = 1800;

    public static QuotaCostCurveResult Build(
        CodexQuotaEstimate currentQuota,
        IReadOnlyList<CodexQuotaCycle> knownPeriods,
        CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow.ToOffset(CodexUsageReader.BeijingOffset);
        var periods = knownPeriods.Count > 0
            ? knownPeriods
            : CodexQuotaCycleReader.ReadWeeklyCycles(currentQuota, now);
        var curves = new List<QuotaCostCurveSeries>();

        foreach (var period in periods.OrderBy(item => item.PeriodStart))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var end = period.IsCurrent ? Min(now, period.PeriodEnd) : period.PeriodEnd;
            if (end <= period.PeriodStart)
            {
                continue;
            }

            var usageRows = CodexUsageReader.ReadCachedDetailRows(period.PeriodStart, end)
                .Where(item => item.StartLocal >= period.PeriodStart && item.StartLocal < end)
                .OrderBy(item => item.StartLocal)
                .ToList();
            if (usageRows.Count == 0)
            {
                continue;
            }

            var timeline = CodexUsageReader.ReadMaterializedQuotaTimeline(
                    usageRows.Select(item => item.StartLocal))
                .Where(item =>
                    item.WeekUsedPercent is not null &&
                    !item.IsAnomaly &&
                    CodexQuotaCycleReader.IsSameQuotaReset(item.WeekResetAtLocal, period.ResetAt))
                .GroupBy(item => item.SnapshotLocal)
                .ToDictionary(group => group.Key, group => group.Last());
            if (timeline.Count == 0)
            {
                continue;
            }

            decimal cumulativeCost = 0;
            var points = new List<QuotaCostCurvePoint>();
            foreach (var row in usageRows)
            {
                cumulativeCost += row.EstimateCost(PriceProfiles.PrimaryCodex);
                if (!timeline.TryGetValue(row.StartLocal, out var snapshot) ||
                    snapshot.WeekUsedPercent is not { } usedPercent)
                {
                    continue;
                }

                points.Add(new QuotaCostCurvePoint(
                    row.StartLocal,
                    Math.Clamp((double)usedPercent, 0, 100),
                    cumulativeCost));
            }

            points = RemoveQuotaRegressions(points);
            if (points.Count < 2)
            {
                continue;
            }

            var baselineCost = points[0].CumulativeCost;
            points = points
                .Select(item => item with { CumulativeCost = Math.Max(0, item.CumulativeCost - baselineCost) })
                .ToList();

            curves.Add(new QuotaCostCurveSeries(
                period.IsCurrent ? $"Current {period.PeriodStart:MM-dd HH:mm}" : $"{period.PeriodStart:MM-dd HH:mm}",
                ResolvePlanName(period.PeriodStart, end),
                period.IsCurrent,
                Downsample(points, MaxPlotPointsPerCycle)));
        }

        var selectedPlan = curves.FirstOrDefault(item => item.IsCurrent)?.PlanName ??
                           curves.LastOrDefault()?.PlanName ??
                           "未设置套餐";
        var selectedCurves = curves
            .Where(item => string.Equals(item.PlanName, selectedPlan, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return new QuotaCostCurveResult(
            curves.OrderByDescending(item => item.IsCurrent).ThenByDescending(item => item.Points[0].TimestampLocal).ToList(),
            BuildBands(selectedCurves),
            selectedPlan);
    }

    internal static IReadOnlyList<QuotaCostCurveBand> BuildBands(IReadOnlyList<QuotaCostCurveSeries> curves)
    {
        var result = new List<QuotaCostCurveBand>();
        for (var lower = 0; lower < 100; lower += 10)
        {
            var upper = lower + 10;
            var costs = new List<decimal>();
            foreach (var curve in curves)
            {
                var lowerCost = InterpolateCost(curve.Points, lower);
                var upperCost = InterpolateCost(curve.Points, upper);
                if (lowerCost is null || upperCost is null || upperCost < lowerCost)
                {
                    continue;
                }

                costs.Add(upperCost.Value - lowerCost.Value);
            }

            if (costs.Count == 0)
            {
                result.Add(new QuotaCostCurveBand($"{lower}-{upper}%", "-", "-", "-", 0));
                continue;
            }

            costs.Sort();
            var average = costs.Average();
            var median = costs.Count % 2 == 1
                ? costs[costs.Count / 2]
                : (costs[costs.Count / 2 - 1] + costs[costs.Count / 2]) / 2m;
            result.Add(new QuotaCostCurveBand(
                $"{lower}-{upper}%",
                FormatMoney(average),
                FormatMoney(median),
                FormatMoney(median / 10m),
                costs.Count));
        }

        return result;
    }

    private static string ResolvePlanName(DateTimeOffset startLocal, DateTimeOffset endLocal)
    {
        var summary = SubscriptionPlanStore.Summarize(startLocal, endLocal);
        var dominant = summary.Records
            .Select(item => new
            {
                item.PlanName,
                Duration = Min(endLocal, item.EndLocal) - Max(startLocal, item.StartLocal)
            })
            .Where(item => item.Duration > TimeSpan.Zero && !string.IsNullOrWhiteSpace(item.PlanName))
            .GroupBy(item => item.PlanName.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group => new
            {
                PlanName = group.Key,
                Ticks = group.Sum(item => item.Duration.Ticks)
            })
            .OrderByDescending(item => item.Ticks)
            .FirstOrDefault();
        return dominant?.PlanName ?? "未设置套餐";
    }

    private static decimal? InterpolateCost(IReadOnlyList<QuotaCostCurvePoint> source, double targetPercent)
    {
        var points = source
            .GroupBy(item => Math.Round(item.UsedPercent, 3))
            .Select(group => new
            {
                UsedPercent = group.Key,
                Cost = group.Min(item => item.CumulativeCost)
            })
            .OrderBy(item => item.UsedPercent)
            .ToList();
        if (points.Count == 0 || targetPercent < points[0].UsedPercent || targetPercent > points[^1].UsedPercent)
        {
            return null;
        }

        var exact = points.FirstOrDefault(item => Math.Abs(item.UsedPercent - targetPercent) < 0.001);
        if (exact is not null)
        {
            return exact.Cost;
        }

        var afterIndex = points.FindIndex(item => item.UsedPercent > targetPercent);
        if (afterIndex <= 0)
        {
            return null;
        }

        var before = points[afterIndex - 1];
        var after = points[afterIndex];
        var span = after.UsedPercent - before.UsedPercent;
        if (span <= 0)
        {
            return before.Cost;
        }

        var ratio = (decimal)((targetPercent - before.UsedPercent) / span);
        return before.Cost + (after.Cost - before.Cost) * ratio;
    }

    private static IReadOnlyList<QuotaCostCurvePoint> Downsample(
        IReadOnlyList<QuotaCostCurvePoint> points,
        int maxPoints)
    {
        if (points.Count <= maxPoints)
        {
            return points;
        }

        var result = new List<QuotaCostCurvePoint>(maxPoints) { points[0] };
        var step = (points.Count - 1d) / (maxPoints - 1d);
        for (var index = 1; index < maxPoints - 1; index++)
        {
            result.Add(points[(int)Math.Round(index * step)]);
        }

        result.Add(points[^1]);
        return result;
    }

    private static List<QuotaCostCurvePoint> RemoveQuotaRegressions(
        IReadOnlyList<QuotaCostCurvePoint> source)
    {
        var result = new List<QuotaCostCurvePoint>(source.Count);
        double? highestUsedPercent = null;
        foreach (var point in source)
        {
            if (highestUsedPercent is not null && point.UsedPercent + 0.5 < highestUsedPercent.Value)
            {
                continue;
            }

            highestUsedPercent = Math.Max(highestUsedPercent ?? point.UsedPercent, point.UsedPercent);
            result.Add(point);
        }

        return result;
    }

    private static DateTimeOffset Min(DateTimeOffset first, DateTimeOffset second)
    {
        return first <= second ? first : second;
    }

    private static DateTimeOffset Max(DateTimeOffset first, DateTimeOffset second)
    {
        return first >= second ? first : second;
    }

    private static string FormatMoney(decimal value)
    {
        var symbol = PriceProfiles.PrimaryCodex.CurrencySymbol;
        var prefix = string.Equals(symbol, "Credits", StringComparison.OrdinalIgnoreCase)
            ? "Credits "
            : symbol;
        return value switch
        {
            >= 100 => $"{prefix}{value:N0}",
            >= 10 => $"{prefix}{value:N2}",
            >= 1 => $"{prefix}{value:N3}",
            _ => $"{prefix}{value:N4}"
        };
    }
}

internal sealed record QuotaCostCurveResult(
    IReadOnlyList<QuotaCostCurveSeries> Curves,
    IReadOnlyList<QuotaCostCurveBand> Bands,
    string SelectedPlan);

internal sealed record QuotaCostCurveSeries(
    string Label,
    string PlanName,
    bool IsCurrent,
    IReadOnlyList<QuotaCostCurvePoint> Points);

internal sealed record QuotaCostCurvePoint(
    DateTimeOffset TimestampLocal,
    double UsedPercent,
    decimal CumulativeCost);

internal sealed record QuotaCostCurveBand(
    string Range,
    string AverageCost,
    string MedianCost,
    string CostPerPercent,
    int Samples);
