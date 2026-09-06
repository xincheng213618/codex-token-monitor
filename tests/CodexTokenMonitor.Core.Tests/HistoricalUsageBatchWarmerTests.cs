using System.Text;
using System.Text.Json;
using Xunit;
using ZstdSharp;

namespace CodexTokenMonitor.Tests;

public sealed class HistoricalUsageBatchWarmerTests
{
    private static readonly TimeSpan Beijing = TimeSpan.FromHours(8);
    private static readonly DateTimeOffset FirstDay = new(1999, 2, 1, 0, 0, 0, Beijing);

    public static IEnumerable<object[]> Readers => new[]
    {
        new object[] { "Claude", "ClaudeCodeTokenMonitor", "ClaudeCode" },
        new object[] { "ZCode", "ZCodeTokenMonitor", "ZCode" },
        new object[] { "WorkBuddy", "WorkBuddyTokenMonitor", "WorkBuddy" },
        new object[] { "Dsh", "DshTokenMonitor", "Dsh" }
    };

    [Theory]
    [MemberData(nameof(Readers))]
    public void Batch_ScansFilesOncePreservesDailyIdentityAndImports(
        string source, string cacheFolder, string logFolder)
    {
        var root = CreateRoot();
        using var cacheScope = MonitorCachePaths.PushLocalAppDataRoot(root);
        using var logScope = UsageLogPaths.PushRoot(Path.Combine(root, "logs"));
        try
        {
            WriteLog(source, Path.Combine(root, "logs", logFolder));
            var thirdDay = FirstDay.AddDays(2);
            var emptyDay = FirstDay.AddDays(3);
            var expectedFirst = ReadDailyRows(source, FirstDay).Sum(row => row.TotalTokens);
            var expectedThird = ReadDailyRows(source, thirdDay).Sum(row => row.TotalTokens);
            var cache = UsageCacheStore.Load(cacheFolder);
            cache.MergeImportedDetailEvents(new[]
            {
                new TokenUsageEvent(FirstDay.AddHours(12), 50, 0, 5, 0, 55, "imported-only")
            });
            var now = DateTimeOffset.UtcNow.ToOffset(Beijing);
            var today = new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, Beijing);
            var requested = new[]
            {
                FirstDay.ToUniversalTime(), thirdDay, emptyDay, FirstDay, today, today.AddDays(1)
            };
            var completed = new List<DateTimeOffset>();
            var progress = new List<(int Completed, int Total)>();

            Warm(source, requested, dayCompleted: completed.Add,
                fileProgress: (done, total) => progress.Add((done, total)));

            Assert.Equal(new[] { emptyDay, thirdDay, FirstDay }, completed);
            Assert.Equal(new[] { (0, 1), (1, 1) }, progress);
            Assert.Equal(expectedFirst + 55, cache.ReadRange(FirstDay, FirstDay.AddDays(1)).TotalTokens);
            Assert.Equal(expectedThird, cache.ReadRange(thirdDay, thirdDay.AddDays(1)).TotalTokens);
            Assert.Equal(2, cache.GetDetailEvents(DateOnly.FromDateTime(FirstDay.DateTime)).Count);
            Assert.Single(cache.GetDetailEvents(DateOnly.FromDateTime(thirdDay.DateTime)));
            Assert.False(cache.TryGetRecord(DateOnly.FromDateTime(FirstDay.AddDays(1).DateTime), out _));
            Assert.False(cache.TryGetRecord(DateOnly.FromDateTime(today.DateTime), out _));
            foreach (var day in completed)
            {
                Assert.Empty(UsageCacheStore.GetIncompleteDays(cacheFolder, day, day));
            }

            completed.Clear();
            progress.Clear();
            Warm(source, requested, dayCompleted: completed.Add,
                fileProgress: (done, total) => progress.Add((done, total)));
            Assert.Equal(3, completed.Count);
            Assert.Empty(progress);
            Assert.Equal(expectedFirst + 55, cache.ReadRange(FirstDay, FirstDay.AddDays(1)).TotalTokens);
        }
        finally
        {
            UsageCacheStore.Delete(cacheFolder);
            DeleteRoot(root);
        }
    }

    [Theory]
    [MemberData(nameof(Readers))]
    public void Batch_UnreadableFileRetainsImportsAndDoesNotReportCompletion(
        string source, string cacheFolder, string logFolder)
    {
        var root = CreateRoot();
        using var cacheScope = MonitorCachePaths.PushLocalAppDataRoot(root);
        using var logScope = UsageLogPaths.PushRoot(Path.Combine(root, "logs"));
        try
        {
            var path = WriteLog(source, Path.Combine(root, "logs", logFolder));
            var cache = UsageCacheStore.Load(cacheFolder);
            cache.MergeImportedDetailEvents(new[]
            {
                new TokenUsageEvent(FirstDay.AddHours(12), 50, 0, 5, 0, 55, "imported-only")
            });
            var completed = new List<DateTimeOffset>();
            using (var heldFile = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                Warm(source, new[] { FirstDay, FirstDay.AddDays(2) }, dayCompleted: completed.Add);
            }

            Assert.Empty(completed);
            Assert.Single(UsageCacheStore.GetIncompleteDays(cacheFolder, FirstDay, FirstDay));
            Assert.Equal(55, cache.ReadRange(FirstDay, FirstDay.AddDays(1)).TotalTokens);
            Assert.Equal("imported-only",
                Assert.Single(cache.GetDetailEvents(DateOnly.FromDateTime(FirstDay.DateTime))).Key);

            Warm(source, new[] { FirstDay, FirstDay.AddDays(2) }, dayCompleted: completed.Add);
            Assert.Equal(2, completed.Count);
        }
        finally
        {
            UsageCacheStore.Delete(cacheFolder);
            DeleteRoot(root);
        }
    }

    [Theory]
    [MemberData(nameof(Readers))]
    public void Batch_CanceledScanDoesNotPersistOrReportCompletion(
        string source, string cacheFolder, string logFolder)
    {
        var root = CreateRoot();
        using var cacheScope = MonitorCachePaths.PushLocalAppDataRoot(root);
        using var logScope = UsageLogPaths.PushRoot(Path.Combine(root, "logs"));
        using var cancellation = new CancellationTokenSource();
        try
        {
            WriteLog(source, Path.Combine(root, "logs", logFolder));
            var completed = new List<DateTimeOffset>();
            Assert.Throws<OperationCanceledException>(() =>
                Warm(source, new[] { FirstDay, FirstDay.AddDays(2) }, cancellation.Token,
                    completed.Add, (done, _) =>
                    {
                        if (done > 0)
                        {
                            cancellation.Cancel();
                        }
                    }));
            Assert.Empty(completed);
            Assert.False(UsageCacheStore.Load(cacheFolder)
                .TryGetRecord(DateOnly.FromDateTime(FirstDay.DateTime), out _));
        }
        finally
        {
            UsageCacheStore.Delete(cacheFolder);
            DeleteRoot(root);
        }
    }

    private static void Warm(string source, IEnumerable<DateTimeOffset> days,
        CancellationToken cancellationToken = default,
        Action<DateTimeOffset>? dayCompleted = null, Action<int, int>? fileProgress = null)
    {
        switch (source)
        {
            case "Claude":
                ClaudeUsageReader.WarmHistoricalDays(days, cancellationToken, dayCompleted, fileProgress);
                break;
            case "ZCode":
                ZCodeUsageReader.WarmHistoricalDays(days, cancellationToken, dayCompleted, fileProgress);
                break;
            case "WorkBuddy":
                WorkBuddyUsageReader.WarmHistoricalDays(days, cancellationToken, dayCompleted, fileProgress);
                break;
            case "Dsh":
                DshUsageReader.WarmHistoricalDays(days, cancellationToken, dayCompleted, fileProgress);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(source));
        }
    }

    private static IReadOnlyList<TokenUsageBucket> ReadDailyRows(string source, DateTimeOffset day)
    {
        return source switch
        {
            "Claude" => ClaudeUsageReader.ReadTransientDetailRows(day, day.AddDays(1)),
            "ZCode" => ZCodeUsageReader.ReadTransientDetailRows(day, day.AddDays(1)),
            "WorkBuddy" => WorkBuddyUsageReader.ReadTransientDetailRows(day, day.AddDays(1)),
            "Dsh" => DshUsageReader.ReadTransientDetailRows(day, day.AddDays(1)),
            _ => throw new ArgumentOutOfRangeException(nameof(source))
        };
    }

    private static string WriteLog(string source, string root)
    {
        if (source == "ZCode")
        {
            root = Path.Combine(root, "rollout");
        }
        if (source == "Dsh")
        {
            root = Path.Combine(root, "batch");
        }
        Directory.CreateDirectory(root);
        // The same key repeats within a day and across days. A full-range
        // scan must match the results of independent daily scans.
        var text = string.Join("\n", new[]
        {
            LogLine(source, FirstDay.AddHours(9), 10),
            LogLine(source, FirstDay.AddHours(9), 40),
            LogLine(source, FirstDay.AddDays(1).AddHours(9), 100),
            LogLine(source, FirstDay.AddDays(2).AddHours(9), 200)
        }) + "\n";
        var path = Path.Combine(root, source == "Dsh" ? "session.jsonl.zstd" : "model-io-batch.jsonl");
        if (source == "Dsh")
        {
            using var compressor = new Compressor();
            File.WriteAllBytes(path, compressor.Wrap(Encoding.UTF8.GetBytes(text)).ToArray());
        }
        else
        {
            File.WriteAllText(path, text);
        }
        return path;
    }

    private static string LogLine(string source, DateTimeOffset timestamp, long input)
    {
        object entry = source switch
        {
            "Claude" => new
            {
                type = "assistant", timestamp,
                message = new { id = "repeated", usage = new { input_tokens = input, cache_read_input_tokens = 40, cache_creation_input_tokens = 20, output_tokens = 10 } }
            },
            "WorkBuddy" => new
            {
                timestamp,
                message = new { id = "repeated", usage = new { input_tokens = input + 60, cache_read_input_tokens = 40, cache_write_input_tokens = 20, output_tokens = 10, total_tokens = input + 70 } }
            },
            "ZCode" => new
            {
                type = "model_io", completedAt = timestamp, requestId = "repeated",
                response = new { usage = new { inputTokens = input + 60, cacheReadTokens = 40, cacheWriteTokens = 20, outputTokens = 10, totalTokens = input + 70 } }
            },
            "Dsh" => new
            {
                type = "assistant/chunk", seq = 1, time = timestamp.ToUnixTimeMilliseconds(),
                data = new { chunk = new { type = "usage", usage = new { inputTokens = input, cacheReadTokens = 40, cacheWriteTokens = 20, outputTokens = 10 } } }
            },
            _ => throw new ArgumentOutOfRangeException(nameof(source))
        };
        return JsonSerializer.Serialize(entry);
    }

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"HistoricalUsageBatchWarmerTests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteRoot(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var prefix = Path.Combine(Path.GetTempPath(), "HistoricalUsageBatchWarmerTests-");
        if (!fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Unexpected test cleanup path.");
        }
        if (Directory.Exists(fullPath))
        {
            Directory.Delete(fullPath, recursive: true);
        }
    }
}
