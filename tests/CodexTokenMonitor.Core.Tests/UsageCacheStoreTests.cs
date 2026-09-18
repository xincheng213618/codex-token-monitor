using Microsoft.Data.Sqlite;
using Xunit;

namespace CodexTokenMonitor.Tests;

public sealed class UsageCacheStoreTests
{
    [Fact]
    public void CacheRoundTrip_PreservesCacheWriteCounters()
    {
        var folder = $"CodexTokenMonitorTests-{Guid.NewGuid():N}";
        var dayStart = new DateTimeOffset(2026, 8, 24, 0, 0, 0, TimeSpan.FromHours(8));
        var usageEvent = new TokenUsageEvent(
            dayStart.AddHours(9),
            1000,
            600,
            50,
            10,
            1050,
            "cache-write-roundtrip",
            100);
        var bucket = new TokenUsageBucket { StartLocal = dayStart };
        bucket.Add(
            usageEvent.Timestamp,
            usageEvent.InputTokens,
            usageEvent.CachedInputTokens,
            usageEvent.CacheWriteInputTokens,
            usageEvent.OutputTokens,
            usageEvent.ReasoningOutputTokens,
            usageEvent.TotalTokens);

        try
        {
            var cache = UsageCacheStore.Load(folder);
            cache.Put(bucket, isComplete: true, detailEvents: new[] { usageEvent });

            var summary = cache.ReadRange(dayStart, dayStart.AddDays(1));
            var detail = Assert.Single(cache.GetDetailEvents(DateOnly.FromDateTime(dayStart.DateTime)));

            Assert.Equal(100, summary.CacheWriteInputTokens);
            Assert.Equal(300, summary.UncachedInputTokens);
            Assert.Equal(100, detail.CacheWriteInputTokens);
            Assert.Equal(dayStart, Assert.Single(summary.DailyBuckets).StartLocal);
        }
        finally
        {
            UsageCacheStore.Delete(folder);
        }
    }

    [Fact]
    public void GetIncompleteDays_IncludesDaysMissingFromFreshCache()
    {
        var folder = $"CodexTokenMonitorTests-{Guid.NewGuid():N}";
        var start = new DateTimeOffset(2026, 7, 10, 0, 0, 0, TimeSpan.FromHours(8));
        try
        {
            var cache = UsageCacheStore.Load(folder);
            cache.Put(new TokenUsageBucket { StartLocal = start.AddDays(1) }, isComplete: true);

            var incomplete = UsageCacheStore.GetIncompleteDays(folder, start, start.AddDays(2));

            Assert.Equal(new[] { start.AddDays(2), start }, incomplete);
        }
        finally
        {
            UsageCacheStore.Delete(folder);
        }
    }

    [Fact]
    public void IncompleteCacheChecks_PropagateCancellationBeforeOpeningDatabase()
    {
        var folder = $"CodexTokenMonitorTests-{Guid.NewGuid():N}";
        var start = new DateTimeOffset(2026, 7, 10, 0, 0, 0, TimeSpan.FromHours(8));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try
        {
            Assert.Throws<OperationCanceledException>(() => UsageCacheStore.GetIncompleteDays(
                folder,
                start,
                start.AddDays(2),
                cancellation.Token));
            Assert.Throws<OperationCanceledException>(() => QuotaSnapshotCacheStore.GetIncompleteDays(
                folder,
                start,
                start.AddDays(2),
                cancellation.Token));
            Assert.Throws<OperationCanceledException>(() => QuotaSnapshotCacheStore.Load(folder).GetIncompleteTimelineDays(
                start,
                start.AddDays(2),
                cancellation.Token));
        }
        finally
        {
            UsageCacheStore.Delete(folder);
        }
    }

