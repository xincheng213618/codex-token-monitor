using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
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
    private readonly DispatcherTimer refreshTimer = new() { Interval = TimeSpan.FromMinutes(1) };
    private IReadOnlyList<QuotaCycleBandRow> bandRows = Array.Empty<QuotaCycleBandRow>();
    private bool analysisLoading;
    private bool hasSuccessfulResult;
    private bool bandSizeChangePending;
    private decimal selectedBandSizePercent = 1m;
    private decimal displayedBandSizePercent = 1m;

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
        RefreshAnalysisButton.ToolTip = period.IsCurrent
            ? "当前周期每分钟自动更新，也可立即刷新；显示 0% 后仍继续统计。"
            : "重新读取此周期的用量记录";
        refreshTimer.Tick += RefreshTimer_Tick;
        ForecastPanel.PriceGroup = priceGroup;
        ChartHost.Content = chart;
        chart.BandSelected += Chart_BandSelected;
        ConsumptionTimelineView.BandSelected += Chart_BandSelected;
        ForecastPanel.ForecastChanged += ConsumptionTimelineView.SetForecast;
        ForecastPanel.RefreshRequested += async (_, _) => await LoadAnalysisAsync();
        ApplyChartView();

        CycleTitleText.Text = $"{cycleTitle ?? "7d 周期"} · {period.PeriodStart:MM-dd HH:mm} → {period.PeriodEnd:MM-dd HH:mm}";
        CycleMetaText.Text = $"重置 {period.ResetAt:yyyy-MM-dd HH:mm} · {period.SnapshotCount:N0} 个额度快照";

        Loaded += async (_, _) =>
        {
            if (period.IsCurrent) refreshTimer.Start();
            await LoadAnalysisAsync();
        };
        Closed += (_, _) =>
        {
            refreshTimer.Stop();
            querySession.Dispose();
        };
    }

    private Task LoadAnalysisAsync() => LoadAnalysisCoreAsync(showErrors: true);

    private async void RefreshTimer_Tick(object? sender, EventArgs e)
    {
        if (IsVisible && WindowState != WindowState.Minimized)
            await LoadAnalysisCoreAsync(showErrors: false);
    }

    private async Task LoadAnalysisCoreAsync(bool showErrors)
    {
        if (analysisLoading || querySession.IsStopping) return;
        analysisLoading = true;
        RefreshAnalysisButton.IsEnabled = false;
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
                    if (preview.Value.Capacities.Estimates.Count == 0)
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
            StatusText.Text += $" · 已更新 {BeijingClock.Now:HH:mm:ss}" +
                               (refreshTimer.IsEnabled ? " · 每分钟自动刷新" : "");
        }
        catch (OperationCanceledException)
        {
            // The owner is closing.
        }
        catch (Exception ex)
        {
            if (querySession.IsStopping || !IsLoaded) return;
            StatusText.Text = hasSuccessfulResult ? "周期分析失败，保留上次成功结果" : "周期分析暂不可用";
            if (showErrors) MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            analysisLoading = false;
            if (!querySession.IsStopping && IsLoaded)
            {
                ForecastPanel.SetLoading(false);
                RefreshAnalysisButton.IsEnabled = true;
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
        if (!result.Period.IsCurrent) refreshTimer.Stop();
        ApplyPostZeroUsage(result);
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
            EquivalentCostNote.ToolTip = null;
            if (result.PostZeroUsage is not null)
            {
                EquivalentCostValue.Text = FormatMoney(result.EquivalentCost);
                EquivalentCostNote.Text = $"{result.Tokens / 1_000_000d:N3}M tokens";
                bandRows = new[] { QuotaCycleBandRow.FromPostZero(result.PostZeroUsage, result.BandSizePercent, result.Period.IsCurrent) };
                BandGrid.ItemsSource = bandRows;
                BandGrid.SelectedItem = bandRows[0];
                StatusText.Text += " · 0% 后用量继续记录";
            }
            return;
        }

        QuotaDropValue.Text = $"{result.ObservedQuotaDropPercent:N1}%";
        QuotaDropNote.Text = $"{result.AlignedSampleCount:N0} 个模型/额度对齐点";
        EquivalentCostValue.Text = FormatMoney(result.EquivalentCost);
        EquivalentCostNote.Text = $"{result.Tokens / 1_000_000d:N3}M tokens";
        EquivalentCostNote.ToolTip = result.UnattributedTokens > 0
            ? $"包含显示 0% 后的 {result.UnattributedTokens / 1_000_000d:N3}M Token / {FormatMoney(result.UnattributedCost)}；总用量继续累计，这部分没有可观测的百分比变化，不参与额度分段和拟合。"
            : null;
        FullEstimateValue.Text = FormatMoney(result.EstimatedFullQuotaCost);
        FullEstimateNote.Text = result.Period.IsCurrent
            ? "含当前未跳点用量 · 与顶部 7d 同步"
            : "全部分段按额度跌幅加权";
        if (result.PostZeroUsage is not null) FullEstimateNote.Text = "按可见额度跌幅 · 0% 后单独统计";
        VolatilityValue.Text = $"{result.VolatilityPercent:N0}%";
        VolatilityNote.Text = $"{VolatilityLabel(result.VolatilityPercent)} · {FormatMoney(result.MinimumBandEstimate)}–{FormatMoney(result.MaximumBandEstimate)}";
        DominantModelValue.Text = QuotaCycleModelPalette.ShortName(result.CostDominantModel);
        DominantModelValue.Foreground = QuotaCycleModelPalette.GetBrush(result.CostDominantModel);
        DominantModelValue.ToolTip = result.CostDominantModel;

        var dominant = result.Models.OrderByDescending(item => item.EquivalentCost).FirstOrDefault();
        DominantModelNote.Text = dominant is null
            ? "没有已识别模型"
            : $"折算代价占比 {dominant.CostSharePercent:N0}% · {dominant.Tokens / 1_000_000d:N2}M tokens";

        var tokenCapacities = QuotaModelTokenCapacityCalculator.Calculate(
            result, capacities, PriceSettingsStore.Current.CodexPresets);
        ApplyModelCapacityEstimate(result, capacities, tokenCapacities);
        ApplyModelTokenCapacity(tokenCapacities);

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
        }).Concat(result.PostZeroUsage is { } tail
            ? new[] { QuotaCycleBandRow.FromPostZero(tail, result.BandSizePercent, result.Period.IsCurrent) }
            : Array.Empty<QuotaCycleBandRow>()).ToList();
        BandGrid.ItemsSource = bandRows;
        if (bandRows.Count > 0)
        {
            BandGrid.SelectedItem = bandRows.FirstOrDefault(item => item.IsPostZero && selectedBand?.QuotaDropPercent == 0m) ??
                bandRows.FirstOrDefault(item => selectedUsedMidpoint is { } used &&
                item.Band.UsedFromPercent <= used && used < item.Band.UsedToPercent) ?? bandRows[0];
        }

        InsightText.Text = BuildInsight(result);
        StatusText.Text = $"已形成 {result.Bands.Count:N0} 个 {displayedBandSizePercent:N0}% 额度分段" +
                          (result.PostZeroUsage is not null ? $" + 0% 后阶段，共 {bandRows.Count:N0} 级" : "");
    }

    private void ApplyPostZeroUsage(QuotaCycleAnalysisResult result)
    {
        var tail = result.PostZeroUsage;
        PostZeroUsagePanel.Visibility = tail is null ? Visibility.Collapsed : Visibility.Visible;
        if (tail is null) return;
        PostZeroUsageValue.Text = $"第 {(int)Math.Ceiling(100m / result.BandSizePercent) + 1} 级 · 0% → 结束" +
            (result.Period.IsCurrent ? "（记录中）" : "（截至周期结束）") +
            $" · 继续使用 {tail.Tokens / 1_000_000d:N3}M Token · {FormatMoney(tail.EquivalentCost)}";
        var models = string.Join(" / ", tail.Models.Select(item =>
            $"{QuotaCycleModelPalette.ShortName(item.ModelId)} {item.Tokens / 1_000_000d:N3}M" +
            (item.IsPriced ? "" : "（未计价）")));
        PostZeroUsageNote.Text = $"{tail.StartLocal:MM-dd HH:mm} 显示 0% → 最后用量记录 {tail.EndLocal:MM-dd HH:mm}" +
            $" · 经过 {QuotaConsumptionSpeedRow.FormatDuration(tail.EndLocal - tail.StartLocal)}（含空闲）" +
            (models.Length > 0 ? $" · {models}" : " · 暂未记录到后续用量") +
            "\n显示 0% 不代表实际已无法继续使用；后续用量计入总量，实际还可用多久暂不可估。";
    }

    private void ApplyModelCapacityEstimate(
        QuotaCycleAnalysisResult result,
        QuotaModelCapacityReport report,
        QuotaModelTokenCapacityReport tokenCapacities)
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
            .Select(item => QuotaModelTokenCapacityCalculator.ModelKey(item.ModelId))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var extrapolations = tokenCapacities.Rows.Where(item => item.IsShareExtrapolation).ToList();
        estimatedModels.UnionWith(extrapolations.Select(item => item.ModelId));
        var referenceValues = extrapolations.Select(item =>
            $"{QuotaCycleModelPalette.ShortName(item.ModelId)} ≈{FormatMoney(item.FullQuotaCost)} [本期占比外推，仅供参考]");
        var insufficient = result.Models
            .Where(item => item.EquivalentCost > 0m &&
                           Math.Round(item.CostSharePercent, 1, MidpointRounding.AwayFromZero) > 0m)
            .Select(item => QuotaModelTokenCapacityCalculator.ModelKey(item.ModelId))
            .Where(item => !estimatedModels.Contains(item))
            .Take(2)
            .Select(item => $"{QuotaCycleModelPalette.ShortName(item)} 样本不足");
        var display = values.Concat(referenceValues).Concat(insufficient).ToList();
        ModelCapacityValue.Text = display.Count > 0
            ? $"模型 100% 动态估算（{report.PlanName}）：{string.Join("   ·   ", display)}"
            : "没有满足条件的单模型分段";
        ModelCapacityBadge.ToolTip =
            "同套餐、不重叠的往期校准按时间与样本量形成历史基准；单段历史需有超过 0.5 个百分点的模型额度归因。" +
            "本期可计价分段按模型成本联合回归更新。缺少可靠历史的模型按其余模型的历史估算推算，标记为“本期混用推算”，" +
            "区间包含其他模型估算范围及额度读数取整的影响，不代表独立实测或统计置信区间。" +
            "尚无校准的模型按本期折算代价 ÷ 归因额度降幅 × 100% 外推，标为“本期占比外推”，仅供参考，不纳入历史校准。";
    }

    private void ApplyModelTokenCapacity(QuotaModelTokenCapacityReport report)
    {
        if (report.Rows.Count == 0 && report.UnestimatedModels.Count == 0)
        {
            ModelTokenCapacityValue.Text = report.UnavailableReason;
            ModelTokenCapacityValue.ToolTip = null;
            return;
        }
        var values = report.Rows.Select(item =>
            $"{QuotaCycleModelPalette.ShortName(item.ModelId)}：100% ≈ {item.FullQuotaMillionTokens:N1}M Token" +
            (item.IsShareExtrapolation ? "（本期占比外推）" : ""));
        var missing = report.UnestimatedModels.Select(model =>
            $"{QuotaCycleModelPalette.ShortName(model.ModelId)}：{model.Reason switch
            {
                QuotaModelTokenCapacityMissingReason.MissingPrice => "未设置可用价格",
                QuotaModelTokenCapacityMissingReason.InsufficientCalibration => "已计价，暂无额度归因或校准",
                QuotaModelTokenCapacityMissingReason.MissingTokenMix => "缺少 Token 构成",
                QuotaModelTokenCapacityMissingReason.ZeroTokenCost => "当前 Token 构成的折算代价为 0",
                _ => "估算超出可用范围"
            }}");
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
                (item.IsShareExtrapolation
                    ? $"本期归因额度降幅 {item.AttributedQuotaDropPercent:N3}%，占比外推，仅供参考，非独立校准。"
                    : $"样本估算范围 {item.MinimumMillionTokens:N1}–{item.MaximumMillionTokens:N1}M。"))) +
            "\n缓存比例、输出比例或速度模式变化时，可用 Token 量也会变化；此处为同套餐样本估算。" +
            "本期占比外推只作参考；缺少价格或可用归因额度降幅时暂不显示数值，其已有用量仍保留在模型构成中。";
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
            : $"每行按 {displayedBandSizePercent:N0}% 额度带聚合；拟合按模型代价联合回归，偏离为相对拟合差值；0% 后用量单独统计";
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
    private async void RefreshAnalysisButton_Click(object sender, RoutedEventArgs e) => await LoadAnalysisAsync();
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
    public int Level => Band.BandIndex + 1;
    public bool IsPostZero { get; init; }
    public QuotaConsumptionSpeedRow? Consumption { get; init; }
    public string ConsumptionTimeRange => Consumption?.TimeRange ?? "未对齐";
    public string ConsumptionDuration => Consumption?.Duration ?? "—";
    public string ConsumptionRate => Consumption?.Rate ?? "—";
    public decimal? ConsumptionRateValue => Consumption?.RateValue;
    public DateTimeOffset? ConsumptionStart => Consumption?.Band.StartLocal;
    public TimeSpan? ConsumptionElapsed => Consumption?.Elapsed;
    public string FitDetail { get; init; } = "";

    public static QuotaCycleBandRow FromPostZero(QuotaCyclePostZeroUsage tail, decimal bandSizePercent, bool isCurrent)
    {
        var band = tail.ToDisplayBand(bandSizePercent);
        var row = From(band, 0m, Array.Empty<QuotaModelCapacityEstimate>());
        return row with
        {
            IsPostZero = true,
            QuotaRange = "0% → 结束",
            FullEstimate = "—", FittedEstimate = "—", FitDifference = "—", Difference = "—",
            Reliability = isCurrent ? "记录中" : "周期结束",
            FitDetail = "这一阶段持续记录显示 0% 后的实际用量；截至最后用量记录，不代表已经无法使用。没有百分比变化，不计算 100% 折算或拟合。",
            Consumption = QuotaConsumptionSpeedRow.From(band) with { Rate = "—", RateValue = null }
        };
    }

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
        var capacityModels = capacities.Select(item => CodexModelCost.ModelKey(item.ModelId))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missingModels = band.Models.Where(item => item.EquivalentCost > 0m &&
                !capacityModels.Contains(CodexModelCost.ModelKey(item.ModelId)))
            .Select(item => QuotaCycleModelPalette.ShortName(item.ModelId));
        var fitDetail = fitted is not null
            ? "拟合满额 = 1 ÷ Σ（模型代价占比 ÷ 模型满额校准）；偏离 =（换算满额 ÷ 拟合满额 − 1）× 100%。这是拟合残差，当前分段也可参与回归。"
            : band.Models.Any(item => !item.IsPriced && item.QuotaSharePercent > 0m)
                ? "此分段包含未计价模型，无法计算完整拟合。"
                : $"缺少模型校准：{string.Join("、", missingModels)}。占比外推参考值不作为拟合基准。";
        return new QuotaCycleBandRow(
            band,
            $"{band.RemainingFromPercent:N1} → {band.RemainingToPercent:N1}%",
            $"{band.StartLocal:MM-dd HH:mm} – {band.EndLocal:MM-dd HH:mm}",
            mix,
            modelTokens,
            $"{band.Tokens / 1_000_000d:N3}M",
            FormatMoney(band.EquivalentCost),
            FormatMoney(band.EstimatedFullQuotaCost),
            fitted is null ? "待校准" : FormatMoney(fitted),
            fitDifference is null ? "待校准" : $"{fitDifference.Value:+0;-0;0}%",
            $"{difference:+0;-0;0}%",
            band.QuotaDropPercent >= 4m ? "较稳" : band.QuotaDropPercent >= 2m ? "参考" : "小样本")
        {
            FitDetail = fitDetail
        };
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
