namespace CodexTokenMonitor;

/// <summary>
/// Adapts the ZCode plan to the shared cycle-analysis pipeline. Balance
/// snapshots are persisted here as the quota time series (one Codex-style
/// point per token bucket, mapped onto the week-window fields), and
/// <see cref="BuildAnalysis"/> aggregates ZCode usage events between snapshot
/// anchors so the shared band/forecast machinery can consume them. Unlike the
/// Codex weekly cycle the plan period is explicit (period_start/expires_at),
/// so no reset-boundary inference runs.
/// </summary>
internal sealed class ZCodeQuotaAnalysisSource : IQuotaCycleAnalysisSource
{
    private const string CacheFolder = "ZCodeTokenMonitor";
    private const int HistoryDayLimit = 60;

    private readonly string? historyLimitPrefix;

    /// <summary>
    /// When set, analysis only sees balance points of that plan (prefix of the
    /// stored LimitId). Multiple concurrent plans each meter their own buckets,
    /// so an unscoped query would mix one plan's consumption into another's.
    /// </summary>
    public ZCodeQuotaAnalysisSource(string? historyLimitPrefix = null)
    {
        this.historyLimitPrefix = historyLimitPrefix;
    }

    public bool ResolvesCurrentPeriod => false;

    /// <summary>Plan-scoped LimitId prefix used by the persisted balance history.</summary>
    public static string HistoryLimitPrefix(ZCodeQuotaPlan plan) => plan.SelectionKey + ":";

    private static string HistoryLimitId(ZCodeQuotaPlan plan, ZCodeQuotaBalance balance) =>
        HistoryLimitPrefix(plan) + (balance.ModelId ?? balance.ModelName);

    private bool MatchesScope(CodexQuotaSnapshot point) =>
        historyLimitPrefix is null ||
        (point.LimitId?.StartsWith(historyLimitPrefix, StringComparison.Ordinal) ?? false);

    // ---- balance history ----

    /// <summary>
    /// Appends one point per metered balance of every plan after a successful
    /// quota read. Points within two seconds of an existing one for the same
    /// bucket are merged, so a manual refresh right after the timed one cannot
    /// double the series.
    /// </summary>
    public static void RecordSnapshot(ZCodeQuotaSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        var points = snapshot.Plans
            .SelectMany(plan => plan.Balances.Select(item => (plan, item)))
            .Where(entry => entry.item.UsedPercent is not null)
            .Select(entry => new CodexQuotaSnapshot(
                snapshot.SnapshotLocal,
                LimitId: HistoryLimitId(entry.plan, entry.item),
                LimitName: entry.item.ModelName,
                FiveHourUsedPercent: null,
                FiveHourResetAtLocal: null,
                WeekUsedPercent: entry.item.UsedPercent,
                WeekResetAtLocal: entry.item.ExpiresAtLocal ?? entry.item.PeriodEndLocal))
            .ToArray();
        if (points.Length == 0)
        {
            return;
        }

        var cache = QuotaSnapshotCacheStore.Load(CacheFolder);
        var day = DateOnly.FromDateTime(snapshot.SnapshotLocal.DateTime);
        var dayStart = StartOfDay(day);
        var merged = cache.GetSnapshots(dayStart, dayStart.AddDays(1), cancellationToken)
            .Where(old => !points.Any(point =>
                string.Equals(point.LimitId, old.LimitId, StringComparison.Ordinal) &&
                Math.Abs((point.SnapshotLocal - old.SnapshotLocal).Ticks) <= TimeSpan.FromSeconds(2).Ticks))
            .Concat(points)
            .OrderBy(item => item.SnapshotLocal)
            .ToList();
        cache.Put(day, merged, isComplete: true, scannedThroughLocal: null, cancellationToken);
    }

    public static IReadOnlyList<CodexQuotaSnapshot> ReadHistory(
        DateTimeOffset startLocal,
        DateTimeOffset endLocal,
        CancellationToken cancellationToken = default)
    {
        var cache = QuotaSnapshotCacheStore.Load(CacheFolder);
        var startDay = DateOnly.FromDateTime(startLocal.DateTime);
        var endDay = DateOnly.FromDateTime(endLocal.DateTime);
        if (endDay.DayNumber - startDay.DayNumber > HistoryDayLimit)
        {
            startDay = endDay.AddDays(-HistoryDayLimit);
        }

        var result = new List<CodexQuotaSnapshot>();
        for (var day = startDay; day <= endDay; day = day.AddDays(1))
        {
            cancellationToken.ThrowIfCancellationRequested();
            result.AddRange(cache.GetSnapshots(StartOfDay(day), StartOfDay(day).AddDays(1), cancellationToken));
        }

        return result;
    }

