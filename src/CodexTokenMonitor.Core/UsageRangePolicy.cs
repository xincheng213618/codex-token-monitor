namespace CodexTokenMonitor;

internal static class UsageRangePolicy
{
    private static readonly TimeSpan LiveEndTolerance = TimeSpan.FromSeconds(2);

    public static bool ShouldReadLiveToday(SelectedRange range, DateTimeOffset nowLocal)
    {
        ArgumentNullException.ThrowIfNull(range);

        var localNow = nowLocal.ToOffset(CodexUsageReader.BeijingOffset);
        return range.Start <= localNow &&
               range.End >= localNow.Subtract(LiveEndTolerance);
    }
}
