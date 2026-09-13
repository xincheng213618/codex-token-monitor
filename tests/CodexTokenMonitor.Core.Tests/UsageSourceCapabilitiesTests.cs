using System.Text;
using System.Text.Json;
using Xunit;
using ZstdSharp;

namespace CodexTokenMonitor.Tests;

public sealed class UsageSourceCapabilitiesTests
{
    private static readonly DateTimeOffset Day = new(1999, 3, 1, 0, 0, 0, TimeSpan.FromHours(8));

    [Theory]
    [InlineData((int)UsageSource.Codex, "CodexTokenMonitor")]
    [InlineData((int)UsageSource.ClaudeCode, "ClaudeCodeTokenMonitor")]
    [InlineData((int)UsageSource.ZCode, "ZCodeTokenMonitor")]
    [InlineData((int)UsageSource.WorkBuddy, "WorkBuddyTokenMonitor")]
    [InlineData((int)UsageSource.Dsh, "DshTokenMonitor")]
    public void CachedQueries_DoNotReplaceIncompleteCacheWithSourceLogs(int sourceValue, string cacheFolder)
    {
        var source = (UsageSource)sourceValue;
        using var isolated = new IsolatedSource(cacheFolder);
        WriteSourceLog(source, isolated.LogRoot);
        var cache = UsageCacheStore.Load(cacheFolder);
        var cachedEvent = new TokenUsageEvent(Day.AddHours(8), 40, 10, 2, 0, 42, "cached-only");
        var bucket = new TokenUsageBucket { StartLocal = Day };
        bucket.Add(cachedEvent);
        cache.Put(bucket, isComplete: false, scannedThroughLocal: Day.AddHours(12), detailEvents: new[] { cachedEvent });
        var definition = UsageSourceRegistry.For(source);
        var cached = definition.CachedQueries;
        using var diagnostics = CacheOperationDiagnostics.Begin();

        // The persisted day is deliberately incomplete and source logs contain
        // different usage. Cached queries must neither repair nor merge it.
        Assert.Equal(42, cached.ReadCachedRange(Day, Day.AddDays(1)).TotalTokens);
        Assert.Equal(42, Assert.Single(cached.ReadCachedDetailRows(Day, Day.AddDays(1))).TotalTokens);
        Assert.Single(cached.GetIncompleteHistoricalDays(Day, Day));

        // Establish that this source fixture is valid and reachable only by an
        // explicit source query, rather than relying on an empty/malformed log.
        var sourceRows = definition.Queries.ReadTransientDetailRows(Day, Day.AddDays(1));
        Assert.Equal(970, Assert.Single(sourceRows).TotalTokens);
        Assert.Equal(42, cached.ReadCachedRange(Day, Day.AddDays(1)).TotalTokens);
        Assert.Single(cached.GetIncompleteHistoricalDays(Day, Day));
        Assert.Empty(diagnostics.Warnings);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UsageService_AcceptsQueryWithoutMaintenanceCapability(bool cacheOnly)
    {
        var reader = new QueryOnlyFake();
        var service = new UsageQueryService(timeProvider: new FixedClock());
        var range = new SelectedRange(Day, Day.AddDays(1), "day", "details", RangeMode.Day);

        var result = service.Execute(new(reader, range, cacheOnly, IncludeLiveToday: false, CachedQuota: null));

        Assert.Equal(42, result.Summary.TotalTokens);
        Assert.Single(result.DetailRows);
        Assert.Equal(cacheOnly ? 0 : 1, reader.DayQueries);
        Assert.Empty(result.CacheWarnings);
    }

    [Fact]
    public void CompletenessChecks_AcceptCachedCapabilityWithoutSourceQueries()
    {
        var reader = new CachedOnlyFake();
        var service = new UsageQueryService(timeProvider: new FixedClock());
        var range = new SelectedRange(Day, Day.AddDays(1), "cycle", "details", RangeMode.Cycle);

        Assert.False(service.HasIncompleteHistoricalCache(reader, range));
        Assert.True(service.CanCacheCycleResult(reader, range, includeLiveToday: false));
        reader.IsIncomplete = true;
        Assert.True(service.HasIncompleteHistoricalCache(reader, range));
        Assert.False(service.CanCacheCycleResult(reader, range, includeLiveToday: false));
    }

    [Fact]
    public void NarrowRegistryViews_RetainTheExistingSharedAdapter()
    {
        foreach (var definition in UsageSourceRegistry.All)
        {
            Assert.Same(definition.Reader, definition.CachedQueries);
            Assert.Same(definition.Reader, definition.Queries);
            Assert.Same(definition.Reader, definition.CacheMaintenance);
            Assert.Same(definition.Reader, UsageSourceReaders.For(definition.Source));
        }
    }

    private static void WriteSourceLog(UsageSource source, string logRoot)
    {
        var root = Path.Combine(logRoot, source.ToString());
        root = source switch
        {
            UsageSource.Codex => Path.Combine(root, "sessions"),
            UsageSource.ZCode => Path.Combine(root, "rollout"),
            UsageSource.Dsh => Path.Combine(root, "fixture"),
            _ => root
        };
        Directory.CreateDirectory(root);
        var timestamp = Day.AddHours(9);
        object entry = source switch
        {
            UsageSource.Codex => new
            {
                timestamp, type = "event_msg",
                payload = new { type = "token_count", turn_id = "source-only", info = new
                {
                    last_token_usage = new { input_tokens = 960, cached_input_tokens = 40,
                        cache_write_input_tokens = 20, output_tokens = 10, reasoning_output_tokens = 0, total_tokens = 970 }
                } }
            },
            UsageSource.ClaudeCode => new
            {
                type = "assistant", timestamp,
                message = new { id = "source-only", usage = new { input_tokens = 900,
                    cache_read_input_tokens = 40, cache_creation_input_tokens = 20, output_tokens = 10 } }
            },
            UsageSource.WorkBuddy => new
            {
                timestamp,
                message = new { id = "source-only", usage = new { input_tokens = 960,
                    cache_read_input_tokens = 40, cache_write_input_tokens = 20, output_tokens = 10, total_tokens = 970 } }
            },
            UsageSource.ZCode => new
            {
                type = "model_io", completedAt = timestamp, requestId = "source-only",
                response = new { usage = new { inputTokens = 960, cacheReadTokens = 40,
                    cacheWriteTokens = 20, outputTokens = 10, totalTokens = 970 } }
            },
            UsageSource.Dsh => new
            {
                type = "assistant/chunk", seq = 1, time = timestamp.ToUnixTimeMilliseconds(),
                data = new { chunk = new { type = "usage", usage = new { inputTokens = 900,
                    cacheReadTokens = 40, cacheWriteTokens = 20, outputTokens = 10 } } }
            },
            _ => throw new ArgumentOutOfRangeException(nameof(source))
        };
        var text = JsonSerializer.Serialize(entry) + "\n";
        if (source == UsageSource.Dsh)
        {
            using var compressor = new Compressor();
            File.WriteAllBytes(Path.Combine(root, "session.jsonl.zstd"), compressor.Wrap(Encoding.UTF8.GetBytes(text)).ToArray());
        }
        else
        {
            var name = source == UsageSource.Codex ? "rollout-capabilities.jsonl" : "model-io-capabilities.jsonl";
            File.WriteAllText(Path.Combine(root, name), text);
        }
    }

    private class CachedOnlyFake : IUsageCacheQuery
    {
        public UsageSource Source => UsageSource.ClaudeCode;
        public string Title => "query fixture";
        public bool SupportsQuota => false;
        public bool IsIncomplete { get; set; }

        public TokenUsageSummary ReadCachedRange(DateTimeOffset startLocal, DateTimeOffset endLocal,
            CancellationToken cancellationToken = default) =>
            UsageSummaryBuilder.FromRows(startLocal, endLocal, ReadCachedDetailRows(startLocal, endLocal, cancellationToken));

        public IReadOnlyList<TokenUsageBucket> ReadCachedDetailRows(DateTimeOffset startLocal, DateTimeOffset endLocal,
            CancellationToken cancellationToken = default) => new[]
        {
            new TokenUsageBucket { StartLocal = Day.AddHours(8), Events = 1, InputTokens = 40,
                UncachedInputTokens = 30, CachedInputTokens = 10, OutputTokens = 2, TotalTokens = 42 }
        };

        public IReadOnlyList<DateTimeOffset> GetIncompleteHistoricalDays(DateTimeOffset startInclusive, DateTimeOffset endInclusive,
            CancellationToken cancellationToken = default) => IsIncomplete ? new[] { Day } : Array.Empty<DateTimeOffset>();
    }

    // Deliberately implements no cache-maintenance methods or composite facade.
    private sealed class QueryOnlyFake : CachedOnlyFake, IUsageQuery
    {
        public int DayQueries { get; private set; }

        public DailyUsageSnapshot ReadDay(DateTimeOffset startLocal, DateTimeOffset endLocal, bool includeLiveToday,
            CancellationToken cancellationToken = default)
        {
            DayQueries++;
            return new(ReadCachedRange(startLocal, endLocal, cancellationToken),
                ReadCachedDetailRows(startLocal, endLocal, cancellationToken));
        }

        public TokenUsageSummary ReadRange(DateTimeOffset startLocal, DateTimeOffset endLocal, bool includeLiveToday,
            CancellationToken cancellationToken = default) => throw new InvalidOperationException("Unexpected range scan.");
        public IReadOnlyList<TokenUsageBucket> ReadDetailRows(DateTimeOffset startLocal, DateTimeOffset endLocal, bool includeLiveToday,
            CancellationToken cancellationToken = default) => throw new InvalidOperationException("Unexpected detail scan.");
        public IReadOnlyList<TokenUsageBucket> ReadTransientDetailRows(DateTimeOffset startLocal, DateTimeOffset endLocal,
            CancellationToken cancellationToken = default) => throw new InvalidOperationException("Unexpected transient scan.");
    }

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Day.AddDays(2).ToUniversalTime();
    }

    private sealed class IsolatedSource : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), $"UsageSourceCapabilitiesTests-{Guid.NewGuid():N}");
        private readonly string cacheFolder;
        private readonly IDisposable cacheScope;
        private readonly IDisposable logScope;
        public string LogRoot => Path.Combine(root, "logs");

        public IsolatedSource(string cacheFolder)
        {
            this.cacheFolder = cacheFolder;
            Directory.CreateDirectory(root);
            cacheScope = MonitorCachePaths.PushLocalAppDataRoot(root);
            logScope = UsageLogPaths.PushRoot(LogRoot);
        }

        public void Dispose()
        {
            UsageCacheStore.Delete(cacheFolder);
            logScope.Dispose();
            cacheScope.Dispose();
            var resolved = Path.GetFullPath(root);
            var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!resolved.StartsWith(temp, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(resolved).StartsWith("UsageSourceCapabilitiesTests-", StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing to clean up outside the isolated test directory.");
            if (Directory.Exists(resolved)) Directory.Delete(resolved, recursive: true);
        }
    }
}
