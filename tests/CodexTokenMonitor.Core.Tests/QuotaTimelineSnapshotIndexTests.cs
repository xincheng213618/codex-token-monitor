using Xunit;

namespace CodexTokenMonitor.Tests;

public sealed class QuotaTimelineSnapshotIndexTests
{
    [Fact]
    public void IndexedNeighborsMatchReferenceForExactTiesBoundariesAndSparseWindows()
    {
        var day = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.FromHours(8));
        var source = Enumerable.Range(0, 1000).Select(i => new CodexQuotaSnapshot(
            day.AddSeconds(i * 20), "codex", null, i % 3 == 0 ? i % 100 : null,
            day.AddHours(5), i % 100, day.AddDays(7))).ToArray();
        foreach (var snapshots in new[] { source, source.Where(s => s.FiveHourUsedPercent is not null).ToArray(), Array.Empty<CodexQuotaSnapshot>() })
        {
            var index = new QuotaTimelineSnapshotIndex(snapshots);
            for (var i = -1; i < 2002; i++)
            {
                var anchor = day.AddSeconds(i * 10);
                var actual = index.Find(anchor);
                Assert.Equal(snapshots.LastOrDefault(s => s.SnapshotLocal <= anchor), actual.Before);
                Assert.Equal(snapshots.FirstOrDefault(s => s.SnapshotLocal >= anchor), actual.After);
                Assert.Equal(snapshots.OrderBy(s => (s.SnapshotLocal - anchor).Duration()).FirstOrDefault(), actual.Nearest);
                Assert.Equal(snapshots.Any(s => (s.SnapshotLocal - anchor).Duration() <= TimeSpan.FromMinutes(10) && s.WeekUsedPercent > 80),
                    index.AnyNearby(anchor, TimeSpan.FromMinutes(10), s => s.WeekUsedPercent > 80));
            }
        }
    }
}
