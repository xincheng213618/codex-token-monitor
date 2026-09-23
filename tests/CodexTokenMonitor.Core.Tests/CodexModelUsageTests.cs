using System.Text.Json;
using Xunit;

namespace CodexTokenMonitor.Tests;

public sealed class CodexModelUsageTests
{
    [Fact]
    public void RepeatedSessionMetadataPreservesModelAndNewSessionClearsIt()
    {
        var context = new CodexModelContext();
        context.Observe("""{"type":"session_meta","payload":{"id":"session-a"}}""");
        context.Observe("""{"type":"turn_context","payload":{"model":"gpt-5.6-sol"}}""");
        context.Observe("""{"type":"session_meta","payload":{"id":"session-a","cli_version":"new"}}""");
        Assert.Equal("gpt-5.6-sol", context.ModelId);
        context.Observe("""{"type":"turn_context","payload":{"info":{"model":"gpt-5.6-luna"}}}""");
        context.Observe("""{"type":"session_meta","payload":{"session_id":"session-a"}}""");
        Assert.Equal("gpt-5.6-luna", context.ModelId);
        context.Observe("""{"type":"session_meta","payload":{"id":"session-b"}}""");
        Assert.Null(context.ModelId);
    }

    [Fact]
    public void RepeatedSessionMetadataKeepsHistoricalUsagePriced()
    {
        using var env = new EnvironmentScope();
        env.Write(Context(Day, "gpt-5.6-sol"),
            """{"type":"session_meta","payload":{"id":"a"}}""", Token(Day.AddHours(1), "repeated-meta"));
        UsageSourceReaders.Codex.WarmHistoricalDays(new[] { Day });
        Assert.Equal("gpt-5.6-sol", Assert.Single(UsageCacheStore.Load().GetAllDetailEvents()).ModelId);
        Assert.True(CodexModelCost.Estimate(UsageCacheStore.Load().ReadRange(Day, Day.AddDays(1))).IsComplete);
    }

    [Fact]
    public void SparkAndMissingModelAreExplainedSeparatelyAndUserPricesStillWork()
    {
        var settings = new PriceSettings();
        CodexModelCost.AddMissingPresets(settings, new[] { "gpt-5.3-codex-spark" });
        var usage = new TokenUsageBucket();
        usage.Add(Event(Day, "spark", "gpt-5.3-codex-spark"));
        var cost = CodexModelCost.Estimate(usage, settings.CodexPresets);
        Assert.Contains("Spark 未计价", cost.Format());
        Assert.Contains("暂无公开 API 单价", cost.MissingPriceDescription);
        Assert.DoesNotContain("0x", cost.Format());
        usage.Add(Event(Day.AddMinutes(1), "missing", null));
        Assert.Contains("缺模型名", CodexModelCost.Estimate(usage, settings.CodexPresets).Format());
        var price = Assert.Single(settings.CodexPresets, p => p.ModelId == "gpt-5.3-codex-spark");
        price.UncachedInput = 1; price.CachedInput = 1; price.Output = 1;
        Assert.NotNull(Assert.Single(CodexModelCost.Estimate(usage, settings.CodexPresets).Models).Cost);
    }

    [Fact]
    public void AutoReviewUsesExplicitReferencePriceAndUnknownModelNamesRemainVisible()
    {
        var usage = new TokenUsageBucket();
        usage.Add(new TokenUsageEvent(DateTimeOffset.UtcNow, 1_000_000, 500_000, 100_000, 0, 1_100_000, ModelId: "codex-auto-review"));
        var priced = CodexModelCost.Estimate(usage, PricePreset.DefaultsForGroup(PricePresetGroups.Codex));
        Assert.True(priced.IsComplete);
        Assert.Equal(0.23m, priced.KnownCost);
        usage.Add(new TokenUsageEvent(DateTimeOffset.UtcNow, 100, 0, 10, 0, 110, ModelId: "unlisted-model"));
        var partial = CodexModelCost.Estimate(usage, PricePreset.DefaultsForGroup(PricePresetGroups.Codex));
        Assert.False(partial.IsComplete);
        Assert.Contains("unlisted-model", partial.MissingPriceDescription);
        Assert.Contains("1 条", partial.MissingPriceDescription);
    }

