using System.Text.Json;
using Xunit;

namespace CodexTokenMonitor.Tests;

public sealed class QoderUsageReaderTests : IDisposable
{
    private static readonly DateTimeOffset Day = new(2026, 9, 22, 0, 0, 0, TimeSpan.FromHours(8));
    private readonly string root = Path.Combine(Path.GetTempPath(), $"QoderReaderTests-{Guid.NewGuid():N}");
    private readonly IDisposable cacheScope;
    private readonly IDisposable logScope;
    private readonly QoderUsageReader reader = new();

    public QoderUsageReaderTests()
    {
        cacheScope = MonitorCachePaths.PushLocalAppDataRoot(Path.Combine(root, "cache"));
        logScope = UsageLogPaths.PushRoot(Path.Combine(root, "logs"));
    }

    [Fact]
    public void CompletedResponse_IsCapturedWithCacheSplitAndModelIdentity()
    {
        Write(Response(Day.AddHours(8)));
        var rows = reader.ReadTransientDetailRows(Day, Day.AddDays(1));
        var row = Assert.Single(rows);
        Assert.Equal(100, row.InputTokens);
        Assert.Equal(50, row.CachedInputTokens);
        Assert.Equal(10, row.CacheWriteInputTokens);
        Assert.Equal(40, row.UncachedInputTokens);
        Assert.Equal(20, row.OutputTokens);
        Assert.Equal(120, row.TotalTokens);
        var summary = UsageSummaryBuilder.FromRows(Day, Day.AddDays(1), rows);
        Assert.Equal(1, summary.Events);
        Assert.Equal("auto", Assert.Single(summary.ModelUsage).Key);
        // No source query is allowed to touch the application's real cache.
        Assert.False(Directory.Exists(Path.Combine(root, "cache")));
    }

    [Fact]
    public void ZeroTokenCompletedResponseStillCountsAsEvent()
    {
        // Qoder currently logs zero counts while token telemetry is
        // unavailable; a completed response is still a definitive model call.
        Write(Response(Day.AddHours(8), input: 0, cached: 0, written: 0, output: 0));
        var scan = QoderUsageReader.ReadEvents(Day, Day.AddDays(1));
        Assert.True(scan.IsComplete);
        var row = Assert.Single(reader.ReadTransientDetailRows(Day, Day.AddDays(1)));
        Assert.Equal(0, row.TotalTokens);
        Assert.Equal(1, UsageSummaryBuilder.FromRows(Day, Day.AddDays(1),
            new[] { row }).Events);
    }

    [Fact]
    public void NonCompletedLinesArePrefilteredAndDoNotBlockCompletion()
    {
        Write(Response(Day.AddHours(8)) +
            JsonSerializer.Serialize(new
            {
                ts = Day.AddHours(9), type = "model.response.failed", request_id = "req-failed",
                data = new { input_tokens = 5, output_tokens = 5 }
            }) + "\n" +
            JsonSerializer.Serialize(new { type = "turn.started", ts = Day.AddHours(10) }) + "\n");
        var scan = QoderUsageReader.ReadEvents(Day, Day.AddDays(1));
        Assert.True(scan.IsComplete);
        Assert.Equal(120, Assert.Single(scan.Events).TotalTokens);
    }

    [Fact]
    public void LinesEchoingMarkerSubstringInOtherRecordsDoNotBlockCompletion()
    {
        // A tool.requested record can echo the marker text, e.g. a shell command
        // grepping for it. The substring prefilter matches the line, but the
        // record is not a usage event and must not poison day completion.
        Write(Response(Day.AddHours(8)) +
            JsonSerializer.Serialize(new
            {
                ts = Day.AddHours(9), type = "tool.requested", request_id = "req-tool",
                data = new { command = "grep 'model.response.completed' segments/*.jsonl" }
            }) + "\n");
        var scan = QoderUsageReader.ReadEvents(Day, Day.AddDays(1));
        Assert.True(scan.IsComplete);
        Assert.Equal(120, Assert.Single(scan.Events).TotalTokens);
    }

    [Fact]
    public void DuplicateRequestIdsDeduplicateAcrossFilesAndWithinOneFile_ButSessionsRemainSeparate()
    {
        var line = Response(Day.AddHours(8));
        Write(line + line, file: "a.jsonl");
        Write(line, file: "b.jsonl");
        Write(line, session: "second-session");
        var first = QoderUsageReader.ReadEvents(Day, Day.AddDays(1));
        var second = QoderUsageReader.ReadEvents(Day, Day.AddDays(1));
        Assert.Equal(2, first.Events.Count);
        Assert.Equal(first.Events.Select(item => item.Key), second.Events.Select(item => item.Key));
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("\"100\"")]
    [InlineData("9223372036854775808")]
    public void InvalidCountersAreNotInventedAsZero(string invalid)
    {
        var valid = Response(Day.AddHours(8));
        Write(valid.Replace("\"input_tokens\":100", "\"input_tokens\":" + invalid) +
            Response(Day.AddHours(9), requestId: "req-2"));
        var scan = QoderUsageReader.ReadEvents(Day, Day.AddDays(1));
        Assert.Single(scan.Events);
        Assert.False(scan.IsComplete);
    }

