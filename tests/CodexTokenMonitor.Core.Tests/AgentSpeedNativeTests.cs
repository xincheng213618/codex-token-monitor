using System.Text;
using System.Text.Json;
using CodexTokenMonitor;
using Microsoft.Data.Sqlite;
using Xunit;

namespace CodexTokenMonitor.Core.Tests;

public sealed class AgentSpeedNativeTests
{
    private static readonly DateTimeOffset Day = new(2026, 10, 25, 0, 0, 0, TimeSpan.FromHours(8));
    private static DateTimeOffset At(double second) => Day.AddSeconds(second);
    private static string Row(double second, string type, object? message = null, object? extra = null) =>
        JsonSerializer.Serialize(new { timestamp = At(second), type, message, providerData = extra });

    [Fact]
    public void ClaudeFinalUsageReplacesStreamingSnapshotAndPreciseStartWins()
    {
        var parser = new ClaudeSpeedParser("session.jsonl"); var engine = new AgentSpeedEngine();
        parser.ReadResponses(Row(0, "api-request")); parser.ReadResponses(Row(1, "user", new { content = "prompt" }));
        var partial = Assert.Single(parser.ReadResponses(Row(2, "assistant", new { id = "r", model = "m", stop_reason = (string?)null,
            usage = new { input_tokens = 100, output_tokens = 8 } })));
        Assert.False(partial.Final); Assert.Equal(At(0), partial.Start); engine.Add(partial);
        Assert.Null(engine.Snapshot(At(2), Day).Average);
        var final = Assert.Single(parser.ReadResponses(Row(4, "assistant", new { id = "r", model = "m", stop_reason = "tool_use",
            usage = new { input_tokens = 100, cache_read_input_tokens = 50, cache_creation_input_tokens = 25, output_tokens = 400, speed = "fast" } })));
        engine.Add(final); var snapshot = engine.Snapshot(At(4), Day);
        Assert.Equal(1, snapshot.Requests); Assert.Equal(1, snapshot.MeasuredRequests);
        Assert.Equal(100, snapshot.Average); Assert.Equal(175, final.InputTokens); Assert.Equal("Fast（回执）", final.ExecutionMode);
        Assert.Equal(AgentSpeedPhase.Tools, parser.Agent.Phase);
        parser.ReadResponses(Row(5, "user", new { content = "[Request interrupted by user]" }));
        Assert.Equal(AgentSpeedPhase.Idle, parser.Agent.Phase);
    }

    [Fact]
    public void DshSnapshotUnwrapsNativeRecordsAndExcludesInheritedHistory()
    {
        var parser = new DshSpeedParser("child/session.v4.jsonl");
        string Event(double second, string type, object data, int seq = 1) => JsonSerializer.Serialize(new { type, time = At(second).ToUnixTimeMilliseconds(), seq, data });
        parser.ReadResponses(JsonSerializer.Serialize(new { type = "session", id = "child", createdAt = At(10).ToUnixTimeMilliseconds(), parentSession = "parent" }));
        parser.ReadResponses(Event(0, "step/start", new { }));
        Assert.Empty(parser.ReadResponses(Event(1, "assistant/message", new { message = new { id = "copied" }, usage = new { outputTokens = 500 } })));
        parser.ReadResponses(Event(12, "request/header", new { header = new { config = new { model = "deepseek", speed = "standard" } } }));
        parser.ReadResponses(JsonSerializer.Serialize(new { type = "event", @event = new { type = "step/start", time = At(13).ToUnixTimeMilliseconds() } }));
        var response = Assert.Single(parser.ReadResponses(Event(17, "assistant/message", new { message = new { id = "r", content = new[] { new { type = "tool-call" } } },
            usage = new { outputTokens = 400, inputTokens = 100, cacheReadTokens = 50, cacheWriteTokens = 25 } })));
        Assert.Equal(100, response.OutputPerSecond); Assert.Equal(175, response.InputTokens);
        Assert.Equal(UsageSource.Dsh, response.Source); Assert.Equal("Standard（设置）", response.ExecutionMode);
        Assert.Equal("parent", parser.Agent.ParentSessionId); Assert.Equal(AgentSpeedPhase.Tools, parser.Agent.Phase);
    }

