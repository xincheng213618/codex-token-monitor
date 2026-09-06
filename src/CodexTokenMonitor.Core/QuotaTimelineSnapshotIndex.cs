namespace CodexTokenMonitor;

// Built once per source range, then reused for every usage-event anchor.
internal sealed class QuotaTimelineSnapshotIndex(IEnumerable<CodexQuotaSnapshot> source)
{
    private readonly CodexQuotaSnapshot[] snapshots = source.OrderBy(item => item.SnapshotLocal).ToArray();

    public (CodexQuotaSnapshot? Before, CodexQuotaSnapshot? After, CodexQuotaSnapshot? Nearest) Find(DateTimeOffset anchor)
    {
        var index = LowerBound(anchor);
        var after = index < snapshots.Length ? snapshots[index] : null;
        var before = after?.SnapshotLocal == anchor ? after : index > 0 ? snapshots[index - 1] : null;
        var nearest = before is null ? after : after is null ? before :
            anchor - before.SnapshotLocal <= after.SnapshotLocal - anchor ? before : after;
        return (before, after, nearest);
    }

    public bool AnyNearby(DateTimeOffset anchor, TimeSpan tolerance, Func<CodexQuotaSnapshot, bool> predicate)
    {
        for (var index = LowerBound(anchor - tolerance);
             index < snapshots.Length && snapshots[index].SnapshotLocal <= anchor + tolerance; index++)
        {
            if (predicate(snapshots[index])) return true;
        }
        return false;
    }

    private int LowerBound(DateTimeOffset anchor)
    {
        var low = 0;
        var high = snapshots.Length;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (snapshots[middle].SnapshotLocal < anchor) low = middle + 1;
            else high = middle;
        }
        return low;
    }
}
