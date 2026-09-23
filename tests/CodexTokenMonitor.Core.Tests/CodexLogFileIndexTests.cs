using System.Text.Json;
using Xunit;

namespace CodexTokenMonitor.Tests;

public sealed class CodexLogFileIndexTests
{
    private static readonly DateTimeOffset Day = new(2000, 1, 1, 0, 0, 0, TimeSpan.FromHours(8));

    [Fact]
    public void RestartSkipsUnchangedUnrelatedFileButRetainsCrossDayRecords()
    {
        using var env = new EnvironmentScope();
        File.WriteAllLines(env.Log, new[] { Line(Day), Line(Day.AddDays(2)), Line(Day.AddDays(1)) });
        using (var index = new CodexLogFileIndex())
            Assert.Equal(3, Read(index, env.Log, Day.AddDays(10)));
        using var reopened = new CodexLogFileIndex();
        Assert.Equal(0, Read(reopened, env.Log, Day.AddDays(10)));
        Assert.Equal(3, Read(reopened, env.Log, Day.AddDays(1)));
        Assert.Equal(3, Read(reopened, env.Log, Day.AddDays(2))); // Inclusive last timestamp.
    }

    [Fact]
    public void NewFileAndAppendWithPreservedMtimeAreRead()
    {
        using var env = new EnvironmentScope();
        File.WriteAllText(env.Log, Line(Day) + "\n");
        using var index = new CodexLogFileIndex();
        Assert.Equal(1, Read(index, env.Log, Day.AddDays(1)));
        var mtime = File.GetLastWriteTimeUtc(env.Log);
        File.AppendAllText(env.Log, Line(Day.AddDays(1)) + "\n");
        File.SetLastWriteTimeUtc(env.Log, mtime);
        Assert.Equal(2, Read(index, env.Log, Day.AddDays(1)));
        var copied = env.Log + ".copied";
        File.Copy(env.Log, copied);
        Assert.Equal(2, Read(index, copied, Day.AddDays(1)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReplacementWithPreservedDatesAndTruncationInvalidateIndex(bool truncate)
    {
        using var env = new EnvironmentScope();
        File.WriteAllText(env.Log, Line(Day) + "\n" + (truncate ? Line(Day) + "\n" : ""));
        using var index = new CodexLogFileIndex();
        Read(index, env.Log, Day.AddDays(1));
        var mtime = File.GetLastWriteTimeUtc(env.Log);
        var created = File.GetCreationTimeUtc(env.Log);
        File.WriteAllText(env.Log, Line(Day.AddDays(1)) + "\n");
        File.SetLastWriteTimeUtc(env.Log, mtime);
        File.SetCreationTimeUtc(env.Log, created);
        Assert.Equal(1, Read(index, env.Log, Day.AddDays(1)));
    }

    [Fact]
    public void CancelledOrChangingScanCannotCreateAFalseCacheHit()
    {
        using var env = new EnvironmentScope();
        File.WriteAllText(env.Log, Line(Day) + "\n");
        using (var index = new CodexLogFileIndex())
        {
            using var cancel = new CancellationTokenSource();
            Assert.ThrowsAny<OperationCanceledException>(() => index.Read(env.Log, Day.AddDays(1), Day.AddDays(2),
                _ => cancel.Cancel(), cancel.Token));
            var changed = false;
            Assert.False(index.Read(env.Log, Day.AddDays(1), Day.AddDays(2), _ =>
            {
                if (changed) return;
                changed = true;
                File.AppendAllText(env.Log, Line(Day.AddDays(1)) + "\n");
            }, default));
        }
        using var reopened = new CodexLogFileIndex();
        Assert.Equal(2, Read(reopened, env.Log, Day.AddDays(1)));
    }

    [Fact]
    public void UnreadableFileDoesNotBecomeSuccessfulEvenWhenIndexedOutsideRange()
    {
        using var env = new EnvironmentScope();
        File.WriteAllText(env.Log, Line(Day) + "\n");
        using var index = new CodexLogFileIndex();
        Read(index, env.Log, Day.AddDays(1));
        using var locked = new FileStream(env.Log, FileMode.Open, FileAccess.Read, FileShare.None);
        Assert.False(index.Read(env.Log, Day.AddDays(1), Day.AddDays(2), _ => { }, default));
    }

    [Fact]
    public void DamagedIndexFallsBackAndClearCacheRemovesIt()
    {
        using var env = new EnvironmentScope();
        File.WriteAllText(env.Log, Line(Day) + "\n");
        var path = Path.Combine(MonitorCachePaths.LocalAppData, "CodexTokenMonitor", CodexLogFileIndex.FileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{broken");
        using (var index = new CodexLogFileIndex())
            Assert.Equal(1, Read(index, env.Log, Day.AddDays(1)));
        Assert.True(File.Exists(path));
        UsageCacheStore.Delete("CodexTokenMonitor");
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void LaterDayWarmupReadsCopiedAndAppendedHistoryWithOriginalModelContext()
    {
        using var env = new EnvironmentScope();
        File.WriteAllLines(env.Log, new[]
        {
            "{\"type\":\"turn_context\",\"payload\":{\"model\":\"gpt-5.6-sol\",\"service_tier\":\"fast\"}}",
            Line(Day), Line(Day.AddDays(1))
        });
        var reader = new CodexUsageReader();
        reader.WarmHistoricalDays(new[] { Day });
        File.AppendAllText(env.Log, Line(Day.AddDays(2)) + "\n");
        reader = new CodexUsageReader(); // Index must survive an application restart.
        reader.WarmHistoricalDays(new[] { Day.AddDays(1), Day.AddDays(2) });
        var rows = reader.ReadCachedDetailRows(Day.AddDays(1), Day.AddDays(3));
        Assert.Equal(2, rows.Count);
        Assert.Equal(220, rows.Sum(row => row.TotalTokens));
        Assert.All(rows, row => Assert.Contains("gpt-5.6-sol", row.ModelUsage.Keys));
        // Quota uses the same index but must still parse an overlapping file.
        reader.WarmQuotaSnapshotDays(new[] { Day.AddDays(1), Day.AddDays(2) });
        Assert.Equal(2, reader.ReadCachedQuotaSnapshots(Day.AddDays(1), Day.AddDays(3)).Count);
    }

    private static int Read(CodexLogFileIndex index, string file, DateTimeOffset day)
    {
        var lines = 0;
        Assert.True(index.Read(file, day, day.AddDays(1), _ => lines++, default));
        return lines;
    }

    private static string Line(DateTimeOffset timestamp) => JsonSerializer.Serialize(new
    {
        timestamp, type = "event_msg", payload = new
        {
            type = "token_count", turn_id = timestamp.ToString("O"),
            info = new { last_token_usage = new { input_tokens = 100, cached_input_tokens = 40,
                output_tokens = 10, reasoning_output_tokens = 0, total_tokens = 110 } },
            rate_limits = new { limit_id = "codex", secondary = new { used_percent = 30,
                window_minutes = 10080, resets_at = Day.AddDays(7).ToUnixTimeSeconds() } }
        }
    });

    private sealed class EnvironmentScope : IDisposable
    {
        private readonly IDisposable cacheScope;
        private readonly IDisposable logScope;
        public string Log { get; }
        public EnvironmentScope()
        {
            var root = Path.Combine(Path.GetTempPath(), "CodexLogFileIndexTests-" + Guid.NewGuid().ToString("N"));
            cacheScope = MonitorCachePaths.PushLocalAppDataRoot(root);
            logScope = UsageLogPaths.PushRoot(Path.Combine(root, "logs"));
            var folder = Path.Combine(root, "logs", "Codex", "sessions");
            Directory.CreateDirectory(folder);
            Log = Path.Combine(folder, "session.jsonl");
        }
        public void Dispose()
        {
            UsageCacheStore.Delete("CodexTokenMonitor");
            logScope.Dispose();
            cacheScope.Dispose();
        }
    }
}
