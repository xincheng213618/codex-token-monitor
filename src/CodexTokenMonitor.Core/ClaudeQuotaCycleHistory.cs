namespace CodexTokenMonitor;

internal sealed record ClaudeWeeklyQuotaObservation(
    DateTimeOffset ResetAt, DateTimeOffset ObservedAt, double UsedPercent, double? FableUsedPercent = null)
{
    public static ClaudeWeeklyQuotaObservation? FromSnapshot(ClaudeRateLimitSnapshot snapshot)
    {
        if (snapshot.SevenDay is not { } week) return null;
        var reset = ClaudeQuotaCycleHistory.NormalizeReset(week.ResetLocal);
        var fable = snapshot.FableWeek;
        return new(reset, week.ObservedLocal ?? snapshot.RecordedLocal, week.UsedPercent,
            fable is not null && ClaudeQuotaCycleHistory.NormalizeReset(fable.ResetLocal) == reset ? fable.UsedPercent : null);
    }
}

internal sealed record ClaudeQuotaCycleUsage(
    DateTimeOffset Start, DateTimeOffset Reset, bool IsCurrent,
    long? Tokens, long? Requests, decimal? ApiCostUsd, long UnpricedTokens,
    bool UsageIsPartial, DateTimeOffset? UsageThrough,
    ClaudeWeeklyQuotaObservation? Official, bool OfficialIsSample, decimal? FullReferenceUsd);

internal static class ClaudeQuotaCycleHistory
{
    private static readonly TimeSpan Week = TimeSpan.FromDays(7);

    // The server can report the same boundary at :59.999 or :00.
    public static DateTimeOffset NormalizeReset(DateTimeOffset reset)
    {
        var local = reset.ToOffset(CodexUsageReader.BeijingOffset);
        var seconds = local.TimeOfDay.Ticks % TimeSpan.TicksPerMinute;
        if (seconds <= TimeSpan.TicksPerSecond) return local.AddTicks(-seconds);
        var toMinute = TimeSpan.TicksPerMinute - seconds;
        return toMinute <= TimeSpan.TicksPerSecond ? local.AddTicks(toMinute) : local;
    }

    public static IReadOnlyList<ClaudeQuotaCycleUsage> Read(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var observations = ClaudeRateLimitLog.ReadWeeklyHistory();
        var readings = ClaudeQuotaReadingStore.Load();
        if (observations.Count == 0 && !readings.Any(IsOfficialWeek)) return [];

        // A single transient scan gives every row identical coverage and keeps
        // the history window independent of token-cache warmup.
        var (rows, complete) = ClaudeUsageReader.ReadTransientDetailRowsWithStatus(
            DateTimeOffset.UnixEpoch, now, cancellationToken);
        return Build(now, observations, readings, rows, complete, cancellationToken);
    }

    internal static IReadOnlyList<ClaudeQuotaCycleUsage> Build(
        DateTimeOffset now, IReadOnlyList<ClaudeWeeklyQuotaObservation> observations,
        IReadOnlyList<ClaudeQuotaReading> readings, IReadOnlyList<TokenUsageBucket> rows,
        bool complete, CancellationToken cancellationToken = default)
    {
        var official = observations.Where(reading => reading.ObservedAt <= now)
            .GroupBy(reading => NormalizeReset(reading.ResetAt))
            .ToDictionary(group => group.Key, group => group.MaxBy(reading => reading.ObservedAt)!);
        var samples = readings.Where(reading => IsOfficialWeek(reading) && reading.RecordedLocal <= now)
            .GroupBy(reading => NormalizeReset(reading.ResetLocal))
            .ToDictionary(group => group.Key, group => group.MaxBy(reading => reading.RecordedLocal)!);
        var anchors = official.Keys.Concat(samples.Keys).ToArray();
        if (anchors.Length == 0) return [];
        var anchor = anchors.Max();
        var current = EndOfWeek(now, anchor);
        var usageByCycle = new Dictionary<DateTimeOffset, TokenUsageBucket>();
        foreach (var group in rows.GroupBy(row => EndOfWeek(row.StartLocal, anchor)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var usage = ClaudeQuotaEstimator.ClaudeModelUsage(group, group.Key - Week, group.Key);
            if (usage.Events > 0) usageByCycle[group.Key] = usage;
        }

        // Official reset records take precedence. Older periods without an
        // official readout are projected from the fixed schedule and can still
        // show local usage. An empty interval has no recorded usage evidence.
        var cycles = anchors.Concat(usageByCycle.Keys).Append(current)
            .Where(reset => reset <= current && EndOfWeek(reset, anchor) == reset + Week)
            .Distinct().OrderDescending();
        var result = new List<ClaudeQuotaCycleUsage>();
        foreach (var reset in cycles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            usageByCycle.TryGetValue(reset, out var usage);
            samples.TryGetValue(reset, out var sample);
            official.TryGetValue(reset, out var observed);
            var observedIsSample = sample is not null &&
                (observed is null || sample.RecordedLocal - observed.ObservedAt > TimeSpan.FromSeconds(1));
            if (observedIsSample)
                observed = new(reset, sample!.RecordedLocal, sample.UsedPercent);

            var priced = usage is null ? null : CodexModelCost.Estimate(usage, PricePresetGroups.ClaudeCode);
            var cost = priced?.CostTotals.Where(total => total.CurrencySymbol == "$").Sum(total => total.Amount) ?? sample?.CostUsd;
            var reference = sample?.CapacityUsd;
            if (reset == current && observed is { UsedPercent: >= 1 } && priced is { UnpricedTokens: 0 } && cost is > 0)
            {
                var matching = ClaudeQuotaEstimator.ClaudeModelUsage(
                    rows.Where(row => row.StartLocal <= observed.ObservedAt), reset - Week, reset);
                var atObservation = ClaudeQuotaEstimator.UsdCost(matching);
                if (atObservation > 0) reference = atObservation * 100m / (decimal)observed.UsedPercent;
            }
            result.Add(new(reset - Week, reset, reset == current, usage?.TotalTokens ?? sample?.Tokens,
                usage?.Events ?? sample?.Events, cost, priced?.UnpricedTokens ?? 0,
                !complete || usage is null && sample is not null,
                usage is null ? sample?.RecordedLocal : reset == current ? now : reset,
                observed, observedIsSample, reference));
        }
        return result;
    }

    private static bool IsOfficialWeek(ClaudeQuotaReading reading) =>
        reading is { Kind: ClaudeQuotaWindowKind.Week, Source: ClaudeQuotaReadingSource.Official } &&
        reading.RecordedLocal < reading.ResetLocal && reading.RecordedLocal >= reading.ResetLocal - Week;

    private static DateTimeOffset EndOfWeek(DateTimeOffset at, DateTimeOffset anchor)
    {
        var weeks = Math.DivRem((at - anchor).Ticks, Week.Ticks, out var remainder);
        return anchor.AddDays((remainder < 0 ? weeks : weeks + 1) * 7);
    }
}
