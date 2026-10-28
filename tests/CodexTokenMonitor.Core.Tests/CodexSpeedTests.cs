using System.Text.Json;
using CodexTokenMonitor;
using Xunit;

namespace CodexTokenMonitor.Core.Tests;

public sealed class CodexSpeedTests
{
    private static readonly DateTimeOffset Day = new(2026, 10, 25, 0, 0, 0, TimeSpan.FromHours(8));
    private static DateTimeOffset At(double seconds) => Day.AddSeconds(seconds);
    private static string Line(double seconds, string type, object payload) =>
        JsonSerializer.Serialize(new { timestamp = At(seconds), type, payload });
    private static string Meta(string id = "s") => Line(0, "session_meta", new { id });
    private static string Input(double seconds) => Line(seconds, "event_msg", new { type = "task_started" });
    private static string Usage(double seconds, string id, int output = 300) => Line(seconds, "token_usage_record",
        new { response_id = id, usage = new { input_tokens = 1000, output_tokens = output } });
    private static string Legacy(double seconds, int total, int last = 300) => Line(seconds, "event_msg", new
    {
        type = "token_count", info = new
        {
            total_token_usage = new { input_tokens = total * 2, output_tokens = total, total_tokens = total * 3 },
            last_token_usage = new { input_tokens = last * 2, output_tokens = last, total_tokens = last * 3 }
        }
    });

    [Fact]
    public void ToolWaitIsExcludedAndStreamingDoesNotResetRequestStart()
    {
        var parser = new CodexSpeedParser("file");
        parser.Read(Meta()); parser.Read(Input(1));
        Assert.Equal(100, parser.Read(Usage(4, "r1"))!.OutputPerSecond);
        Assert.Equal(AgentSpeedPhase.Tools, parser.Agent.Phase);
        parser.Read(Line(34, "response_item", new { type = "function_call_output" }));
        parser.Read(Line(35, "response_item", new { type = "reasoning" }));
        parser.Read(Line(36, "response_item", new { type = "function_call_output" }));
        var next = parser.Read(Usage(37, "r2"))!;
        Assert.Equal(At(34), next.Start);
        Assert.Equal(100, next.OutputPerSecond);
    }

    [Fact]
    public void ModernUsageDedupesIgnoresLegacyAndClearsOnAbort()
    {
        var parser = new CodexSpeedParser("file");
        parser.Read(Meta()); parser.Read(Input(1));
        Assert.NotNull(parser.Read(Usage(4, "r1")));
        Assert.Null(parser.Read(Usage(4, "r1")));
        Assert.Null(parser.Read(Legacy(5, 300)));
        parser.Read(Input(6));
        parser.Read(Line(7, "event_msg", new { type = "turn_aborted" }));
        Assert.Equal(AgentSpeedPhase.Idle, parser.Agent.Phase);
        Assert.Null(parser.Agent.RequestStart);
        Assert.Null(parser.Read(Usage(8, "r2"))!.OutputPerSecond);
    }

    [Fact]
    public void ModelFollowsSessionAndAppliedThreadSettings()
    {
        var parser = new CodexSpeedParser("file");
        parser.Read(Line(0, "session_meta", new { id = "s", model = "initial" }));
        parser.Read(Input(1));
        Assert.Equal("initial", parser.Read(Usage(4, "r1"))!.Model);
        parser.Read(Line(5, "event_msg", new { type = "thread_settings_applied", thread_settings = new { model = "changed" } }));
        parser.Read(Input(6));
        Assert.Equal("changed", parser.Read(Usage(9, "r2"))!.Model);
    }

    [Fact]
    public void LegacyUsesDeltaAndFallsBackWhenCountersReset()
    {
        var parser = new CodexSpeedParser("file");
        parser.Read(Meta()); parser.Read(Input(1));
        Assert.Equal(300, parser.Read(Legacy(4, 300))!.OutputTokens);
        Assert.Null(parser.Read(Legacy(5, 300)));
        Assert.Equal(At(4), parser.Agent.RequestStart);
        Assert.Equal(200, parser.Read(Legacy(6, 500, last: 999))!.OutputTokens);
        Assert.Equal(50, parser.Read(Legacy(7, 50, last: 50))!.OutputTokens);
    }

    [Fact]
    public void CopiedHistoryMalformedLinesAndForeignMailboxDoNotGenerate()
    {
        var parser = new CodexSpeedParser("child");
        parser.Read(Line(100, "session_meta", new { id = "child", thread_source = "subagent", agent_path = "/root/a" }));
        Assert.Null(parser.Read(Input(1)));
        Assert.Null(parser.Read(Usage(3, "copied")));
        Assert.Null(parser.Read("{partial")); Assert.Null(parser.Read("[]"));
        parser.Read(Line(102, "response_item", new { type = "agent_message", recipient = "/root/b" }));
        Assert.Equal(AgentSpeedPhase.Idle, parser.Agent.Phase);
        Assert.Null(parser.Agent.RequestStart);
        parser.Read(Line(103, "response_item", new { type = "agent_message", recipient = "/root/a" }));
        Assert.Equal(At(103), parser.Agent.RequestStart);
        Assert.Equal(AgentSpeedPhase.Idle, parser.Agent.Phase);
    }

    [Theory]
    [InlineData(0, 0, .5)]
    [InlineData(0, .1, .5)]
    [InlineData(0, 900, 900d)]
    [InlineData(0, 901, null)]
    [InlineData(2, 1, null)]
    public void TimingValidation(double start, double end, double? seconds)
    {
        var response = new AgentSpeedResponse("r", "a", "m", At(start), At(end), 100, 0);
        Assert.Equal(seconds, response.DurationSeconds);
    }

