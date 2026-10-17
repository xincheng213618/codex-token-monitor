using System.Text.Json;
using Microsoft.Data.Sqlite;
using Xunit;

namespace CodexTokenMonitor.Tests;

public sealed class QuotaEstimateHistoryTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 9, 1, 8, 0, 0, TimeSpan.FromHours(8));
    private static DateTimeOffset CurrentStart => Start.AddDays(14);
    private static DateTimeOffset Now => CurrentStart.AddHours(3);
    private readonly string root = Path.Combine(Path.GetTempPath(), "QuotaEstimateHistoryTests-" + Guid.NewGuid().ToString("N"));
    private readonly IDisposable cacheScope;
    private readonly IDisposable logScope;
    private string HistoryPath => Path.Combine(root, "CodexTokenMonitor", "quota-history-v2.jsonl");

    public QuotaEstimateHistoryTests()
    {
        cacheScope = MonitorCachePaths.PushLocalAppDataRoot(root);
        logScope = UsageLogPaths.PushRoot(Path.Combine(root, "empty-logs"));
        for (var index = 0; index < 3; index++)
        {
            var start = Start.AddDays(index * 7);
            SeedQuota(start, 10, index == 2 ? 20 : 80);
            SeedUsage(start.AddMinutes(90), (index + 1) * 1_000_000, fast: index == 0);
        }
    }

    private static CodexQuotaEstimate Quota(DateTimeOffset? now = null, decimal used = 20) =>
        new(now ?? Now, "codex", "Codex", null,
            new CodexQuotaWindowEstimate("7d", used, 10080, CurrentStart, now ?? Now,
                CurrentStart.AddDays(7), UsageCacheStore.Load().ReadRange(CurrentStart, now ?? Now), 0, null, null));

    private static void SeedQuota(DateTimeOffset start, decimal first, decimal last)
    {
        var snapshots = new[]
        {
            new CodexQuotaSnapshot(start.AddHours(1), "codex", "Codex", null, null, first, start.AddDays(7)),
            new CodexQuotaSnapshot(start.AddHours(2), "codex", "Codex", null, null, last, start.AddDays(7))
        };
        QuotaSnapshotCacheStore.Load("CodexTokenMonitor").Put(DateOnly.FromDateTime(start.DateTime),
            snapshots, true, start.AddDays(1), propagateErrors: true);
    }

    private static void SeedUsage(DateTimeOffset at, long tokens, bool fast = false)
    {
        var usageEvent = new TokenUsageEvent(at, tokens, 0, 0, 0, tokens, Key: "history-event",
            ModelId: "gpt-5.6-sol", ServiceTier: fast ? "priority" : null);
        var bucket = new TokenUsageBucket { StartLocal = new DateTimeOffset(at.Year, at.Month, at.Day, 0, 0, 0, at.Offset) };
        bucket.Add(usageEvent);
        UsageCacheStore.Load().Put(bucket, true, bucket.StartLocal.AddDays(1), new[] { usageEvent }, propagateErrors: true);
    }

    private static QuotaEstimateLoadResult Load(DateTimeOffset? now = null, decimal used = 20) =>
        QuotaEstimateCalculator.BuildLoadResult(Quota(now, used), now ?? Now);

    [Fact]
    public void Reopen_ReusesPersistedHistoryAndRefreshesOnlyCurrentCycle()
    {
        var first = Load();
        Assert.Equal(3, first.PeriodCount);
        Assert.Equal(0, first.CachedHistoricalPeriodCount);
        UsageSourceReaders.Codex.Cycles.InvalidateCache();
        SeedUsage(Now.AddDays(1).AddMinutes(10), 4_000_000);

        var second = Load(Now.AddDays(1).AddHours(1), 30);

        Assert.True(second.UsedSavedPeriods);
        Assert.Equal(2, second.CachedHistoricalPeriodCount);
        Assert.Equal(first.WeeklyRows.Skip(1), second.WeeklyRows.Skip(1));
        Assert.NotEqual(first.WeeklyRows[0].Tokens, second.WeeklyRows[0].Tokens);
        Assert.Equal("70%", second.WeeklyRows[0].Remaining);
    }

    [Fact]
    public void HistoricalUsageCorrection_RebuildsOnlyAffectedSummary()
    {
        var first = Load();
        SeedUsage(Start.AddMinutes(90), 5_000_000, fast: true);

        var second = Load();

        Assert.True(second.UsedSavedPeriods);
        Assert.Equal(1, second.CachedHistoricalPeriodCount);
        Assert.NotEqual(first.WeeklyRows[^1].Tokens, second.WeeklyRows[^1].Tokens);
        Assert.Equal(first.WeeklyRows[1], second.WeeklyRows[1]);
    }

    [Fact]
    public void HistoricalUsageDeletion_DoesNotReuseDeletedTokens()
    {
        Load();
        Assert.True(UsageCacheStore.Load().DeleteDay(DateOnly.FromDateTime(Start.DateTime)));

        var second = Load();

        Assert.Equal(1, second.CachedHistoricalPeriodCount);
        Assert.Equal("0.000M", second.WeeklyRows[^1].Tokens);
    }

    [Fact]
    public void HistoricalQuotaCorrection_InvalidatesPeriodMetadata()
    {
        Load();
        SeedQuota(Start, 10, 90);

        var second = Load();

        Assert.False(second.UsedSavedPeriods);
        Assert.Equal("90%", second.WeeklyRows[^1].UsedPercent);
        Assert.Equal(2, second.CachedHistoricalPeriodCount);
    }

    [Fact]
    public void LiveHistoryAppend_ReusesMetadataButHistoricalAppendRebuildsIt()
    {
        Load();
        AppendHistory(Now.AddMinutes(5), CurrentStart.AddDays(7), 25);
        var live = Load(Now.AddHours(1), 25);
        Assert.True(live.UsedSavedPeriods);
        AppendHistory(Start.AddMinutes(150), Start.AddDays(7), 95);

        var corrected = Load(Now.AddHours(1), 25);

        Assert.False(corrected.UsedSavedPeriods);
        Assert.Equal("95%", corrected.WeeklyRows[^1].UsedPercent);
    }

    private void AppendHistory(DateTimeOffset at, DateTimeOffset reset, decimal used)
    {
        var before = new FileInfo(HistoryPath);
        var length = before.Exists ? before.Length : -1;
        var ticks = before.Exists ? before.LastWriteTimeUtc.Ticks : 0;
        File.AppendAllText(HistoryPath, JsonSerializer.Serialize(new
        {
            snapshotLocal = at, limitId = "codex", limitName = "Codex",
            week = new { usedPercent = used, windowMinutes = 10080, resetAtLocal = reset }
        }) + Environment.NewLine);
        QuotaEstimateHistoryStore.HistoryAppended(at, length, ticks);
    }

    [Fact]
    public void ExternalHistoryEdit_CannotBeHiddenByNextLiveAppend()
    {
        Load();
        File.AppendAllText(HistoryPath, JsonSerializer.Serialize(new
        {
            snapshotLocal = Start.AddMinutes(150), limitId = "codex", limitName = "Codex",
            week = new { usedPercent = 96, windowMinutes = 10080, resetAtLocal = Start.AddDays(7) }
        }) + Environment.NewLine);
        AppendHistory(Now.AddMinutes(5), CurrentStart.AddDays(7), 25);

        var corrected = Load(Now.AddHours(1), 25);

        Assert.False(corrected.UsedSavedPeriods);
        Assert.Equal("96%", corrected.WeeklyRows[^1].UsedPercent);
    }

    [Fact]
    public void NewReset_RebuildsPeriodsAndKeepsNewCurrentCycle()
    {
        Load();
        var nextStart = CurrentStart.AddDays(2);
        SeedQuota(nextStart, 1, 2);
        var nextNow = nextStart.AddHours(3);
        var nextQuota = new CodexQuotaEstimate(nextNow, "codex", "Codex", null,
            new CodexQuotaWindowEstimate("7d", 2, 10080, nextStart, nextNow, nextStart.AddDays(7),
                new TokenUsageSummary { StartLocal = nextStart, EndLocal = nextNow }, 0, null, null));

        var next = QuotaEstimateCalculator.BuildLoadResult(nextQuota, nextNow);

        Assert.False(next.UsedSavedPeriods);
        Assert.Equal(4, next.PeriodCount);
        Assert.Equal(nextStart, next.Periods[0].PeriodStart);
        Assert.True(next.Periods[0].IsCurrent);
    }

    [Fact]
    public void ConcurrentQuotaCorrection_DoesNotPublishStalePeriodMetadata()
    {
        var periods = QuotaEstimateCalculator.BuildWeeklyPeriods(Quota(), Now);
        using (var history = QuotaEstimateHistoryStore.Open()!)
        {
            Assert.NotNull(history.ReadPeriods(Quota()));
            SeedQuota(Start, 10, 98);
            history.SavePeriods(periods, CancellationToken.None);
        }
        using var reopened = QuotaEstimateHistoryStore.Open()!;

        Assert.Null(reopened.ReadPeriods(Quota()));
        Assert.Equal("98%", Load().WeeklyRows[^1].UsedPercent);
    }

    [Fact]
    public void PriceAndPlanChanges_RepriceCachedTokens()
    {
        var first = Load();
        var prices = PriceSettingsStore.Current.Clone();
        prices.CodexPresets.Single(item => CodexModelCost.NormalizeModelId(
            string.IsNullOrWhiteSpace(item.ModelId) ? item.Model : item.ModelId) == "gpt-5.6-sol").UncachedInput *= 2;
        PriceSettingsStore.Save(prices);
        SubscriptionPlanStore.Save(new[] { new SubscriptionPlanRecord
        {
            StartLocal = Start, EndLocal = Start.AddDays(21), PlanName = "updated plan", AmountCny = 2100
        } });

        var second = Load();

        Assert.Equal(2, second.CachedHistoricalPeriodCount);
        Assert.NotEqual(first.WeeklyRows[^1].CostDetail, second.WeeklyRows[^1].CostDetail);
        // Sol's subscription quota baseline is fixed; only its API price changes.
        Assert.Equal(first.WeeklyRows[^1].UsedCost, second.WeeklyRows[^1].UsedCost);
        Assert.Equal("updated plan", second.WeeklyRows[^1].PlanNames);
        Assert.Equal("¥700", second.WeeklyRows[^1].PlanAmount);
    }

    [Fact]
    public void ConcurrentCorrection_DoesNotPersistSummaryUnderOldRevision()
    {
        var period = new CodexQuotaCycle(Start, Start.AddDays(7), Start.AddDays(7), 2, 80, false);
        using (var history = QuotaEstimateHistoryStore.Open()!)
        {
            var before = history.ReadUsage(period, UsageCacheStore.Load(), CancellationToken.None);
            Assert.Equal(1_000_000, before.TotalTokens);
            SeedUsage(Start.AddMinutes(90), 5_000_000);
            history.Flush(CancellationToken.None);
        }
        using var reopened = QuotaEstimateHistoryStore.Open()!;

        var after = reopened.ReadUsage(period, UsageCacheStore.Load(), CancellationToken.None);

        Assert.Equal(0, reopened.UsageCacheHits);
        Assert.Equal(5_000_000, after.TotalTokens);
    }

    [Fact]
    public void DamagedDerivedEntries_RebuildWithoutLosingSourceData()
    {
        var first = Load();
        using (var connection = MonitorSettingsDatabase.OpenConnection(UsageCacheStore.GetCachePath("CodexTokenMonitor")))
            MonitorSettingsDatabase.ExecuteNonQuery(connection, null, """
                UPDATE quota_estimate_usage SET usage_json = 'broken json';
                UPDATE quota_estimate_periods SET periods_json = 'broken json';
                """);

        var rebuilt = Load();

        Assert.False(rebuilt.UsedSavedPeriods);
        Assert.Equal(0, rebuilt.CachedHistoricalPeriodCount);
        Assert.Equal(first.WeeklyRows, rebuilt.WeeklyRows);
        Assert.Equal(2, Load().CachedHistoricalPeriodCount);
    }

    [Fact]
    public void PersistedSummary_PreservesModelTierAndDayCounters()
    {
        var period = new CodexQuotaCycle(Start, Start.AddDays(7), Start.AddDays(7), 2, 80, false);
        TokenUsageSummary first;
        using (var history = QuotaEstimateHistoryStore.Open()!)
        {
            first = history.ReadUsage(period, UsageCacheStore.Load(), CancellationToken.None);
            history.Flush(CancellationToken.None);
        }
        using var reopened = QuotaEstimateHistoryStore.Open()!;

        var second = reopened.ReadUsage(period, UsageCacheStore.Load(), CancellationToken.None);

        Assert.Equal(1, reopened.UsageCacheHits);
        Assert.Equal(first.TotalTokens, second.TotalTokens);
        Assert.Equal(first.DailyBuckets.Count, second.DailyBuckets.Count);
        Assert.Equal(JsonSerializer.Serialize(first.ModelUsage), JsonSerializer.Serialize(second.ModelUsage));
        Assert.Equal(JsonSerializer.Serialize(CodexModelCost.Estimate(first)),
            JsonSerializer.Serialize(CodexModelCost.Estimate(second)));
    }

    public void Dispose()
    {
        logScope.Dispose();
        cacheScope.Dispose();
        var resolved = Path.GetFullPath(root);
        var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!resolved.StartsWith(temp, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(resolved).StartsWith("QuotaEstimateHistoryTests-", StringComparison.Ordinal))
            throw new InvalidOperationException("Refusing to remove a path outside this test fixture.");
        if (Directory.Exists(resolved)) Directory.Delete(resolved, recursive: true);
    }
}
