using System.Text.Json;
using Xunit;

namespace CodexTokenMonitor.Core.Tests;

public sealed class CodexCompactionUsageReaderTests
{
    private static readonly DateTimeOffset Day = new(2000, 1, 18, 0, 0, 0, TimeSpan.FromHours(8));

    [Fact]
    public void MixedRolloutCountsEachCompactionOnlyWhenItAddsUsage()
    {
        using var fixture = new RolloutFixture("sessions");
        var start = Day.AddHours(8);
        fixture.Write(
            Header(start),
            Context(start, "gpt-6.2-sol", "priority"),
            Modern(start.AddSeconds(1), "ordinary", 100),
            Legacy(start.AddMilliseconds(1010), 100),
            Modern(start.AddSeconds(2), "paired-compaction", 200),
            Compacted(start.AddMilliseconds(2020), "paired-compaction"),
            Legacy(start.AddMilliseconds(2030), 200),
            Modern(start.AddSeconds(3), "unpaired-compaction", 300),
            Compacted(start.AddMilliseconds(3030), "unpaired-compaction"),
            Modern(start.AddSeconds(4), "unconfirmed", 400));

        var reader = new CodexUsageReader();
        var rows = reader.ReadDetailRows(Day, Day.AddDays(1));

        Assert.Equal(new long[] { 100, 200, 300 }, rows.Select(row => row.TotalTokens));
        Assert.Equal(600L, reader.ReadCachedDetailRows(Day, Day.AddDays(1)).Sum(row => row.TotalTokens));
        var events = UsageCacheStore.Load("CodexTokenMonitor").GetDetailEvents(DateOnly.FromDateTime(Day.Date));
        var supplement = Assert.Single(events, item => item.Key?.StartsWith("codex-compaction:", StringComparison.Ordinal) == true);
        Assert.Equal("codex-compaction:owner:unpaired-compaction", supplement.Key);
        Assert.Equal("gpt-6.2-sol", supplement.ModelId);
        Assert.Equal("priority", supplement.ServiceTier);
        Assert.Equal(150L, supplement.CachedInputTokens);
        Assert.Equal(10L, supplement.CacheWriteInputTokens);
    }

    [Fact]
    public void IndexedCompactionOnlyArchiveRetainsUsageTimeAndModel()
    {
        using var fixture = new RolloutFixture("archived_sessions");
        var usageTime = Day.AddHours(8);
        fixture.Write(
            Header(Day),
            Context(Day.AddHours(7), "gpt-6.2-astra", "flex"),
            Modern(usageTime, "archive-compaction", 600),
            Compacted(usageTime.AddMilliseconds(500), "archive-compaction"));

        Assert.Empty(new CodexUsageReader().ReadTransientDetailRows(Day.AddDays(2), Day.AddDays(3)));
        Assert.True(File.Exists(Path.Combine(MonitorCachePaths.LocalAppData, "CodexTokenMonitor", CodexLogFileIndex.FileName)));
        var reader = new CodexUsageReader();
        Assert.Empty(reader.ReadTransientDetailRows(Day, usageTime));
        var row = Assert.Single(reader.ReadTransientDetailRows(usageTime, usageTime.AddMilliseconds(1)));

        Assert.Equal(usageTime, row.StartLocal);
        Assert.Equal(600L, row.TotalTokens);
        Assert.Equal(600L, row.ModelUsage["gpt-6.2-astra"].ServiceTierUsage["flex"].TotalTokens);
    }

    private static string Header(DateTimeOffset timestamp) =>
        Line(timestamp, "session_meta", new { id = "owner", timestamp });

    private static string Context(DateTimeOffset timestamp, string model, string tier) =>
        Line(timestamp, "turn_context", new { turn_id = "turn", model, service_tier = tier });

    private static string Modern(DateTimeOffset timestamp, string response, long total) =>
        Line(timestamp, "token_usage_record", new { thread_id = "owner", response_id = response, turn_id = "turn", usage = Usage(total) });

    private static string Compacted(DateTimeOffset timestamp, string response) =>
        Line(timestamp, "compacted", new { compaction_response_id = response });

    private static string Legacy(DateTimeOffset timestamp, long total) =>
        Line(timestamp, "event_msg", new { type = "token_count", info = new { last_token_usage = Usage(total) } });

    private static object Usage(long total) => new
    {
        input_tokens = total - 10,
        cached_input_tokens = total / 2,
        cache_write_input_tokens = 10,
        output_tokens = 10,
        reasoning_output_tokens = 4,
        total_tokens = total
    };

    private static string Line(DateTimeOffset timestamp, string type, object payload) =>
        JsonSerializer.Serialize(new { timestamp, type, payload });

    private sealed class RolloutFixture : IDisposable
    {
        private readonly IDisposable cacheScope;
        private readonly IDisposable logScope;
        private readonly string log;

        internal RolloutFixture(string folder)
        {
            var root = Path.Combine(Path.GetTempPath(), "compaction-reader-" + Guid.NewGuid().ToString("N"));
            cacheScope = MonitorCachePaths.PushLocalAppDataRoot(Path.Combine(root, "cache"));
            logScope = UsageLogPaths.PushRoot(Path.Combine(root, "logs"));
            var directory = Path.Combine(UsageLogPaths.GetCodexHome(), folder);
            Directory.CreateDirectory(directory);
            log = Path.Combine(directory, "rollout-compaction.jsonl");
        }

        internal void Write(params string[] lines) => File.WriteAllLines(log, lines);

        public void Dispose()
        {
            UsageCacheStore.Delete("CodexTokenMonitor");
            File.Delete(log);
            logScope.Dispose();
            cacheScope.Dispose();
        }
    }
}