    [Fact]
    public void ParallelLiveAddsButAverageIsWeightedByRequestDuration()
    {
        var engine = new AgentSpeedEngine();
        engine.Add(new("a", "a", "m", At(1), At(4), 300, 1000));
        engine.Add(new("b", "b", "m", At(1), At(4), 300, 1000));
        engine.Add(new("b", "copy", "m", At(1), At(4), 300, 1000));
        var frame = engine.Snapshot(At(4), Day);
        Assert.Equal(200, frame.Live);
        Assert.Equal(100, frame.Average);
        Assert.Equal(2, frame.Requests);
        Assert.Equal(0, frame.EstimatedShare);
        Assert.Equal(300, frame.History.Count);
        Assert.Equal(600, frame.History.Sum(p => p.Measured), 6);
        engine.Add(new("slow", "a", "m", At(10), At(20), 100, 0));
        Assert.Equal(700d / 16, engine.Snapshot(At(20), Day).Average!.Value, 6);
    }

    [Fact]
    public void InFlightUsesSameModelAndCompletionReplacesEstimate()
    {
        var engine = new AgentSpeedEngine();
        engine.Add(new("old", "a", "m", At(1), At(4), 300, 0));
        engine.Update(new("a", "s", "m", false, AgentSpeedPhase.Generating, At(10), At(10)));
        var before = engine.Snapshot(At(13), Day);
        Assert.Equal(100, before.Live); Assert.Equal(1, before.EstimatedShare);
        engine.Add(new("new", "a", "m", At(10), At(13), 150, 0));
        engine.Update(new("a", "s", "m", false, AgentSpeedPhase.Tools, At(13), At(13)));
        var after = engine.Snapshot(At(13), Day);
        Assert.Equal(50, after.Live); Assert.Equal(0, after.EstimatedShare);
        Assert.Equal(0, engine.Snapshot(At(16), Day).Live);
        engine.Update(new("other", "s2", "unknown", false, AgentSpeedPhase.Generating, At(20), At(20)));
        Assert.Equal(0, engine.Snapshot(At(23), Day).Live);
    }

    [Fact]
    public void StaleGenerationStopsAndInvalidTimingsRemainInCoverage()
    {
        var engine = new AgentSpeedEngine();
        engine.Add(new("r", "a", "m", At(1), At(4), 300, 0));
        engine.Add(new("unknown", "a", "m", null, At(5), 600, 0));
        engine.Update(new("a", "s", "m", false, AgentSpeedPhase.Generating, At(6), At(6)));
        var frame = engine.Snapshot(At(307), Day);
        Assert.Equal(0, frame.Live);
        Assert.Equal(AgentSpeedPhase.Idle, Assert.Single(frame.Agents).Phase);
        Assert.Equal(2, frame.Requests); Assert.Equal(1, frame.MeasuredRequests);
        Assert.Equal(100, frame.Average);
        Assert.Equal(900, Assert.Single(frame.Models).OutputTokens);
        var nextDay = engine.Snapshot(Day.AddDays(1).AddSeconds(5), Day.AddDays(1));
        Assert.Equal(0, nextDay.Requests); Assert.Null(nextDay.Average);
    }

    [Fact]
    public void MonitorTailsPartialLinesDedupesCopiesAndReadsCompressedArchives()
    {
        var root = Path.Combine(Path.GetTempPath(), "codex-speed-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "sessions"));
        Directory.CreateDirectory(Path.Combine(root, "archived_sessions"));
        try
        {
            var file = Path.Combine(root, "sessions", "s.jsonl");
            File.WriteAllText(file, Meta() + "\n" + Input(1) + "\n" + Usage(4, "r1") + "\n");
            using var monitor = new AgentSpeedMonitor(root);
            Assert.Equal(1, monitor.Read(At(5), Day).Requests);
            Assert.Equal(1, monitor.Read(At(6), Day).Requests);
            File.AppendAllText(file, Input(7) + "\n" + Usage(10, "r2"));
            var partial = monitor.Read(At(11), Day);
            Assert.Equal(1, partial.Requests); Assert.Equal(1, partial.UnreadableFiles);
            File.AppendAllText(file, "\n");
            Assert.Equal(2, monitor.Read(At(12), Day).Requests);
            File.Copy(file, Path.Combine(root, "archived_sessions", "copy.jsonl"));
            using (var stream = File.Create(Path.Combine(root, "archived_sessions", "z.jsonl.zst")))
            using (var encoder = new ZstdSharp.CompressionStream(stream))
            using (var writer = new StreamWriter(encoder))
                writer.Write(Meta("z") + "\n" + Input(13) + "\n" + Usage(16, "r3") + "\n");
            Assert.True(SpinWait.SpinUntil(() => monitor.Read(At(20), Day).Requests == 3, TimeSpan.FromSeconds(3)),
                "new archives are discovered through asynchronous file notifications");
            // A truncated replacement replays from the beginning without adding old response keys.
            File.WriteAllText(file, Meta() + "\n" + Input(1) + "\n" + Usage(4, "r1") + "\n");
            Assert.Equal(3, monitor.Read(At(21), Day).Requests);
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            Assert.Throws<OperationCanceledException>(() => monitor.Read(At(22), Day, cancellation.Token));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
