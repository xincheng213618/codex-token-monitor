using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using MessageBox = System.Windows.MessageBox;

namespace CodexTokenMonitor;

public partial class QuotaCycleAnalysisWindow : Window
{
    private readonly CodexQuotaCycle period;
    private readonly CodexQuotaWindowEstimate? currentWeek;
    private readonly CodexQuotaCycle? previousPeriod;
    private readonly QuotaCycleAnalysisChart chart = new();
    private readonly AnalysisQuerySession querySession;
    private readonly QuotaCycleAnalysisQueryService queryService;
    private IReadOnlyList<QuotaCycleBandRow> bandRows = Array.Empty<QuotaCycleBandRow>();
    private bool analysisLoading;
    private bool hasSuccessfulResult;
    private bool bandSizeChangePending;
    private decimal selectedBandSizePercent = QuotaCycleAnalysisCalculator.DefaultBandSizePercent;
    private decimal displayedBandSizePercent = QuotaCycleAnalysisCalculator.DefaultBandSizePercent;

    internal QuotaCycleAnalysisWindow(
        CodexQuotaCycle period,
        CodexQuotaWindowEstimate? currentWeek = null,
        CodexQuotaCycle? previousPeriod = null,
        MonitorRuntime? runtime = null,
        IQuotaCycleAnalysisSource? analysisSource = null,
        string? cycleTitle = null,
        string? priceGroup = null)
    {
        this.period = period;
        this.currentWeek = currentWeek;
        this.previousPeriod = previousPeriod;
        querySession = new AnalysisQuerySession(runtime);
        queryService = analysisSource is null
            ? new QuotaCycleAnalysisQueryService()
            : new QuotaCycleAnalysisQueryService(analysisSource);
        InitializeComponent();
        ForecastPanel.PriceGroup = priceGroup;
        ChartHost.Content = chart;
        chart.BandSelected += Chart_BandSelected;
        ConsumptionTimelineView.BandSelected += Chart_BandSelected;
        ForecastPanel.ForecastChanged += ConsumptionTimelineView.SetForecast;
        ForecastPanel.RefreshRequested += async (_, _) => await LoadAnalysisAsync();
        ApplyChartView();

        CycleTitleText.Text = $"{cycleTitle ?? "7d 周期"} · {period.PeriodStart:MM-dd HH:mm} → {period.PeriodEnd:MM-dd HH:mm}";
        CycleMetaText.Text = $"重置 {period.ResetAt:yyyy-MM-dd HH:mm} · {period.SnapshotCount:N0} 个额度快照";

        Loaded += async (_, _) => await LoadAnalysisAsync();
        Closed += (_, _) => querySession.Dispose();
    }

