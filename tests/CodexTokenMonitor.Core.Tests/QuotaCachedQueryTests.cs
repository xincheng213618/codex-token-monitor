using System.Text.Json;
using Xunit;

namespace CodexTokenMonitor.Tests;

public sealed class QuotaCachedQueryTests
{
    private static readonly TimeSpan Beijing = TimeSpan.FromHours(8);
    private static readonly DateTimeOffset Day = new(2026, 2, 15, 0, 0, 0, Beijing);
    private static readonly DateTimeOffset Now = Day.AddHours(12);
    private static readonly DateTimeOffset Reset = Day.AddDays(1);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CachedTimeline_ProjectsMissingAnchorsWithoutWritingAndMatchesMaterialization(bool sourceIsHistoryCache)
    {
        using var isolated = new IsolatedCache();
        var cache = QuotaSnapshotCacheStore.Load("CodexTokenMonitor");
        var snapshots = new[] { Snapshot(Day.AddHours(9), 10, 30), Snapshot(Day.AddHours(10), 30, 50) };
        if (sourceIsHistoryCache)
        {
            var history = Path.Combine(Path.GetDirectoryName(isolated.CachePath)!, "quota-history-v2.jsonl");
            File.WriteAllLines(history, snapshots.Select(snapshot => JsonSerializer.Serialize(new
            {
                snapshotLocal = snapshot.SnapshotLocal,
                limitId = "codex",
                limitName = "Codex",
                fiveHour = new { usedPercent = snapshot.FiveHourUsedPercent, windowMinutes = 300, resetAtLocal = snapshot.FiveHourResetAtLocal },
                week = new { usedPercent = snapshot.WeekUsedPercent, windowMinutes = 10080, resetAtLocal = snapshot.WeekResetAtLocal }
            })));
        }
        else
        {
            cache.Put(DateOnly.FromDateTime(Day.DateTime), snapshots, true, Day.AddDays(1), propagateErrors: true);
        }
        var anchors = new[] { Day.AddHours(9), Day.AddHours(9).AddMinutes(30), Day.AddHours(10) };
        using var operation = CacheOperationDiagnostics.Begin();

        var readOnly = UsageSourceReaders.Codex.ReadCachedQuotaTimeline(anchors);

        Assert.Equal(3, readOnly.Count);
        Assert.Equal(20m, readOnly[1].FiveHourUsedPercent);
        Assert.Equal(40m, readOnly[1].WeekUsedPercent);
        Assert.Empty(cache.GetTimelineSnapshots(Day, Day.AddDays(1)));
        var materialized = UsageSourceReaders.Codex.ReadMaterializedQuotaTimeline(anchors);
        Assert.Equal(readOnly, materialized);
        Assert.Equal(materialized, cache.GetTimelineSnapshots(Day, Day.AddDays(1)));
        Assert.Equal(materialized, UsageSourceReaders.Codex.ReadCachedQuotaTimeline(anchors));
        Assert.Empty(operation.Warnings);
    }

