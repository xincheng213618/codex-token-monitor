using System.Text.Json;
using Xunit;

namespace CodexTokenMonitor.Tests;

public sealed class QuotaCapacityEvidenceTests
{
    private static readonly DateTimeOffset Start = new(2001, 4, 9, 0, 0, 0, TimeSpan.FromHours(8));

    [Theory]
    [InlineData(3, 100)]
    [InlineData(300, 1)]
    public void CalibrationUsesInteriorMovementAndIsIndependentOfResetAndOpenTail(int first, int tail)
    {
        var period = new CodexQuotaCycle(Start, Start.AddDays(1), Start.AddDays(7), 0, 6m, false);
        var samples = new[] { Sample(0, 0, 0), Sample(1, 1, first), Sample(2, 3, 20),
            Sample(3, 6, 30), Sample(4, 6, tail) };
        var catalog = new[] { new PricePreset { Provider = "OpenAI", Model = "model-a", UncachedInput = 1,
            CachedInput = 1, Output = 1, Divisor = 1 } };
        var visible = QuotaCycleAnalysisCalculator.BuildFromSamples(period, samples, priceCatalog: catalog);
        var calibration = QuotaCycleAnalysisCalculator.BuildCalibrationFromSamples(period, samples, priceCatalog: catalog);

        Assert.Equal(first + 50 + tail, visible.Tokens);
        Assert.Equal(50, calibration.Tokens);
        Assert.Equal(5m, calibration.ObservedQuotaDropPercent);
        var band = Assert.Single(calibration.Bands);
        Assert.Equal(1m, band.UsedFromPercent);
        Assert.Equal(6m, band.UsedToPercent);
        Assert.Equal(Start.AddMinutes(3), band.EndLocal);
        Assert.Equal(1_000m, Assert.Single(QuotaModelCapacityEstimator.EstimatePureModels(calibration)).AverageFullQuotaCost);
    }

    [Fact]
    public void CalibrationExcludesRoundedExhaustionBoundaryAndPostZeroUsage()
    {
        var p = new CodexQuotaCycle(Start, Start.AddDays(1), Start.AddDays(7), 0, 100m, false);
        var result = QuotaCycleAnalysisCalculator.BuildCalibrationFromSamples(p,
            new[] { Sample(0, 97, 0), Sample(1, 99, 20), Sample(2, 100, 500), Sample(3, 100, 500) });
        Assert.Equal(2m, result.ObservedQuotaDropPercent);
        Assert.Equal(20, result.Tokens);
        Assert.Null(result.PostZeroUsage);
        Assert.Equal(99m, Assert.Single(result.Bands).UsedToPercent);
    }

    [Fact]
    public void QuotaUpdatesKeepTheirSnapshotsButDoNotRepeatLastRequestUsage()
    {
        var usage = new SubagentReplayFilter();
        var quota = new SubagentReplayFilter();
        var first = Token(Start, 100, 100);
        var repeat = Token(Start.AddMinutes(1), 100, 100);
        var next = Token(Start.AddMinutes(2), 200, 100);
        Assert.True(usage.ShouldReadUsage(first));
        Assert.False(usage.ShouldReadUsage(repeat));
        Assert.True(usage.IsRepeatedUsage);
        Assert.True(usage.ShouldReadUsage(next)); // Identical request size, real cumulative increase.
        Assert.True(quota.ShouldReadTokenCount(first));
        Assert.True(quota.ShouldReadTokenCount(repeat));
        Assert.True(quota.ShouldReadTokenCount(next));
        Assert.True(usage.ShouldReadUsage(Token(Start.AddMinutes(3), 10, 10))); // Counter reset.
        Assert.False(usage.ShouldReadUsage(JsonSerializer.Serialize(new
            { type = "session_meta", payload = new { id = "one" } })));
        Assert.False(usage.ShouldReadUsage(JsonSerializer.Serialize(new
            { type = "session_meta", payload = new { id = "two" } })));
        Assert.True(usage.ShouldReadUsage(Token(Start.AddMinutes(4), 10, 10))); // New session.
    }