    [Fact]
    public void MissingRequestIdFallsBackToSequenceNumber()
    {
        Write(Response(Day.AddHours(8), requestId: null) + Response(Day.AddHours(9), requestId: null, seq: 2));
        var scan = QoderUsageReader.ReadEvents(Day, Day.AddDays(1));
        Assert.True(scan.IsComplete);
        Assert.Equal(2, scan.Events.Count);
    }

    [Fact]
    public void LiveRefreshFindsLateFlushedEvents_WithoutDoubleCounting()
    {
        var today = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(8));
        today = new(today.Year, today.Month, today.Day, 0, 0, 0, today.Offset);
        var path = Write(Response(today.AddMinutes(1)));
        var end = today.AddHours(1);
        Assert.Single(reader.ReadDetailRows(today, end, true));
        File.AppendAllText(path, Response(today.AddMinutes(2), requestId: "req-2"));
        Assert.Equal(2, reader.ReadDetailRows(today, end, true).Count);
        Assert.Equal(2, reader.ReadDetailRows(today, end, true).Count);
        Assert.Equal(2, reader.ReadCachedDetailRows(today, end).Count);
    }

    [Fact]
    public void TruncatedResponseTailDoesNotSealHistory_AndRecoversWhenCompleted()
    {
        var historical = Day.AddYears(-3);
        var next = Response(historical.AddHours(9), requestId: "req-2");
        var path = Write(Response(historical.AddHours(8)) + next[..^10]);
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
        Write(Response(historical.AddTicks(-TimeSpan.TicksPerMillisecond)) + Response(historical, requestId: "req-2") +
            Response(historical.AddDays(1), requestId: "req-3") + Response(historical.AddDays(2), requestId: "req-4"));
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
        Write(Response(Day.AddHours(8)));
        Assert.Equal(0, reader.ReadCachedRange(Day, Day.AddDays(1)).Events);
        Assert.Empty(reader.ReadCachedDetailRows(Day, Day.AddDays(1)));
        Assert.Equal(0, reader.ReadDay(Day, Day.AddDays(1), false).Summary.Events);
        Assert.Single(reader.ReadTransientDetailRows(Day, Day.AddDays(1)));
        Assert.Equal(0, reader.ReadCachedRange(Day, Day.AddDays(1)).Events);
    }

    [Fact]
    public void CancellationAndUnavailableFilesDoNotBecomeSuccessfulEmptyHistory()
    {
        var path = Write(Response(Day.AddHours(8)));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => reader.ReadTransientDetailRows(Day, Day.AddDays(1), cancelled.Token));
        using var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var diagnostics = CacheOperationDiagnostics.Begin();
        var scan = QoderUsageReader.ReadEvents(Day, Day.AddDays(1));
        Assert.False(scan.IsComplete);
        Assert.NotEmpty(diagnostics.Warnings);
    }

    [Fact]
    public void ClearAndRebuildPreserveRawLog_AndOtherSourceCache()
    {
        var path = Write(Response(Day.AddHours(8)));
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

    private string Write(string contents, string project = "workspace", string session = "sample-session",
        string file = "segment.jsonl")
    {
        var path = Path.Combine(root, "logs", "Qoder", project, session, "segments", file);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
        return path;
    }

    private static string Response(DateTimeOffset timestamp, long input = 100, long cached = 50, long written = 10,
        long output = 20, string model = "auto", string? requestId = "req-1", int seq = 1) =>
        JsonSerializer.Serialize(new
        {
            ts = timestamp,
            seq,
            type = "model.response.completed",
            turn_id = "turn-1",
            loop_id = "loop-1",
            request_id = requestId,
            data = new
            {
                request_index = 1,
                provider = "qoder",
                model,
                stop_reason = "tool_use",
                input_tokens = input,
                output_tokens = output,
                cache_read_input_tokens = cached,
                cache_creation_input_tokens = written
            }
        }) + "\n";

    public void Dispose()
    {
        logScope.Dispose();
        cacheScope.Dispose();
        var resolved = Path.GetFullPath(root);
        var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!resolved.StartsWith(temp, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(resolved).StartsWith("QoderReaderTests-", StringComparison.Ordinal))
            throw new InvalidOperationException("Refusing to clean up outside the isolated test directory.");
        if (Directory.Exists(resolved)) Directory.Delete(resolved, recursive: true);
    }
}
