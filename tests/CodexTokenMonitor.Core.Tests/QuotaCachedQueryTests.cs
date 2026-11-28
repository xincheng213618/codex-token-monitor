using System.Text.Json;
using Xunit;

namespace CodexTokenMonitor.Tests;

public sealed class QuotaCachedQueryTests
{
    private static readonly TimeSpan Beijing = TimeSpan.FromHours(8);
    private static readonly DateTimeOffset Day = new(2026, 2, 15, 0, 0, 0, Beijing);
    private static readonly DateTimeOffset Now = Day.AddHours(12);
    private static readonly DateTimeOffset Reset = Day.AddDays(1);

    [Fact]
    public async Task CurrentCyclePreviewBypassesGateWithoutSealingDaysOrWritingCalibration()
    {
        using var isolated = new IsolatedCache();
        var now = BeijingClock.Now;
        var start = new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, Beijing).AddDays(-1);
        var period = new CodexQuotaCycle(start, now, start.AddDays(7), 2, 10m, true);
        var events = new[]
        {
            new TokenUsageEvent(start.AddHours(1), 100, 40, 10, 0, 110, "codex:first", ModelId: "gpt-5.6-sol"),
            new TokenUsageEvent(start.AddHours(2), 200, 80, 20, 0, 220, "codex:second", ModelId: "gpt-5.6-sol")
        };
        var usage = UsageCacheStore.Load();
        var bucket = new TokenUsageBucket { StartLocal = start };
        foreach (var item in events) bucket.Add(item);
        usage.Put(bucket, isComplete: false, scannedThroughLocal: start.AddHours(2), detailEvents: events);
        var quota = QuotaSnapshotCacheStore.Load("CodexTokenMonitor");
        quota.Put(DateOnly.FromDateTime(start.DateTime), events.Select((item, i) => new CodexQuotaSnapshot(
            item.Timestamp, "codex", "Codex", null, null, (i + 1) * 5m, period.ResetAt)).ToArray(), false, start.AddHours(2));
        var cacheFolder = Path.GetDirectoryName(isolated.CachePath)!;
        var settingsPath = Path.Combine(cacheFolder, "monitor-settings.sqlite3");
        Assert.False(File.Exists(settingsPath));
        using var runtime = new MonitorRuntime();
        using var session = new AnalysisQuerySession(runtime);
        await runtime.SharedIoGate.WaitAsync();
        try
        {
            var loaded = await session.RunAsync("cached cycle", token =>
                new QuotaCycleAnalysisQueryService().ExecuteCached(new(period), token)).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Empty(loaded.CacheWarnings);
            Assert.True(loaded.Value.Analysis.HasData);
            Assert.Equal(330, loaded.Value.Analysis.Tokens);
            Assert.Empty(loaded.Value.Capacities.Estimates);
            Assert.Empty(quota.GetTimelineSnapshots(start, now));
            Assert.True(usage.TryGetRecord(DateOnly.FromDateTime(start.DateTime), out var record));
            Assert.False(record.IsComplete);
            Assert.False(File.Exists(settingsPath));
            Assert.False(File.Exists(Path.Combine(cacheFolder, CodexLogFileIndex.FileName)));
        }
        finally { runtime.SharedIoGate.Release(); }
    }

    [Fact]
    public void CachedCyclePreviewReadsSavedFitWithoutChangingSettingsOrTraining()
    {
        using var isolated = new IsolatedCache();
        var period = new CodexQuotaCycle(Day, Day.AddDays(1), Day.AddDays(7), 2, 10m, false);
        var events = new[]
        {
            new TokenUsageEvent(Day.AddHours(1), 100, 40, 10, 0, 110, ModelId: "gpt-5.6-sol"),
            new TokenUsageEvent(Day.AddHours(2), 200, 80, 20, 0, 220, ModelId: "gpt-5.6-sol")
        };
        var bucket = new TokenUsageBucket { StartLocal = Day };
        foreach (var item in events) bucket.Add(item);
        UsageCacheStore.Load().Put(bucket, true, Day.AddDays(1), detailEvents: events);
        QuotaSnapshotCacheStore.Load("CodexTokenMonitor").Put(DateOnly.FromDateTime(Day.DateTime),
            events.Select((item, index) => new CodexQuotaSnapshot(item.Timestamp, "codex", "Codex", null, null,
                (index + 1) * 5m, period.ResetAt)).ToArray(), true, Day.AddDays(1));
        SubscriptionPlanStore.Save(new[] { new SubscriptionPlanRecord
        {
            Id = "plan", StartLocal = Day, EndLocal = Day.AddDays(8), PlanName = "test plan", AmountCny = 1m
        } });
        QuotaModelCapacityCalibrationStore.Upsert("test plan", period, new[]
        {
            new QuotaModelCapacityEstimate("gpt-5.6-sol", 3, 1_200m, 1_000m, 1_400m,
                QuotaModelCapacitySource.CurrentPeriodBlended, Day)
        });
        var before = File.ReadAllBytes(MonitorSettingsDatabase.Path);
        var result = new QuotaCycleAnalysisQueryService().ExecuteCached(new(period));
        Assert.Equal(1_200m, Assert.Single(result.Capacities.Estimates).AverageFullQuotaCost);
        Assert.NotNull(QuotaModelCapacityEstimator.FitFullQuotaCost(result.Analysis.Bands[0], result.Capacities.Estimates));
        Assert.Equal(before, File.ReadAllBytes(MonitorSettingsDatabase.Path));
    }

    [Fact]
    public void CachedCycleUsesObservedQuotaTransitionsBeforeInterpolatingMissingLevels()
    {
        using var isolated = new IsolatedCache();
        var reset = Day.AddDays(7);
        var end = Day.AddMinutes(90);
        var period = new CodexQuotaCycle(Day, end, reset, 4, 5m, false);
        var events = new[]
        {
            UsageAt(10, 1_000_000, "first"),
            UsageAt(20, 2_000_000, "at-transition"),
            UsageAt(20, 3_000_000, "also-at-transition"),
            UsageAt(25, 4_000_000, "second"),
            UsageAt(30, 1_000_000, "at-second-transition"),
            UsageAt(50, 4_000_000, "missing-level"),
            UsageAt(60, 4_000_000, "at-jump"),
            UsageAt(70, 2_000_000, "before-snapshot-without-usage")
        };
        var bucket = new TokenUsageBucket { StartLocal = Day };
        foreach (var item in events) bucket.Add(item);
        UsageCacheStore.Load().Put(bucket, true, end, detailEvents: events, propagateErrors: true);

        var quota = QuotaSnapshotCacheStore.Load("CodexTokenMonitor");
        quota.Put(DateOnly.FromDateTime(Day.DateTime), new[]
        {
            WeeklySnapshot(Day.AddMinutes(20), 1m),
            WeeklySnapshot(Day.AddMinutes(30), 2m),
            WeeklySnapshot(Day.AddMinutes(60), 4m),
            WeeklySnapshot(Day.AddMinutes(80), 5m)
        }, true, end, propagateErrors: true);
        var stale = events.Select(item => item.Timestamp).Distinct()
            .Select((time, index) => WeeklySnapshot(time, 10m + index)).ToArray();
        quota.PutTimelineSnapshots(stale, stale.ToDictionary(
            item => item.SnapshotLocal,
            _ => ((DateTimeOffset?)Day, (DateTimeOffset?)end)));

        var result = QuotaCycleAnalysisCalculator.BuildCached(period, currentWeek: null, bandSizePercent: 1m);

        Assert.Equal(5m, result.ObservedQuotaDropPercent);
        Assert.Equal(21_000_000, result.Tokens);
        Assert.Equal(new long[] { 6_000_000, 5_000_000, 4_000_000, 4_000_000, 2_000_000 },
            result.Bands.Select(band => band.Tokens).ToArray());
        Assert.Equal(new[] { 0, 20, 30, 45, 60 }.Select(minutes => Day.AddMinutes(minutes)),
            result.Bands.Select(band => band.StartLocal));
        Assert.Equal(new[] { 20, 30, 45, 60, 80 }.Select(minutes => Day.AddMinutes(minutes)),
            result.Bands.Select(band => band.EndLocal));
        Assert.Equal(stale, quota.GetTimelineSnapshots(Day, end));

        TokenUsageEvent UsageAt(int minute, long tokens, string key) => new(
            Day.AddMinutes(minute), tokens, 0, 0, 0, tokens, key,
            ModelId: "gpt-6.2-sol", ServiceTier: "default");

        CodexQuotaSnapshot WeeklySnapshot(DateTimeOffset timestamp, decimal used) =>
            new(timestamp, "codex", "Codex", null, null, used, reset);
    }

    [Fact]
    public void CurrentCycleReadsQuotaCrossingsFromSessionJsonl()
    {
        using var isolated = new IsolatedCache();
        var end = BeijingClock.Now.AddSeconds(-1);
        var start = end.AddMinutes(-5);
        var reset = start.AddDays(7);
        var period = new CodexQuotaCycle(start, end, reset, 0, 2m, true);
        var folder = Path.Combine(UsageLogPaths.GetCodexHome(), "sessions");
        Directory.CreateDirectory(folder);
        File.WriteAllLines(Path.Combine(folder, "cycle-quota.jsonl"), new[]
        {
            JsonSerializer.Serialize(new
            {
                timestamp = start, type = "turn_context",
                payload = new { model = "gpt-6.2-sol", service_tier = "default" }
            }),
            UsageSnapshot(1, 0, 1_000_000),
            UsageSnapshot(1.5, 1, null),
            UsageSnapshot(2, 1, 2_000_000),
            UsageSnapshot(3, 2, 4_000_000)
        });
        Assert.Empty(UsageSourceReaders.Codex.ReadCachedQuotaSnapshots(start, end));

        var result = QuotaCycleAnalysisCalculator.Build(period, currentWeek: null, bandSizePercent: 1m);

        Assert.Collection(result.Bands,
            first =>
            {
                Assert.Equal(start.AddMinutes(1.5), first.EndLocal);
                Assert.Equal(1_000_000, first.Tokens);
            },
            second =>
            {
                Assert.Equal(start.AddMinutes(3), second.EndLocal);
                Assert.Equal(6_000_000, second.Tokens);
            });

        string UsageSnapshot(double minute, int usedPercent, long? tokens) => JsonSerializer.Serialize(new
        {
            timestamp = start.AddMinutes(minute), type = "event_msg", payload = new
            {
                type = "token_count",
                info = tokens is { } count
                    ? new { last_token_usage = new { input_tokens = count, total_tokens = count } }
                    : null,
                rate_limits = new
                {
                    limit_id = "codex",
                    secondary = new { used_percent = usedPercent, window_minutes = 10080, resets_at = reset.ToUnixTimeSeconds() }
                }
            }
        });
    }

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