    [Fact]
    public void ReserveUsesLunaPriceAndDoesNotCreateAZeroPlaceholder()
    {
        var settings = new PriceSettings();
        Assert.Equal(0, CodexModelCost.AddMissingPresets(settings, new[] { CodexModelCost.ReserveModelId }));

        var usage = new TokenUsageBucket();
        usage.Add(Event(Day, "reserve", CodexModelCost.ReserveModelId));
        var estimate = CodexModelCost.Estimate(usage, settings.CodexPresets);

        Assert.True(estimate.IsComplete);
        Assert.Equal(.157m, estimate.KnownCost);
        Assert.Equal(.157m, Assert.Single(estimate.Models).Cost);
        Assert.Equal(2.5m, CodexModelCost.FastQuotaMultiplier(CodexModelCost.ReserveModelId));
    }

    [Fact]
    public void ReserveFallsBackToLunaWhenAnOlderZeroPlaceholderIsPersisted()
    {
        var settings = new PriceSettings();
        settings.CodexPresets.Add(new PricePreset
        {
            Provider = "OpenAI",
            Model = CodexModelCost.ReserveModelId,
            ModelId = CodexModelCost.ReserveModelId,
            CurrencySymbol = "$",
            UnitLabel = "USD / 1M tokens",
            Divisor = 1_000_000m,
            Source = CodexModelCost.PlaceholderPriceSource
        });
        var usage = new TokenUsageBucket();
        usage.Add(Event(Day, "reserve", CodexModelCost.ReserveModelId));

        var estimate = CodexModelCost.Estimate(usage, settings.CodexPresets);

        Assert.True(estimate.IsComplete);
        Assert.Equal(.157m, estimate.KnownCost);
    }

    [Fact]
    public void FindModelUsageMergesDateSuffixedReserveIds()
    {
        var usage = new TokenUsageBucket();
        usage.Add(Event(Day, "reserve-a", "gpt-reserve-2026-09-12"));
        usage.Add(Event(Day.AddMinutes(1), "reserve-b", CodexModelCost.ReserveModelId));

        var reserve = CodexModelCost.FindModelUsage(usage, CodexModelCost.ReserveModelId);

        Assert.NotNull(reserve);
        Assert.Equal(2, reserve.Events);
        Assert.Equal(2_100_000, reserve.TotalTokens);
    }

    private static readonly DateTimeOffset Day = new(2001, 4, 9, 0, 0, 0, TimeSpan.FromHours(8));

    [Theory]
    [InlineData("gpt-5.5", "priority", 2.5)]
    [InlineData("gpt-5.6-luna", "fast", 2.5)]
    [InlineData("gpt-6-astra", "fast", 2.5)]
    [InlineData("gpt-6-sol", "fast", 2.5)]
    [InlineData("gpt-6-luna", "fast", 2.5)]
    [InlineData("gpt-5.4", "priority", 2)]
    public void FastQuotaWeightOnlyChangesFastSubsetAndLeavesComparisonTokensAlone(string model, string tier, double multiplier)
    {
        var preset = Price(model, 1m, 1m, 1m); preset.ModelId = model; preset.CacheWriteInput = 1m;
        var bucket = new TokenUsageBucket();
        bucket.Add(Event(Day, "normal", model) with { ServiceTier = "default" });
        bucket.Add(Event(Day.AddMinutes(1), "fast", model) with { ServiceTier = tier });
        var result = CodexModelCost.Estimate(bucket, new[] { preset });
        Assert.Equal(2.1m, result.KnownCost);
        Assert.Equal(1.05m * (1 + (decimal)multiplier), result.QuotaEquivalentCost);
        Assert.Equal(result.KnownCost, bucket.EstimateCost(preset.ToProfile()));
        Assert.Equal(1, result.FastEvents); Assert.Equal(0, result.UnknownTierEvents);
        var merged = new TokenUsageBucket(); merged.MergeFrom(bucket);
        Assert.Equal(result.QuotaEquivalentCost, CodexModelCost.Estimate(merged, new[] { preset }).QuotaEquivalentCost);
        Assert.Equal(2_100_000, merged.TotalTokens);
    }

