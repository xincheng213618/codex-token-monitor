namespace CodexTokenMonitor;

/// <summary>
/// Reuses one sorted quota timeline for all rows in a breakdown table.
/// </summary>
internal sealed class QuotaSnapshotLookup
{
    private static readonly TimeSpan FallbackTolerance = TimeSpan.FromMinutes(2);
    private readonly CodexQuotaSnapshot[] ordered;
    private readonly CodexQuotaSnapshot[] trusted;
    private readonly IReadOnlyDictionary<DateTimeOffset, CodexQuotaSnapshot> exact;

    public QuotaSnapshotLookup(IEnumerable<CodexQuotaSnapshot> snapshots)
    {
        ordered = snapshots.OrderBy(item => item.SnapshotLocal).ToArray();
        trusted = ordered.Where(item => !item.IsAnomaly).ToArray();
        exact = ordered
            .GroupBy(item => item.SnapshotLocal)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderByDescending(HasQuotaUsage)
                    .ThenBy(item => item.IsAnomaly)
                    .First());
    }

    public CodexQuotaSnapshot? Select(SelectedRange range, TokenUsageBucket bucket, bool eventBreakdown)
    {
        if (ordered.Length == 0)
        {
            return null;
        }

        if (eventBreakdown)
        {
            var tolerance = range.Mode == RangeMode.Day || range.IsCustomStart
                ? TimeSpan.FromMinutes(2)
                : TimeSpan.FromMinutes(10);
            return SelectForAnchor(bucket.StartLocal, tolerance);
        }

        var bucketEnd = bucket.StartLocal.AddDays(1);
        var inBucketIndex = LowerBound(ordered, bucketEnd) - 1;
        if (inBucketIndex >= 0 && ordered[inBucketIndex].SnapshotLocal >= bucket.StartLocal)
        {
            return ordered[inBucketIndex];
        }

        var beforeIndex = UpperBound(ordered, bucketEnd) - 1;
        if (beforeIndex >= 0)
        {
            return ordered[beforeIndex];
        }

        var afterIndex = UpperBound(ordered, bucketEnd);
        return afterIndex < ordered.Length && ordered[afterIndex].SnapshotLocal <= bucketEnd.Add(FallbackTolerance)
            ? ordered[afterIndex]
            : null;
    }

    private CodexQuotaSnapshot? SelectForAnchor(DateTimeOffset anchor, TimeSpan tolerance)
    {
        if (exact.TryGetValue(anchor, out var exactMatch))
        {
            return exactMatch;
        }

        var nearest = FindNearest(ordered, anchor, tolerance);
        if (nearest?.IsAnomaly == true && Math.Abs((nearest.SnapshotLocal - anchor).TotalSeconds) <= 2)
        {
            return nearest;
        }

        var beforeIndex = UpperBound(trusted, anchor) - 1;
        var afterIndex = LowerBound(trusted, anchor);
        var before = beforeIndex >= 0 ? trusted[beforeIndex] : null;
        var after = afterIndex < trusted.Length ? trusted[afterIndex] : null;
        if (before is not null && after is not null && before.SnapshotLocal != after.SnapshotLocal)
        {
            var fiveHour = Interpolate(
                before,
                after,
                anchor,
                item => item.FiveHourUsedPercent,
                item => item.FiveHourResetAtLocal);
            var week = Interpolate(
                before,
                after,
                anchor,
                item => item.WeekUsedPercent,
                item => item.WeekResetAtLocal);
            if (fiveHour is not null || week is not null)
            {
                return new CodexQuotaSnapshot(
                    anchor,
                    !string.IsNullOrWhiteSpace(before.LimitId) ? before.LimitId : after.LimitId,
                    !string.IsNullOrWhiteSpace(before.LimitName) ? before.LimitName : after.LimitName,
                    fiveHour,
                    SelectReset(before.FiveHourResetAtLocal, after.FiveHourResetAtLocal),
                    week,
                    SelectReset(before.WeekResetAtLocal, after.WeekResetAtLocal));
            }
        }

        return nearest is { IsAnomaly: false }
            ? nearest
            : FindNearest(trusted, anchor, tolerance);
    }

    private static CodexQuotaSnapshot? FindNearest(
        IReadOnlyList<CodexQuotaSnapshot> source,
        DateTimeOffset anchor,
        TimeSpan tolerance)
    {
        if (source.Count == 0)
        {
            return null;
        }

        var index = LowerBound(source, anchor);
        CodexQuotaSnapshot? best = null;
        for (var candidateIndex = Math.Max(0, index - 1);
             candidateIndex < source.Count && source[candidateIndex].SnapshotLocal <= anchor.Add(tolerance);
             candidateIndex++)
        {
            var candidate = source[candidateIndex];
            var distance = Math.Abs((candidate.SnapshotLocal - anchor).TotalSeconds);
            if (distance > tolerance.TotalSeconds)
            {
                continue;
            }

            if (best is null || IsBetter(candidate, best, anchor))
            {
                best = candidate;
            }
        }

        return best;
    }

    private static bool IsBetter(CodexQuotaSnapshot candidate, CodexQuotaSnapshot current, DateTimeOffset anchor)
    {
        var candidateDistance = Math.Abs((candidate.SnapshotLocal - anchor).TotalSeconds);
        var currentDistance = Math.Abs((current.SnapshotLocal - anchor).TotalSeconds);
        if (candidateDistance != currentDistance)
        {
            return candidateDistance < currentDistance;
        }

        if (HasQuotaUsage(candidate) != HasQuotaUsage(current))
        {
            return HasQuotaUsage(candidate);
        }

        return candidate.SnapshotLocal <= anchor && current.SnapshotLocal > anchor;
    }

    private static decimal? Interpolate(
        CodexQuotaSnapshot before,
        CodexQuotaSnapshot after,
        DateTimeOffset anchor,
        Func<CodexQuotaSnapshot, decimal?> usedSelector,
        Func<CodexQuotaSnapshot, DateTimeOffset?> resetSelector)
    {
        var beforeUsed = usedSelector(before);
        var afterUsed = usedSelector(after);
        if (beforeUsed is null || afterUsed is null)
        {
            return beforeUsed ?? afterUsed;
        }

        if (!SameReset(resetSelector(before), resetSelector(after)) || afterUsed.Value + 1m < beforeUsed.Value)
        {
            return null;
        }

        var totalSeconds = (after.SnapshotLocal - before.SnapshotLocal).TotalSeconds;
        if (totalSeconds <= 0)
        {
            return Clamp(beforeUsed.Value);
        }

        var ratio = (decimal)((anchor - before.SnapshotLocal).TotalSeconds / totalSeconds);
        return Clamp(beforeUsed.Value + ((afterUsed.Value - beforeUsed.Value) * ratio));
    }

    private static DateTimeOffset? SelectReset(DateTimeOffset? before, DateTimeOffset? after)
    {
        return SameReset(before, after) ? after ?? before : null;
    }

    private static bool SameReset(DateTimeOffset? first, DateTimeOffset? second)
    {
        return first is not null && second is not null &&
               Math.Abs((first.Value - second.Value).TotalMinutes) <= 10;
    }

    private static decimal Clamp(decimal value) => Math.Max(0m, Math.Min(100m, value));

    private static bool HasQuotaUsage(CodexQuotaSnapshot snapshot)
    {
        return (snapshot.FiveHourUsedPercent ?? 0m) > 0m || (snapshot.WeekUsedPercent ?? 0m) > 0m;
    }

    private static int LowerBound(IReadOnlyList<CodexQuotaSnapshot> source, DateTimeOffset value)
    {
        var low = 0;
        var high = source.Count;
        while (low < high)
        {
            var middle = low + ((high - low) / 2);
            if (source[middle].SnapshotLocal < value)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }

    private static int UpperBound(IReadOnlyList<CodexQuotaSnapshot> source, DateTimeOffset value)
    {
        var low = 0;
        var high = source.Count;
        while (low < high)
        {
            var middle = low + ((high - low) / 2);
            if (source[middle].SnapshotLocal <= value)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }
}