    [Fact]
    public void CachedTimeline_UsesSupplementalSnapshotsWithoutPersistingThem()
    {
        using var isolated = new IsolatedCache();
        var cache = QuotaSnapshotCacheStore.Load("CodexTokenMonitor");
        var supplemental = new[] { Snapshot(Day.AddHours(9), 10, 30), Snapshot(Day.AddHours(10), 30, 50) };
        var anchor = Day.AddHours(9).AddMinutes(30);

        var result = UsageSourceReaders.Codex.ReadCachedQuotaTimeline(new[] { anchor }, supplemental);

        Assert.Equal(40m, Assert.Single(result).WeekUsedPercent);
        Assert.Empty(cache.GetSnapshots(Day, Day.AddDays(1)));
        Assert.Empty(cache.GetTimelineSnapshots(Day, Day.AddDays(1)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WeeklyCycleFailure_DoesNotBecomeSuccessfulCacheHitAfterStorageRecovery(bool hasParentDiagnostics)
    {
        using var isolated = new IsolatedCache();
        Directory.CreateDirectory(Path.GetDirectoryName(isolated.CachePath)!);
        File.WriteAllText(isolated.CachePath, "deliberately invalid test database");
        var quota = CurrentQuota();
        using (var parent = hasParentDiagnostics ? CacheOperationDiagnostics.Begin() : null)
        {
            var failed = UsageSourceReaders.Codex.Cycles.ReadWeeklyCycles(quota, Now);
            Assert.DoesNotContain(failed, period => !period.IsCurrent);
            if (parent is not null)
                Assert.Contains(parent.Warnings, warning => warning.Kind == CacheWarningKind.Corrupt);
        }

        // Only the test fixture removes its damaged file. Keep the cycle cache
        // untouched to detect publication of the preceding fallback result.
        UsageCacheStore.Delete("CodexTokenMonitor");
        SeedPreviousCycle();
        using var recovered = CacheOperationDiagnostics.Begin();
        var actual = UsageSourceReaders.Codex.Cycles.ReadWeeklyCycles(quota, Now);

        Assert.Contains(actual, period => !period.IsCurrent && period.ResetAt == Reset.AddDays(-7));
        Assert.Empty(recovered.Warnings);
    }

    [Fact]
    public void WeeklyCycleCache_IsolatedByDatabasePath()
    {
        using var first = new IsolatedCache();
        SeedPreviousCycle();
        var quota = CurrentQuota();
        var firstResult = UsageSourceReaders.Codex.Cycles.ReadWeeklyCycles(quota, Now);
        Assert.Contains(firstResult, period => !period.IsCurrent);
        using var second = new IsolatedCache();
        QuotaSnapshotCacheStore.Load("CodexTokenMonitor");

        var secondResult = UsageSourceReaders.Codex.Cycles.ReadWeeklyCycles(quota, Now);

        Assert.DoesNotContain(secondResult, period => !period.IsCurrent);
        Assert.Single(secondResult);
    }

    [Fact]
    public void NestedDiagnostics_ExplicitPropagationPreservesWarningsOnException()
    {
        using var parent = CacheOperationDiagnostics.Begin();
        var error = new IOException("synthetic read failure");
        Assert.Throws<InvalidOperationException>((Action)(() =>
        {
            using var nested = CacheOperationDiagnostics.Begin(propagateToParent: true);
            CacheOperationDiagnostics.Report("fixture", "Read", error);
            throw new InvalidOperationException("synthetic outer failure");
        }));
        using (var nested = CacheOperationDiagnostics.Begin(propagateToParent: true))
        {
            CacheOperationDiagnostics.Report("fixture", "Read", error);
        }
        Assert.Single(parent.Warnings);
        using (var isolated = CacheOperationDiagnostics.Begin())
        {
            CacheOperationDiagnostics.Report("independent", "Read", error);
            Assert.Single(isolated.Warnings);
        }
        Assert.Equal("fixture", Assert.Single(parent.Warnings).Path);
    }

    private static void SeedPreviousCycle()
    {
        var previousTime = Day.AddDays(-7).AddHours(9);
        var previous = new CodexQuotaSnapshot(previousTime, "codex", "Codex", 20, previousTime.AddHours(5), 80, Reset.AddDays(-7));
        QuotaSnapshotCacheStore.Load("CodexTokenMonitor").Put(
            DateOnly.FromDateTime(previousTime.DateTime), new[] { previous }, true, previousTime.AddDays(1), propagateErrors: true);
    }

    private static CodexQuotaEstimate CurrentQuota()
    {
        var week = new CodexQuotaWindowEstimate("7d", 20, 10080, Reset.AddDays(-7), Now,
            Reset, new TokenUsageSummary(), 0, null, null);
        return new CodexQuotaEstimate(Now, "codex", "Codex", null, week);
    }

    private static CodexQuotaSnapshot Snapshot(DateTimeOffset at, decimal fiveHour, decimal week) =>
        new(at, "codex", "Codex", fiveHour, Day.AddHours(14), week, Reset);

    private sealed class IsolatedCache : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), $"CodexQuotaQueryTests-{Guid.NewGuid():N}");
        private readonly IDisposable cacheScope;
        private readonly IDisposable logScope;
        public string CachePath => UsageCacheStore.GetCachePath("CodexTokenMonitor");

        public IsolatedCache()
        {
            Directory.CreateDirectory(root);
            cacheScope = MonitorCachePaths.PushLocalAppDataRoot(root);
            logScope = UsageLogPaths.PushRoot(Path.Combine(root, "logs"));
        }

        public void Dispose()
        {
            UsageCacheStore.Delete("CodexTokenMonitor");
            logScope.Dispose();
            cacheScope.Dispose();
            var resolved = Path.GetFullPath(root);
            var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!resolved.StartsWith(temp, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(resolved).StartsWith("CodexQuotaQueryTests-", StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing to clean up outside the isolated test directory.");
            if (Directory.Exists(resolved)) Directory.Delete(resolved, recursive: true);
        }
    }
}
