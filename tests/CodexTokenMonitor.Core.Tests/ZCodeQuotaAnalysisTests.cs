using Xunit;

namespace CodexTokenMonitor.Tests;

public sealed class ZCodeQuotaAnalysisTests
{
    private static readonly DateTimeOffset DayStart = new(2026, 9, 1, 0, 0, 0, TimeSpan.FromHours(8));
    private static readonly DateTimeOffset PlanStart = new(2026, 9, 1, 8, 0, 0, TimeSpan.FromHours(8));
    private static readonly DateTimeOffset PlanEnd = new(2026, 9, 3, 8, 0, 0, TimeSpan.FromHours(8));

    private sealed class IsolatedCache : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), $"ZCodeQuotaAnalysisTests-{Guid.NewGuid():N}");
        private readonly IDisposable cacheScope;
        private readonly IDisposable logScope;

        public IsolatedCache()
        {
            Directory.CreateDirectory(root);
            cacheScope = MonitorCachePaths.PushLocalAppDataRoot(root);
            logScope = UsageLogPaths.PushRoot(Path.Combine(root, "logs"));
        }

        public void Dispose()
        {
            UsageCacheStore.Delete("ZCodeTokenMonitor");
            logScope.Dispose();
            cacheScope.Dispose();
            var resolved = Path.GetFullPath(root);
            var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!resolved.StartsWith(temp, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(resolved).StartsWith("ZCodeQuotaAnalysisTests-", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Refusing to clean up outside the isolated test directory.");
            }

            if (Directory.Exists(resolved)) Directory.Delete(resolved, recursive: true);
        }
    }

    private static ZCodeQuotaBalance Balance(
        string modelName,
        string? modelId,
        long total,
        long used,
        DateTimeOffset? expiresAt = null) =>
        new(modelName, modelId, total, used, total - used, total - used, expiresAt ?? PlanEnd, PlanStart, PlanEnd);

    private static ZCodeQuotaSnapshot Snapshot(DateTimeOffset local, params ZCodeQuotaBalance[] balances) =>
        new(local, "plan-1", "GLM Coding Plan", null, "active", balances, local);

    private static TokenUsageEvent Event(DateTimeOffset timestamp, long total, string key, string modelId) =>
        new(timestamp, InputTokens: total, CachedInputTokens: 0, OutputTokens: 0,
            ReasoningOutputTokens: 0, TotalTokens: total, Key: key, ModelId: modelId);

    [Fact]
    public void RecordSnapshot_RoundTripsPointsPerBucketAndDedupesSameInstant()
    {
        using var isolated = new IsolatedCache();
        var first = Snapshot(PlanStart.AddHours(2),
            Balance("GLM-5.3-Flash", "GLM-5.3-Flash", 1_000_000, 949_000),
            Balance("GLM-5.2", "GLM-5.2", 2_000_000, 200_000));
        var second = Snapshot(PlanStart.AddHours(7),
            Balance("GLM-5.3-Flash", "GLM-5.3-Flash", 1_000_000, 800_000));

        ZCodeQuotaAnalysisSource.RecordSnapshot(first);
        ZCodeQuotaAnalysisSource.RecordSnapshot(first);
        ZCodeQuotaAnalysisSource.RecordSnapshot(second);

        var history = ZCodeQuotaAnalysisSource.ReadHistory(PlanStart, PlanEnd);
        Assert.Equal(3, history.Count);
        Assert.All(history, point =>
        {
            Assert.NotNull(point.WeekUsedPercent);
            Assert.Equal(PlanEnd, point.WeekResetAtLocal);
            Assert.Null(point.FiveHourUsedPercent);
        });
        Assert.Equal(2, history.Count(point => point.LimitId == "GLM-5.3-Flash"));
        Assert.Single(history, point => point.LimitId == "GLM-5.2");
        Assert.Equal(94.9m, history.Single(point => point.SnapshotLocal == first.SnapshotLocal &&
                                                    point.LimitId == "GLM-5.3-Flash").WeekUsedPercent);
    }

    [Fact]
    public void RecordSnapshot_IgnoresBucketsWithoutMeteredUsage()
    {
        using var isolated = new IsolatedCache();
        var snapshot = Snapshot(PlanStart.AddHours(2),
            new ZCodeQuotaBalance("GLM-5.3-Flash", "GLM-5.3-Flash", 0, 0, 0, null, PlanEnd, PlanStart, PlanEnd));

        ZCodeQuotaAnalysisSource.RecordSnapshot(snapshot);

        Assert.Empty(ZCodeQuotaAnalysisSource.ReadHistory(PlanStart, PlanEnd));
    }

    [Fact]
    public void DescribeCurrent_MapsPlanPeriodEstimateAndHistoryCount()
    {
        using var isolated = new IsolatedCache();
        var snapshot = Snapshot(PlanStart.AddHours(2),
            Balance("GLM-5.3-Flash", "GLM-5.3-Flash", 1_000_000, 400_000));
        ZCodeQuotaAnalysisSource.RecordSnapshot(snapshot);

        var description = ZCodeQuotaAnalysisSource.DescribeCurrent(snapshot);

        Assert.NotNull(description);
        var period = description!.Period;
        Assert.Equal(PlanStart, period.PeriodStart);
        Assert.Equal(PlanEnd, period.PeriodEnd);
        Assert.Equal(PlanEnd, period.ResetAt);
        Assert.Equal(1, period.SnapshotCount);
        Assert.Equal(40m, period.MaxWeekUsedPercent);
        var estimate = description.Estimate;
        Assert.Equal("ZCode 套餐", estimate.Label);
        Assert.Equal(40m, estimate.UsedPercent);
        Assert.Equal((int)(PlanEnd - PlanStart).TotalMinutes, estimate.WindowMinutes);
        Assert.Equal(PlanStart, estimate.WindowStartLocal);
        Assert.Equal(1_000_000, estimate.EstimatedTokenLimit);
    }

    [Fact]
    public void DescribeCurrent_ReturnsNullWithoutMeteredBalance()
    {
        var snapshot = Snapshot(PlanStart.AddHours(2),
            new ZCodeQuotaBalance("GLM-5.3-Flash", "GLM-5.3-Flash", 0, 0, 0, null, null, null, null));

        Assert.Null(ZCodeQuotaAnalysisSource.DescribeCurrent(snapshot));
    }

    [Fact]
    public void BuildSamples_AnchorsOnSnapshotsAndCarriesTrailingUsage()
    {
        var points = new[]
        {
            new CodexQuotaSnapshot(PlanStart.AddHours(2), "GLM-5.3-Flash", "GLM-5.3-Flash", null, null, 50m, PlanEnd),
            new CodexQuotaSnapshot(PlanStart.AddHours(4), "GLM-5.3-Flash", "GLM-5.3-Flash", null, null, 80m, PlanEnd)
        };
        var rows = new[]
        {
            new TokenUsageBucket { StartLocal = PlanStart.AddHours(1), TotalTokens = 1_000 },
            new TokenUsageBucket { StartLocal = PlanStart.AddHours(3), TotalTokens = 2_000 },
            new TokenUsageBucket { StartLocal = PlanStart.AddHours(5), TotalTokens = 4_000 }
        };

        var samples = ZCodeQuotaAnalysisSource.BuildSamples(PlanStart, points, rows);

        Assert.Equal(4, samples.Count);
        Assert.Equal(PlanStart, samples[0].TimestampLocal);
        Assert.Equal(0m, samples[0].UsedPercent);
        Assert.Equal(0, samples[0].Usage.TotalTokens);
        Assert.Equal(PlanStart.AddHours(2), samples[1].TimestampLocal);
        Assert.Equal(50m, samples[1].UsedPercent);
        Assert.Equal(1_000, samples[1].Usage.TotalTokens);
        Assert.Equal(PlanStart.AddHours(4), samples[2].TimestampLocal);
        Assert.Equal(80m, samples[2].UsedPercent);
        Assert.Equal(2_000, samples[2].Usage.TotalTokens);
        Assert.Equal(PlanStart.AddHours(5), samples[3].TimestampLocal);
        Assert.Equal(80m, samples[3].UsedPercent);
        Assert.Equal(4_000, samples[3].Usage.TotalTokens);
    }

    [Fact]
    public void BuildFromSamples_PricesZCodeUsageThroughTheGroupCatalog()
    {
        var usage = new TokenUsageBucket { StartLocal = PlanStart };
        usage.Add(Event(PlanStart.AddMinutes(30), 1_000_000, "glm-1", "GLM-5.3-Flash"));
        usage.Add(Event(PlanStart.AddMinutes(45), 1_000_000, "glm-2", "GLM-5.3-Flash"));
        var samples = new[]
        {
            new QuotaCycleAnalysisSample(PlanStart, 0m, new TokenUsageBucket { StartLocal = PlanStart }),
            new QuotaCycleAnalysisSample(PlanStart.AddHours(2), 50m, usage)
        };
        var period = new CodexQuotaCycle(PlanStart, PlanEnd, PlanEnd, 2, 50m, IsCurrent: false);

        var result = QuotaCycleAnalysisCalculator.BuildFromSamples(
            period, samples, priceGroup: PricePresetGroups.ZCode);

        Assert.True(result.HasData);
        // A 50% drop splits into 5%-wide bands; the 2M tokens spread evenly
        // across them, so each band estimates the same GLM full-quota cost.
        Assert.Equal(10, result.Bands.Count);
        Assert.Equal(2_000_000, result.Tokens);
        Assert.All(result.Bands, band =>
        {
            Assert.Equal(200_000, band.Tokens);
            Assert.Equal(3.2m, band.EstimatedFullQuotaCost);
        });
        Assert.Equal("GLM-5.3-Flash", result.DominantModel);
    }

    [Fact]
    public void BuildAnalysis_CombinesSnapshotAnchorsWithCachedUsageIntoBands()
    {
        using var isolated = new IsolatedCache();
        var day = DayStart;
        var usageCache = UsageCacheStore.Load("ZCodeTokenMonitor");
        var dayBucket = new TokenUsageBucket { StartLocal = day };
        var detailEvents = new[]
        {
            Event(PlanStart.AddHours(1), 500_000, "zcode:e1", "GLM-5.3-Flash"),
            Event(PlanStart.AddHours(3), 300_000, "zcode:e2", "GLM-5.3-Flash")
        };
        foreach (var item in detailEvents)
        {
            dayBucket.Add(item);
        }
        usageCache.Put(dayBucket, detailEvents: detailEvents);

        ZCodeQuotaAnalysisSource.RecordSnapshot(Snapshot(PlanStart.AddHours(2),
            Balance("GLM-5.3-Flash", "GLM-5.3-Flash", 1_000_000, 500_000)));
        ZCodeQuotaAnalysisSource.RecordSnapshot(Snapshot(PlanStart.AddHours(4),
            Balance("GLM-5.3-Flash", "GLM-5.3-Flash", 1_000_000, 800_000)));

        var period = new CodexQuotaCycle(PlanStart, PlanEnd, PlanEnd, 2, 80m, IsCurrent: false);
        var result = new ZCodeQuotaAnalysisSource().BuildAnalysis(period, null, QuotaCycleAnalysisCalculator.DefaultBandSizePercent, default);

        Assert.True(result.HasData);
        // Drops of 50% then 30% split into 10 + 6 bands; usage is distributed
        // proportionally inside each interval (500k/10 and 300k/6 per band).
        Assert.Equal(16, result.Bands.Count);
        Assert.Equal(80m, result.ObservedQuotaDropPercent);
        Assert.Equal(800_000, result.Tokens);
        Assert.Equal(5m, result.Bands[0].UsedToPercent - result.Bands[0].UsedFromPercent);
        Assert.Equal(50_000, result.Bands[0].Tokens);
        Assert.Equal(50m, result.Bands[9].UsedToPercent);
        Assert.Equal(80m, result.Bands[15].UsedToPercent);
        Assert.All(result.Bands, band => Assert.True(band.EstimatedFullQuotaCost > 0m));
    }
}