    [Fact]
    public void LiveScanFindsNewAndAppendedTokensInOldFilesWithPreservedMtime()
    {
        var root = Path.Combine(Path.GetTempPath(), "QuotaLiveDiscovery-" + Guid.NewGuid().ToString("N"));
        using var caches = MonitorCachePaths.PushLocalAppDataRoot(root);
        using var logs = UsageLogPaths.PushRoot(Path.Combine(root, "logs"));
        var now = BeijingClock.Now;
        var day = new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, now.Offset);
        var path = Path.Combine(root, "logs", "Codex", "sessions", "old-open-handle.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, Token(day.AddTicks(1), 100, 100) + "\n");
        var oldMtime = now.AddDays(-10).UtcDateTime;
        File.SetLastWriteTimeUtc(path, oldMtime);
        var reader = new CodexUsageReader();
        Assert.Equal(100, reader.ReadDetailRows(day, now.AddTicks(-2)).Sum(b => b.TotalTokens));
        File.AppendAllText(path, Token(day.AddTicks(2), 200, 100) + "\n");
        File.SetLastWriteTimeUtc(path, oldMtime);
        Assert.Equal(200, reader.ReadDetailRows(day, now.AddTicks(-1)).Sum(b => b.TotalTokens));
        // A new reader has neither a tracked tail nor remembered model context.
        Assert.Equal(200, new CodexUsageReader().ReadDetailRows(day, BeijingClock.Now).Sum(b => b.TotalTokens));
        UsageCacheStore.Delete("CodexTokenMonitor");
        File.Delete(path);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void RescanRemovesKnownCachedRepeatsAndPreservesOtherComputerUsage(int mode)
    {
        var root = Path.Combine(Path.GetTempPath(), "QuotaCapacityEvidence-" + Guid.NewGuid().ToString("N"));
        using var caches = MonitorCachePaths.PushLocalAppDataRoot(root);
        using var logs = UsageLogPaths.PushRoot(Path.Combine(root, "logs"));
        var now = BeijingClock.Now;
        var today = new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, now.Offset);
        var day = mode == 2 ? today : Start;
        var path = Path.Combine(root, "logs", "Codex", "sessions", "usage.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllLines(path, new[] { Token(day.AddTicks(1), 100, 100),
            Token(day.AddTicks(2), 100, 100), Token(day.AddTicks(3), 200, 100) });
        var first = new TokenUsageEvent(day.AddTicks(1), 100, 0, 0, 0, 100);
        var duplicate = first with { Timestamp = day.AddTicks(2) };
        var next = first with { Timestamp = day.AddTicks(3) };
        var remote = first with { Timestamp = day.AddTicks(4), Key = "remote-only" };
        var bucket = new TokenUsageBucket { StartLocal = day };
        foreach (var e in new[] { first, duplicate, next, remote }) bucket.Add(e);
        var cache = UsageCacheStore.Load();
        cache.Put(bucket, isComplete: false, scannedThroughLocal: null,
            detailEvents: new[] { first, duplicate, next, remote });
        var reader = new CodexUsageReader();
        if (mode == 1) reader.WarmHistoricalDays(new[] { day });
        else reader.ReadDetailRows(day, day.AddDays(1));
        var actual = cache.GetDetailEvents(DateOnly.FromDateTime(day.DateTime));
        Assert.Equal(3, actual.Count);
        Assert.DoesNotContain(actual, e => e.Timestamp == duplicate.Timestamp);
        Assert.Contains(actual, e => e.Key == "remote-only");
        Assert.Equal(300, cache.ReadRange(day, day.AddDays(1)).TotalTokens);
        Assert.Equal(3, new CodexUsageReader().ReadDetailRows(day, day.AddDays(1)).Sum(b => b.Events));
        UsageCacheStore.Delete("CodexTokenMonitor");
        File.Delete(path);
    }

    private static QuotaCycleAnalysisSample Sample(int minute, decimal used, int tokens)
    {
        var bucket = new TokenUsageBucket { StartLocal = Start.AddMinutes(minute) };
        bucket.Add(new(Start.AddMinutes(minute), tokens, 0, 0, 0, tokens, ModelId: "model-a"));
        return new(Start.AddMinutes(minute), used, bucket);
    }

    private static string Token(DateTimeOffset time, long total, long last) => JsonSerializer.Serialize(new
    {
        timestamp = time, type = "event_msg", payload = new { type = "token_count", info = new
        {
            total_token_usage = new { input_tokens = total, total_tokens = total },
            last_token_usage = new { input_tokens = last, total_tokens = last }
        }, rate_limits = new { secondary = new { used_percent = 1, window_minutes = 10080 } } }
    });
}
