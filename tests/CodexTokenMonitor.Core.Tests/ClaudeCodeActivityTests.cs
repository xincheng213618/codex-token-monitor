using Xunit;

namespace CodexTokenMonitor.Tests;

public sealed class ClaudeCodeActivityTests : IDisposable
{
    private static readonly TimeSpan Beijing = TimeSpan.FromHours(8);
    private readonly string root = Path.Combine(Path.GetTempPath(), $"ClaudeActivityTests-{Guid.NewGuid():N}");
    private readonly IDisposable logScope;
    private readonly IDisposable cacheScope;

    public ClaudeCodeActivityTests()
    {
        cacheScope = MonitorCachePaths.PushLocalAppDataRoot(root);
        logScope = UsageLogPaths.PushRoot(root);
    }

    public void Dispose()
    {
        logScope.Dispose();
        cacheScope.Dispose();
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch
        {
            // Best-effort cleanup of the temporary tree.
        }
    }

    private static TokenUsageBucket Row(DateTimeOffset timestamp, long tokens, string model = "claude-opus-5-5")
    {
        var row = new TokenUsageBucket { StartLocal = timestamp };
        row.Add(new TokenUsageEvent(timestamp, tokens, 0, 0, 0, tokens, $"k{timestamp.Ticks}", ModelId: model));
        return row;
    }

    private void WriteLog(DateTimeOffset at, string id, int input, string model = "claude-opus-5-5")
    {
        var project = Path.Combine(root, UsageSource.ClaudeCode.ToString(), "-C-work");
        Directory.CreateDirectory(project);
        File.AppendAllText(
            Path.Combine(project, "session.jsonl"),
            "{\"timestamp\":\"" + at.ToUniversalTime().ToString("O") + "\",\"type\":\"assistant\",\"message\":{\"id\":\"" + id +
            "\",\"model\":\"" + model + "\",\"usage\":{\"input_tokens\":" + input +
            ",\"cache_creation_input_tokens\":0,\"cache_read_input_tokens\":0,\"output_tokens\":0}}}\n");
    }

    private void WriteRateLimits(DateTimeOffset recorded, double fivePercent, DateTimeOffset fiveReset, double weekPercent, DateTimeOffset weekReset)
    {
        var folder = Path.Combine(root, "ClaudeCodeTokenMonitor");
        Directory.CreateDirectory(folder);
        File.AppendAllText(Path.Combine(folder, "rate-limits.jsonl"),
            $"{{\"recorded_at\":{recorded.ToUnixTimeSeconds()},\"five_hour\":{{\"used_percentage\":{fivePercent},\"resets_at\":{fiveReset.ToUnixTimeSeconds()}}}," +
            $"\"seven_day\":{{\"used_percentage\":{weekPercent},\"resets_at\":{weekReset.ToUnixTimeSeconds()}}}}}\n");
    }

    private static readonly DateTimeOffset Day = new(2026, 10, 30, 0, 0, 0, Beijing);

    [Fact]
    public void Build_StartsAtFirstRequestAndSplitsAtFiveHours()
    {
        var blocks = ClaudeCodeBlockCalculator.Build(new[]
        {
            Row(Day.AddHours(9).AddMinutes(40), 100),
            Row(Day.AddHours(14).AddMinutes(30), 200),
            // 14:40 is the end of the 09:40 window, so this opens a new one.
            Row(Day.AddHours(14).AddMinutes(45), 300)
        });

        Assert.Equal(2, blocks.Count);
        Assert.Equal(Day.AddHours(9).AddMinutes(40), blocks[0].StartLocal);
        Assert.Equal(Day.AddHours(14).AddMinutes(40), blocks[0].EndLocal);
        Assert.Equal(300, blocks[0].Usage.TotalTokens);
        Assert.Equal(Day.AddHours(14).AddMinutes(45), blocks[1].StartLocal);
    }

    [Fact]
    public void Build_AnchorKeepsBoundariesWhenTheReadWindowMoves()
    {
        // Review reproduction: continuous use 21:00–03:00. The full log gives
        // 21:00–02:00; reading only from 22:10 used to shift it to 22:10.
        var rows = Enumerable.Range(0, 13).Select(index => Row(Day.AddHours(21).AddMinutes(index * 30), 100)).ToList();
        var full = ClaudeCodeBlockCalculator.Build(rows);
        var truncated = rows.Where(row => row.StartLocal >= Day.AddHours(22).AddMinutes(10)).ToList();

        var anchored = ClaudeCodeBlockCalculator.Build(truncated, full[0].StartLocal);

        Assert.Equal(Day.AddHours(21), full[0].StartLocal);
        Assert.Equal(full.Select(block => block.StartLocal), anchored.Select(block => block.StartLocal));
        Assert.Equal(full[^1].Usage.TotalTokens, anchored[^1].Usage.TotalTokens);
    }

    [Fact]
    public void Block_IsActiveOnlyBeforeItsEnd()
    {
        var block = Assert.Single(ClaudeCodeBlockCalculator.Build(new[]
        {
            Row(Day.AddHours(10), 1_000),
            Row(Day.AddHours(10).AddMinutes(10), 1_000)
        }));

        Assert.True(block.IsActiveAt(Day.AddHours(12)));
        Assert.False(block.IsActiveAt(Day.AddHours(15)));
        Assert.Equal(200d, block.TokensPerMinute);
        // 2,000 so far plus 200/min for the 180 minutes left until 15:00.
        Assert.Equal(38_000L, block.ProjectedTotalTokens(Day.AddHours(12)));
    }

