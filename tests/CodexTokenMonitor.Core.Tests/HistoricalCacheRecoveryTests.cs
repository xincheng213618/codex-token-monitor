using System.Text;
using System.Text.Json;
using Xunit;
using ZstdSharp;

namespace CodexTokenMonitor.Tests;

public sealed class HistoricalCacheRecoveryTests
{
    private static readonly TimeSpan Beijing = TimeSpan.FromHours(8);

    public static IEnumerable<object[]> ReaderCases => new[]
    {
        new object[] { "Claude", "ClaudeCodeTokenMonitor", "ClaudeCode", "claude:recovery" },
        new object[] { "ZCode", "ZCodeTokenMonitor", "ZCode", "zcode:recovery" },
        new object[] { "WorkBuddy", "WorkBuddyTokenMonitor", "WorkBuddy", "workbuddy:recovery" },
        new object[] { "Dsh", "DshTokenMonitor", "Dsh", "dsh:recovery:1" }
    };

    public static IEnumerable<object[]> RecoveryCases =>
        from reader in ReaderCases
        from details in new[] { false, true }
        from empty in new[] { false, true }
        select reader.Concat(new object[] { details, empty }).ToArray();

    [Theory]
    [MemberData(nameof(RecoveryCases))]
    public void InvalidatedHistoricalDay_RescansDespiteEndOfDayWatermark(
        string source, string cacheFolder, string logFolder, string eventKey, bool details, bool empty)
    {
        var root = Path.Combine(Path.GetTempPath(), $"HistoricalCacheRecoveryTests-{Guid.NewGuid():N}");
        using var cacheScope = MonitorCachePaths.PushLocalAppDataRoot(root);
        using var logScope = UsageLogPaths.PushRoot(Path.Combine(root, "logs"));
        var start = new DateTimeOffset(1999, 1, 18, 0, 0, 0, Beijing);
        var end = start.AddDays(1);
        var date = DateOnly.FromDateTime(start.DateTime);

        try
        {
            var cache = UsageCacheStore.Load(cacheFolder);
            var events = new List<TokenUsageEvent>();
            if (!empty)
            {
                // The imported event has no local log and must survive repair.
                events.Add(new TokenUsageEvent(start.AddHours(9), 100, 40, 10, 0, 110, eventKey));
                events.Add(new TokenUsageEvent(start.AddHours(10), 50, 0, 5, 0, 55, "imported-only"));
                WriteLog(source, Path.Combine(root, "logs", logFolder), start.AddHours(9));
            }

            cache.Put(CreateBucket(start, events), isComplete: false,
                scannedThroughLocal: end.AddTicks(-1), detailEvents: events);

            var first = Read(source, start, end, details, includeLiveToday: false);
            var second = Read(source, start, end, details, includeLiveToday: false);

            Assert.Equal(empty ? 0 : 2, first.Events);
            Assert.Equal(empty ? 0 : 165, first.TotalTokens);
            Assert.Equal(empty ? 0 : 20, first.CacheWriteInputTokens);
            Assert.Equal(first.TotalTokens, second.TotalTokens);
            Assert.Equal(first.Events, second.Events);
            Assert.True(cache.TryGetRecord(date, out var record));
            Assert.True(record.IsComplete);
            Assert.Equal(first.Events, record.DetailEventCount);
            Assert.Empty(UsageCacheStore.GetIncompleteDays(cacheFolder, start, start));
            if (!empty)
            {
                Assert.Contains(cache.GetDetailEvents(date), item => item.Key == "imported-only");
            }
        }
        finally
        {
            UsageCacheStore.Delete(cacheFolder);
            DeleteTestRoot(root);
        }
    }

    [Theory]
    [MemberData(nameof(ReaderCases))]
    public void CurrentDay_StillUsesExistingWatermark(
        string source, string cacheFolder, string logFolder, string eventKey)
    {
        var root = Path.Combine(Path.GetTempPath(), $"HistoricalCacheRecoveryTests-{Guid.NewGuid():N}");
        using var cacheScope = MonitorCachePaths.PushLocalAppDataRoot(root);
        using var logScope = UsageLogPaths.PushRoot(Path.Combine(root, "logs"));
        var now = DateTimeOffset.UtcNow.ToOffset(Beijing);
        var start = new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, Beijing);
        var end = start.AddHours(12);
        var item = new TokenUsageEvent(start.AddHours(9), 100, 40, 10, 0, 110, eventKey);