    [Fact]
    public void WorkBuddyParallelToolsWaitForLastResultAndUsageUpdatesDoNotMultiplyRequests()
    {
        var parser = new WorkBuddySpeedParser("s.jsonl"); var engine = new AgentSpeedEngine();
        parser.ReadResponses(JsonSerializer.Serialize(new { timestamp = At(0), type = "message", role = "user" }));
        string Call(string call) => JsonSerializer.Serialize(new { timestamp = At(2), type = "function_call", callId = call,
            providerData = new { messageId = "r", model = "hy3", usage = new { inputTokens = 100, outputTokens = 200 } } });
        foreach (var response in parser.ReadResponses(Call("a"))) engine.Add(response);
        foreach (var response in parser.ReadResponses(Call("b"))) engine.Add(response);
        Assert.Equal(1, engine.Snapshot(At(2), Day).Requests);
        parser.ReadResponses(JsonSerializer.Serialize(new { timestamp = At(20), type = "function_call_result", callId = "a" }));
        Assert.Equal(AgentSpeedPhase.Tools, parser.Agent.Phase);
        parser.ReadResponses(JsonSerializer.Serialize(new { timestamp = At(30), type = "function_call_result", callId = "b" }));
        Assert.Equal(AgentSpeedPhase.Generating, parser.Agent.Phase); Assert.Equal(At(30), parser.Agent.RequestStart);
        var next = Assert.Single(parser.ReadResponses(JsonSerializer.Serialize(new { timestamp = At(34), type = "model-usage",
            providerData = new { messageId = "next", model = "hy3", rawUsage = new { prompt_tokens = 1000, completion_tokens = 400 } } })));
        Assert.Equal(100, next.OutputPerSecond); Assert.Equal(AgentSpeedPhase.Idle, parser.Agent.Phase);
    }

    [Fact]
    public void KimiLegacyAndNativeMirrorsShareRequestAndNativeCopiesDedupe()
    {
        var parser = new KimiSpeedParser("s/agents/main/wire.jsonl"); var engine = new AgentSpeedEngine();
        parser.ReadResponses(JsonSerializer.Serialize(new { type = "llm.request", time = At(0).ToUnixTimeMilliseconds(), turnStep = "0.1" }));
        var legacy = Assert.Single(parser.ReadResponses(JsonSerializer.Serialize(new { type = "usage.record", time = At(4).ToUnixTimeMilliseconds(),
            model = "kimi", usageScope = "turn", usage = new { inputOther = 100, inputCacheRead = 50, output = 400 } })));
        engine.Add(legacy);
        var nativeRow = JsonSerializer.Serialize(new { type = "context.append_message", time = At(4).ToUnixTimeMilliseconds(),
            message = new { id = "provider-r", role = "assistant", model = "kimi", usage = new { inputOther = 100, inputCacheRead = 50, output = 400 } } });
        engine.Add(Assert.Single(parser.ReadResponses(nativeRow)));
        Assert.Equal(1, engine.Snapshot(At(4), Day).Requests);
        var copy = new KimiSpeedParser("fork/agents/main/wire.jsonl");
        copy.ReadResponses(JsonSerializer.Serialize(new { type = "llm.request", time = At(0).ToUnixTimeMilliseconds(), turnStep = "0.1" }));
        engine.Add(Assert.Single(copy.ReadResponses(JsonSerializer.Serialize(new { type = "usage.record", time = At(4).ToUnixTimeMilliseconds(),
            model = "kimi", usageScope = "turn", usage = new { inputOther = 100, inputCacheRead = 50, output = 400 } }))));
        engine.Add(Assert.Single(copy.ReadResponses(nativeRow)));
        var snapshot = engine.Snapshot(At(4), Day);
        Assert.Equal(1, snapshot.Requests); Assert.Equal(100, snapshot.Average);
    }