    [Fact]
    public void MissingSpeedAndUnknownModelMultiplierRemainExplicit()
    {
        var preset = Price("gpt-future", 1m, 1m, 1m); preset.ModelId = "gpt-future";
        var bucket = new TokenUsageBucket();
        bucket.Add(Event(Day, "fast", "gpt-future") with { ServiceTier = "fast" });
        bucket.Add(Event(Day.AddMinutes(1), "unknown", "gpt-future"));
        var cost = CodexModelCost.Estimate(bucket, new[] { preset });
        Assert.Equal(1, cost.UnknownTierEvents); Assert.Equal(1, cost.UnknownFastRateEvents);
        Assert.Equal(cost.KnownCost, cost.QuotaEquivalentCost);
        Assert.DoesNotContain("速度未记录", cost.FormatQuotaCost());
        Assert.Contains("默认按普速 1x", cost.SpeedDescription);
        Assert.Contains("Fast 倍率待补", cost.FormatQuotaCost());
    }

    [Theory]
    [InlineData("gpt-5.6-sol")]
    [InlineData("openai/gpt-5.6-sol-2026-09-05")]
    [InlineData("gpt-5.6")]
    public void SolApiPromotionDoesNotReduceSubscriptionQuotaReference(string model)
    {
        var bucket = new TokenUsageBucket();
        // 0.3M uncached + 0.6M cached + 0.1M writes + 0.05M output per event.
        bucket.Add(Event(Day, "standard", model) with { ServiceTier = "default" });
        bucket.Add(Event(Day.AddMinutes(1), "fast", model) with { ServiceTier = "priority" });
        var price = Price("GPT-5.6 Sol", 5m, .5m, 30m); price.CacheWriteInput = 6.25m;
        var before = CodexModelCost.Estimate(bucket, new[] { price });
        price.UncachedInput = 4m; price.CachedInput = .4m; price.Output = 20m; price.CacheWriteInput = 5m;
        var after = CodexModelCost.Estimate(bucket, new[] { price });

        Assert.Equal(7.85m, before.KnownCost);
        Assert.Equal(5.88m, after.KnownCost);
        Assert.Equal(7.85m, after.QuotaBaseCost);
        Assert.Equal(5.8875m, after.FastSurcharge);
        Assert.Equal(13.7375m, after.QuotaEquivalentCost);
        Assert.Equal(before.QuotaEquivalentCost, after.QuotaEquivalentCost);
        Assert.Equal(after.KnownCost, bucket.EstimateCost(price.ToProfile()));
        Assert.Equal(1, after.FastEvents);
        Assert.Equal(0, after.UnknownTierEvents);
    }

    [Fact]
    public void SolNormalAndUnknownSpeedUseSubscriptionBaselineWithoutInventingFast()
    {
        var bucket = new TokenUsageBucket();
        bucket.Add(new TokenUsageEvent(Day, 1_000_000, 1_000_000, 0, 0, 1_000_000,
            ModelId: "gpt-5.6-sol", ServiceTier: "default"));
        bucket.Add(new TokenUsageEvent(Day.AddMinutes(1), 1_000_000, 1_000_000, 0, 0, 1_000_000,
            ModelId: "gpt-5.6-sol"));
        var estimate = CodexModelCost.Estimate(bucket, PricePreset.DefaultsForGroup(PricePresetGroups.Codex));
        Assert.Equal(.8m, estimate.KnownCost);
        Assert.Equal(1m, estimate.QuotaEquivalentCost);
        Assert.Equal(0, estimate.FastSurcharge);
        Assert.Equal(0, estimate.FastEvents);
        Assert.Equal(1, estimate.UnknownTierEvents);
        Assert.Equal(2m, CodexModelCost.EstimateQuotaValue(bucket, 50m));
    }