        try
        {
            var cache = UsageCacheStore.Load(cacheFolder);
            cache.Put(CreateBucket(start, new[] { item }), isComplete: false,
                scannedThroughLocal: end.AddTicks(-1), detailEvents: new[] { item });
            WriteLog(source, Path.Combine(root, "logs", logFolder), item.Timestamp);

            var summary = Read(source, start, end, details: false, includeLiveToday: true);
            var rows = Read(source, start, end, details: true, includeLiveToday: true);

            Assert.Equal(110, summary.TotalTokens);
            Assert.Equal(0, summary.CacheWriteInputTokens);
            Assert.Equal(110, rows.TotalTokens);
            Assert.Equal(0, rows.CacheWriteInputTokens);
            Assert.True(cache.TryGetRecord(DateOnly.FromDateTime(start.DateTime), out var record));
            Assert.False(record.IsComplete);
        }
        finally
        {
            UsageCacheStore.Delete(cacheFolder);
            DeleteTestRoot(root);
        }
    }

    private static TokenUsageBucket Read(
        string source, DateTimeOffset start, DateTimeOffset end, bool details, bool includeLiveToday)
    {
        if (!details)
        {
            return source switch
            {
                "Claude" => ClaudeUsageReader.ReadRange(start, end, includeLiveToday),
                "ZCode" => ZCodeUsageReader.ReadRange(start, end, includeLiveToday),
                "WorkBuddy" => WorkBuddyUsageReader.ReadRange(start, end, includeLiveToday),
                "Dsh" => DshUsageReader.ReadRange(start, end, includeLiveToday),
                _ => throw new ArgumentOutOfRangeException(nameof(source))
            };
        }

        var rows = source switch
        {
            "Claude" => ClaudeUsageReader.ReadDetailRows(start, end, includeLiveToday),
            "ZCode" => ZCodeUsageReader.ReadDetailRows(start, end, includeLiveToday),
            "WorkBuddy" => WorkBuddyUsageReader.ReadDetailRows(start, end, includeLiveToday),
            "Dsh" => DshUsageReader.ReadDetailRows(start, end, includeLiveToday),
            _ => throw new ArgumentOutOfRangeException(nameof(source))
        };
        var total = new TokenUsageBucket { StartLocal = start };
        foreach (var row in rows)
        {
            total.Add(row.StartLocal, row.InputTokens, row.CachedInputTokens, row.CacheWriteInputTokens,
                row.OutputTokens, row.ReasoningOutputTokens, row.TotalTokens);
        }
        return total;
    }

    private static TokenUsageBucket CreateBucket(DateTimeOffset start, IEnumerable<TokenUsageEvent> events)
    {
        var bucket = new TokenUsageBucket { StartLocal = start };
        foreach (var item in events)
        {
            bucket.Add(item.Timestamp, item.InputTokens, item.CachedInputTokens, item.CacheWriteInputTokens,
                item.OutputTokens, item.ReasoningOutputTokens, item.TotalTokens);
        }
        return bucket;
    }

    private static void WriteLog(string source, string root, DateTimeOffset timestamp)
    {
        if (source == "ZCode")
        {
            root = Path.Combine(root, "rollout");
        }
        Directory.CreateDirectory(root);
        object entry = source switch
        {
            "Claude" => new
            {
                type = "assistant", timestamp,
                message = new { id = "recovery", usage = new { input_tokens = 40, cache_read_input_tokens = 40, cache_creation_input_tokens = 20, output_tokens = 10 } }
            },
            "WorkBuddy" => new
            {
                timestamp,
                message = new { id = "recovery", usage = new { input_tokens = 100, cache_read_input_tokens = 40, cache_write_input_tokens = 20, output_tokens = 10, total_tokens = 110 } }
            },
            "ZCode" => new
            {
                type = "model_io", completedAt = timestamp, requestId = "recovery",
                response = new { usage = new { inputTokens = 100, cacheReadTokens = 40, cacheWriteTokens = 20, outputTokens = 10, totalTokens = 110 } }
            },
            "Dsh" => new
            {
                type = "assistant/chunk", seq = 1, time = timestamp.ToUnixTimeMilliseconds(),
                data = new { chunk = new { type = "usage", usage = new { inputTokens = 40, cacheReadTokens = 40, cacheWriteTokens = 20, outputTokens = 10 } } }
            },
            _ => throw new ArgumentOutOfRangeException(nameof(source))
        };
        var text = JsonSerializer.Serialize(entry) + "\n";
        if (source == "Dsh")
        {
            var session = Path.Combine(root, "recovery");
            Directory.CreateDirectory(session);
            using var compressor = new Compressor();
            File.WriteAllBytes(Path.Combine(session, "session.jsonl.zstd"), compressor.Wrap(Encoding.UTF8.GetBytes(text)).ToArray());
        }
        else
        {
            File.WriteAllText(Path.Combine(root, "model-io-recovery.jsonl"), text);
        }
    }

    private static void DeleteTestRoot(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var expectedPrefix = Path.Combine(Path.GetTempPath(), "HistoricalCacheRecoveryTests-");
        if (!fullPath.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Unexpected test cleanup path.");
        }
        if (Directory.Exists(fullPath))
        {
            Directory.Delete(fullPath, recursive: true);
        }
    }
}