    private async Task LoadAnalysisAsync()
    {
        if (analysisLoading || querySession.IsStopping) return;
        analysisLoading = true;
        ForecastPanel.SetLoading(true);
        try
        {
            var requestedBandSize = selectedBandSizePercent;
            var request = new QuotaCycleAnalysisRequest(period, currentWeek, previousPeriod, requestedBandSize);
            if (!hasSuccessfulResult)
            {
                StatusText.Text = "正在读取已有分析缓存...";
                var preview = await querySession.RunAsync("周期分析缓存预览",
                    token => queryService.ExecuteCached(request, token));
                if (querySession.IsStopping || !IsLoaded) return;
                if (requestedBandSize != selectedBandSizePercent)
                {
                    bandSizeChangePending = true;
                    return;
                }
                if (preview.CacheWarnings.Count == 0 && preview.Value.Analysis.HasData)
                {
                    ApplyResult(preview.Value.Analysis, preview.Value.Capacities);
                    ModelCapacityValue.Text = "缓存预览 · 模型动态估算将在数据补齐后更新";
                    hasSuccessfulResult = true;
                }
            }
            StatusText.Text = hasSuccessfulResult
                ? "已显示缓存分析，正在等待数据补齐与更新..."
                : "正在等待数据补齐与分析...";
            var result = await querySession.RunAsync("周期分析",
                token => (Analysis: queryService.Execute(request, token),
                    Plan: SubscriptionPlanStore.Summarize(period.PeriodStart, period.PeriodEnd)),
                requiresSharedIo: true);
            if (querySession.IsStopping || !IsLoaded) return;
            if (requestedBandSize != selectedBandSizePercent)
            {
                bandSizeChangePending = true;
                return;
            }
            bandSizeChangePending = false;

            if (result.CacheWarnings.Count > 0)
            {
                StatusText.Text = CacheFailureText.Summary(result.CacheWarnings, hasSuccessfulResult);
                StatusText.ToolTip = CacheFailureText.Detail(result.CacheWarnings);
                if (!hasSuccessfulResult)
                    InsightText.Text = "周期分析暂不可用，恢复缓存后重新打开或刷新此页。";
                return;
            }

            var loadResult = result.Value.Analysis;
            ApplyResult(loadResult.Analysis, loadResult.Capacities);
            var plan = result.Value.Plan;
            CycleMetaText.Text = $"重置 {period.ResetAt:yyyy-MM-dd HH:mm} · {period.SnapshotCount:N0} 个额度快照" +
                                 (plan.HasRecords ? $" · {plan.PlanNames}" : " · 未设置套餐");
            hasSuccessfulResult = true;
            StatusText.ToolTip = null;
            if (!string.IsNullOrWhiteSpace(loadResult.RefreshReason)) StatusText.Text = loadResult.RefreshReason;
        }
        catch (OperationCanceledException)
        {
            // The owner is closing.
        }
        catch (Exception ex)
        {
            if (querySession.IsStopping || !IsLoaded) return;
            StatusText.Text = hasSuccessfulResult ? "周期分析失败，保留上次成功结果" : "周期分析暂不可用";
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            analysisLoading = false;
            if (!querySession.IsStopping && IsLoaded)
            {
                ForecastPanel.SetLoading(false);
                if (bandSizeChangePending)
                {
                    bandSizeChangePending = false;
                    _ = LoadAnalysisAsync();
                }
            }
        }
    }

