using System.Text.Json;
using Xunit;

namespace CodexTokenMonitor.Tests;

public sealed class KimiUsageReaderTests : IDisposable
{
    private static readonly DateTimeOffset Day = new(2026, 9, 22, 0, 0, 0, TimeSpan.FromHours(8));
    private readonly string root = Path.Combine(Path.GetTempPath(), $"KimiReaderTests-{Guid.NewGuid():N}");
    private readonly IDisposable cacheScope;
    private readonly IDisposable logScope;
    private readonly KimiUsageReader reader = new();

    public KimiUsageReaderTests()
    {
        cacheScope = MonitorCachePaths.PushLocalAppDataRoot(Path.Combine(root, "cache"));
        logScope = UsageLogPaths.PushRoot(Path.Combine(root, "logs"));
    }

    [Fact]
    public void RecordedFailedTurn_ReconcilesWithIndependentDesktopTelemetry()
    {
        Write(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Kimi", "wire.jsonl")));
        var rows = reader.ReadTransientDetailRows(Day, Day.AddDays(1));
        var summary = UsageSummaryBuilder.FromRows(Day, Day.AddDays(1), rows);
        Assert.Equal(16, rows.Count);
        Assert.Equal(29_562, summary.UncachedInputTokens);
        Assert.Equal(460_288, summary.CachedInputTokens);
        Assert.Equal(0, summary.CacheWriteInputTokens);
        Assert.Equal(489_850, summary.InputTokens);
        Assert.Equal(3_762, summary.OutputTokens);
        Assert.Equal(493_612, summary.TotalTokens);
        Assert.Equal(493_612, Assert.Single(summary.ModelUsage).Value.TotalTokens);
        Assert.Equal("k2d8-preview", Assert.Single(summary.ModelUsage).Key);
        // No source query is allowed to touch the application's real cache.
        Assert.False(Directory.Exists(Path.Combine(root, "cache")));
    }

    [Fact]
    public void BackgroundTitleGenerationIsIncludedAlongsideTheMainTask()
    {
        Write(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Kimi", "wire.jsonl")));
        Write(Usage(Day.AddHours(15).AddMinutes(48), input: 443, cached: 256, output: 51), session: "ctitle-sample");
        var summary = reader.ReadRange(Day, Day.AddDays(1), true);
        Assert.Equal(17, summary.Events);
        Assert.Equal(494_362, summary.TotalTokens);
        Assert.Equal(460_544, summary.CachedInputTokens);
        Assert.Equal(30_005, summary.UncachedInputTokens);
        var estimate = CodexModelCost.Estimate(summary, PricePresetGroups.Kimi);
        Assert.True(estimate.IsComplete);
        Assert.Equal("$", estimate.CurrencySymbol);
        Assert.Equal(0.160393m, estimate.KnownCost);
    }

    [Fact]
    public void CountsDisjointInputAndCacheWrite_WithoutCountingEmbeddedOrAggregateUsage()
    {
        var line = Usage(Day.AddHours(8), input: 100, cached: 500, written: 250, output: 20);
        Write(line + JsonSerializer.Serialize(new { type = "context.append_loop_event", output = line }) + "\n" +
            JsonSerializer.Serialize(new { type = "segment_complete", input_tokens = 100, output_tokens = 20, cache_hit = 500 }) + "\n" +
            "{\"type\":\"context_usage\",\"contextTokens\":38909}\n" +
            "{\"type\":\"turn.error\",\"message\":\"403 quota exhausted\"}\n");
        var row = Assert.Single(reader.ReadTransientDetailRows(Day, Day.AddDays(1)));
        Assert.Equal(850, row.InputTokens);
        Assert.Equal(100, row.UncachedInputTokens);
        Assert.Equal(500, row.CachedInputTokens);
        Assert.Equal(250, row.CacheWriteInputTokens);
        Assert.Equal(870, row.TotalTokens);
    }

    [Fact]
    public void DuplicateFilesAndUsageRowsDeduplicate_ButDifferentSessionsAndAgentsRemainSeparate()
    {
        var line = Usage(Day.AddHours(8));
        Write(line + line, workspace: "original");
        Write(line, workspace: "copied");
        Write(line, session: "second-session");
        Write(line, agent: "helper");
        var first = KimiUsageReader.ReadEvents(Day, Day.AddDays(1));
        var second = KimiUsageReader.ReadEvents(Day, Day.AddDays(1));
        Assert.Equal(3, first.Events.Count);
        Assert.Equal(first.Events.Select(item => item.Key), second.Events.Select(item => item.Key));
    }

    [Fact]
    public void RequestsAtSameMillisecondKeepDistinctIdentities()
    {
        var time = Day.AddHours(8).ToUnixTimeMilliseconds();
        Write($"{{\"type\":\"llm.request\",\"turnStep\":\"0.1\",\"time\":{time}}}\n" + Usage(Day.AddHours(8)) +
            $"{{\"type\":\"llm.request\",\"turnStep\":\"0.2\",\"time\":{time}}}\n" + Usage(Day.AddHours(8)));
        Assert.Equal(2, reader.ReadTransientDetailRows(Day, Day.AddDays(1)).Count);
    }

    [Fact]
    public void LiveRefreshFindsLateFlushedEvents_WithoutDoubleCounting()
    {
        var today = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(8));
        today = new(today.Year, today.Month, today.Day, 0, 0, 0, today.Offset);
        var path = Write(Usage(today.AddMinutes(1)));
        var end = today.AddHours(1);
        Assert.Single(reader.ReadDetailRows(today, end, true));
        File.AppendAllText(path, Usage(today.AddMinutes(2)));
        Assert.Equal(2, reader.ReadDetailRows(today, end, true).Count);
        Assert.Equal(2, reader.ReadDetailRows(today, end, true).Count);
        Assert.Equal(2, reader.ReadCachedDetailRows(today, end).Count);
    }

    [Fact]
    public void TruncatedUsageTailDoesNotSealHistory_AndRecoversWhenCompleted()
    {
        var historical = Day.AddYears(-3);
        var next = Usage(historical.AddHours(9));
        var path = Write(Usage(historical.AddHours(8)) + next[..^10]);
        Assert.Single(reader.ReadDetailRows(historical, historical.AddDays(1), true));
        Assert.Single(reader.GetIncompleteHistoricalDays(historical, historical));
        File.AppendAllText(path, next[^10..]);
        Assert.Equal(2, reader.ReadDetailRows(historical, historical.AddDays(1), true).Count);
        Assert.Empty(reader.GetIncompleteHistoricalDays(historical, historical));
    }

    [Fact]
    public void HistoricalBatchAndClippedRangesAgree_AndPreserveModelAttribution()
    {
        var historical = Day.AddYears(-3);
        Write(Usage(historical.AddTicks(-TimeSpan.TicksPerMillisecond)) + Usage(historical) +
            Usage(historical.AddDays(1)) + Usage(historical.AddDays(2)));
        reader.WarmHistoricalDays(new[] { historical, historical.AddDays(1) });
        var summary = reader.ReadRange(historical, historical.AddDays(2), false);
        Assert.Equal(2, summary.Events);
        Assert.Equal(2, summary.DailyBuckets.Count);
        Assert.Equal(2, Assert.Single(summary.ModelUsage).Value.Events);
        Assert.Single(reader.ReadDetailRows(historical.AddHours(1), historical.AddDays(2), false));
        Assert.Empty(reader.GetIncompleteHistoricalDays(historical, historical.AddDays(1)));
    }

    [Fact]
    public void CachedQueriesNeverScanOrRepairSourceLogs()
    {
        Write(Usage(Day.AddHours(8)));
        Assert.Equal(0, reader.ReadCachedRange(Day, Day.AddDays(1)).Events);
        Assert.Empty(reader.ReadCachedDetailRows(Day, Day.AddDays(1)));
        Assert.Equal(0, reader.ReadDay(Day, Day.AddDays(1), false).Summary.Events);
        Assert.Single(reader.ReadTransientDetailRows(Day, Day.AddDays(1)));
        Assert.Equal(0, reader.ReadCachedRange(Day, Day.AddDays(1)).Events);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"100\"")]
    [InlineData("-1")]
    [InlineData("9223372036854775808")]
    public void InvalidCountersAreNotInventedAsZero(string invalid)
    {
        var valid = Usage(Day.AddHours(8));
        Write(valid.Replace("\"inputOther\":100", "\"inputOther\":" + invalid) + Usage(Day.AddHours(9)));
        var scan = KimiUsageReader.ReadEvents(Day, Day.AddDays(1));
        Assert.Single(scan.Events);
        Assert.False(scan.IsComplete);
    }