    [Fact]
    public void FastSwitchesPersistAcrossRawReadersDayCacheRangeAndSync()
    {
        using var env = new EnvironmentScope();
        string Tier(DateTimeOffset time, string tier) => System.Text.Json.JsonSerializer.Serialize(new
        { timestamp = time, type = "event_msg", payload = new { type = "thread_settings_applied", thread_settings = new { service_tier = tier } } });
        env.Write(Context(Day.AddMinutes(-1), "gpt-5.6-sol"), Tier(Day.AddSeconds(-1), "priority"),
            """{"type":"session_meta","payload":{"id":"same"}}""", Token(Day.AddHours(1), "fast"),
            Tier(Day.AddHours(2), "default"), Token(Day.AddHours(3), "normal"));
        UsageSourceReaders.Codex.WarmHistoricalDays(new[] { Day });
        var cache = UsageCacheStore.Load();
        foreach (var events in new[] { cache.GetAllDetailEvents(), cache.GetDetailEvents(DateOnly.FromDateTime(Day.DateTime)),
            cache.GetDetailEvents(Day, Day.AddDays(1)), cache.EnumerateDetailEvents(null, null).ToArray() })
            Assert.Equal(new[] { "priority", "default" }, events.Select(e => e.ServiceTier));
        foreach (var bucket in new[] { cache.ReadRange(Day, Day.AddDays(1)), cache.ReadRange(Day.AddMinutes(1), Day.AddHours(4)) })
        {
            var cost = CodexModelCost.Estimate(bucket);
            Assert.Equal(1, cost.FastEvents); Assert.Equal(0, cost.UnknownTierEvents);
        }
        var file = Path.Combine(env.Root, "fast.json");
        CodexDataTransferService.Export(file, "CodexTokenMonitor", "source", "Source", Day.AddDays(1));
        CodexDataTransferService.Import(new[] { file }, "fast-peer");
        var remote = UsageCacheStore.Load("fast-peer");
        Assert.Equal(cache.GetAllDetailEvents(), remote.GetAllDetailEvents());
        Assert.Equal(1, CodexModelCost.Estimate(remote.ReadRange(Day, Day.AddDays(1))).FastEvents);
        Assert.Equal(0, CodexDataTransferService.Import(new[] { file }, "fast-peer").AddedUsageEventCount);
    }

    [Fact]
    public void SyncMetadataEnrichmentDoesNotDropKnownModelOrSpeedOrDuplicateEvents()
    {
        var named = Event(Day, "same", "gpt-5.6-sol");
        var tiered = named with { ModelId = null, ServiceTier = "priority" };
        foreach (var events in new[] { new[] { named, tiered }, new[] { tiered, named } })
        {
            var result = Assert.Single(UsageEventMerger.Merge(events));
            Assert.Equal("gpt-5.6-sol", result.ModelId); Assert.Equal("priority", result.ServiceTier);
        }
    }

    [Fact]
    public void NewModelsGetEditableZeroPresetsAndRepricingChangesHistoricalCost()
    {
        var settings = new PriceSettings();
        Assert.Equal(1, CodexModelCost.AddMissingPresets(settings, new[] { "new-model", "new-model", "gpt-5.6-sol" }));
        Assert.Equal(0, CodexModelCost.AddMissingPresets(settings, new[] { "new-model" }));
        var preset = Assert.Single(settings.CodexPresets, p => p.ModelId == "new-model");
        var bucket = new TokenUsageBucket();
        bucket.Add(Event(Day,"new", "new-model"));
        Assert.Equal(0m, CodexModelCost.Estimate(bucket, settings.CodexPresets).KnownCost);
        Assert.Contains("0x", CodexModelCost.Estimate(bucket, settings.CodexPresets).Format());
        preset.UncachedInput = 5; preset.CachedInput = .5m; preset.CacheWriteInput = 5; preset.Output = 30;
        var repriced = CodexModelCost.Estimate(bucket, settings.CodexPresets);
        Assert.True(repriced.IsComplete);
        Assert.Equal(3.8m, repriced.KnownCost);
    }

