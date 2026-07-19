using Xunit;

namespace CodexTokenMonitor.Tests;

public sealed class SubagentReplayFilterTests
{
    [Fact]
    public void RejectsReplayedTokenAndQuotaCountsBeforeChildTaskBoundary()
    {
        var filter = new SubagentReplayFilter();

        Assert.False(filter.ShouldReadTokenCount(SessionMeta("subagent", "2026-07-13T01:15:00.000Z")));
        Assert.False(filter.ShouldReadTokenCount(TokenCount("2026-07-13T01:15:00.100Z", includeRateLimits: false)));
        Assert.False(filter.ShouldReadTokenCount(TokenCount("2026-07-13T01:15:00.150Z", includeRateLimits: true)));
        Assert.False(filter.ShouldReadTokenCount(Bootstrap("2026-07-13T01:15:00.200Z")));
        Assert.False(filter.ShouldReadTokenCount(TaskStarted("2026-07-13T01:15:00.300Z")));

        Assert.True(filter.ShouldReadTokenCount(TokenCount("2026-07-13T01:15:01.500Z", includeRateLimits: true)));
    }

    [Fact]
    public void InterAgentMetadataStartsLiveChildTrafficWithoutBootstrapText()
    {
        var filter = new SubagentReplayFilter();

        Assert.False(filter.ShouldReadTokenCount(SessionMeta("subagent", "2026-07-13T01:15:00.000Z")));
        Assert.False(filter.ShouldReadTokenCount(TokenCount("2026-07-13T01:15:00.100Z", includeRateLimits: true)));
        Assert.False(filter.ShouldReadTokenCount("{\"timestamp\":\"2026-07-13T01:15:00.200Z\",\"type\":\"inter_agent_communication_metadata\",\"payload\":{}}"));

        Assert.True(filter.ShouldReadTokenCount(TokenCount("2026-07-13T01:15:00.300Z", includeRateLimits: true)));
    }

    [Fact]
    public void KeepsNormalSessionTokenCounts()
    {
        var filter = new SubagentReplayFilter();

        Assert.False(filter.ShouldReadTokenCount(SessionMeta("user", "2026-07-13T01:15:00.000Z")));
        Assert.True(filter.ShouldReadTokenCount(TokenCount("2026-07-13T01:15:00.100Z", includeRateLimits: true)));
    }

    private static string SessionMeta(string threadSource, string timestamp)
    {
        return $"{{\"timestamp\":\"{timestamp}\",\"type\":\"session_meta\",\"payload\":{{\"thread_source\":\"{threadSource}\"}}}}";
    }

    private static string TokenCount(string timestamp, bool includeRateLimits)
    {
        var rateLimits = includeRateLimits
            ? ",\"rate_limits\":{\"limit_id\":\"codex\",\"secondary\":{\"used_percent\":8,\"window_minutes\":10080,\"resets_at\":1784500000}}"
            : "";
        return $"{{\"timestamp\":\"{timestamp}\",\"type\":\"event_msg\",\"payload\":{{\"type\":\"token_count\",\"info\":{{\"last_token_usage\":{{\"total_tokens\":100}}}}{rateLimits}}}}}";
    }

    private static string Bootstrap(string timestamp)
    {
        return $"{{\"timestamp\":\"{timestamp}\",\"type\":\"response_item\",\"payload\":{{\"type\":\"message\",\"content\":\"You are an agent in a team of agents collaborating to complete a task.\"}}}}";
    }

    private static string TaskStarted(string timestamp)
    {
        return $"{{\"timestamp\":\"{timestamp}\",\"type\":\"event_msg\",\"payload\":{{\"type\":\"task_started\"}}}}";
    }
}
