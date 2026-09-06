using Xunit;

namespace CodexTokenMonitor.Tests;

public sealed class ReaderCacheConsistencyTests
{
    private static readonly TimeSpan Beijing = TimeSpan.FromHours(8);

    public static IEnumerable<object[]> Readers => new[]
    {
        new object[] { "Codex", "CodexTokenMonitor" },
        new object[] { "Claude", "ClaudeCodeTokenMonitor" },
        new object[] { "Dsh", "DshTokenMonitor" },
        new object[] { "WorkBuddy", "WorkBuddyTokenMonitor" },
        new object[] { "ZCode", "ZCodeTokenMonitor" }
    };

    [Theory]
    [InlineData(10, 48)]
    [InlineData(0, 34)]
    [InlineData(10, 34)]
    public void Codex_ClippedHistoricalRangeUsesCompleteDetailsIncludingImportedModelAndSpeed(int startHour, int endHour)
    {
        var cacheRoot = CreateIsolatedCacheRoot();
        using var cacheScope = MonitorCachePaths.PushLocalAppDataRoot(cacheRoot);
        using var logScope = UsageLogPaths.PushRoot(Path.Combine(cacheRoot, "logs"));
        var day = new DateTimeOffset(1999, 1, 20, 0, 0, 0, Beijing);
        try
        {
            var cache = UsageCacheStore.Load("CodexTokenMonitor");
            var events = new List<TokenUsageEvent>();
            foreach (var offset in new[] { 0, 24 })
            {
                var dayStart = day.AddHours(offset);
                var details = new[] { 9, 10, 13, 14 }.Select(hour => new TokenUsageEvent(
                    dayStart.AddHours(hour), 100, 60, 10, 2, 110, $"imported:{offset}:{hour}")
                    { ModelId = "gpt-5.5", ServiceTier = "priority" }).ToArray();
                events.AddRange(details);
                var bucket = new TokenUsageBucket { StartLocal = dayStart };
                foreach (var item in details) bucket.Add(item);
                cache.Put(bucket, isComplete: true, scannedThroughLocal: dayStart.AddDays(1).AddTicks(-1), detailEvents: details);
            }

            var start = day.AddHours(startHour);
            var end = day.AddHours(endHour);
            var expected = events.Where(e => e.Timestamp >= start && e.Timestamp < end).ToArray();
            // There are no local rollouts: these events came from another device.
            // Scanning source files instead of the complete cache loses both the
            // boundary-day counters and their model / Fast metadata.
            var actual = CodexUsageReader.ReadRange(start.ToUniversalTime(), end.ToUniversalTime(), includeLiveToday: false);

            Assert.Equal(expected.Length, actual.Events);
            Assert.Equal(expected.Sum(e => e.TotalTokens), actual.TotalTokens);
            Assert.Equal(expected.Length, actual.ModelUsage["gpt-5.5"].ServiceTierUsage["priority"].Events);
            Assert.Equal(actual.Events, actual.DailyBuckets.Sum(b => b.Events));
            Assert.Equal(start, actual.StartLocal);
            Assert.Equal(end, actual.EndLocal);
        }
        finally
        {
            UsageCacheStore.Delete("CodexTokenMonitor");
            DeleteIsolatedCacheRoot(cacheRoot);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Codex_RebuildsInvalidatedHistoricalDayWithEndOfDayWatermark(
        bool readDetailsDirectly,
        bool hasImportedEvent)
    {
        var cacheRoot = CreateIsolatedCacheRoot();
        using var cacheScope = MonitorCachePaths.PushLocalAppDataRoot(cacheRoot);
        using var logScope = UsageLogPaths.PushRoot(Path.Combine(cacheRoot, "logs"));
        var dayStart = new DateTimeOffset(1999, 1, 18, 0, 0, 0, Beijing);
        var reader = UsageSourceReaders.For(UsageSource.Codex);

        try
        {
            var cache = UsageCacheStore.Load("CodexTokenMonitor");
            cache.Put(
                new TokenUsageBucket { StartLocal = dayStart },
                isComplete: false,
                scannedThroughLocal: dayStart.AddDays(1).AddTicks(-1),
                detailEvents: Array.Empty<TokenUsageEvent>());
            if (hasImportedEvent)
            {
                cache.MergeImportedDetailEvents(new[]
                {
                    new TokenUsageEvent(dayStart.AddHours(9), 100, 60, 10, 2, 110, "imported:retained")
                });
            }

            Assert.Single(reader.GetIncompleteHistoricalDays(dayStart, dayStart));
            RebuildCodexHistoricalDay(reader, dayStart, readDetailsDirectly);

            Assert.True(cache.TryGetRecord(DateOnly.FromDateTime(dayStart.DateTime), out var repaired));
            Assert.True(repaired.IsComplete);
            Assert.Equal(hasImportedEvent ? 1 : 0, repaired.Events);
            Assert.Equal(repaired.Events, repaired.DetailEventCount);
            Assert.Empty(reader.GetIncompleteHistoricalDays(dayStart, dayStart));

            RebuildCodexHistoricalDay(reader, dayStart, readDetailsDirectly);
            Assert.Equal(hasImportedEvent ? 110 : 0, reader.ReadCachedRange(dayStart, dayStart.AddDays(1)).TotalTokens);
        }
        finally
        {
            UsageCacheStore.Delete("CodexTokenMonitor");
            DeleteIsolatedCacheRoot(cacheRoot);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Codex_InvalidatedHistoricalDayReloadsCacheWriteTelemetryAndPreservesImports(
        bool readDetailsDirectly,
        bool hasTurnId)
    {
        var cacheRoot = CreateIsolatedCacheRoot();
        using var cacheScope = MonitorCachePaths.PushLocalAppDataRoot(cacheRoot);
        using var logScope = UsageLogPaths.PushRoot(Path.Combine(cacheRoot, "logs"));
        var dayStart = new DateTimeOffset(1999, 1, 19, 0, 0, 0, Beijing);
        var reader = UsageSourceReaders.For(UsageSource.Codex);
        var obsoleteEvent = new TokenUsageEvent(
            dayStart.AddHours(9), 1000, 600, 50, 10, 1050,
            hasTurnId ? "codex:cache-write-recovery" : null);
        var expectedPersistedKey = hasTurnId
            ? "codex:cache-write-recovery"
            : $"{obsoleteEvent.Timestamp:O}|1000|600|50|10|1050";

        try
        {
            var sessions = Path.Combine(cacheRoot, "logs", "Codex", "sessions");
            Directory.CreateDirectory(sessions);
            var logLine = """
                {"timestamp":"1999-01-19T01:00:00Z","type":"event_msg","payload":{"type":"token_count","turn_id":"cache-write-recovery","info":{"last_token_usage":{"input_tokens":1000,"cached_input_tokens":600,"cache_write_input_tokens":100,"output_tokens":50,"reasoning_output_tokens":10,"total_tokens":1050}}}}
                """;
            if (!hasTurnId)
            {
                logLine = logLine.Replace("\"turn_id\":\"cache-write-recovery\",", "");
            }
            File.WriteAllText(Path.Combine(sessions, "rollout-test.jsonl"), logLine);
            var cache = UsageCacheStore.Load("CodexTokenMonitor");
            var obsoleteBucket = new TokenUsageBucket { StartLocal = dayStart };
            Add(obsoleteBucket, obsoleteEvent);
            cache.Put(
                obsoleteBucket,
                isComplete: false,
                scannedThroughLocal: dayStart.AddDays(1).AddTicks(-1),
                detailEvents: new[] { obsoleteEvent });
            Assert.Equal(expectedPersistedKey, Assert.Single(cache.GetDetailEvents(DateOnly.FromDateTime(dayStart.DateTime))).Key);
            cache.MergeImportedDetailEvents(new[]
            {
                new TokenUsageEvent(dayStart.AddHours(10), 100, 60, 10, 2, 110, "imported:retained")
            });

            RebuildCodexHistoricalDay(reader, dayStart, readDetailsDirectly);

            var cachedEvents = cache.GetDetailEvents(DateOnly.FromDateTime(dayStart.DateTime));
            Assert.Equal(2, cachedEvents.Count);
            Assert.Equal(100, Assert.Single(cachedEvents, item => item.Key == expectedPersistedKey).CacheWriteInputTokens);
            Assert.Single(cachedEvents, item => item.Key == "imported:retained");
            var summary = reader.ReadCachedRange(dayStart, dayStart.AddDays(1));
            Assert.Equal(1160, summary.TotalTokens);
            Assert.Equal(100, summary.CacheWriteInputTokens);
            Assert.Empty(reader.GetIncompleteHistoricalDays(dayStart, dayStart));
        }
        finally
        {
            UsageCacheStore.Delete("CodexTokenMonitor");
            DeleteIsolatedCacheRoot(cacheRoot);
        }
    }

    private static void RebuildCodexHistoricalDay(
        IUsageSourceReader reader,
        DateTimeOffset dayStart,
        bool readDetailsDirectly)
    {
        if (readDetailsDirectly)
        {
            _ = reader.ReadDetailRows(dayStart, dayStart.AddDays(1), includeLiveToday: false);
        }
        else
        {
            reader.WarmHistoricalDay(dayStart);
        }
    }

    [Theory]
    [MemberData(nameof(Readers))]
    public void ReadRange_RepairsCompleteSummaryWithPartialDetails(string source, string cacheFolder)
    {
        var cacheRoot = CreateIsolatedCacheRoot();
        using var cacheScope = MonitorCachePaths.PushLocalAppDataRoot(cacheRoot);
        using var logScope = UsageLogPaths.PushRoot(Path.Combine(cacheRoot, "logs"));
        var dayStart = new DateTimeOffset(1999, 1, 15, 0, 0, 0, Beijing);
        var first = new TokenUsageEvent(dayStart.AddHours(9), 100, 60, 10, 2, 110, $"{source}:first");
        var second = new TokenUsageEvent(dayStart.AddHours(10), 200, 120, 20, 4, 220, $"{source}:second");

        try
        {
            var cache = UsageCacheStore.Load(cacheFolder);
            var summaryBucket = new TokenUsageBucket { StartLocal = dayStart };
            Add(summaryBucket, first);
            Add(summaryBucket, second);
            cache.Put(
                summaryBucket,
                isComplete: true,
                scannedThroughLocal: dayStart.AddDays(1).AddTicks(-1),
                detailEvents: new[] { first });

            var result = ReadRange(source, dayStart, dayStart.AddDays(1));
            var cached = cache.ReadRange(dayStart, dayStart.AddDays(1));

            Assert.Equal(1, result.Events);
            Assert.Equal(1, Assert.Single(result.DailyBuckets).Events);
            Assert.Equal(1, cached.Events);
            Assert.Single(cache.GetDetailEvents(DateOnly.FromDateTime(dayStart.DateTime)));
        }
        finally
        {
            UsageCacheStore.Delete(cacheFolder);
            DeleteIsolatedCacheRoot(cacheRoot);
        }
    }

    [Theory]
    [MemberData(nameof(Readers))]
    public void ReadRange_RepairsIncompleteSummaryEvenWhenDetailsMatch(string source, string cacheFolder)
    {
        var cacheRoot = CreateIsolatedCacheRoot();
        using var cacheScope = MonitorCachePaths.PushLocalAppDataRoot(cacheRoot);
        using var logScope = UsageLogPaths.PushRoot(Path.Combine(cacheRoot, "logs"));
        var dayStart = new DateTimeOffset(1999, 1, 16, 0, 0, 0, Beijing);
        var item = new TokenUsageEvent(dayStart.AddHours(9), 100, 60, 10, 2, 110, $"{source}:complete-details");

        try
        {
            var cache = UsageCacheStore.Load(cacheFolder);
            var bucket = new TokenUsageBucket { StartLocal = dayStart };
            Add(bucket, item);
            cache.Put(
                bucket,
                isComplete: false,
                scannedThroughLocal: dayStart.AddHours(12),
                detailEvents: new[] { item });

            var result = ReadRange(source, dayStart, dayStart.AddDays(1));

            Assert.Equal(1, result.Events);
            Assert.True(cache.TryGetRecord(DateOnly.FromDateTime(dayStart.DateTime), out var repaired));
            Assert.True(repaired.IsComplete);
            Assert.Equal(1, repaired.DetailEventCount);
        }
        finally
        {
            UsageCacheStore.Delete(cacheFolder);
            DeleteIsolatedCacheRoot(cacheRoot);
        }
    }

    [Theory]
    [MemberData(nameof(Readers))]
    public void ReadRange_RepairsSummaryWhenDetailsContainAdditionalEvents(string source, string cacheFolder)
    {
        var cacheRoot = CreateIsolatedCacheRoot();
        using var cacheScope = MonitorCachePaths.PushLocalAppDataRoot(cacheRoot);
        using var logScope = UsageLogPaths.PushRoot(Path.Combine(cacheRoot, "logs"));
        var dayStart = new DateTimeOffset(1999, 1, 16, 0, 0, 0, Beijing);
        var first = new TokenUsageEvent(dayStart.AddHours(9), 100, 60, 10, 2, 110, $"{source}:summary");
        var additional = new TokenUsageEvent(dayStart.AddHours(10), 200, 120, 20, 4, 220, $"{source}:additional");

        try
        {
            var cache = UsageCacheStore.Load(cacheFolder);
            var summaryBucket = new TokenUsageBucket { StartLocal = dayStart };
            Add(summaryBucket, first);
            cache.Put(
                summaryBucket,
                isComplete: true,
                scannedThroughLocal: dayStart.AddDays(1).AddTicks(-1),
                detailEvents: new[] { first, additional });

            var result = ReadRange(source, dayStart, dayStart.AddDays(1));

            Assert.Equal(2, result.Events);
            Assert.Equal(300, result.InputTokens);
            Assert.True(cache.TryGetRecord(DateOnly.FromDateTime(dayStart.DateTime), out var repaired));
            Assert.Equal(2, repaired.Events);
            Assert.Equal(2, repaired.DetailEventCount);
        }
        finally
        {
            UsageCacheStore.Delete(cacheFolder);
            DeleteIsolatedCacheRoot(cacheRoot);
        }
    }

    [Theory]
    [MemberData(nameof(Readers))]
    public void ReadRange_NormalizesNonBeijingBounds(string source, string cacheFolder)
    {
        var cacheRoot = CreateIsolatedCacheRoot();
        using var cacheScope = MonitorCachePaths.PushLocalAppDataRoot(cacheRoot);
        using var logScope = UsageLogPaths.PushRoot(Path.Combine(cacheRoot, "logs"));
        var dayStart = new DateTimeOffset(1999, 1, 17, 0, 0, 0, Beijing);
        var item = new TokenUsageEvent(dayStart.AddHours(9), 100, 60, 10, 2, 110, $"{source}:utc-range");

        try
        {
            var cache = UsageCacheStore.Load(cacheFolder);
            var bucket = new TokenUsageBucket { StartLocal = dayStart };
            Add(bucket, item);
            cache.Put(
                bucket,
                isComplete: true,
                scannedThroughLocal: dayStart.AddDays(1).AddTicks(-1),
                detailEvents: new[] { item });

            var result = ReadRange(
                source,
                dayStart.ToUniversalTime(),
                dayStart.AddDays(1).ToUniversalTime());

            Assert.Equal(dayStart, result.StartLocal);
            Assert.Equal(dayStart.AddDays(1), result.EndLocal);
            Assert.Equal(1, result.Events);
            Assert.Equal(110, result.TotalTokens);
        }
        finally
        {
            UsageCacheStore.Delete(cacheFolder);
            DeleteIsolatedCacheRoot(cacheRoot);
        }
    }

    private static TokenUsageSummary ReadRange(
        string source,
        DateTimeOffset startLocal,
        DateTimeOffset endLocal)
    {
        return source switch
        {
            "Codex" => CodexUsageReader.ReadRange(startLocal, endLocal, includeLiveToday: false),
            "Claude" => ClaudeUsageReader.ReadRange(startLocal, endLocal, includeLiveToday: false),
            "Dsh" => DshUsageReader.ReadRange(startLocal, endLocal, includeLiveToday: false),
            "WorkBuddy" => WorkBuddyUsageReader.ReadRange(startLocal, endLocal, includeLiveToday: false),
            "ZCode" => ZCodeUsageReader.ReadRange(startLocal, endLocal, includeLiveToday: false),
            _ => throw new ArgumentOutOfRangeException(nameof(source), source, "Unknown source")
        };
    }

    private static void Add(TokenUsageBucket bucket, TokenUsageEvent item)
    {
        bucket.Add(
            item.Timestamp,
            item.InputTokens,
            item.CachedInputTokens,
            item.OutputTokens,
            item.ReasoningOutputTokens,
            item.TotalTokens);
    }

    private static string CreateIsolatedCacheRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), $"CodexTokenMonitorReaderTests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteIsolatedCacheRoot(string path)
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
            // Test cleanup is best-effort; the root is unique and outside the
            // application's data directory.
        }
    }
}