    [Fact]
    public void MixedModelCostAndCounterfactualCostAreIndependent()
    {
        var bucket = new TokenUsageBucket { StartLocal = Day };
        bucket.Add(Event(Day, "a", "gpt-5.6-sol"));
        bucket.Add(Event(Day.AddMinutes(1), "b", "gpt-5.6-luna"));
        var prices = new[] { Price("GPT-5.6 Sol", 5m, .5m, 30m), Price("GPT-5.6 Luna", 1m, .1m, 6m) };

        var actual = CodexModelCost.Estimate(bucket, prices);
        // Each request: 0.3M uncached + 0.6M cached + 0.1M writes + 0.05M output.
        Assert.Equal(4.56m, actual.KnownCost);
        Assert.True(actual.IsComplete);
        Assert.Equal(7.6m, bucket.EstimateCost(prices[0].ToProfile()));
        Assert.Equal(actual.KnownCost, CodexModelCost.Estimate(bucket, prices.Reverse()).KnownCost);
        Assert.Equal(2_100_000, bucket.TotalTokens);
        Assert.Equal(20_000, bucket.ReasoningOutputTokens); // Already included in output; never billed twice.
    }

    [Fact]
    public void Gpt6SolAndLunaUsageUsesAllFourPublishedTokenRates()
    {
        var bucket = new TokenUsageBucket { StartLocal = Day };
        bucket.Add(Event(Day, "sol", "gpt-6-sol"));
        bucket.Add(Event(Day.AddMinutes(1), "luna", "gpt-6-luna"));

        var result = CodexModelCost.Estimate(bucket, PricePreset.DefaultsForGroup(PricePresetGroups.Codex));

        Assert.True(result.IsComplete);
        Assert.Equal(1.5435m, result.KnownCost);
        Assert.Equal(0, result.UnpricedEvents);
    }

    [Fact]
    public void UnknownModelsUseZeroReferencePriceWithoutBlockingQuotaEstimates()
    {
        var bucket = new TokenUsageBucket { StartLocal = Day };
        bucket.Add(Event(Day, "a", "gpt-5.6-sol"));
        bucket.Add(Event(Day.AddMinutes(1), "b", "gpt-new-model"));
        bucket.Add(Event(Day.AddMinutes(2), "c", null));
        var result = CodexModelCost.Estimate(bucket, new[] { Price("GPT-5.6 Sol", 5m, .5m, 30m) });
        Assert.False(result.IsComplete);
        Assert.Null(result.CompleteCost);
        Assert.Equal(2_100_000, result.UnpricedTokens);
        Assert.Equal(2, result.UnpricedEvents);
        Assert.Contains("0x 待填", result.Format());
        Assert.Contains("未识别", CodexModelCost.DescribeModels(bucket));
        Assert.Equal(CodexModelCost.Estimate(bucket).QuotaEquivalentCost * 2m, CodexModelCost.EstimateQuotaValue(bucket, 50m));
    }

    [Fact]
    public void ExactIdOverridesDisplayNameAndDateAliasesMatchWithoutGuessingOtherModels()
    {
        var bucket = new TokenUsageBucket();
        bucket.Add(Event(Day, "a", "openai/gpt-custom-2026-09-05"));
        var preset = Price("我的自定义名字", 5m, .5m, 30m);
        preset.ModelId = "gpt-custom";
        Assert.True(CodexModelCost.Estimate(bucket, new[] { preset }).IsComplete);
        preset.ModelId = "gpt-custo";
        Assert.False(CodexModelCost.Estimate(bucket, new[] { preset }).IsComplete);
        Assert.Equal("gpt-custo", preset.Clone().ModelId);
    }

