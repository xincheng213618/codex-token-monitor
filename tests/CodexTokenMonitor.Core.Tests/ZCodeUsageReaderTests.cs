using Xunit;

namespace CodexTokenMonitor.Tests;

public sealed class ZCodeUsageReaderTests : IDisposable
{
    private static readonly TimeSpan Beijing = TimeSpan.FromHours(8);
    private readonly string root = Path.Combine(Path.GetTempPath(), $"ZCodeReaderTests-{Guid.NewGuid():N}");
    private readonly IDisposable cacheScope;
    private readonly IDisposable logScope;

    public ZCodeUsageReaderTests()
    {
        // Dual scope: the reader touches both the isolated log tree and its
        // own cache folder; tests must never reach the real user data.
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


    private static string ModelIoLine(string requestId, DateTimeOffset timestamp, string modelId, int total)
    {
        var timestampText = timestamp.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture);
        return "{\"type\":\"model_io\",\"completedAt\":\"" + timestampText + "\",\"requestId\":\"" + requestId +
               "\",\"model\":{\"modelId\":\"" + modelId + "\",\"providerId\":\"builtin:zai-start-plan\",\"role\":\"main\"}," +
               "\"response\":{\"usage\":{\"inputTokens\":1000,\"cacheReadTokens\":4000," +
               "\"cacheWriteTokens\":100,\"outputTokens\":200,\"reasoningTokens\":50,\"totalTokens\":" + total + "}}}";
    }

    private DateTimeOffset WriteTranscripts()
    {
        // The scanner filters files by mtime >= range start, so anchor the
        // fixture on real wall-clock time instead of a fixed date.
        var start = DateTimeOffset.Now.AddMinutes(-60);
        var first = DateTimeOffset.Now.AddMinutes(-50);
        var second = DateTimeOffset.Now.AddMinutes(-45);
        var rollout = Path.Combine(root, UsageSource.ZCode.ToString(), "rollout");
        Directory.CreateDirectory(rollout);
        File.WriteAllLines(
            Path.Combine(rollout, "model-io-sess-test.jsonl"),
            new[]
            {
                ModelIoLine("req-1", first, "GLM-5.3-Flash", 5350),
                ModelIoLine("req-2", second, "glm-5.3-flash", 5350)
            });
        return start;
    }

    [Fact]
    public void ReadTransientDetailRows_CarriesModelIdFromLog()
    {
        var start = WriteTranscripts();

        var rows = ZCodeUsageReader.ReadTransientDetailRows(start, start.AddHours(2));

        Assert.Equal(2, rows.Count);
        Assert.All(rows, row =>
        {
            var model = Assert.Single(row.ModelUsage);
            // ModelUsage keys keep the log's raw casing; estimation matches case-insensitively.
            Assert.Equal("glm-5.3-flash", model.Key, ignoreCase: true);
            Assert.Equal(1, model.Value.Events);
        });
        var total = rows.Sum(item => item.TotalTokens);
        Assert.Equal(5350 * 2, total);
    }

    [Fact]
    public void ReadRange_SummaryTracksModelUsage()
    {
        var start = WriteTranscripts();

        // The range is today, so the live scan must be included for a summary.
        var summary = ZCodeUsageReader.ReadRange(start, start.AddHours(2), includeLiveToday: true);

        Assert.Equal(2, summary.Events);
        // ModelUsage is case-insensitive on the raw model id, so both log
        // casings aggregate into one entry.
        var model = Assert.Single(summary.ModelUsage);
        Assert.Equal("glm-5.3-flash", model.Key, ignoreCase: true);
        Assert.Equal(2, model.Value.Events);
        Assert.Equal(10700, summary.TotalTokens);
    }
}