    [Fact]
    public void SourceModeThroughputPeakAndParentClosureRemainIndependent()
    {
        var engine = new AgentSpeedEngine();
        engine.Add(new("r", "a", "m", At(1), At(4), 300, 900, ExecutionMode: "Fast"));
        engine.Add(new("r", "b", "m", At(1), At(4), 150, 300, UsageSource.ClaudeCode));
        engine.Update(new("parent", "p", "m", false, AgentSpeedPhase.Idle, At(-1000), null));
        engine.Update(new("a", "c", "m", true, AgentSpeedPhase.Tools, At(4), null, ExecutionMode: "Fast", ParentSessionId: "p"));
        var snapshot = engine.Snapshot(At(4), Day);
        Assert.Equal(2, snapshot.Requests); Assert.Equal(150, snapshot.MeasuredPeak); Assert.Equal(2, snapshot.Models.Count);
        Assert.Equal("parent", snapshot.Agents[0].Agent.Id); Assert.Equal(1, snapshot.Agents[1].Depth);
        Assert.Equal(550, engine.Snapshot(At(4), Day, AgentSpeedMetric.TotalThroughput).Live);
        Assert.Equal(100, engine.Snapshot(At(4), Day, source: UsageSource.Codex).MeasuredPeak);
        engine.Add(new("future", "a", "m", At(10), At(13), 900, 0));
        Assert.Equal(150, engine.Snapshot(At(4), Day).MeasuredPeak);
        Assert.Equal(300, engine.Snapshot(At(13), Day).MeasuredPeak);
    }

    [Fact]
    public void CompressedTailRetainsIncompleteFrameAndSplitUtf8Line()
    {
        using var fixture = new Fixture(); var path = Path.Combine(fixture.Root, "frames.zst");
        var bytes = Encoding.UTF8.GetBytes("中文\nsecond\n");
        using var compressor = new ZstdSharp.Compressor();
        var first = compressor.Wrap(bytes.AsSpan(0, 2)).ToArray(); var second = compressor.Wrap(bytes.AsSpan(2)).ToArray();
        File.WriteAllBytes(path, first.Concat(second[..^2]).ToArray());
        using var tail = new ZstdJsonlTailReader(); var lines = new List<string>(); var restarts = 0;
        Assert.False(tail.Read(path, lines.Add, () => restarts++, default)); Assert.Empty(lines);
        using (var stream = new FileStream(path, FileMode.Append)) stream.Write(second.AsSpan(second.Length - 2));
        Assert.True(tail.Read(path, lines.Add, () => restarts++, default));
        Assert.Equal(new[] { "中文", "second" }, lines); Assert.Equal(1, restarts);
        Assert.True(tail.Read(path, lines.Add, () => restarts++, default)); Assert.Equal(2, lines.Count);
    }

    [Fact]
    public void QoderRequestCanCrossSegmentFilesAndRetriesKeepOriginalStart()
    {
        using var fixture = new Fixture(); var segments = Path.Combine(fixture.Root, "s", "segments"); Directory.CreateDirectory(segments);
        File.WriteAllText(Path.Combine(segments, "001.jsonl"), JsonSerializer.Serialize(new { ts = At(0), type = "model.request.started", request_id = "r", data = new { model = "qoder" } }) + "\n");
        File.WriteAllText(Path.Combine(segments, "002.jsonl"), JsonSerializer.Serialize(new { ts = At(3), type = "model.request.attempt_failed", request_id = "r", data = new { will_retry = true } }) + "\n" +
            JsonSerializer.Serialize(new { ts = At(5), type = "model.response.completed", request_id = "r", data = new { model = "qoder", output_tokens = 500, input_tokens = 1000 } }) + "\n");
        using var monitor = new AgentSpeedMonitor(fixture.Root, UsageSource.Qoder); var snapshot = monitor.Read(At(5), Day);
        Assert.Equal(1, snapshot.Requests); Assert.Equal(100, snapshot.Average); Assert.Equal(UsageSource.Qoder, snapshot.Last!.Source);
    }

    [Fact]
    public void ClosedAndDetachedWalReadsDoNotCreateSidecarsAtTheSource()
    {
        using var fixture = new Fixture(); var original = Path.Combine(fixture.Root, "original.sqlite3");
        var detached = Path.Combine(fixture.Root, "detached.sqlite3");
        using (var writer = new SqliteConnection("Data Source=" + original + ";Pooling=False"))
        {
            writer.Open(); using var command = writer.CreateCommand();
            command.CommandText = "PRAGMA journal_mode=WAL; CREATE TABLE sample(value INTEGER); INSERT INTO sample VALUES(42)"; command.ExecuteNonQuery();
            File.Copy(original, detached); File.Copy(original + "-wal", detached + "-wal");
        }
        var before = Directory.GetFiles(fixture.Root).Order().ToArray();
        using (var database = new AgentSqliteReadOnly(original))
        { using var command = database.Connection.CreateCommand(); command.CommandText = "SELECT value FROM sample"; Assert.Equal(42L, command.ExecuteScalar()); }
        using (var database = new AgentSqliteReadOnly(detached))
        { using var command = database.Connection.CreateCommand(); command.CommandText = "SELECT value FROM sample"; Assert.Equal(42L, command.ExecuteScalar()); }
        Assert.Equal(before, Directory.GetFiles(fixture.Root).Order().ToArray());
        Assert.False(File.Exists(original + "-shm")); Assert.False(File.Exists(detached + "-shm"));
    }

