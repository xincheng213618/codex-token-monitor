using Xunit;

namespace CodexTokenMonitor.Tests;

/// <summary>
/// Boundary behaviours of the subagent replay filter beyond the basic
/// bootstrap/inter-agent boundaries covered in SubagentReplayFilterTests.
/// </summary>
public sealed class SubagentReplayFilterBoundaryTests
{
    [Fact]
    public void ForkedSessionId_DetectedAsSubagentWithoutThreadSource()
    {
        var filter = new SubagentReplayFilter();

        var meta = "{\"timestamp\":\"2026-07-13T01:15:00.000Z\",\"type\":\"session_meta\",\"payload\":{" +
                   "\"forked_from_id\":\"abc\",\"parent_thread_id\":\"def\"}}";
        Assert.False(filter.ShouldReadTokenCount(meta));
        // A forked replay without any boundary signal stays suppressed even
        // across many small-timestamp records.
        Assert.False(filter.ShouldReadTokenCount(TokenCount("2026-07-13T01:15:00.100Z")));
        Assert.False(filter.ShouldReadTokenCount(TokenCount("2026-07-13T01:15:00.500Z")));
    }

    [Fact]
    public void TimestampJump_BelowOneSecond_StaysReplay()
    {
        var filter = new SubagentReplayFilter();
        filter.ShouldReadTokenCount(SessionMeta("subagent", "2026-07-13T01:15:00.000Z"));
        filter.ShouldReadTokenCount(TokenCount("2026-07-13T01:15:00.000Z"));

        Assert.False(filter.ShouldReadTokenCount(TokenCount("2026-07-13T01:15:00.900Z")));
    }

    [Fact]
    public void TimestampJump_OverOneSecond_StartsLiveWithoutAnyBoundaryMarker()
    {
        var filter = new SubagentReplayFilter();
        filter.ShouldReadTokenCount(SessionMeta("subagent", "2026-07-13T01:15:00.000Z"));

        Assert.False(filter.ShouldReadTokenCount(TokenCount("2026-07-13T01:15:00.100Z")));
        Assert.True(filter.ShouldReadTokenCount(TokenCount("2026-07-13T01:15:01.200Z")));
    }

    [Fact]
    public void TaskStartedWithoutBootstrap_DoesNotStartLiveTraffic()
    {
        var filter = new SubagentReplayFilter();
        filter.ShouldReadTokenCount(SessionMeta("subagent", "2026-07-13T01:15:00.000Z"));

        // task_started only counts after the collaboration bootstrap: without
        // it, a stray task_started cannot open the child's live traffic.
        Assert.False(filter.ShouldReadTokenCount(TaskStarted("2026-07-13T01:15:00.100Z")));
        Assert.False(filter.ShouldReadTokenCount(TokenCount("2026-07-13T01:15:00.200Z")));
    }

    [Fact]
    public void UnparseableTimestamp_MidReplay_DoesNotAdvanceBoundary()
    {
        var filter = new SubagentReplayFilter();
        filter.ShouldReadTokenCount(SessionMeta("subagent", "2026-07-13T01:15:00.000Z"));
        filter.ShouldReadTokenCount(TokenCount("2026-07-13T01:15:00.100Z"));

        // A token_count without a usable timestamp neither starts live traffic
        // nor moves the replay boundary forward.
        Assert.False(filter.ShouldReadTokenCount(TokenCountWithoutTimestamp()));
        Assert.False(filter.ShouldReadTokenCount(TokenCount("2026-07-13T01:15:00.800Z")));
    }

    [Fact]
    public void ModelContext_IsObservedEvenWhileReplayIsSuppressed()
    {
        var filter = new SubagentReplayFilter();
        filter.ShouldReadTokenCount(SessionMeta("subagent", "2026-07-13T01:15:00.000Z"));

        filter.ShouldReadTokenCount(
            "{\"timestamp\":\"2026-07-13T01:15:00.050Z\",\"type\":\"turn_context\",\"payload\":{\"model\":\"gpt-5.6-sol\"}}");

        // Attribution must survive replay suppression, or the child session's
        // usage would be priced with a stale model.
        Assert.Equal("gpt-5.6-sol", filter.ModelId);
    }

    private static string SessionMeta(string threadSource, string timestamp)
    {
        return $"{{\"timestamp\":\"{timestamp}\",\"type\":\"session_meta\",\"payload\":{{\"thread_source\":\"{threadSource}\"}}}}";
    }

    private static string TokenCount(string timestamp)
    {
        return $"{{\"timestamp\":\"{timestamp}\",\"type\":\"event_msg\",\"payload\":{{\"type\":\"token_count\",\"info\":{{\"last_token_usage\":{{\"total_tokens\":100}}}}}}}}";
    }

    private static string TokenCountWithoutTimestamp()
    {
        return "{\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"last_token_usage\":{\"total_tokens\":100}}}}";
    }

    private static string TaskStarted(string timestamp)
    {
        return $"{{\"timestamp\":\"{timestamp}\",\"type\":\"event_msg\",\"payload\":{{\"type\":\"task_started\"}}}}";
    }
}
