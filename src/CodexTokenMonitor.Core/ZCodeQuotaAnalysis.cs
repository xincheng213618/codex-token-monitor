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

    public bool ResolvesCurrentPeriod => false;

    // ---- balance history ----

    /// <summary>
    /// Appends one point per metered balance after a successful quota read.
    /// Points within two seconds of an existing one for the same bucket are
    /// merged, so a manual refresh right after the timed one cannot double the
    /// series.
    /// </summary>
    public static void RecordSnapshot(ZCodeQuotaSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        var points = snapshot.Balances
            .Where(item => item.UsedPercent is not null)
            .Select(item => new CodexQuotaSnapshot(
                snapshot.SnapshotLocal,
                LimitId: item.ModelId ?? item.ModelName,
                LimitName: item.ModelName,
                FiveHourUsedPercent: null,
                FiveHourResetAtLocal: null,
                WeekUsedPercent: item.UsedPercent,
                WeekResetAtLocal: item.ExpiresAtLocal ?? item.PeriodEndLocal))
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

    /// <summary>Maps the live balance onto the analysis period and window estimate.</summary>
    public static ZCodeQuotaAnalysisDescription? DescribeCurrent(ZCodeQuotaSnapshot snapshot)
    {
        var balance = snapshot.PrimaryBalance;
        if (balance?.UsedPercent is not { } usedPercent)
        {
            return null;
        }

        var start = balance.PeriodStartLocal ??
                    snapshot.Balances.Select(item => item.PeriodStartLocal)
                        .Where(item => item is not null).Min() ??
                    snapshot.SnapshotLocal;
        var end = balance.ExpiresAtLocal ?? balance.PeriodEndLocal;
        if (end is not { } periodEnd || periodEnd <= start)
        {
            return null;
        }

        var now = BeijingClock.Now;
        var historyPoints = ReadHistory(start, periodEnd)
            .Count(item => item.WeekUsedPercent is not null &&
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
        DateTimeOffset start, DateTimeOffset end, CancellationToken token) => ReadHistory(start, end, token);

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
