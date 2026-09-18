using System.Text.Json;
using Xunit;

namespace CodexTokenMonitor.Tests;

public sealed class CodexQuotaBatchTests
{
    private static readonly DateTimeOffset Day = new(1999, 1, 18, 0, 0, 0, TimeSpan.FromHours(8));

    [Fact]
    public void SingleScanIncludesCrossDayRecordsAndSkipsCompletedDays()
    {
        using var env = new EnvironmentScope();
        var file = env.WriteLog("both.jsonl", Day, Day.AddDays(2));
        File.SetLastWriteTimeUtc(file, Day.AddDays(-10).UtcDateTime);
        var progress = new List<(int, int)>();
        var complete = new List<DateTimeOffset>();
        UsageSourceReaders.Codex.WarmQuotaSnapshotDays(new[] { Day, Day.AddDays(2), Day.AddDays(3), Day },
            dayCompleted: complete.Add, fileProgress: (n, total) => progress.Add((n, total)));
        Assert.Equal(new[] { (0, 1), (1, 1) }, progress);
        Assert.Equal(3, complete.Count);
        Assert.Equal(2, UsageSourceReaders.Codex.ReadCachedQuotaSnapshots(Day, Day.AddDays(4)).Count);
        Assert.Single(UsageSourceReaders.Codex.GetIncompleteQuotaSnapshotDays(Day, Day.AddDays(3)), Day.AddDays(1));
        progress.Clear();
        UsageSourceReaders.Codex.WarmQuotaSnapshotDays(new[] { Day, Day.AddDays(2), Day.AddDays(3) },
            fileProgress: (n, total) => progress.Add((n, total)));
        Assert.Empty(progress);
    }

    [Fact]
    public void UnreadableFileDoesNotSealDaysOrLoseImportedSnapshots()
    {
        using var env = new EnvironmentScope();
        var file = env.WriteLog("locked.jsonl", Day);
        var cache = QuotaSnapshotCacheStore.Load("CodexTokenMonitor");
        cache.MergeImportedSnapshots(new[] { new CodexQuotaSnapshot(Day.AddHours(11), "codex", null,
            null, null, 10, Day.AddDays(7)) });
        using var locked = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None);
        var complete = new List<DateTimeOffset>();
        UsageSourceReaders.Codex.WarmQuotaSnapshotDays(new[] { Day }, dayCompleted: complete.Add);
        Assert.Empty(complete);
        Assert.Single(UsageSourceReaders.Codex.GetIncompleteQuotaSnapshotDays(Day, Day));
        Assert.Single(cache.GetSnapshots(DateOnly.FromDateTime(Day.DateTime)));
    }

    [Fact]
    public void CancellationDuringScanDoesNotPublishPartialDays()
    {
        using var env = new EnvironmentScope();
        env.WriteLog("first.jsonl", Day);
        env.WriteLog("second.jsonl", Day.AddDays(1));
        using var cancel = new CancellationTokenSource();
        Assert.Throws<OperationCanceledException>(() => UsageSourceReaders.Codex.WarmQuotaSnapshotDays(
            new[] { Day, Day.AddDays(1) }, cancel.Token,
            fileProgress: (count, _) => { if (count == 1) cancel.Cancel(); }));
        Assert.Equal(2, UsageSourceReaders.Codex.GetIncompleteQuotaSnapshotDays(Day, Day.AddDays(1)).Count);
        Assert.Empty(UsageSourceReaders.Codex.ReadCachedQuotaSnapshots(Day, Day.AddDays(2)));
    }

    [Fact]
    public void CancellationDuringSaveCanResumeAndLiveDaysRemainOpen()
    {
        using var env = new EnvironmentScope();
        env.WriteLog("both.jsonl", Day, Day.AddDays(1));
        using var cancel = new CancellationTokenSource();
        Assert.Throws<OperationCanceledException>(() => UsageSourceReaders.Codex.WarmQuotaSnapshotDays(
            new[] { Day, Day.AddDays(1) }, cancel.Token, _ => cancel.Cancel()));
        Assert.Single(UsageSourceReaders.Codex.GetIncompleteQuotaSnapshotDays(Day, Day.AddDays(1)), Day);
        UsageSourceReaders.Codex.WarmQuotaSnapshotDays(new[] { Day, Day.AddDays(1), BeijingClock.Now, BeijingClock.Now.AddDays(1) });
        Assert.Empty(UsageSourceReaders.Codex.GetIncompleteQuotaSnapshotDays(Day, Day.AddDays(1)));
        Assert.Equal(2, UsageSourceReaders.Codex.GetIncompleteQuotaSnapshotDays(BeijingClock.Now, BeijingClock.Now.AddDays(1)).Count);
    }

    private sealed class EnvironmentScope : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "CodexQuotaBatchTests-" + Guid.NewGuid().ToString("N"));
        private readonly IDisposable cache;
        private readonly IDisposable logs;
        public EnvironmentScope()
        {
            cache = MonitorCachePaths.PushLocalAppDataRoot(root);
            logs = UsageLogPaths.PushRoot(Path.Combine(root, "logs"));
        }
        public string WriteLog(string name, params DateTimeOffset[] days)
        {
            var folder = Path.Combine(root, "logs", "Codex", "sessions");
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, name);
            File.WriteAllLines(path, days.Select(day => JsonSerializer.Serialize(new
            {
                timestamp = day.AddHours(9), type = "event_msg",
                payload = new { type = "token_count", info = new { }, rate_limits = new
                {
                    limit_id = "codex", secondary = new { used_percent = 30, window_minutes = 10080,
                        resets_at = Day.AddDays(7).ToUnixTimeSeconds() }
                } }
            })));
            return path;
        }
        public void Dispose()
        {
            UsageCacheStore.Delete("CodexTokenMonitor");
            logs.Dispose(); cache.Dispose();
        }
    }
}
