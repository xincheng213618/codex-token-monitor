using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using MessageBox = System.Windows.MessageBox;

namespace CodexTokenMonitor;

public partial class QuotaCycleAnalysisWindow : Window
{
    private readonly CodexQuotaCycle period;
    private readonly QuotaCycleAnalysisChart chart = new();
    private readonly CancellationTokenSource loadCancellation = new();
    private IReadOnlyList<QuotaCycleBandRow> bandRows = Array.Empty<QuotaCycleBandRow>();

    internal QuotaCycleAnalysisWindow(CodexQuotaCycle period)
    {
        this.period = period;
        InitializeComponent();
        ChartHost.Content = chart;
        chart.BandSelected += Chart_BandSelected;

        var plan = SubscriptionPlanStore.Summarize(period.PeriodStart, period.PeriodEnd);
        CycleTitleText.Text = $"7d 周期 · {period.PeriodStart:MM-dd HH:mm} → {period.PeriodEnd:MM-dd HH:mm}";
        CycleMetaText.Text = $"重置 {period.ResetAt:yyyy-MM-dd HH:mm} · {period.SnapshotCount:N0} 个额度快照" +
                             (plan.HasRecords ? $" · {plan.PlanNames}" : " · 未设置套餐");

        Loaded += async (_, _) => await LoadAnalysisAsync();
        Closed += (_, _) =>
        {
            loadCancellation.Cancel();
            loadCancellation.Dispose();
        };
    }

    private async Task LoadAnalysisAsync()
    {
        var cancellationToken = loadCancellation.Token;
        try
        {
            StatusText.Text = "正在对齐模型记录与额度时间线...";
            var result = await Task.Run(
                () => QuotaCycleAnalysisCalculator.Build(period, cancellationToken),
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsLoaded) return;

            ApplyResult(result);
        }
        catch (OperationCanceledException)
        {
            // The owner is closing.
        }
        catch (Exception ex)
        {
            if (!IsLoaded) return;
            StatusText.Text = "周期分析失败";
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ApplyResult(QuotaCycleAnalysisResult result)
    {
        chart.SetData(result);
        if (!result.HasData)
        {
            StatusText.Text = result.EmptyReason;
            QuotaDropNote.Text = result.EmptyReason;
            return;
        }

        QuotaDropValue.Text = $"{result.ObservedQuotaDropPercent:N1}%";
        QuotaDropNote.Text = $"{result.AlignedSampleCount:N0} 个模型/额度对齐点";
        EquivalentCostValue.Text = FormatMoney(result.EquivalentCost);
        EquivalentCostNote.Text = $"{result.Tokens / 1_000_000d:N3}M tokens";
        FullEstimateValue.Text = FormatMoney(result.EstimatedFullQuotaCost);
        FullEstimateNote.Text = "全部分段按额度跌幅加权";
        VolatilityValue.Text = $"{result.VolatilityPercent:N0}%";
        VolatilityNote.Text = $"{VolatilityLabel(result.VolatilityPercent)} · {FormatMoney(result.MinimumBandEstimate)}–{FormatMoney(result.MaximumBandEstimate)}";
        DominantModelValue.Text = QuotaCycleModelPalette.ShortName(result.DominantModel);
        DominantModelValue.Foreground = QuotaCycleModelPalette.GetBrush(result.DominantModel);
        DominantModelValue.ToolTip = result.DominantModel;

        var dominant = result.Models.FirstOrDefault();
        DominantModelNote.Text = dominant is null
            ? "没有已识别模型"
            : $"归因额度 {dominant.QuotaSharePercent:N0}% · {dominant.Tokens / 1_000_000d:N2}M tokens";

        ModelShareList.ItemsSource = result.Models.Select(item => new QuotaCycleModelRow(
            QuotaCycleModelPalette.ShortName(item.ModelId),
            $"{item.QuotaSharePercent:N1}%",
            (double)item.QuotaSharePercent,
            QuotaCycleModelPalette.GetBrush(item.ModelId),
            $"{item.ModelId}\n归因额度 {item.QuotaDropPercent:N2}% · {item.Tokens / 1_000_000d:N3}M tokens\n" +
            $"折算代价 {FormatMoney(item.EquivalentCost)}" + (item.IsPriced ? "" : " · 含未计价记录"))).ToList();

        var average = result.EstimatedFullQuotaCost ?? 0m;
        bandRows = result.Bands.Select(item => QuotaCycleBandRow.From(item, average)).ToList();
        BandGrid.ItemsSource = bandRows;
        if (bandRows.Count > 0)
        {
            BandGrid.SelectedIndex = 0;
        }

        InsightText.Text = BuildInsight(result);
        StatusText.Text = $"已形成 {result.Bands.Count:N0} 个 5% 额度分段";
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
    }

    private static string BuildInsight(QuotaCycleAnalysisResult result)
    {
        var high = result.Bands.MaxBy(item => item.EstimatedFullQuotaCost ?? decimal.MinValue)!;
        var low = result.Bands.MinBy(item => item.EstimatedFullQuotaCost ?? decimal.MaxValue)!;
        var highModel = QuotaCycleModelPalette.ShortName(high.DominantModel);
        var lowModel = QuotaCycleModelPalette.ShortName(low.DominantModel);
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
    string DominantModel,
    string ModelMix,
    string QuotaDrop,
    string Tokens,
    string EquivalentCost,
    string FullEstimate,
    string Difference,
    string Reliability)
{
    public static QuotaCycleBandRow From(QuotaCycleAnalysisBand band, decimal average)
    {
        var estimate = band.EstimatedFullQuotaCost ?? 0m;
        var difference = average <= 0m ? 0m : (estimate / average - 1m) * 100m;
        var mix = string.Join(" / ", band.Models.Take(3).Select(item =>
            $"{QuotaCycleModelPalette.ShortName(item.ModelId)} {item.QuotaSharePercent:N0}%"));
        return new QuotaCycleBandRow(
            band,
            $"{band.RemainingFromPercent:N1} → {band.RemainingToPercent:N1}%",
            $"{band.StartLocal:MM-dd HH:mm} – {band.EndLocal:MM-dd HH:mm}",
            QuotaCycleModelPalette.ShortName(band.DominantModel),
            mix,
            $"{band.QuotaDropPercent:N2}%",
            $"{band.Tokens / 1_000_000d:N3}M",
            FormatMoney(band.EquivalentCost),
            FormatMoney(band.EstimatedFullQuotaCost),
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