    [Fact]
    public void ReadCurrent_MeasuresTheOfficialWindowNotTheCalendar()
    {
        var now = DateTimeOffset.UtcNow.ToOffset(Beijing);
        WriteLog(now.AddHours(-6), "msg_before_window", 1_000);
        WriteLog(now.AddHours(-2), "msg_in_window", 4_000);
        // Official 5h window: 3h in, resets in 2h. The week window began
        // 6.5 days ago, so both events fall inside it.
        WriteRateLimits(now.AddMinutes(-1), 50, now.AddHours(2), 20, now.AddHours(12));

        var snapshot = ClaudeCodeActivityReader.ReadCurrent(now);

        var five = Assert.IsType<ClaudeQuotaWindowUsage>(snapshot.FiveHourWindow);
        Assert.Equal(ClaudeWindowSource.Official, five.Source);
        Assert.Equal(50, five.OfficialPercent);
        Assert.Equal(now.AddHours(-3), five.StartLocal, TimeSpan.FromSeconds(1));
        Assert.Equal(4_000, five.Usage.TotalTokens);
        var week = Assert.IsType<ClaudeQuotaWindowUsage>(snapshot.WeekWindow);
        Assert.Equal(5_000, week.Usage.TotalTokens);
        Assert.True(week.IsComplete);
        // The official reset pins the current block too.
        Assert.Equal(now.AddHours(-3), Assert.IsType<ClaudeCodeBlock>(snapshot.CurrentBlock).StartLocal, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void ReadCurrent_ManualReadingDefinesTheWindow()
    {
        var now = DateTimeOffset.UtcNow.ToOffset(Beijing);
        WriteLog(now.AddHours(-4.5), "msg_old", 1_000);
        WriteLog(now.AddMinutes(-30), "msg_new", 2_000);
        var reading = new ClaudeQuotaReading(ClaudeQuotaWindowKind.FiveHour, now.AddMinutes(-5), 50,
            now.AddHours(-4), now.AddHours(1), 10m, 2_000, 1);

        var snapshot = ClaudeCodeActivityReader.ReadCurrent(now, new[] { reading });

        var five = Assert.IsType<ClaudeQuotaWindowUsage>(snapshot.FiveHourWindow);
        Assert.Equal(ClaudeWindowSource.Manual, five.Source);
        // The event at −4.5h opened the inferred block but lies before the entered window.
        Assert.Equal(2_000, five.Usage.TotalTokens);
    }

    [Fact]
    public void ReadCurrent_ScansHistoryDaysTheCacheHasNotFinished()
    {
        // Review reproduction: yesterday is not cached; it used to be dropped silently.
        var now = DateTimeOffset.UtcNow.ToOffset(Beijing);
        var todayStart = ClaudeCodeActivitySnapshot.StartOfDay(now);
        WriteLog(todayStart.AddDays(-1).AddHours(12), "msg_yesterday", 100);
        WriteLog(now.AddMinutes(-1) < todayStart ? todayStart : now.AddMinutes(-1), "msg_today", 100);

        var snapshot = ClaudeCodeActivityReader.ReadCurrent(now);

        Assert.Equal(200, snapshot.Week.TotalTokens);
        Assert.Equal(2, snapshot.WeekActiveDays);
        Assert.True(snapshot.WeekIsComplete);
    }

    [Fact]
    public void OfficialCalibrationPairsCostWithItsObservationTime()
    {
        var now = DateTimeOffset.UtcNow.ToOffset(Beijing);
        WriteLog(now.AddHours(-2), "before_snapshot", 1_000_000);
        WriteLog(now.AddMinutes(-30), "after_snapshot", 9_000_000);
        WriteRateLimits(now.AddHours(-1), 50, now.AddHours(2), 20, now.AddHours(12));
        var snapshot = ClaudeCodeActivityReader.ReadCurrent(now);
        ClaudeQuotaEstimator.RecordOfficial(snapshot, Array.Empty<ClaudeQuotaReading>());
        var reading = ClaudeQuotaReadingStore.Load().Single(item => item.Kind == ClaudeQuotaWindowKind.FiveHour);
        Assert.Equal(10_000_000, snapshot.FiveHourWindow!.Usage.TotalTokens);
        Assert.Equal(1_000_000, reading.Tokens);
        Assert.Equal(8m, reading.CapacityUsd);
        Assert.Equal(now.AddHours(-1), reading.RecordedLocal, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void RateLimitLog_KeepsNewestOpenWindowAndSkipsBrokenLines()
    {
        var now = Day.AddHours(12);
        string Line(DateTimeOffset recorded, double five, DateTimeOffset reset) =>
            $"{{\"recorded_at\":{recorded.ToUnixTimeSeconds()},\"five_hour\":{{\"used_percentage\":{five},\"resets_at\":{reset.ToUnixTimeSeconds()}}}}}";

        var snapshots = ClaudeRateLimitLog.Parse(new[]
        {
            "﻿" + Line(now.AddHours(-6), 90, now.AddHours(-1)),
            Line(now.AddMinutes(-20), 10, now.AddHours(4)),
            Line(now.AddMinutes(-5), 12.5, now.AddHours(4)),
            "{\"recorded_at\":17"
        });
        var latest = ClaudeRateLimitLog.Latest(snapshots, now);

        Assert.Equal(3, snapshots.Count);
        Assert.Equal(12.5, latest!.FiveHour!.UsedPercent);
        Assert.Null(latest.SevenDay);
        Assert.Null(ClaudeRateLimitLog.Latest(snapshots.Take(1), now));
    }
}