    [Fact]
    public void CancellationAndUnavailableFilesDoNotBecomeSuccessfulEmptyHistory()
    {
        var path = Write(Usage(Day.AddHours(8)));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => reader.ReadTransientDetailRows(Day, Day.AddDays(1), cancelled.Token));
        using var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var diagnostics = CacheOperationDiagnostics.Begin();
        var scan = KimiUsageReader.ReadEvents(Day, Day.AddDays(1));
        Assert.False(scan.IsComplete);
        Assert.NotEmpty(diagnostics.Warnings);
    }

    [Fact]
    public void ClearAndRebuildPreserveRawLog_AndOtherSourceCache()
    {
        var path = Write(Usage(Day.AddHours(8)));
        var original = File.ReadAllText(path);
        _ = reader.ReadDetailRows(Day, Day.AddDays(1), true);
        var other = UsageCacheStore.Load("WorkBuddyTokenMonitor");
        var item = new TokenUsageEvent(Day.AddHours(8), 10, 0, 2, 0, 12, "other");
        var bucket = new TokenUsageBucket { StartLocal = Day };
        bucket.Add(item);
        other.Put(bucket, false, Day.AddHours(9), new[] { item });
        reader.ClearCache();
        Assert.Single(reader.ReadDetailRows(Day, Day.AddDays(1), true));
        Assert.Equal(original, File.ReadAllText(path));
        Assert.Equal(12, other.ReadRange(Day, Day.AddDays(1)).TotalTokens);
    }

    private string Write(string contents, string workspace = "workspace", string session = "sample-session", string agent = "main")
    {
        var path = Path.Combine(root, "logs", "Kimi", workspace, session, "agents", agent, "wire.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
        return path;
    }

    private static string Usage(DateTimeOffset timestamp, long input = 100, long cached = 50, long written = 0, long output = 20) =>
        JsonSerializer.Serialize(new
        {
            type = "usage.record", model = "k2d8-preview", usageScope = "turn", time = timestamp.ToUnixTimeMilliseconds(),
            usage = new { inputOther = input, inputCacheRead = cached, inputCacheCreation = written, output }
        }) + "\n";

    public void Dispose()
    {
        logScope.Dispose();
        cacheScope.Dispose();
        var resolved = Path.GetFullPath(root);
        var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!resolved.StartsWith(temp, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(resolved).StartsWith("KimiReaderTests-", StringComparison.Ordinal))
            throw new InvalidOperationException("Refusing to clean up outside the isolated test directory.");
        if (Directory.Exists(resolved)) Directory.Delete(resolved, recursive: true);
    }
}
