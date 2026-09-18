using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Xunit;

namespace CodexTokenMonitor.Tests;

public sealed class QuotaSnapshotLookupTests
{
    private static readonly TimeSpan Beijing = TimeSpan.FromHours(8);

    [Fact]
    public void Select_ReturnsExactEventSnapshot()
    {
        var start = new DateTimeOffset(2026, 7, 11, 0, 0, 0, Beijing);
        var exact = Snapshot(start.AddMinutes(5), 20m, 40m);
        var lookup = new QuotaSnapshotLookup(new[] { exact });

        var selected = lookup.Select(DayRange(start), Bucket(start.AddMinutes(5)), eventBreakdown: true);

        Assert.Same(exact, selected);
    }

    [Fact]
    public void Select_InterpolatesMissingEventAnchor()
    {
        var start = new DateTimeOffset(2026, 7, 11, 0, 0, 0, Beijing);
        var reset = start.AddHours(5);
        var lookup = new QuotaSnapshotLookup(new[]
        {
            Snapshot(start, 10m, 30m, reset),
            Snapshot(start.AddMinutes(10), 20m, 40m, reset)
        });

        var selected = lookup.Select(DayRange(start), Bucket(start.AddMinutes(5)), eventBreakdown: true);

        Assert.NotNull(selected);
        Assert.Equal(15m, selected!.FiveHourUsedPercent);
        Assert.Equal(35m, selected.WeekUsedPercent);
    }

    [Fact]
    public void Select_UsesLastSnapshotInsideDailyBucket()
    {
        var start = new DateTimeOffset(2026, 7, 11, 0, 0, 0, Beijing);
        var evening = Snapshot(start.AddHours(23), 75m, 80m);
        var lookup = new QuotaSnapshotLookup(new[]
        {
            Snapshot(start.AddHours(1), 10m, 20m),
            evening,
            Snapshot(start.AddDays(1), 1m, 2m)
        });

        var selected = lookup.Select(DayRange(start), Bucket(start), eventBreakdown: false);

        Assert.Same(evening, selected);
    }

    [Fact]
    public void Select_UsesProvidedIntervalForAggregatedMultiDayBucket()
    {
        var start = new DateTimeOffset(2026, 7, 11, 0, 0, 0, Beijing);
        var first = Snapshot(start.AddMinutes(5), 10m, 20m);
        var nextBucket = Snapshot(start.AddMinutes(15), 30m, 40m);
        var lookup = new QuotaSnapshotLookup(new[] { first, nextBucket });
        var range = new SelectedRange(start, start.AddDays(7), "", "", RangeMode.Week);

        var selected = lookup.Select(
            range,
            Bucket(start),
            eventBreakdown: false,
            bucketInterval: TimeSpan.FromMinutes(10));

        Assert.Same(first, selected);
    }

    [Fact]
    public void Select_DoesNotPullNextBucketBoundaryBackward()
    {
        var start = new DateTimeOffset(2026, 7, 11, 0, 0, 0, Beijing);
        var previous = Snapshot(start.AddMinutes(-1), 10m, 20m);
        var nextBucket = Snapshot(start.AddMinutes(10), 30m, 40m);
        var lookup = new QuotaSnapshotLookup(new[] { previous, nextBucket });
        var range = new SelectedRange(start, start.AddDays(7), "", "", RangeMode.Week);

        var selected = lookup.Select(
            range,
            Bucket(start),
            eventBreakdown: false,
            bucketInterval: TimeSpan.FromMinutes(10));

        Assert.Same(previous, selected);
    }