    /// <summary>Maps the selected plan's live balance onto the analysis period and window estimate.</summary>
    public static ZCodeQuotaAnalysisDescription? DescribeCurrent(ZCodeQuotaSnapshot snapshot, ZCodeQuotaPlan plan)
    {
        var balance = plan.PrimaryBalance;
        if (balance?.UsedPercent is not { } usedPercent)
        {
            return null;
        }

        var start = balance.PeriodStartLocal ??
                    plan.Balances.Select(item => item.PeriodStartLocal)
                        .Where(item => item is not null).Min() ??
                    plan.StartsAtLocal ??
                    snapshot.SnapshotLocal;
        var end = balance.ExpiresAtLocal ?? balance.PeriodEndLocal ?? plan.EndsAtLocal;
        if (end is not { } periodEnd || periodEnd <= start)
        {
            return null;
        }

        var prefix = HistoryLimitPrefix(plan);
        var now = BeijingClock.Now;
        var historyPoints = ReadHistory(start, periodEnd)
            .Count(item => item.WeekUsedPercent is not null &&
                           (item.LimitId?.StartsWith(prefix, StringComparison.Ordinal) ?? false) &&
                           item.SnapshotLocal >= start && item.SnapshotLocal <= periodEnd);
        var period = new CodexQuotaCycle(
            start,
            periodEnd,
            ResetAt: periodEnd,
            historyPoints,
            MaxWeekUsedPercent: usedPercent,
            IsCurrent: now < periodEnd);
        var estimate = new CodexQuotaWindowEstimate(
            Label: "ZCode 套餐",
            UsedPercent: usedPercent,
            WindowMinutes: (int)(periodEnd - start).TotalMinutes,
            WindowStartLocal: start,
            WindowEndLocal: now < periodEnd ? now : periodEnd,
            ResetAtLocal: periodEnd,
            Usage: new TokenUsageSummary { StartLocal = start, EndLocal = periodEnd },
            UsedGptCost: 0m,
            EstimatedGptLimit: null,
            EstimatedTokenLimit: balance.TotalUnits);
        return new ZCodeQuotaAnalysisDescription(period, estimate);
    }

    // ---- pipeline adaptation ----

    public IReadOnlyList<CodexQuotaSnapshot> ReadSnapshots(
        DateTimeOffset start, DateTimeOffset end, CancellationToken token) =>
        ReadHistory(start, end, token).Where(MatchesScope).ToList();

    private const int MaxPeriods = 30;