    [Fact]
    public void DetailCacheReads_PropagateCancellationBeforeOpeningDatabase()
    {
        var folder = $"CodexTokenMonitorTests-{Guid.NewGuid():N}";
        var dayStart = new DateTimeOffset(2026, 7, 10, 0, 0, 0, TimeSpan.FromHours(8));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try
        {
            var cache = UsageCacheStore.Load(folder);

            var date = DateOnly.FromDateTime(dayStart.DateTime);
            Assert.Throws<OperationCanceledException>(() => cache.GetDetailEvents(date, cancellation.Token));
            Assert.Throws<OperationCanceledException>(() => cache.GetDetailEvents(
                dayStart,
                dayStart.AddDays(1),
                cancellation.Token));
            Assert.Throws<OperationCanceledException>(() => cache.HasDetailEvents(date, cancellation.Token));
        }
        finally
        {
            UsageCacheStore.Delete(folder);
        }
    }

    [Fact]
    public void TransferEnumerators_PropagateCancellationBeforeOpeningReader()
    {
        var folder = $"CodexTokenMonitorTests-{Guid.NewGuid():N}";
        var start = new DateTimeOffset(2026, 7, 10, 0, 0, 0, TimeSpan.FromHours(8));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try
        {
            var usageCache = UsageCacheStore.Load(folder);
            var quotaCache = QuotaSnapshotCacheStore.Load(folder);

            Assert.Throws<OperationCanceledException>(() => usageCache
                .EnumerateDetailEvents(start, start.AddDays(1), cancellation.Token)
                .ToList());
            Assert.Throws<OperationCanceledException>(() => quotaCache
                .EnumerateSnapshots(start, start.AddDays(1), cancellation.Token)
                .ToList());
        }
        finally
        {
            UsageCacheStore.Delete(folder);
        }
    }

    [Fact]
    public void IncompleteTimelineDays_NormalizesUtcBoundsToBeijing()
    {
        var folder = $"CodexTokenMonitorTests-{Guid.NewGuid():N}";
        var firstDay = new DateTimeOffset(2026, 7, 13, 0, 0, 0, TimeSpan.FromHours(8));
        var targetDay = firstDay.AddDays(1);
        var usageEvent = new TokenUsageEvent(
            targetDay.AddHours(2),
            100,
            50,
            20,
            0,
            120,
            "timeline-utc-boundary");
        var bucket = new TokenUsageBucket { StartLocal = targetDay };
        bucket.Add(
            usageEvent.Timestamp,
            usageEvent.InputTokens,
            usageEvent.CachedInputTokens,
            usageEvent.OutputTokens,
            usageEvent.ReasoningOutputTokens,
            usageEvent.TotalTokens);

        try
        {
            UsageCacheStore.Load(folder).Put(
                bucket,
                isComplete: true,
                detailEvents: new[] { usageEvent });

            var incomplete = QuotaSnapshotCacheStore.Load(folder).GetIncompleteTimelineDays(
                firstDay.ToUniversalTime(),
                targetDay.ToUniversalTime());

            Assert.Equal(new[] { targetDay }, incomplete);
        }
        finally
        {
            UsageCacheStore.Delete(folder);
        }
    }

    [Fact]
    public void Put_DoesNotMutateUsageCacheWhenCancellationIsAlreadyRequested()
    {
        var folder = $"CodexTokenMonitorTests-{Guid.NewGuid():N}";
        var dayStart = new DateTimeOffset(2026, 7, 16, 0, 0, 0, TimeSpan.FromHours(8));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try
        {
            var cache = UsageCacheStore.Load(folder);

            Assert.Throws<OperationCanceledException>(() => cache.Put(
                new TokenUsageBucket { StartLocal = dayStart },
                cancellationToken: cancellation.Token));

            Assert.False(cache.TryGet(DateOnly.FromDateTime(dayStart.DateTime), out _));
        }
        finally
        {
            UsageCacheStore.Delete(folder);
        }
    }