    [Fact]
    public void ModelContextSwitchesSurviveRawScanAllCacheReadersAndPortableRoundTrip()
    {
        using var env = new EnvironmentScope();
        env.Write(
            Context(Day.AddMinutes(-1), "gpt-5.6-sol"),
            Token(Day.AddHours(1), "a"),
            Context(Day.AddHours(2), "gpt-5.6-luna"),
            Token(Day.AddHours(2), "b"),
            Token(Day.AddHours(3), "c", "gpt-6-astra"));
        var scanned = UsageSourceReaders.Codex.ReadTransientDetailRows(Day, Day.AddDays(1));
        Assert.Equal(new[] { "gpt-5.6-sol", "gpt-5.6-luna", "gpt-6-astra" }, scanned.Select(b => Assert.Single(b.ModelUsage).Key));
        UsageSourceReaders.Codex.WarmHistoricalDays(new[] { Day });
        var cache = UsageCacheStore.Load();
        var events = cache.GetAllDetailEvents();
        Assert.Equal(3, events.Count);
        Assert.Equal(new[] { "gpt-5.6-luna", "gpt-5.6-sol", "gpt-6-astra" }, cache.GetModelIds());
        Assert.Equal(events, cache.GetDetailEvents(DateOnly.FromDateTime(Day.DateTime)));
        Assert.Equal(events, cache.GetDetailEvents(Day, Day.AddDays(1)));
        Assert.Equal(events, cache.EnumerateDetailEvents(Day, Day.AddDays(1)).ToList());
        Assert.Equal(3, cache.ReadRange(Day, Day.AddDays(1)).ModelUsage.Count);
        Assert.Equal(3, cache.ReadRange(Day.AddMinutes(1), Day.AddHours(4)).ModelUsage.Count);
        Assert.All(cache.ReadDetailRows(Day, Day.AddDays(1)), b => Assert.Single(b.ModelUsage));
        Assert.True(cache.TryGetRecord(DateOnly.FromDateTime(Day.DateTime), out var record));
        Assert.Equal(3, record.ModelUsage.Count);
        Assert.True(cache.TryGet(DateOnly.FromDateTime(Day.DateTime), out var cached));
        Assert.Equal(3, cached.ModelUsage.Count);

        var path = Path.Combine(env.Root, "transfer.json");
        CodexDataTransferService.Export(path, "CodexTokenMonitor", "pc-a", "PC A", Day.AddDays(1));
        CodexDataTransferService.Import(new[] { path }, "PC-B");
        var repeated = CodexDataTransferService.Import(new[] { path }, "PC-B");
        Assert.Equal(0, repeated.AddedUsageEventCount);
        var remote = UsageCacheStore.Load("PC-B");
        Assert.Equal(events, remote.GetAllDetailEvents());
        Assert.Equal(3, remote.ReadRange(Day, Day.AddDays(1)).ModelUsage.Count);
    }