    /// <summary>
    /// Rebuilds the plan's period list (newest first) from the recorded balance
    /// history: points sharing a reset time form one period, and consecutive
    /// reset times tile the period starts. Mirrors the Codex cycle list so the
    /// shared 按周期 toolbar and analysis pipeline can consume any past window,
    /// not only the live one.
    /// </summary>
    public static IReadOnlyList<CodexQuotaCycle> ReadPeriods(
        string? historyLimitPrefix, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var end = now;
        var start = end.AddDays(-HistoryDayLimit);
        var points = ReadHistory(start, end, cancellationToken)
            .Where(item => item.WeekUsedPercent is not null && item.WeekResetAtLocal is not null)
            .Where(item => historyLimitPrefix is null ||
                           (item.LimitId?.StartsWith(historyLimitPrefix, StringComparison.Ordinal) ?? false))
            .OrderBy(item => item.WeekResetAtLocal)
            .ThenBy(item => item.SnapshotLocal)
            .ToList();
        if (points.Count == 0)
        {
            return Array.Empty<CodexQuotaCycle>();
        }

        var clusters = new List<List<CodexQuotaSnapshot>>();
        foreach (var point in points)
        {
            if (clusters.Count > 0 &&
                CodexQuotaCycleReader.IsSameQuotaReset(clusters[^1][0].WeekResetAtLocal, point.WeekResetAtLocal))
            {
                clusters[^1].Add(point);
            }
            else
            {
                clusters.Add(new List<CodexQuotaSnapshot> { point });
            }
        }

        var periods = new List<CodexQuotaCycle>(clusters.Count);
        for (var index = 0; index < clusters.Count; index++)
        {
            var cluster = clusters[index];
            var resetAt = cluster[0].WeekResetAtLocal!.Value;
            // Consecutive reset times tile the boundary; the oldest cluster has
            // no predecessor, so its first observation stands in for the start.
            var periodStart = index > 0
                ? clusters[index - 1][0].WeekResetAtLocal!.Value.AddSeconds(1)
                : cluster.Min(item => item.SnapshotLocal);
            if (periodStart >= resetAt)
            {
                periodStart = cluster.Min(item => item.SnapshotLocal);
            }

            periods.Add(new CodexQuotaCycle(
                periodStart,
                resetAt,
                resetAt,
                cluster.Count,
                cluster.Max(item => item.WeekUsedPercent) ?? 0m,
                IsCurrent: now < resetAt));
        }

        periods.Reverse();
        return periods.Count > MaxPeriods ? periods.Take(MaxPeriods).ToList() : periods;
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
            return QuotaCycleAnalysisResult.Empty(period, "套餐周期时间范围无效") with
            {
                BandSizePercent = bandSizePercent
            };
        }

        var rows = ZCodeUsageReader.ReadDetailRows(start, end, includeLiveToday: true, token)
            .Where(item => item.StartLocal >= start && item.StartLocal < end)
            .OrderBy(item => item.StartLocal)
            .ToList();
        if (rows.Count == 0)
        {
            return QuotaCycleAnalysisResult.Empty(period, "这个套餐周期没有可用的逐条 Token 记录") with
            {
                BandSizePercent = bandSizePercent
            };
        }

        var points = ReadHistory(start, end, token)
            .Where(MatchesScope)
            .Where(item => item.WeekUsedPercent is not null &&
                           CodexQuotaCycleReader.IsSameQuotaReset(item.WeekResetAtLocal, period.ResetAt))
            .OrderBy(item => item.SnapshotLocal)
            .ToList();
        var samples = BuildSamples(start, points, rows, token);
        return QuotaCycleAnalysisCalculator.BuildFromSamples(
            period, samples, bandSizePercent, token, priceGroup: PricePresetGroups.ZCode);
    }

    public QuotaModelCapacityReport BuildCapacities(
        CodexQuotaCycle period, QuotaCycleAnalysisResult analysis,
        CodexQuotaCycle? previousPeriod, CancellationToken token) =>
        new("-", Array.Empty<QuotaModelCapacityEstimate>());

    /// <summary>
    /// Anchors samples on the balance snapshots instead of usage rows (the
    /// Codex path): each sample carries the usage recorded since the previous
    /// snapshot, plus one trailing sample for events newer than the last poll.
    /// Buckets sharing an expiry collapse to the highest observed usage so a
    /// timestamp represents one quota observation.
    /// </summary>
    internal static IReadOnlyList<QuotaCycleAnalysisSample> BuildSamples(
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

        foreach (var point in points.GroupBy(item => item.SnapshotLocal)
                     .Select(group => group.OrderByDescending(item => item.WeekUsedPercent).First())
                     .OrderBy(item => item.SnapshotLocal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var anchor = point.SnapshotLocal;
            if (anchor <= previousTime)
            {
                continue;
            }

            while (rowIndex < rows.Count && rows[rowIndex].StartLocal < anchor)
            {
                pending.MergeFrom(rows[rowIndex]);
                rowIndex++;
            }

            lastUsedPercent = point.WeekUsedPercent!.Value;
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
            samples.Add(new QuotaCycleAnalysisSample(rows[^1].StartLocal, lastUsedPercent, pending));
        }

        return samples;
    }

    private static DateTimeOffset StartOfDay(DateOnly day) =>
        new(day.Year, day.Month, day.Day, 0, 0, 0, CodexUsageReader.BeijingOffset);
}

internal sealed record ZCodeQuotaAnalysisDescription(
    CodexQuotaCycle Period,
    CodexQuotaWindowEstimate Estimate);