    private async void BandSizeSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (BandSizeSelector is null) return;
        selectedBandSizePercent = BandSizeSelector.SelectedIndex switch
        {
            0 => 1m,
            1 => 2m,
            2 => 5m,
            3 => 10m,
            _ => selectedBandSizePercent
        };
        if (!IsLoaded || querySession.IsStopping) return;
        StatusText.Text = $"正在按 {selectedBandSizePercent:N0}% 额度分段重新计算...";
        if (analysisLoading)
        {
            bandSizeChangePending = true;
            return;
        }
        await LoadAnalysisAsync();
    }

    private void ApplyResult(
        QuotaCycleAnalysisResult result,
        QuotaModelCapacityReport capacities)
    {
        displayedBandSizePercent = result.BandSizePercent;
        VolatilityCaptionText.Text = $"{displayedBandSizePercent:N0}% 分段波动";
        ApplyChartView();
        var selectedBand = (BandGrid.SelectedItem as QuotaCycleBandRow)?.Band;
        var selectedUsedMidpoint = selectedBand is null
            ? (decimal?)null
            : (selectedBand.UsedFromPercent + selectedBand.UsedToPercent) / 2m;
        CycleTitleText.Text = $"7d 周期 · {result.Period.PeriodStart:MM-dd HH:mm} → {result.Period.PeriodEnd:MM-dd HH:mm}";
        chart.SetData(result);
        ConsumptionTimelineView.SetData(result);
        ForecastPanel.SetData(result, capacities);
        if (!result.HasData)
        {
            bandRows = Array.Empty<QuotaCycleBandRow>();
            BandGrid.ItemsSource = bandRows;
            ModelShareList.ItemsSource = null;
            QuotaDropValue.Text = EquivalentCostValue.Text = FullEstimateValue.Text = VolatilityValue.Text = DominantModelValue.Text = "—";
            EquivalentCostNote.Text = FullEstimateNote.Text = VolatilityNote.Text = DominantModelNote.Text = "";
            ModelCapacityValue.Text = "样本不足";
            ModelTokenCapacityValue.Text = "样本不足，暂时无法估算满额 Token 用量";
            ModelTokenCapacityValue.ToolTip = null;
            InsightText.Text = "暂无可归因的分段";
            StatusText.Text = result.EmptyReason;
            QuotaDropNote.Text = result.EmptyReason;
            return;
        }

        QuotaDropValue.Text = $"{result.ObservedQuotaDropPercent:N1}%";
        QuotaDropNote.Text = $"{result.AlignedSampleCount:N0} 个模型/额度对齐点";
        EquivalentCostValue.Text = FormatMoney(result.EquivalentCost);
        EquivalentCostNote.Text = $"{result.Tokens / 1_000_000d:N3}M tokens";
        FullEstimateValue.Text = FormatMoney(result.EstimatedFullQuotaCost);
        FullEstimateNote.Text = result.Period.IsCurrent
            ? "含当前未跳点用量 · 与顶部 7d 同步"
            : "全部分段按额度跌幅加权";
        VolatilityValue.Text = $"{result.VolatilityPercent:N0}%";
        VolatilityNote.Text = $"{VolatilityLabel(result.VolatilityPercent)} · {FormatMoney(result.MinimumBandEstimate)}–{FormatMoney(result.MaximumBandEstimate)}";
        DominantModelValue.Text = QuotaCycleModelPalette.ShortName(result.CostDominantModel);
        DominantModelValue.Foreground = QuotaCycleModelPalette.GetBrush(result.CostDominantModel);
        DominantModelValue.ToolTip = result.CostDominantModel;

        var dominant = result.Models.OrderByDescending(item => item.EquivalentCost).FirstOrDefault();
        DominantModelNote.Text = dominant is null
            ? "没有已识别模型"
            : $"折算代价占比 {dominant.CostSharePercent:N0}% · {dominant.Tokens / 1_000_000d:N2}M tokens";

        ApplyModelCapacityEstimate(result, capacities);
        ApplyModelTokenCapacity(result, capacities);

        ModelShareList.ItemsSource = result.Models.OrderByDescending(item => item.EquivalentCost).Select(item => new QuotaCycleModelRow(
            QuotaCycleModelPalette.ShortName(item.ModelId),
            item.IsPriced ? $"{item.CostSharePercent:N1}%" : "未计价",
            (double)item.CostSharePercent,
            QuotaCycleModelPalette.GetBrush(item.ModelId),
            $"{item.ModelId}\n折算代价占比 {item.CostSharePercent:N2}% · {item.Tokens / 1_000_000d:N3}M tokens\n" +
            $"折算代价 {FormatMoney(item.EquivalentCost)}" + (item.IsPriced ? "" : " · 含未计价记录"))).ToList();

        var average = result.EstimatedFullQuotaCost ?? 0m;
        var consumptionRows = QuotaConsumptionTimelineCalculator.BuildBands(result)
            .ToDictionary(item => item.BandIndex, QuotaConsumptionSpeedRow.From);
        bandRows = result.Bands.Select(item => QuotaCycleBandRow.From(item, average, capacities.Estimates) with
        {
            Consumption = consumptionRows.GetValueOrDefault(item.BandIndex)
        }).ToList();
        BandGrid.ItemsSource = bandRows;
        if (bandRows.Count > 0)
        {
            BandGrid.SelectedItem = bandRows.FirstOrDefault(item => selectedUsedMidpoint is { } used &&
                item.Band.UsedFromPercent <= used && used < item.Band.UsedToPercent) ?? bandRows[0];
        }

        InsightText.Text = BuildInsight(result);
        StatusText.Text = $"已形成 {result.Bands.Count:N0} 个 {displayedBandSizePercent:N0}% 额度分段";
    }

    private void ApplyModelCapacityEstimate(
        QuotaCycleAnalysisResult result,
        QuotaModelCapacityReport report)
    {
        var values = report.Estimates.Select(item =>
        {
            var source = item.Source switch
            {
                QuotaModelCapacitySource.CurrentPeriodApproved => "本期核准",
                QuotaModelCapacitySource.CurrentPeriodBlended => item.HistoricalPeriodCount > 0
                    ? "历史回归+本期"
                    : "本期稳健回归",
                QuotaModelCapacitySource.PreviousPeriodApproved => "历史稳健值",
                _ => "本期混用推算"
            };
            var pureBandCount = Math.Max(0, item.BandCount - item.MixedBandCount);
            var sampleText = item.HistoricalPeriodCount > 0
                ? item.CurrentBandCount > 0
                    ? $"{item.HistoricalPeriodCount}期历史+{item.CurrentBandCount}段本期"
                    : $"{item.HistoricalPeriodCount}期历史"
                : item.CurrentBandCount > 0
                    ? $"{item.CurrentBandCount}段本期"
                    : item.MixedBandCount > 0
                    ? $"{pureBandCount}纯+{item.MixedBandCount}混"
                    : $"{item.BandCount}段";
            var range = item.MinimumFullQuotaCost == item.MaximumFullQuotaCost
                ? $"{sampleText}，{source}"
                : $"{FormatMoney(item.MinimumFullQuotaCost)}–{FormatMoney(item.MaximumFullQuotaCost)}，{sampleText}，{source}";
            return $"{QuotaCycleModelPalette.ShortName(item.ModelId)} ≈{FormatMoney(item.AverageFullQuotaCost)} [{range}]";
        });
        var estimatedModels = report.Estimates
            .Select(item => item.ModelId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var insufficient = result.Models
            .Where(item => item.EquivalentCost > 0m &&
                           Math.Round(item.CostSharePercent, 1, MidpointRounding.AwayFromZero) > 0m)
            .Select(item => CodexModelCost.NormalizeModelId(item.ModelId))
            .Where(item => !estimatedModels.Contains(item))
            .Take(2)
            .Select(item => $"{QuotaCycleModelPalette.ShortName(item)} 样本不足");
        var display = values.Concat(insufficient).ToList();
        ModelCapacityValue.Text = display.Count > 0
            ? $"模型 100% 动态估算（{report.PlanName}）：{string.Join("   ·   ", display)}"
            : "没有满足条件的单模型分段";
        ModelCapacityBadge.ToolTip =
            "同套餐、不重叠的往期校准按时间与样本量形成历史基准；单段历史需有超过 0.5 个百分点的模型额度归因。" +
            "本期可计价分段按模型成本联合回归更新。缺少可靠历史的模型按其余模型的历史估算推算，标记为“本期混用推算”，" +
            "区间包含其他模型估算范围及额度读数取整的影响，不代表独立实测或统计置信区间。";
    }

    private void ApplyModelTokenCapacity(
        QuotaCycleAnalysisResult result,
        QuotaModelCapacityReport capacities)
    {
        var report = QuotaModelTokenCapacityCalculator.Calculate(
            result, capacities, PriceSettingsStore.Current.CodexPresets);
        if (report.Rows.Count == 0)
        {
            ModelTokenCapacityValue.Text = report.UnavailableReason;
            ModelTokenCapacityValue.ToolTip = null;
            return;
        }
        var values = report.Rows.Select(item =>
            $"{QuotaCycleModelPalette.ShortName(item.ModelId)}：100% ≈ {item.FullQuotaMillionTokens:N1}M Token");
        var missing = report.UnestimatedModels.Select(model =>
            $"{QuotaCycleModelPalette.ShortName(model)}：样本不足或未计价");
        ModelTokenCapacityValue.Text = string.Join("   ·   ", values.Concat(missing));
        var uncached = TokenCountMath.SubtractNonNegative(
            TokenCountMath.SubtractNonNegative(report.InputTokens, report.CachedInputTokens),
            report.CacheWriteInputTokens);
        ModelTokenCapacityValue.ToolTip =
            $"统一使用本周期的 Token 构成（普通短上下文）：普通输入 {uncached / 1_000_000d:N3}M，" +
            $"缓存输入 {report.CachedInputTokens / 1_000_000d:N3}M，" +
            $"缓存写入 {report.CacheWriteInputTokens / 1_000_000d:N3}M，" +
            $"输出 {report.OutputTokens / 1_000_000d:N3}M。\n" +
            "100% 额度 Token 用量（M）= 该模型满额折算金额 ÷ 同构成每百万 Token 折算代价。\n" +
            string.Join("\n", report.Rows.Select(item =>
                $"{QuotaCycleModelPalette.ShortName(item.ModelId)}：{FormatMoney(item.FullQuotaCost)} ÷ " +
                $"${item.CostPerMillionTokens:N4}/M ≈ {item.FullQuotaMillionTokens:N1}M；" +
                $"样本估算范围 {item.MinimumMillionTokens:N1}–{item.MaximumMillionTokens:N1}M。")) +
            "\n缓存比例、输出比例或速度模式变化时，可用 Token 量也会变化；此处为同套餐样本估算。";
    }

    private void Chart_BandSelected(object? sender, QuotaCycleAnalysisBand band)
    {
        var row = bandRows.FirstOrDefault(item => item.Band.BandIndex == band.BandIndex);
        if (row is null) return;
        BandGrid.SelectedItem = row;
        BandGrid.ScrollIntoView(row);
    }

    private void BandGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        chart.SelectedBandIndex = (BandGrid.SelectedItem as QuotaCycleBandRow)?.Band.BandIndex;
        ConsumptionTimelineView.SelectBand(chart.SelectedBandIndex);
    }

    private void ChartViewTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ReferenceEquals(e.Source, ChartViewTabs)) ApplyChartView();
    }

    private void ApplyChartView()
    {
        // SelectionChanged can run before later XAML elements have been constructed.
        if (ChartHost is null || ConsumptionTimelineView is null || TimeRangeColumn is null || BandDescriptionText is null || ForecastCard is null) return;
        var showTimeline = ChartViewTabs.SelectedIndex == 1;
        var timelineVisibility = showTimeline ? Visibility.Visible : Visibility.Collapsed;
        var costVisibility = showTimeline ? Visibility.Collapsed : Visibility.Visible;
        ChartHost.Visibility = costVisibility;
        ConsumptionTimelineView.Visibility = timelineVisibility;
        ForecastCard.Visibility = timelineVisibility;
        ModelCompositionPanel.Visibility = costVisibility;
        TimeRangeColumn.Visibility = costVisibility;
        ConsumptionTimeColumn.Visibility = timelineVisibility;
        ConsumptionDurationColumn.Visibility = timelineVisibility;
        ConsumptionRateColumn.Visibility = timelineVisibility;
        ChartDescriptionText.Text = showTimeline
            ? "实际时间 × 剩余额度 100% → 0% · 悬停查看时间 · 点击联动下方明细"
            : "额度位置 × 局部换算代价 · 上方色带为主导模型 · 点击联动下方明细";
        BandDescriptionText.Text = showTimeline
            ? $"同一组 {displayedBandSizePercent:N0}% 分段 · 消耗时段按首次跨刻度对齐（含插值）· 段平均速度包含空闲"
            : $"每行按 {displayedBandSizePercent:N0}% 额度带聚合；模型满额校准固定按 5% 分段；换算 100% 是实测外推";
    }

    private static string BuildInsight(QuotaCycleAnalysisResult result)
    {
        var high = result.Bands.MaxBy(item => item.EstimatedFullQuotaCost ?? decimal.MinValue)!;
        var low = result.Bands.MinBy(item => item.EstimatedFullQuotaCost ?? decimal.MaxValue)!;
        var highModel = QuotaCycleModelPalette.ShortName(high.CostDominantModel);
        var lowModel = QuotaCycleModelPalette.ShortName(low.CostDominantModel);
        return
            $"最高：剩余 {high.RemainingFromPercent:N0}→{high.RemainingToPercent:N0}% 主要用 {highModel}，100%≈{FormatMoney(high.EstimatedFullQuotaCost)}。\n" +
            $"最低：剩余 {low.RemainingFromPercent:N0}→{low.RemainingToPercent:N0}% 主要用 {lowModel}，100%≈{FormatMoney(low.EstimatedFullQuotaCost)}。";
    }

    private static string VolatilityLabel(decimal coefficient) => coefficient switch
    {
        < 15m => "较稳定",
        < 30m => "中等波动",
        _ => "高波动"
    };

    private static string FormatMoney(decimal? value)
    {
        if (value is null) return "-";
        return value.Value switch
        {
            >= 100 => $"${value.Value:N0}",
            >= 10 => $"${value.Value:N1}",
            >= 1 => $"${value.Value:N2}",
            _ => $"${value.Value:N3}"
        };
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}