    [Fact]
    public void MalformedPersistedCountersAreRejectedAsCacheHits()
    {
        var folder = $"CodexTokenMonitorTests-{Guid.NewGuid():N}";
        var date = new DateOnly(2026, 7, 18);
        var dayStart = new DateTimeOffset(date.Year, date.Month, date.Day, 0, 0, 0, TimeSpan.FromHours(8));
        try
        {
            var cache = UsageCacheStore.Load(folder);
            var bucket = new TokenUsageBucket { StartLocal = dayStart };
            bucket.Add(dayStart.AddHours(1), 100, 50, 10, 0, 110);
            cache.Put(bucket, isComplete: true);

            using (var connection = new SqliteConnection($"Data Source={UsageCacheStore.GetCachePath(folder)}"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    UPDATE usage_days
                    SET events = -1,
                        input_tokens = 100,
                        cached_input_tokens = 200,
                        uncached_input_tokens = 999,
                        output_tokens = -10,
                        total_tokens = -1,
                        long_context_events = 99,
                        long_context_input_tokens = 999,
                        long_context_cached_input_tokens = 999,
                        long_context_output_tokens = 999,
                        peak_input_tokens = 999,
                        peak_cached_input_tokens = 999,
                        peak_output_tokens = 999
                    WHERE date = $date
                    """;
                command.Parameters.AddWithValue("$date", date.ToString("yyyy-MM-dd"));
                command.ExecuteNonQuery();
            }

            Assert.True(cache.TryGetRecord(date, out var record));
            Assert.False(record.IsValid);
            Assert.False(record.IsComplete);
            Assert.Equal(100, record.InputTokens);
            Assert.Equal(100, record.CachedInputTokens);
            Assert.False(cache.TryGet(date, out _));
            Assert.Contains(
                UsageCacheStore.GetIncompleteDays(folder, dayStart, dayStart),
                item => item == dayStart);
        }
        finally
        {
            UsageCacheStore.Delete(folder);
        }
    }

    [Fact]
    public void Put_DoesNotMutateQuotaCacheWhenCancellationIsAlreadyRequested()
    {
        var folder = $"CodexTokenMonitorTests-{Guid.NewGuid():N}";
        var date = new DateOnly(2026, 7, 16);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try
        {
            var cache = QuotaSnapshotCacheStore.Load(folder);
            var snapshot = new CodexQuotaSnapshot(
                new DateTimeOffset(2026, 7, 16, 2, 0, 0, TimeSpan.FromHours(8)),
                "codex",
                null,
                null,
                null,
                12m,
                new DateTimeOffset(2026, 7, 23, 0, 0, 0, TimeSpan.FromHours(8)));

            Assert.Throws<OperationCanceledException>(() => cache.Put(
                date,
                new[] { snapshot },
                isComplete: true,
                scannedThroughLocal: snapshot.SnapshotLocal,
                cancellationToken: cancellation.Token));

            Assert.Empty(cache.GetSnapshots(date));
        }
        finally
        {
            UsageCacheStore.Delete(folder);
        }
    }

    [Fact]
    public void ReadQuotaSnapshots_PropagatesCancellationInsteadOfReturningAnEmptyCache()
    {
        var folder = $"CodexTokenMonitorTests-{Guid.NewGuid():N}";
        var date = new DateOnly(2026, 7, 16);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try
        {
            var cache = QuotaSnapshotCacheStore.Load(folder);

            Assert.Throws<OperationCanceledException>(() => cache.GetSnapshots(date, cancellation.Token));
            Assert.Throws<OperationCanceledException>(() => cache.GetSnapshots(
                date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified),
                date.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified),
                cancellation.Token));
        }
        finally
        {
            UsageCacheStore.Delete(folder);
        }
    }

    [Fact]
    public void ReadCachedQuotaEstimate_PropagatesCancellationBeforeOpeningCache()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            UsageSourceReaders.Codex.ReadCachedQuotaEstimate(cancellation.Token));
    }

    [Fact]
    public void ClearCache_ResetsInMemoryQuotaHistoryAfterDeletingTheFile()
    {
        var root = Path.Combine(Path.GetTempPath(), $"CodexTokenMonitorRoot-{Guid.NewGuid():N}");
        var start = new DateTimeOffset(2026, 7, 16, 0, 0, 0, TimeSpan.FromHours(8));
        var historyPath = Path.Combine(root, "CodexTokenMonitor", "quota-history-v2.jsonl");
        try
        {
            using (MonitorCachePaths.PushLocalAppDataRoot(root))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(historyPath)!);
                File.WriteAllText(
                    historyPath,
                    "{\"snapshotLocal\":\"2026-07-16T02:00:00+08:00\",\"limitId\":\"codex\",\"limitName\":\"Codex\",\"fiveHour\":{\"usedPercent\":1,\"windowMinutes\":300,\"resetAtLocal\":\"2026-07-16T07:00:00+08:00\"},\"week\":{\"usedPercent\":12,\"windowMinutes\":10080,\"resetAtLocal\":\"2026-07-23T00:00:00+08:00\"}}\n");

                Assert.Single(UsageSourceReaders.Codex.ReadQuotaHistoryQuotaSnapshots(start, start.AddDays(1)));
                UsageSourceReaders.Codex.ClearCache();

                Assert.False(File.Exists(historyPath));
                Assert.Empty(UsageSourceReaders.Codex.ReadQuotaHistoryQuotaSnapshots(start, start.AddDays(1)));
            }
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public void LoadingUsageCache_DoesNotDeleteCurrentQuotaHistoryFile()
    {
        var root = Path.Combine(Path.GetTempPath(), $"CodexTokenMonitorRoot-{Guid.NewGuid():N}");
        var historyPath = Path.Combine(root, "CodexTokenMonitor", "quota-history-v2.jsonl");
        try
        {
            using (MonitorCachePaths.PushLocalAppDataRoot(root))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(historyPath)!);
                File.WriteAllText(historyPath, "current-history");

                _ = UsageCacheStore.Load("CodexTokenMonitor");

                Assert.True(File.Exists(historyPath));
                Assert.Equal("current-history", File.ReadAllText(historyPath));
            }
        }
        finally
        {
            using (MonitorCachePaths.PushLocalAppDataRoot(root))
            {
                UsageCacheStore.Delete("CodexTokenMonitor");
            }

            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public void PutTimelineSnapshots_DoesNotMutateCacheWhenCancellationIsAlreadyRequested()
    {
        var folder = $"CodexTokenMonitorTests-{Guid.NewGuid():N}";
        var snapshot = new CodexQuotaSnapshot(
            new DateTimeOffset(2026, 7, 16, 2, 0, 0, TimeSpan.FromHours(8)),
            "codex",
            "Codex",
            1m,
            new DateTimeOffset(2026, 7, 16, 7, 0, 0, TimeSpan.FromHours(8)),
            12m,
            new DateTimeOffset(2026, 7, 23, 0, 0, 0, TimeSpan.FromHours(8)));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try
        {
            var cache = QuotaSnapshotCacheStore.Load(folder);

            Assert.Throws<OperationCanceledException>(() => cache.PutTimelineSnapshots(
                new[] { snapshot },
                new Dictionary<DateTimeOffset, (DateTimeOffset? Before, DateTimeOffset? After)>
                {
                    [snapshot.SnapshotLocal] = (snapshot.SnapshotLocal, snapshot.SnapshotLocal)
                },
                cancellation.Token));

            Assert.Empty(cache.GetTimelineSnapshots(
                snapshot.SnapshotLocal,
                snapshot.SnapshotLocal.AddTicks(1)));
        }
        finally
        {
            UsageCacheStore.Delete(folder);
        }
    }

    [Fact]
    public void ReadDetailRows_PropagatesCancellationInsteadOfReturningAnEmptyCache()
    {
        var folder = $"CodexTokenMonitorTests-{Guid.NewGuid():N}";
        var start = new DateTimeOffset(2026, 7, 16, 0, 0, 0, TimeSpan.FromHours(8));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try
        {
            var cache = UsageCacheStore.Load(folder);

            Assert.Throws<OperationCanceledException>(() => cache.ReadDetailRows(
                start,
                start.AddDays(1),
                cancellation.Token));
        }
        finally
        {
            UsageCacheStore.Delete(folder);
        }
    }

    [Fact]
    public void ReadRange_PropagatesCancellationInsteadOfReturningAnEmptySummary()
    {
        var folder = $"CodexTokenMonitorTests-{Guid.NewGuid():N}";
        var start = new DateTimeOffset(2026, 7, 16, 0, 0, 0, TimeSpan.FromHours(8));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try
        {
            var cache = UsageCacheStore.Load(folder);

            Assert.Throws<OperationCanceledException>(() => cache.ReadRange(
                start,
                start.AddDays(1),
                cancellation.Token));
        }
        finally
        {
            UsageCacheStore.Delete(folder);
        }
    }

    [Fact]
    public void GetIncompleteDays_IncludesCompleteSummaryWithPartialDetails()
    {
        var folder = $"CodexTokenMonitorTests-{Guid.NewGuid():N}";
        var dayStart = new DateTimeOffset(2026, 7, 12, 0, 0, 0, TimeSpan.FromHours(8));
        var first = new TokenUsageEvent(dayStart.AddHours(9), 100, 80, 10, 0, 110, "first");
        var second = new TokenUsageEvent(dayStart.AddHours(10), 200, 160, 20, 0, 220, "second");
        try
        {
            var cache = UsageCacheStore.Load(folder);
            var summaryBucket = new TokenUsageBucket { StartLocal = dayStart };
            summaryBucket.Add(first.Timestamp, first.InputTokens, first.CachedInputTokens, first.OutputTokens, first.ReasoningOutputTokens, first.TotalTokens);
            summaryBucket.Add(second.Timestamp, second.InputTokens, second.CachedInputTokens, second.OutputTokens, second.ReasoningOutputTokens, second.TotalTokens);
            cache.Put(summaryBucket, isComplete: true, detailEvents: new[] { first });

            var incomplete = UsageCacheStore.GetIncompleteDays(folder, dayStart, dayStart);

            Assert.Equal(new[] { dayStart }, incomplete);
        }
        finally
        {
            UsageCacheStore.Delete(folder);
        }
    }

    [Fact]
    public void GetIncompleteDays_IncludesIncompleteSummaryEvenWhenDetailsMatch()
    {
        var folder = $"CodexTokenMonitorTests-{Guid.NewGuid():N}";
        var dayStart = new DateTimeOffset(2026, 7, 12, 0, 0, 0, TimeSpan.FromHours(8));
        var item = new TokenUsageEvent(dayStart.AddHours(9), 100, 80, 10, 0, 110, "incomplete");
        try
        {
            var cache = UsageCacheStore.Load(folder);
            var bucket = new TokenUsageBucket { StartLocal = dayStart };
            bucket.Add(item.Timestamp, item.InputTokens, item.CachedInputTokens, item.OutputTokens, item.ReasoningOutputTokens, item.TotalTokens);
            cache.Put(
                bucket,
                isComplete: false,
                scannedThroughLocal: dayStart.AddHours(12),
                detailEvents: new[] { item });

            var incomplete = UsageCacheStore.GetIncompleteDays(folder, dayStart, dayStart);

            Assert.Equal(new[] { dayStart }, incomplete);
        }
        finally
        {
            UsageCacheStore.Delete(folder);
        }
    }

    [Fact]
    public void ReadRange_RebuildsIncompleteDayFromPersistedDetails()
    {
        var folder = $"CodexTokenMonitorTests-{Guid.NewGuid():N}";
        var dayStart = new DateTimeOffset(2026, 7, 12, 0, 0, 0, TimeSpan.FromHours(8));
        var first = new TokenUsageEvent(dayStart.AddHours(9), 100, 80, 10, 0, 110, "first");
        var second = new TokenUsageEvent(dayStart.AddHours(10), 200, 160, 20, 0, 220, "second");
        try
        {
            var cache = UsageCacheStore.Load(folder);
            var summaryBucket = new TokenUsageBucket { StartLocal = dayStart };
            summaryBucket.Add(first.Timestamp, first.InputTokens, first.CachedInputTokens, first.OutputTokens, first.ReasoningOutputTokens, first.TotalTokens);
            summaryBucket.Add(second.Timestamp, second.InputTokens, second.CachedInputTokens, second.OutputTokens, second.ReasoningOutputTokens, second.TotalTokens);
            cache.Put(summaryBucket, isComplete: false, detailEvents: new[] { first });

            var summary = cache.ReadRange(dayStart, dayStart.AddDays(1));

            Assert.Equal(1, summary.Events);
            Assert.Equal(first.InputTokens, summary.InputTokens);
            Assert.Equal(first.TotalTokens, summary.TotalTokens);
        }
        finally
        {
            UsageCacheStore.Delete(folder);
        }
    }

    [Fact]
    public void QuotaGetIncompleteDays_UsesCompleteDayRecords()
    {
        var folder = $"CodexTokenMonitorTests-{Guid.NewGuid():N}";
        var start = new DateTimeOffset(2026, 7, 14, 0, 0, 0, TimeSpan.FromHours(8));
        try
        {
            var cache = QuotaSnapshotCacheStore.Load(folder);
            cache.Put(
                DateOnly.FromDateTime(start.AddDays(1).DateTime),
                new[]
                {
                    new CodexQuotaSnapshot(
                        start.AddDays(1).AddHours(2),
                        "codex",
                        null,
                        null,
                        null,
                        12m,
                        start.AddDays(8))
                },
                isComplete: true,
                scannedThroughLocal: start.AddDays(2).AddTicks(-1));

            var incomplete = QuotaSnapshotCacheStore.GetIncompleteDays(
                folder,
                start,
                start.AddDays(2));

            Assert.Equal(new[] { start.AddDays(2), start }, incomplete);
        }
        finally
        {
            UsageCacheStore.Delete(folder);
        }
    }

    [Fact]
    public void Load_ReusesInitializedStoreAndRangeQueryFiltersEvents()
    {
        var folder = $"CodexTokenMonitorTests-{Guid.NewGuid():N}";
        try
        {
            var first = UsageCacheStore.Load(folder);
            var second = UsageCacheStore.Load(folder);
            var dayStart = new DateTimeOffset(2026, 7, 13, 0, 0, 0, TimeSpan.FromHours(8));
            var early = new TokenUsageEvent(dayStart.AddHours(9), 100, 80, 10, 2, 110, "early");
            var late = new TokenUsageEvent(dayStart.AddHours(11), 200, 160, 20, 4, 220, "late");
            var bucket = new TokenUsageBucket { StartLocal = dayStart };
            bucket.Add(early.Timestamp, early.InputTokens, early.CachedInputTokens, early.OutputTokens, early.ReasoningOutputTokens, early.TotalTokens);
            bucket.Add(late.Timestamp, late.InputTokens, late.CachedInputTokens, late.OutputTokens, late.ReasoningOutputTokens, late.TotalTokens);

            first.Put(bucket, detailEvents: new[] { early, late });
            var rows = second.ReadDetailRows(dayStart.AddHours(8), dayStart.AddHours(10));
            var summary = second.ReadRange(dayStart, dayStart.AddDays(1));

            Assert.Same(first, second);
            var row = Assert.Single(rows);
            Assert.Equal(early.Timestamp, row.StartLocal);
            Assert.Equal(100, row.InputTokens);
            Assert.Equal(300, summary.InputTokens);
            Assert.Equal(2, summary.Events);
            Assert.Equal(300, summary.PeakInputTokens);
            Assert.Equal(240, summary.PeakCachedInputTokens);
            Assert.Equal(30, summary.PeakOutputTokens);
        }
        finally
        {
            UsageCacheStore.Delete(folder);
        }
    }

    [Fact]
    public void Load_DoesNotReuseStoreAcrossCacheRoots()
    {
        var folder = $"CodexTokenMonitorTests-{Guid.NewGuid():N}";
        var firstRoot = Path.Combine(Path.GetTempPath(), $"CodexTokenMonitorRootA-{Guid.NewGuid():N}");
        var secondRoot = Path.Combine(Path.GetTempPath(), $"CodexTokenMonitorRootB-{Guid.NewGuid():N}");
        var date = new DateTimeOffset(2026, 7, 15, 0, 0, 0, TimeSpan.FromHours(8));

        try
        {
            UsageCacheStore first;
            using (MonitorCachePaths.PushLocalAppDataRoot(firstRoot))
            {
                first = UsageCacheStore.Load(folder);
                first.Put(new TokenUsageBucket { StartLocal = date }, isComplete: true);
            }

            using (MonitorCachePaths.PushLocalAppDataRoot(secondRoot))
            {
                var second = UsageCacheStore.Load(folder);
                Assert.NotSame(first, second);
                Assert.False(second.TryGet(DateOnly.FromDateTime(date.DateTime), out _));
            }
        }
        finally
        {
            using (MonitorCachePaths.PushLocalAppDataRoot(firstRoot))
            {
                UsageCacheStore.Delete(folder);
            }

            using (MonitorCachePaths.PushLocalAppDataRoot(secondRoot))
            {
                UsageCacheStore.Delete(folder);
            }

            TryDeleteDirectory(firstRoot);
            TryDeleteDirectory(secondRoot);
        }
    }

    [Fact]
    public void ReadRange_NormalizesUtcBoundsToBeijingCalendarDay()
    {
        var folder = $"CodexTokenMonitorTests-{Guid.NewGuid():N}";
        var dayStart = new DateTimeOffset(2026, 7, 13, 0, 0, 0, TimeSpan.FromHours(8));
        try
        {
            var cache = UsageCacheStore.Load(folder);
            var bucket = new TokenUsageBucket { StartLocal = dayStart };
            bucket.Add(dayStart.AddHours(9), 100, 50, 20, 0, 120);
            cache.Put(bucket, isComplete: true, detailEvents: new[]
            {
                new TokenUsageEvent(dayStart.AddHours(9), 100, 50, 20, 0, 120, "utc-range")
            });

            var summary = cache.ReadRange(dayStart.ToUniversalTime(), dayStart.AddDays(1).ToUniversalTime());
            var rows = cache.ReadDetailRows(dayStart.ToUniversalTime(), dayStart.AddDays(1).ToUniversalTime());

            Assert.Equal(1, summary.Events);
            Assert.Equal(100, summary.InputTokens);
            Assert.Equal(dayStart, summary.StartLocal);
            Assert.Equal(100, Assert.Single(rows).InputTokens);
        }
        finally
        {
            UsageCacheStore.Delete(folder);
        }
    }

    [Fact]
    public void GetDetailEvents_RangeNormalizesStoredUtcOffsets()
    {
        var folder = $"CodexTokenMonitorTests-{Guid.NewGuid():N}";
        var dayStart = new DateTimeOffset(2026, 7, 14, 0, 0, 0, TimeSpan.FromHours(8));
        var storedUtc = dayStart.AddMinutes(30).ToUniversalTime();
        try
        {
            var cache = UsageCacheStore.Load(folder);
            var item = new TokenUsageEvent(storedUtc, 100, 50, 20, 0, 120, "utc-event");
            var bucket = new TokenUsageBucket { StartLocal = dayStart };
            bucket.Add(item.Timestamp, item.InputTokens, item.CachedInputTokens, item.OutputTokens, item.ReasoningOutputTokens, item.TotalTokens);
            cache.Put(bucket, isComplete: true, detailEvents: new[] { item });

            var events = cache.GetDetailEvents(dayStart, dayStart.AddDays(1));

            var result = Assert.Single(events);
            Assert.Equal(dayStart.AddMinutes(30), result.Timestamp);
        }
        finally
        {
            UsageCacheStore.Delete(folder);
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Test cleanup is best-effort; each root is unique and temporary.
        }
    }
}
