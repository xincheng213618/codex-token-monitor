using Xunit;

namespace CodexTokenMonitor.Tests;

public sealed class WorkBuddyUsageReaderTests : IDisposable
{
    private static readonly TimeSpan Beijing = TimeSpan.FromHours(8);
    private readonly string root = Path.Combine(Path.GetTempPath(), $"WorkBuddyReaderTests-{Guid.NewGuid():N}");
    private readonly IDisposable cacheScope;
    private readonly IDisposable logScope;

    public WorkBuddyUsageReaderTests()
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

    private static string UsageLine(string id, DateTimeOffset timestamp, string? model, long input, long cached, long output)
    {
        var providerData = model is null
            ? "{\"messageId\":\"msg-" + id + "\"}"
            : "{\"messageId\":\"msg-" + id + "\",\"model\":\"" + model + "\",\"requestModelId\":\"" + model + "\"}";
        return "{\"id\":\"" + id + "\",\"timestamp\":" + timestamp.ToUnixTimeMilliseconds() +
               ",\"type\":\"function_call\",\"providerData\":" + providerData +
               ",\"message\":{\"usage\":{\"input_tokens\":" + input +
               ",\"output_tokens\":" + output + ",\"total_tokens\":" + (input + output) +
               ",\"cache_read_input_tokens\":" + cached + "}},\"cwd\":\"c:\\\\tmp\"}";
    }

    private DateTimeOffset WriteTranscripts()
    {
        // The scanner filters files by mtime >= range start, so anchor the
        // fixture on real wall-clock time instead of a fixed date.
        var start = DateTimeOffset.Now.AddMinutes(-60);
        var first = DateTimeOffset.Now.AddMinutes(-50);
        var second = DateTimeOffset.Now.AddMinutes(-45);
        var project = Path.Combine(root, UsageSource.WorkBuddy.ToString(), "projects", "c-Users-17917-Demo");
        Directory.CreateDirectory(project);
        File.WriteAllLines(
            Path.Combine(project, "session-test.jsonl"),
            new[]
            {
                UsageLine("evt-1", first, "hy3", 10_000, 8_000, 200),
                UsageLine("evt-2", second, "deepseek-v4-pro", 12_000, 0, 300)
            });
        return start;
    }

    [Fact]
    public void ReadTransientDetailRows_CarriesModelIdFromLog()
    {
        var start = WriteTranscripts();

        var rows = WorkBuddyUsageReader.ReadTransientDetailRows(start, start.AddHours(2));

        Assert.Equal(2, rows.Count);
        var models = rows.SelectMany(row => row.ModelUsage.Keys).ToArray();
        Assert.Equal(2, models.Length);
        Assert.Contains(models, item => string.Equals(item, "hy3", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(models, item => string.Equals(item, "deepseek-v4-pro", StringComparison.OrdinalIgnoreCase));
        foreach (var row in rows)
        {
            var model = Assert.Single(row.ModelUsage);
            Assert.Equal(1, model.Value.Events);
        }
    }

    [Fact]
    public void ReadRange_SummaryTracksModelUsage()
    {
        var start = WriteTranscripts();

        // The range is today, so the live scan must be included for a summary.
        var summary = WorkBuddyUsageReader.ReadRange(start, start.AddHours(2), includeLiveToday: true);

        Assert.Equal(2, summary.Events);
        Assert.Equal(2, summary.ModelUsage.Count);
        var hy3 = summary.ModelUsage.Single(pair => string.Equals(pair.Key, "hy3", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(10_200, hy3.Value.TotalTokens);
        var deepseek = summary.ModelUsage.Single(pair => string.Equals(pair.Key, "deepseek-v4-pro", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(12_300, deepseek.Value.TotalTokens);
        Assert.Equal(22_500, summary.TotalTokens);
    }

    [Fact]
    public void ReadRange_LegacyRowsWithoutProviderModel_StayUnattributed()
    {
        var start = DateTimeOffset.Now.AddMinutes(-60);
        var moment = DateTimeOffset.Now.AddMinutes(-50);
        var project = Path.Combine(root, UsageSource.WorkBuddy.ToString(), "projects", "c-Users-17917-Legacy");
        Directory.CreateDirectory(project);
        File.WriteAllText(
            Path.Combine(project, "session-legacy.jsonl"),
            UsageLine("legacy-1", moment, model: null, input: 5_000, cached: 0, output: 100));

        var summary = WorkBuddyUsageReader.ReadRange(start, start.AddHours(2), includeLiveToday: true);

        Assert.Equal(1, summary.Events);
        Assert.Empty(summary.ModelUsage);
        Assert.Equal(5_100, summary.TotalTokens);
    }
}