    [Fact]
    public void LiveAppendKeepsContextAndTruncationClearsIt()
    {
        using var env = new EnvironmentScope();
        var now = BeijingClock.Now;
        var start = new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, now.Offset);
        // Avoid the midnight boundary: these events are always earlier than the scan end.
        var first = start.AddTicks((now - start).Ticks / 3);
        var second = start.AddTicks((now - start).Ticks * 2 / 3);
        env.Write(Context(start, "gpt-5.6-sol"), Token(first, "first"));
        var initial = UsageSourceReaders.Codex.ReadDetailRows(start, now);
        Assert.Equal("gpt-5.6-sol", Assert.Single(Assert.Single(initial).ModelUsage).Key);
        File.AppendAllLines(env.Log, new[] { Settings(second, "gpt-5.6-luna"),
            """{"type":"event_msg","payload":{"type":"thread_settings_applied","thread_settings":{"service_tier":"priority"}}}""", Token(second, "second") });
        var appended = UsageSourceReaders.Codex.ReadDetailRows(start, BeijingClock.Now);
        Assert.Equal(2, appended.Count);
        Assert.Equal("gpt-5.6-luna", Assert.Single(appended.Last().ModelUsage).Key);
        Assert.Equal(1, CodexModelCost.Estimate(appended.Last()).FastEvents);
        env.Write(Token(second.AddTicks(1), "replacement"));
        var replacement = UsageSourceReaders.Codex.ReadDetailRows(start, BeijingClock.Now);
        Assert.Contains(replacement, b => b.ModelUsage.Count == 0);
    }

    [Fact]
    public void ModelMetadataDoesNotChangeEventIdentityAcrossComputers()
    {
        var identified = Event(Day, "same", "gpt-5.6-luna");
        var unknown = identified with { ModelId = null };
        var merged = UsageEventMerger.Merge(new[] { unknown, identified, unknown, identified });
        Assert.Equal(identified, Assert.Single(merged));
        Assert.Equal(UsageEventMerger.GetStableKey(unknown), UsageEventMerger.GetStableKey(identified));
    }

    [Fact]
    public void RecentSyncRangeCoversRollingWeekOnMonday()
    {
        var monday = new DateTimeOffset(2026, 9, 7, 1, 0, 0, TimeSpan.FromHours(8));
        var range = CodexDataTransferService.GetExportRange(CodexDataExportScope.RecentDays, monday);
        Assert.True(range.StartInclusive <= monday.AddDays(-7));
        Assert.True(range.EndExclusive > monday);
        Assert.Equal(TimeSpan.FromDays(8), range.EndExclusive - range.StartInclusive);
    }

    private static TokenUsageEvent Event(DateTimeOffset timestamp, string key, string? model) =>
        new(timestamp, 1_000_000, 600_000, 50_000, 10_000, 1_050_000, "codex:" + key, 100_000, model);

    private static PricePreset Price(string name, decimal input, decimal cached, decimal output) =>
        new() { Provider = "OpenAI", Model = name, UncachedInput = input, CachedInput = cached, CacheWriteInput = input, Output = output };

    private static string Context(DateTimeOffset timestamp, string model) =>
        JsonSerializer.Serialize(new { timestamp, type = "turn_context", payload = new { model } });

    private static string Settings(DateTimeOffset timestamp, string model) =>
        JsonSerializer.Serialize(new { timestamp, type = "event_msg", payload = new { type = "thread_settings_applied", thread_settings = new { model } } });

    private static string Token(DateTimeOffset timestamp, string key, string? model = null) =>
        JsonSerializer.Serialize(new { timestamp, type = "event_msg", payload = new { type = "token_count", turn_id = key,
            info = new { model, last_token_usage = new { input_tokens = 1000, cached_input_tokens = 600, cache_write_input_tokens = 100,
                output_tokens = 50, reasoning_output_tokens = 10, total_tokens = 1050 } } } });

    private sealed class EnvironmentScope : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "CodexModelUsageTests-" + Guid.NewGuid().ToString("N"));
        public string Log => Path.Combine(Root, "logs", "Codex", "sessions", "model-test.jsonl");
        private readonly IDisposable cacheScope;
        private readonly IDisposable logScope;
        public EnvironmentScope()
        {
            cacheScope = MonitorCachePaths.PushLocalAppDataRoot(Root);
            logScope = UsageLogPaths.PushRoot(Path.Combine(Root, "logs"));
            Directory.CreateDirectory(Path.GetDirectoryName(Log)!);
        }
        public void Write(params string[] lines) => File.WriteAllLines(Log, lines);
        public void Dispose()
        {
            UsageCacheStore.Delete("CodexTokenMonitor");
            UsageCacheStore.Delete("PC-B");
            logScope.Dispose();
            cacheScope.Dispose();
            // Remove individual test files only; no recursive cleanup or user data access.
            foreach (var file in new[] { Log, Path.Combine(Root, "transfer.json") })
                if (File.Exists(file)) File.Delete(file);
        }
    }
}