    [Fact]
    public void ZCodeAcceleratorMetadataSeparatesActualRoutingFromMockSelection()
    {
        using var fixture = new Fixture(); Directory.CreateDirectory(Path.Combine(fixture.Root, "db")); var path = Path.Combine(fixture.Root, "db", "db.sqlite");
        using (var connection = new SqliteConnection("Data Source=" + path + ";Pooling=False"))
        {
            connection.Open(); using var schema = connection.CreateCommand();
            schema.CommandText = "CREATE TABLE session(id TEXT,parent_id TEXT); CREATE TABLE message(id TEXT,session_id TEXT,data TEXT); CREATE TABLE model_usage(id TEXT,session_id TEXT,model_id TEXT,started_at INTEGER,completed_at INTEGER,input_tokens INTEGER,output_tokens INTEGER,provider_id TEXT,parent_user_message_id TEXT)";
            schema.ExecuteNonQuery();
            foreach (var (session, provider, card) in new[] { ("fast", "account:zai-highspeed-card", "real"), ("normal", "account:zai", "real"), ("mock", "account:zai-highspeed-card", "hsc_mock_test") })
            {
                using var command = connection.CreateCommand(); command.CommandText = "INSERT INTO session VALUES($s,'parent'); INSERT INTO message VALUES($s,$s,$data); INSERT INTO model_usage VALUES($s,$s,'glm',$start,$end,900,300,$provider,$s)";
                command.Parameters.AddWithValue("$s", session); command.Parameters.AddWithValue("$start", At(1).ToUnixTimeMilliseconds()); command.Parameters.AddWithValue("$end", At(4).ToUnixTimeMilliseconds());
                command.Parameters.AddWithValue("$provider", provider); command.Parameters.AddWithValue("$data", JsonSerializer.Serialize(new { metadata = new { highspeed = new { schemaVersion = 1, provider = "zai", model = "glm", cardId = card } } })); command.ExecuteNonQuery();
            }
        }
        using var monitor = new AgentSpeedMonitor(fixture.Root, UsageSource.ZCode); var snapshot = monitor.Read(At(5), Day);
        Assert.Equal(3, snapshot.Requests);
        Assert.Contains(snapshot.Models, row => row.ExecutionMode == "Highspeed（实际路由）");
        Assert.Contains(snapshot.Models, row => row.ExecutionMode == "Standard（实际路由；已选加速卡）");
        Assert.Contains(snapshot.Models, row => row.ExecutionMode == "Highspeed（模拟卡设置）");
        Assert.Equal(3, snapshot.Agents.Count(row => row.Depth == 1));
    }

    [Fact]
    public void ZCodeUsesReadOnlyLedgerWithNativeRequestTimingAndIncrementalDeduplication()
    {
        using var fixture = new Fixture(); Directory.CreateDirectory(Path.Combine(fixture.Root, "db")); var path = Path.Combine(fixture.Root, "db", "db.sqlite");
        using (var connection = new SqliteConnection("Data Source=" + path + ";Pooling=False"))
        {
            connection.Open(); using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE model_usage (id TEXT, session_id TEXT, model_id TEXT, started_at INTEGER, completed_at INTEGER, input_tokens INTEGER, output_tokens INTEGER); INSERT INTO model_usage VALUES ('r','s','glm',$start,$end,1000,400)";
            command.Parameters.AddWithValue("$start", At(0).ToUnixTimeMilliseconds()); command.Parameters.AddWithValue("$end", At(4).ToUnixTimeMilliseconds()); command.ExecuteNonQuery();
        }
        using var monitor = new AgentSpeedMonitor(fixture.Root, UsageSource.ZCode);
        var first = monitor.Read(At(4), Day); Assert.Equal(1, first.Requests); Assert.Equal(100, first.Average);
        Assert.Equal(1, monitor.Read(At(5), Day).Requests);
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "agent-speed-native-" + Guid.NewGuid().ToString("N"));
        public Fixture() => Directory.CreateDirectory(Root);
        public void Dispose() { Directory.Delete(Root, recursive: true); }
    }
}