internal sealed record QuotaCycleModelRow(
    string Model,
    string Share,
    double ShareValue,
    Brush Accent,
    string Detail);

internal sealed record QuotaCycleBandRow(
    QuotaCycleAnalysisBand Band,
    string QuotaRange,
    string TimeRange,
    string ModelMix,
    string ModelTokens,
    string Tokens,
    string EquivalentCost,
    string FullEstimate,
    string FittedEstimate,
    string FitDifference,
    string Difference,
    string Reliability)
{
    public QuotaConsumptionSpeedRow? Consumption { get; init; }
    public string ConsumptionTimeRange => Consumption?.TimeRange ?? "未对齐";
    public string ConsumptionDuration => Consumption?.Duration ?? "—";
    public string ConsumptionRate => Consumption?.Rate ?? "—";
    public decimal? ConsumptionRateValue => Consumption?.RateValue;
    public DateTimeOffset? ConsumptionStart => Consumption?.Band.StartLocal;
    public TimeSpan? ConsumptionElapsed => Consumption?.Elapsed;

    public static QuotaCycleBandRow From(
        QuotaCycleAnalysisBand band,
        decimal average,
        IReadOnlyCollection<QuotaModelCapacityEstimate> capacities)
    {
        var estimate = band.EstimatedFullQuotaCost ?? 0m;
        var fitted = QuotaModelCapacityEstimator.FitFullQuotaCost(band, capacities);
        var fitDifference = estimate <= 0m || fitted is not > 0m
            ? (decimal?)null
            : (estimate / fitted.Value - 1m) * 100m;
        var difference = average <= 0m ? 0m : (estimate / average - 1m) * 100m;
        var mix = string.Join(" / ", band.Models.OrderByDescending(item => item.EquivalentCost).Select(item =>
            $"{QuotaCycleModelPalette.ShortName(item.ModelId)} " +
            (item.IsPriced ? $"{item.CostSharePercent:N0}%" : "未计价")));
        var modelTokens = string.Join(" / ", band.Models.OrderByDescending(item => item.EquivalentCost).Select(item =>
            $"{QuotaCycleModelPalette.ShortName(item.ModelId)} {item.Tokens / 1_000_000d:N3}M"));
        return new QuotaCycleBandRow(
            band,
            $"{band.RemainingFromPercent:N1} → {band.RemainingToPercent:N1}%",
            $"{band.StartLocal:MM-dd HH:mm} – {band.EndLocal:MM-dd HH:mm}",
            mix,
            modelTokens,
            $"{band.Tokens / 1_000_000d:N3}M",
            FormatMoney(band.EquivalentCost),
            FormatMoney(band.EstimatedFullQuotaCost),
            FormatMoney(fitted),
            fitDifference is null ? "-" : $"{fitDifference.Value:+0;-0;0}%",
            $"{difference:+0;-0;0}%",
            band.QuotaDropPercent >= 4m ? "较稳" : band.QuotaDropPercent >= 2m ? "参考" : "小样本");
    }

    private static string FormatMoney(decimal? value)
    {
        if (value is null) return "-";
        return value.Value switch
        {
            >= 100 => $"${value.Value:N0}",
            >= 10 => $"${value.Value:N1}",
            >= 1 => $"${value.Value:N2}",
            _ => $"${value.Value:N3}"
        };
    }
}
