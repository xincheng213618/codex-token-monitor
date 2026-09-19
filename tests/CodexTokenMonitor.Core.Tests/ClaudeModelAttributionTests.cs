using Xunit;

namespace CodexTokenMonitor.Tests;

public sealed class ClaudeModelAttributionTests : IDisposable
{
    private static readonly TimeSpan Beijing = TimeSpan.FromHours(8);
    private readonly string root = Path.Combine(Path.GetTempPath(), $"ClaudeModelTests-{Guid.NewGuid():N}");
    private readonly IDisposable logScope;
    private readonly IDisposable cacheScope;

    public ClaudeModelAttributionTests()
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

    private void WriteLogRecord(string timestamp, string model, int input, int output)
    {
        var project = Path.Combine(root, UsageSource.ClaudeCode.ToString(), "-C-work");
        Directory.CreateDirectory(project);
        File.AppendAllText(
            Path.Combine(project, "session-model.jsonl"),
            "{\"timestamp\":\"" + timestamp + "\",\"type\":\"assistant\",\"message\":{\"id\":\"msg_1\"," +
            "\"model\":\"" + model + "\",\"usage\":{\"input_tokens\":" + input +
            ",\"cache_creation_input_tokens\":0,\"cache_read_input_tokens\":0,\"output_tokens\":" + output + "}}}\n");
    }

    [Fact]
    public void ReadTransientDetailRows_CarriesMessageModel()
    {
        var start = DateTimeOffset.Now.AddMinutes(-60);
        WriteLogRecord(start.AddMinutes(10).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"), "mimo-v2.5-pro", 10_000, 500);

        var rows = ClaudeUsageReader.ReadTransientDetailRows(start, start.AddHours(1));

        var row = Assert.Single(rows);
        Assert.Equal("mimo-v2.5-pro", Assert.Single(row.ModelUsage).Key);
        Assert.Equal(10_500L, row.TotalTokens);
    }

    [Fact]
    public void ReadRange_SummaryTracksModelUsage()
    {
        var start = DateTimeOffset.Now.AddMinutes(-60);
        WriteLogRecord(start.AddMinutes(10).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"), "mimo-v2.5-pro", 10_000, 500);

        var summary = ClaudeUsageReader.ReadRange(start, start.AddHours(1), includeLiveToday: false);

        Assert.Equal(1, summary.Events);
        Assert.Equal("mimo-v2.5-pro", Assert.Single(summary.ModelUsage).Key);
    }
}
