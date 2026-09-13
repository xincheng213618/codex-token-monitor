using Xunit;

namespace CodexTokenMonitor.Tests;

public sealed class CacheWriteUsageTests
{
    [Fact]
    public void TokenBucket_PartitionsAndPricesCacheWritesIndependently()
    {
        var timestamp = new DateTimeOffset(2026, 8, 24, 9, 0, 0, TimeSpan.FromHours(8));
        var bucket = new TokenUsageBucket { StartLocal = timestamp };
        bucket.Add(
            timestamp,
            input: 4_000_000,
            cached: 1_000_000,
            cacheWrite: 1_000_000,
            output: 1_000_000,
            reasoning: 0,
            total: 5_000_000);

        Assert.Equal(1_000_000, bucket.CachedInputTokens);
        Assert.Equal(1_000_000, bucket.CacheWriteInputTokens);
        Assert.Equal(2_000_000, bucket.UncachedInputTokens);

        var profile = new PriceProfile(
            "GPT-5.6 Sol",
            "$",
            5.00m,
            0.50m,
            30.00m,
            1_000_000m,
            CacheWriteInputPerMillion: 6.25m);

        Assert.Equal(46.75m, bucket.EstimateCost(profile));
    }

    [Fact]
    public void TokenBucket_UsesOrdinaryInputPriceWhenCacheWritePriceIsMissing()
    {
        var timestamp = new DateTimeOffset(2026, 8, 24, 9, 0, 0, TimeSpan.FromHours(8));
        var bucket = new TokenUsageBucket { StartLocal = timestamp };
        bucket.Add(timestamp, 4_000_000, 1_000_000, 1_000_000, 1_000_000, 0, 5_000_000);

        var legacyProfile = new PriceProfile(
            "legacy",
            "$",
            5.00m,
            0.50m,
            30.00m,
            1_000_000m);

        Assert.Equal(45.50m, bucket.EstimateCost(legacyProfile));
    }

    [Fact]
    public void CodexReader_ParsesCacheWriteInputTokens()
    {
        var root = Path.Combine(Path.GetTempPath(), $"CodexCacheWriteTests-{Guid.NewGuid():N}");
        var logRoot = Path.Combine(root, "logs");
        var sessions = Path.Combine(logRoot, "Codex", "sessions", "2026", "08", "24");
        Directory.CreateDirectory(sessions);
        var logPath = Path.Combine(sessions, "rollout-test.jsonl");
        File.WriteAllText(
            logPath,
            "{\"timestamp\":\"2026-08-24T01:00:00Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"turn_id\":\"cache-write-test\",\"info\":{\"last_token_usage\":{\"input_tokens\":1000,\"cached_input_tokens\":600,\"cache_write_input_tokens\":100,\"output_tokens\":50,\"reasoning_output_tokens\":10,\"total_tokens\":1050}}}}\n");

        using var cacheScope = MonitorCachePaths.PushLocalAppDataRoot(Path.Combine(root, "cache"));
        using var logScope = UsageLogPaths.PushRoot(logRoot);
        try
        {
            var start = new DateTimeOffset(2026, 8, 24, 0, 0, 0, TimeSpan.FromHours(8));
            var row = Assert.Single(CodexUsageReader.ReadTransientDetailRows(start, start.AddDays(1)));

            Assert.Equal(1000, row.InputTokens);
            Assert.Equal(600, row.CachedInputTokens);
            Assert.Equal(100, row.CacheWriteInputTokens);
            Assert.Equal(300, row.UncachedInputTokens);
        }
        finally
        {
            var resolved = Path.GetFullPath(root);
            var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!resolved.StartsWith(temp, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(resolved).StartsWith("CodexCacheWriteTests-", StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing to clean up outside the isolated test directory.");
            Directory.Delete(resolved, recursive: true);
        }
    }
}
