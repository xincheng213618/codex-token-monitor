using System.Text.Json;
using CodexTokenMonitor;
using Xunit;

namespace CodexTokenMonitor.Core.Tests;

/// <summary>Upstream source fixtures and independent reference outputs, pinned to a94a67f.</summary>
public sealed class AgentSpeedGoldenTests
{
    private static string Root => Path.Combine(AppContext.BaseDirectory, "Fixtures", "AgentSpeed");
    public static IEnumerable<object[]> Cases()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "golden.json")));
        return document.RootElement.EnumerateArray().Select(item => new object[] { item.GetProperty("file").GetString()! }).ToArray();
    }
    [Theory]
    [MemberData(nameof(Cases))]
    public void NativeResponsesMatchUpstreamGoldenTimingAndUsage(string target)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "golden.json")));
        var engine = new AgentSpeedEngine();
        foreach (var item in document.RootElement.EnumerateArray())
        {
            var relative = item.GetProperty("file").GetString()!; var path = Path.Combine(Root, relative);
            var source = item.GetProperty("source").GetString() switch
            {
                "codex" => UsageSource.Codex, "claudeCode" or "claudeDesktop" => UsageSource.ClaudeCode,
                "deepseek" => UsageSource.Dsh, _ => UsageSource.WorkBuddy
            };
            var parser = AgentSpeedPaths.Parser(source, path, key => engine.CanAccept(source, key, path));
            void Consume(string line) { foreach (var response in parser.ReadResponses(line)) engine.Add(response); }
            if (path.EndsWith(".zst") || path.EndsWith(".zstd"))
            { using var reader = new ZstdJsonlTailReader(); reader.Read(path, Consume, parser.Reset, default, allowFinalLine: true); }
            else foreach (var line in File.ReadLines(path)) Consume(line);
            if (relative != target) continue;
            var actual = engine.Responses.Where(response => response.AgentId == path).ToArray();
            var expected = item.GetProperty("responses").EnumerateArray().ToArray();
            Assert.Equal(expected.Length, actual.Length);
            foreach (var value in expected)
            {
                var key = value.GetProperty("key").GetString()!;
                var response = Assert.Single(actual, response => response.Key == key || response.Key.EndsWith(":" + key, StringComparison.Ordinal));
                Assert.Equal(value.GetProperty("input").GetInt64(), response.InputTokens);
                Assert.Equal(value.GetProperty("output").GetInt64(), response.OutputTokens);
                var expectedStart = value.GetProperty("startMs").ValueKind == JsonValueKind.Null ? (long?)null : value.GetProperty("startMs").GetInt64();
                Assert.True(expectedStart == response.Start?.ToUnixTimeMilliseconds(), $"{relative} key={key}: expected start {expectedStart}, actual {response.Start?.ToUnixTimeMilliseconds()}");
                Assert.Equal(value.GetProperty("endMs").GetInt64(), response.End.ToUnixTimeMilliseconds());
                Assert.Equal(value.GetProperty("final").GetBoolean(), response.Final);
                Assert.Equal(value.GetProperty("model").GetString() ?? "未知模型", response.Model);
            }
            return;
        }
        Assert.Fail("Missing golden target " + target);
    }

    [Fact]
    public void HistoricalOwnershipAndDurationsMatchGoldenDataAcrossCopiedFiles()
    {
        var fixture = Path.Combine(Path.GetTempPath(), "speed-golden-history-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(fixture);
        using var logs = UsageLogPaths.PushRoot(Path.Combine(fixture, "logs"));
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "golden.json")));
            var expected = document.RootElement.EnumerateArray().ToArray(); var ordering = 0;
            foreach (var item in expected)
            {
                var path = item.GetProperty("file").GetString()!;
                var source = item.GetProperty("source").GetString() switch { "codex" => UsageSource.Codex, "claudeCode" or "claudeDesktop" => UsageSource.ClaudeCode, "deepseek" => UsageSource.Dsh, _ => UsageSource.WorkBuddy };
                var trim = source switch { UsageSource.Codex => "codex/", UsageSource.ClaudeCode => "claude/projects/", UsageSource.Dsh => "dsh/sessions/", _ => "workbuddy/" };
                var target = Path.Combine(UsageLogPaths.GetOverrideRoot(source)!, path[trim.Length..]); Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(Path.Combine(Root, path), target);
                File.SetLastWriteTimeUtc(target, new DateTime(2026, 10, 22, 0, 0, 0, DateTimeKind.Utc).AddSeconds(ordering++));
            }
            var store = new AgentSpeedHistoryStore(Path.Combine(fixture, "history.sqlite3")); var indexer = new AgentSpeedHistoryIndexer(store);
            var now = new DateTimeOffset(2026, 10, 23, 0, 0, 0, TimeSpan.Zero);
            var reports = new Dictionary<UsageSource, AgentSpeedHistoryIndexResult>();
            foreach (var source in new[] { UsageSource.Codex, UsageSource.ClaudeCode, UsageSource.Dsh, UsageSource.WorkBuddy }) reports[source] = indexer.Index(source, now, default);
            var rows = store.Read(new(2026, 10, 20), new(2026, 10, 22));
            var responses = expected.SelectMany(item => item.GetProperty("responses").EnumerateArray()).ToArray();
            Assert.True(responses.Length == rows.Sum(row => row.Requests), "expected " + responses.Length + "; " + string.Join("; ", reports.Select(pair => $"{pair.Key}: requests={rows.Where(row => row.Source == pair.Key).Sum(row => row.Requests)}, unreadable={pair.Value.Unreadable}")));
            Assert.Equal(responses.Sum(row => row.GetProperty("output").GetInt64()), rows.Sum(row => row.OutputTokens));
            Assert.Equal(responses.Sum(row => row.GetProperty("input").GetInt64()), rows.Sum(row => row.InputTokens));
            var measured = responses.Where(row => row.GetProperty("final").GetBoolean() && row.GetProperty("startMs").ValueKind != JsonValueKind.Null)
                .Where(row => row.GetProperty("endMs").GetInt64() - row.GetProperty("startMs").GetInt64() is >= 0 and <= 900_000).ToArray();
            Assert.Equal(measured.Length, rows.Sum(row => row.MeasuredRequests));
            Assert.Equal(measured.Sum(row => Math.Max(500, row.GetProperty("endMs").GetInt64() - row.GetProperty("startMs").GetInt64())), rows.Sum(row => row.MeasuredMilliseconds));
        }
        finally { Directory.Delete(fixture, recursive: true); }
    }
}