    [Fact]
    public void Select_HandlesLargeDayWithoutQuadraticRescans()
    {
        var start = new DateTimeOffset(2026, 7, 11, 0, 0, 0, Beijing);
        var snapshots = Enumerable.Range(0, 12_000)
            .Select(index => Snapshot(start.AddSeconds(index * 7), index % 100, index % 100))
            .ToArray();
        var lookup = new QuotaSnapshotLookup(snapshots);
        var range = DayRange(start);
        var stopwatch = Stopwatch.StartNew();

        for (var index = 0; index < 12_000; index++)
        {
            _ = lookup.Select(range, Bucket(start.AddSeconds(index * 7)), eventBreakdown: true);
        }

        stopwatch.Stop();
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1), $"Lookup took {stopwatch.Elapsed}.");
    }

    [Fact]
    public void QuotaWindowClassification_RejectsThirtyDayResetCardWindow()
    {
        Assert.True(CodexUsageReader.IsFiveHourWindow(300));
        Assert.True(CodexUsageReader.IsWeeklyWindow(10_080));
        Assert.False(CodexUsageReader.IsWeeklyWindow(43_200));
    }

    [Fact]
    public void NormalizeQuotaSnapshotWindows_DropsPersistedThirtyDayWindow()
    {
        var snapshotTime = new DateTimeOffset(2026, 7, 2, 14, 13, 0, Beijing);
        var snapshot = new CodexQuotaSnapshot(
            snapshotTime,
            "codex",
            null,
            null,
            null,
            15m,
            snapshotTime.AddDays(30));

        var normalized = CodexUsageReader.NormalizeQuotaSnapshotWindows(snapshot);

        Assert.Null(normalized.WeekUsedPercent);
        Assert.Null(normalized.WeekResetAtLocal);
    }

    [Fact]
    public void NormalizeQuotaSnapshotWindows_DropsOutOfRangePercentages()
    {
        var snapshotTime = new DateTimeOffset(2026, 7, 2, 14, 13, 0, Beijing);
        var snapshot = new CodexQuotaSnapshot(
            snapshotTime,
            "codex",
            null,
            101m,
            snapshotTime.AddHours(5),
            -1m,
            snapshotTime.AddDays(7));

        var normalized = CodexUsageReader.NormalizeQuotaSnapshotWindows(snapshot);

        Assert.Null(normalized.FiveHourUsedPercent);
        Assert.Null(normalized.FiveHourResetAtLocal);
        Assert.Null(normalized.WeekUsedPercent);
        Assert.Null(normalized.WeekResetAtLocal);
    }

    [Fact]
    public void QuotaCache_DoesNotMarkAllInvalidSnapshotsAsComplete()
    {
        using var paths = new IsolatedPaths();
        var folder = $"CodexTokenMonitorTests-{Guid.NewGuid():N}";
        var date = new DateOnly(2026, 7, 19);
        var snapshotTime = new DateTimeOffset(date.Year, date.Month, date.Day, 2, 0, 0, Beijing);
        var invalid = new CodexQuotaSnapshot(
            snapshotTime,
            "codex",
            "Codex",
            101m,
            snapshotTime.AddHours(5),
            -1m,
            snapshotTime.AddDays(7));

        try
        {
            var cache = QuotaSnapshotCacheStore.Load(folder);
            cache.Put(date, new[] { invalid }, isComplete: true, scannedThroughLocal: snapshotTime);

            Assert.True(cache.TryGetRecord(date, out var record));
            Assert.False(record.IsComplete);
            Assert.Empty(record.Snapshots);
            Assert.Contains(
                QuotaSnapshotCacheStore.GetIncompleteDays(folder, snapshotTime, snapshotTime),
                item => item == new DateTimeOffset(date.Year, date.Month, date.Day, 0, 0, 0, Beijing));
        }
        finally
        {
            UsageCacheStore.Delete(folder);
        }
    }

    [Fact]
    public void QuotaCache_PreflightDetectsCorruptedCompleteDay()
    {
        using var paths = new IsolatedPaths();
        var folder = $"CodexTokenMonitorTests-{Guid.NewGuid():N}";
        var date = new DateOnly(2026, 7, 20);
        var dayStart = new DateTimeOffset(date.Year, date.Month, date.Day, 0, 0, 0, Beijing);
        var snapshotTime = dayStart.AddHours(2);
        try
        {
            var cache = QuotaSnapshotCacheStore.Load(folder);
            cache.Put(
                date,
                new[] { Snapshot(snapshotTime, 10m, 20m) },
                isComplete: true,
                scannedThroughLocal: snapshotTime);

            using (var connection = new SqliteConnection($"Data Source={QuotaSnapshotCacheStore.GetCachePath(folder)}"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    UPDATE quota_snapshots
                    SET five_hour_used_percent = '101',
                        week_used_percent = '-1'
                    WHERE date = $date;
                    UPDATE quota_days SET is_complete = 1 WHERE date = $date;
                    """;
                command.Parameters.AddWithValue("$date", date.ToString("yyyy-MM-dd"));
                command.ExecuteNonQuery();
            }

            Assert.Contains(
                QuotaSnapshotCacheStore.GetIncompleteDays(folder, dayStart, dayStart),
                item => item == dayStart);
            Assert.True(cache.TryGetRecord(date, out var record));
            Assert.False(record.IsValid);
            Assert.False(record.IsComplete);
        }
        finally
        {
            UsageCacheStore.Delete(folder);
        }
    }

    [Fact]
    public void LockedQuotaLogDoesNotMarkHistoricalDayComplete()
    {
        using var paths = new IsolatedPaths();
        var date = new DateOnly(2026, 7, 21);
        var dayStart = new DateTimeOffset(date.Year, date.Month, date.Day, 0, 0, 0, Beijing);
        var codexHome = UsageLogPaths.GetOverrideRoot(UsageSource.Codex)!;
        var sessions = Path.Combine(codexHome, "sessions", "2026", "07", "21");
        var logPath = Path.Combine(sessions, "locked.jsonl");
        Directory.CreateDirectory(sessions);
        File.WriteAllText(logPath, "{\"type\":\"event_msg\"}\n");

        try
        {
            using (var lockStream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                UsageSourceReaders.Codex.WarmQuotaSnapshotDay(dayStart);
            }

            var cache = QuotaSnapshotCacheStore.Load("CodexTokenMonitor");
            Assert.True(cache.TryGetRecord(date, out var record));
            Assert.False(record.IsComplete);
        }
        finally
        {
            UsageCacheStore.Delete("CodexTokenMonitor");
        }
    }

    private static SelectedRange DayRange(DateTimeOffset start)
    {
        return new SelectedRange(start, start.AddDays(1), "", "", RangeMode.Day);
    }

    private static TokenUsageBucket Bucket(DateTimeOffset time)
    {
        return new TokenUsageBucket { StartLocal = time };
    }

    private static CodexQuotaSnapshot Snapshot(
        DateTimeOffset time,
        decimal fiveHour,
        decimal week,
        DateTimeOffset? reset = null)
    {
        return new CodexQuotaSnapshot(time, "codex", "Codex", fiveHour, reset, week, reset);
    }

    private sealed class IsolatedPaths : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), $"QuotaSnapshotPaths-{Guid.NewGuid():N}");
        private readonly IDisposable cacheScope;
        private readonly IDisposable logScope;

        public IsolatedPaths()
        {
            cacheScope = MonitorCachePaths.PushLocalAppDataRoot(Path.Combine(root, "cache"));
            logScope = UsageLogPaths.PushRoot(Path.Combine(root, "logs"));
        }

        public void Dispose()
        {
            logScope.Dispose();
            cacheScope.Dispose();
            var resolved = Path.GetFullPath(root);
            var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!resolved.StartsWith(temp, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(resolved).StartsWith("QuotaSnapshotPaths-", StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing to clean up outside the isolated test directory.");
            if (Directory.Exists(resolved)) Directory.Delete(resolved, recursive: true);
        }
    }
}
