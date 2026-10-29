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

    private static readonly DateTimeOffset Day = new(2026, 10, 30, 0, 0, 0, Beijing);

    [Fact]
    public void Build_FloorsBlockStartToHourAndSplitsAtFiveHours()
    {
        var blocks = ClaudeCodeBlockCalculator.Build(new[]
        {
            Row(Day.AddHours(9).AddMinutes(40), 100),
            Row(Day.AddHours(13).AddMinutes(30), 200),
            // 14:00 is the end of the 09:00 block, so this opens a new one.
            Row(Day.AddHours(14).AddMinutes(5), 300)
        });

        Assert.Equal(2, blocks.Count);
        Assert.Equal(Day.AddHours(9), blocks[0].StartLocal);
        Assert.Equal(Day.AddHours(14), blocks[0].EndLocal);
        Assert.Equal(300, blocks[0].Usage.TotalTokens);
        Assert.Equal(2, blocks[0].Usage.Events);
        Assert.Equal(Day.AddHours(14), blocks[1].StartLocal);
        Assert.Equal(300, blocks[1].Usage.TotalTokens);
    }

    [Fact]
    public void Block_IsActiveOnlyBeforeItsEnd()
    {
        var block = Assert.Single(ClaudeCodeBlockCalculator.Build(new[]
        {
            Row(Day.AddHours(10).AddMinutes(10), 1_000),
            Row(Day.AddHours(10).AddMinutes(20), 1_000)
        }));

        Assert.True(block.IsActiveAt(Day.AddHours(12)));
        Assert.False(block.IsActiveAt(Day.AddHours(15)));
        Assert.Equal(200d, block.TokensPerMinute);
        // 2,000 so far plus 200/min for the 180 minutes left until 15:00.
        Assert.Equal(38_000L, block.ProjectedTotalTokens(Day.AddHours(12)));
        Assert.Null(block.ProjectedTotalTokens(Day.AddHours(15)));
    }

    [Fact]
    public void Build_KeepsModelMixPerBlock()
    {
        var block = Assert.Single(ClaudeCodeBlockCalculator.Build(new[]
        {
            Row(Day.AddHours(8), 900, "claude-opus-5-5"),
            Row(Day.AddHours(8).AddMinutes(1), 100, "mimo-v2.5-pro")
        }));

        Assert.Equal(900, block.Usage.ModelUsage["claude-opus-5-5"].TotalTokens);
        Assert.Equal(100, block.Usage.ModelUsage["mimo-v2.5-pro"].TotalTokens);
    }

    [Fact]
    public void ReadCurrent_FindsTheLiveBlockFromLogs()
    {
        var now = DateTimeOffset.UtcNow.ToOffset(Beijing);
        var project = Path.Combine(root, UsageSource.ClaudeCode.ToString(), "-C-work");
        Directory.CreateDirectory(project);
        File.WriteAllText(
            Path.Combine(project, "session.jsonl"),
            "{\"timestamp\":\"" + now.AddMinutes(-2).ToUniversalTime().ToString("O") +
            "\",\"type\":\"assistant\",\"message\":{\"id\":\"msg_live\",\"model\":\"claude-opus-5-5\"," +
            "\"usage\":{\"input_tokens\":1000,\"cache_creation_input_tokens\":0,\"cache_read_input_tokens\":4000,\"output_tokens\":500}}}\n");

        var snapshot = ClaudeCodeActivityReader.ReadCurrent(now);

        var block = Assert.IsType<ClaudeCodeBlock>(snapshot.CurrentBlock);
        Assert.Equal(5_500, block.Usage.TotalTokens);
        if (now.AddMinutes(-2).Date == now.Date)
        {
            // Just after midnight the event belongs to an uncached yesterday.
            Assert.Equal(5_500, snapshot.Week.TotalTokens);
            Assert.Equal(1, snapshot.WeekActiveDays);
        }
    }
}
