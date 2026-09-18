using Xunit;

namespace CodexTokenMonitor.Tests;

public sealed class QuotaPaceAnalyzerTests
{
    private static readonly TimeSpan Beijing = TimeSpan.FromHours(8);
    private static readonly DateTimeOffset WindowStart = new(2026, 9, 19, 8, 0, 0, Beijing);

    private static CodexQuotaWindowEstimate Window(
        decimal usedPercent,
        TimeSpan elapsed,
        TimeSpan? untilNaturalReset,
        int windowMinutes = 300)
    {
        return new CodexQuotaWindowEstimate(
            "5h",
            usedPercent,
            windowMinutes,
            WindowStart,
            WindowStart + elapsed,
            (WindowStart + elapsed + (untilNaturalReset ?? TimeSpan.Zero)),
            new TokenUsageSummary(),
            UsedGptCost: 0m,
            EstimatedGptLimit: null,
            EstimatedTokenLimit: null);
    }

    private static ResetOpportunitySummary SummaryWith(params ResetOpportunityRecord[] records)
    {
        return new ResetOpportunitySummary(records.Length, records.Length == 0 ? null : records.Min(item => item.ExpiresLocal), records);
    }

    [Fact]
    public void Analyze_OnPace_ProducesZeroDelta()
    {
        var report = QuotaPaceAnalyzer.Analyze(Window(50m, TimeSpan.FromMinutes(150), TimeSpan.Zero));

        Assert.Equal(50m, report.UsedPercent);
        Assert.Equal(50m, report.ExpectedUsedPercent);
        Assert.Equal(0m, report.DeltaPercent);
        Assert.Equal(50m, report.RemainingPercent);
    }

    [Fact]
    public void Analyze_HeavyUse_DeltaAheadOfPace()
    {
        var report = QuotaPaceAnalyzer.Analyze(Window(90m, TimeSpan.FromMinutes(75), TimeSpan.Zero));

        Assert.Equal(25m, report.ExpectedUsedPercent);
        Assert.Equal(65m, report.DeltaPercent);
    }

    [Fact]
    public void Analyze_LightUse_DeltaBehindPace()
    {
        var report = QuotaPaceAnalyzer.Analyze(Window(10m, TimeSpan.FromMinutes(150), TimeSpan.Zero));

        Assert.Equal(-40m, report.DeltaPercent);
    }

    [Fact]
    public void Analyze_EstimatedFullAt_ExtrapolatesLinearly()
    {
        var report = QuotaPaceAnalyzer.Analyze(Window(50m, TimeSpan.FromMinutes(120), TimeSpan.Zero));

        Assert.Equal(WindowStart.AddMinutes(240), report.EstimatedFullAtLocal);
    }

    [Fact]
    public void Analyze_EmptyWindow_HasNoEstimatedFullAt()
    {
        var report = QuotaPaceAnalyzer.Analyze(Window(0m, TimeSpan.FromMinutes(30), TimeSpan.Zero));

        Assert.Null(report.EstimatedFullAtLocal);
    }

    [Fact]
    public void Analyze_ClampsUsedPercentToWindowBounds()
    {
        var over = QuotaPaceAnalyzer.Analyze(Window(150m, TimeSpan.FromMinutes(150), TimeSpan.Zero));
        Assert.Equal(100m, over.UsedPercent);
        Assert.Equal(0m, over.RemainingPercent);

        var under = QuotaPaceAnalyzer.Analyze(Window(-5m, TimeSpan.FromMinutes(150), TimeSpan.Zero));
        Assert.Equal(0m, under.UsedPercent);
        Assert.Equal(100m, under.RemainingPercent);
    }

    [Fact]
    public void Analyze_NaturalResetWithin90Minutes_WaitsForRefresh()
    {
        var report = QuotaPaceAnalyzer.Analyze(Window(60m, TimeSpan.FromMinutes(150), TimeSpan.FromMinutes(60)));

        Assert.Equal("等自然刷新", report.Rating);
    }

    [Fact]
    public void Analyze_WithoutCards_RatesByRemainingResetHorizon()
    {
        var soon = QuotaPaceAnalyzer.Analyze(Window(5m, TimeSpan.FromMinutes(100), TimeSpan.FromHours(5)));
        Assert.Equal("不急", soon.Rating);

        var far = QuotaPaceAnalyzer.Analyze(Window(5m, TimeSpan.FromMinutes(100), TimeSpan.FromHours(20)));
        Assert.Equal("按需", far.Rating);
    }

    [Fact]
    public void Analyze_SummaryWithoutAvailableCards_ReportsNoCard()
    {
        var report = QuotaPaceAnalyzer.Analyze(
            Window(60m, TimeSpan.FromMinutes(150), TimeSpan.FromHours(5)),
            SummaryWith());

        Assert.Equal("无重置卡", report.Rating);
    }

    [Fact]
    public void Analyze_CardExpiringEarly_ProjectsUnacceptableWaste()
    {
        // 10% used after 1h; a card expiring 2h into the window projects only
        // 20% consumed, so 80% of the window would be wasted.
        var card = new ResetOpportunityRecord { ExpiresLocal = WindowStart.AddMinutes(120) };
        var report = QuotaPaceAnalyzer.Analyze(
            Window(10m, TimeSpan.FromMinutes(60), TimeSpan.FromHours(48)),
            SummaryWith(card));

        Assert.Equal("不建议", report.Rating);
    }

    [Fact]
    public void Analyze_CardExpiringNearFullWindow_ProjectsAcceptableWaste()
    {
        // Weekly window at 60% after 100 hours: the linear full estimate lands
        // at 166.7h, past the reset at 166.4h, while a card expiring at 165.8h
        // projects ~99.5% consumed - under the 2% waste tolerance.
        var card = new ResetOpportunityRecord { ExpiresLocal = WindowStart.AddMinutes(9950) };
        var report = QuotaPaceAnalyzer.Analyze(
            Window(60m, TimeSpan.FromMinutes(6000), TimeSpan.FromMinutes(4000), windowMinutes: 10080),
            SummaryWith(card));

        Assert.Equal("临期可用", report.Rating);
    }

    [Fact]
    public void FormatShort_IncludesUsedExpectedAndRating()
    {
        var text = QuotaPaceAnalyzer.FormatShort(Window(50m, TimeSpan.FromMinutes(150), TimeSpan.FromHours(5)));

        Assert.Contains("已50%", text);
        Assert.Contains("应50%", text);
    }

    [Fact]
    public void FormatShort_NullWindow_ReturnsEmpty()
    {
        Assert.Equal("", QuotaPaceAnalyzer.FormatShort(null));
        Assert.Equal("", QuotaPaceAnalyzer.FormatDetailed(null));
    }
}
