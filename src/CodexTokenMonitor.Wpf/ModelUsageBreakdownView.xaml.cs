using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace CodexTokenMonitor;

public partial class ModelUsageBreakdownView : UserControl
{
    private bool compact;
    private string costLabel = "API 等价费用";
    private bool hasUnpricedUsage;

    public ModelUsageBreakdownView() => InitializeComponent();

    internal void Apply(ModelUsageBreakdown? breakdown, UsageSource source)
    {
        var selectedModel = ModelGrid.SelectedValue as string;
        var sortedColumn = ModelGrid.Columns.FirstOrDefault(column => column.SortDirection.HasValue);
        var sort = ModelGrid.Items.SortDescriptions.ToArray();
        var gridScroll = ModelGrid.IsLoaded ? ModelGrid.Template?.FindName("DG_ScrollViewer", ModelGrid) as ScrollViewer : null;
        var verticalOffset = gridScroll?.VerticalOffset ?? 0;
        var totals = breakdown?.CostTotals ?? [];
        var repeatedSymbols = totals.GroupBy(total => total.CurrencySymbol, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var hasMixedUnits = breakdown?.HasMixedCostUnits == true;
        hasUnpricedUsage = breakdown?.HasUnpricedUsage == true;
        var rows = breakdown?.Rows.Select(row => new ModelRow(row.ModelId, QuotaCycleModelPalette.GetBrush(row.ModelId),
            row.Tokens, row.TokenShare,
            row.Costs.Count > 0
                ? string.Join(" + ", row.Costs.Select(cost => FormatCost(cost.Amount, cost.CurrencySymbol, cost.UnitLabel)))
                : "待计价",
            hasMixedUnits ? null : row.Costs.SingleOrDefault()?.Amount,
            hasMixedUnits ? null : row.Costs.SingleOrDefault()?.Share)).ToList() ?? [];

        TokenTotal.Text = breakdown is null ? "—" : UsageDisplayFormatting.TokenAdaptive(breakdown.TotalTokens);
        costLabel = source == UsageSource.Kimi ? "参考费用" : "API 等价费用";
        CostTotal.Text = totals.Count == 0 ? "—" : string.Join(" + ",
            totals.Select(total => FormatCost(total.Amount, total.CurrencySymbol, total.UnitLabel)));

        var notes = new List<string>();
        if (hasUnpricedUsage && breakdown is not null)
        {
            var pending = breakdown.UnpricedTokens > 0
                ? $"{UsageDisplayFormatting.TokenAdaptive(breakdown.UnpricedTokens)} Token"
                : $"{breakdown.UnpricedEvents:N0} 条记录";
            notes.Add(totals.Count > 0 && !hasMixedUnits
                ? $"{pending} 待计价，费用占比按已计价金额计算。"
                : $"{pending} 待计价。");
        }
        if (hasMixedUnits) notes.Add("费用按币种和计价单位分别列示。");
        CoverageNote.Text = string.Join(" ", notes);
        ApplyCoverageLabel();

        ApplyStrip(TokenShareStrip, rows.Select(row => (row, (double?)row.TokenShare, row.TokenText)));
        ApplyStrip(CostShareStrip, rows.Select(row => (row, row.CostShare, row.CostText)));
        CostColumn.CanUserSort = !hasMixedUnits;
        CostShareColumn.CanUserSort = !hasMixedUnits;
        var sortDirection = sortedColumn?.SortDirection;
        var view = new ListCollectionView(rows);
        foreach (var description in sort)
        {
            if (hasMixedUnits && description.PropertyName is nameof(ModelRow.CostAmount) or nameof(ModelRow.CostShare)) continue;
            view.SortDescriptions.Add(description);
        }
        ModelGrid.ItemsSource = view;
        if (sortedColumn?.CanUserSort == true) sortedColumn.SortDirection = sortDirection;
        if (selectedModel is not null) ModelGrid.SelectedValue = selectedModel;
        if (verticalOffset > 0) gridScroll?.ScrollToVerticalOffset(verticalOffset);
        UpdateHighlight();

        string FormatCost(decimal amount, string symbol, string unit)
        {
            var belowDisplayPrecision = amount is > 0m and < .0001m;
            var formatted = belowDisplayPrecision
                ? "<" + ModelCostEstimate.FormatAmount(.0001m, symbol, "N4")
                : ModelCostEstimate.FormatAmount(amount, symbol, amount is > 0m and < .01m ? "N4" : "N2");
            return repeatedSymbols.Contains(symbol) ? $"{formatted} [{unit}]" : formatted;
        }
    }

    internal void ClearModelSelection() => ModelGrid.SelectedIndex = -1;

    private void View_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var compactLayout = e.NewSize.Height < 156;
        if (compactLayout == compact) return;
        compact = compactLayout;
        Grid.SetColumnSpan(TokenMix, compact ? 1 : 2);
        Grid.SetColumnSpan(CostMix, compact ? 1 : 2);
        Grid.SetColumn(CostMix, compact ? 1 : 0);
        Grid.SetRow(CostMix, compact ? 0 : 1);
        TokenMix.Height = CostMix.Height = compact ? 23 : 30;
        TokenMix.Margin = compact ? new Thickness(0, 0, 9, 0) : new Thickness(0);
        CostMix.Margin = compact ? new Thickness(9, 0, 0, 0) : new Thickness(0);
        TokenLabelWidth.Width = CostLabelWidth.Width = new GridLength(compact ? 148 : 200);
        CompositionLayout.Margin = new Thickness(0, 2, 0, compact ? 4 : 9);
        ModelGrid.ColumnHeaderHeight = compact ? 24 : 32;
        ModelGrid.RowHeight = compact ? 22 : 28;
        ApplyCoverageLabel();
    }

    private void ApplyCoverageLabel()
    {
        CostCaption.Text = compact && hasUnpricedUsage ? "已计价费用" : costLabel;
        var description = CoverageNote.Text;
        CostCaption.ToolTip = description.Length > 0 ? description : null;
        AutomationProperties.SetHelpText(CostCaption, description);
        CoverageNote.Visibility = compact || description.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ApplyStrip(Grid strip, IEnumerable<(ModelRow Row, double? Share, string Value)> segments)
    {
        strip.Children.Clear();
        strip.ColumnDefinitions.Clear();
        var visible = segments.Where(segment => segment.Share is > 0d).ToArray();
        for (var index = 0; index < visible.Length; index++)
        {
            var (row, share, value) = visible[index];
            var left = index == 0 ? 5 : 0;
            var right = index == visible.Length - 1 ? 5 : 0;
            var segment = new Border
            {
                Background = row.Color,
                CornerRadius = new CornerRadius(left, right, right, left),
                Cursor = Cursors.Hand,
                Tag = row.ModelId,
                ToolTip = $"{row.Model} · {value} · {FormatShare(share)}"
            };
            AutomationProperties.SetName(segment, (string)segment.ToolTip);
            segment.MouseLeftButtonDown += (_, e) =>
            {
                if (ReferenceEquals(ModelGrid.SelectedItem, row)) ClearModelSelection();
                else
                {
                    ModelGrid.SelectedItem = row;
                    ModelGrid.ScrollIntoView(row);
                }
                e.Handled = true;
            };
            strip.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(share!.Value, GridUnitType.Star) });
            Grid.SetColumn(segment, index);
            strip.Children.Add(segment);
        }
    }

    private void ModelGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateHighlight();

    private void ModelGrid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || ModelGrid.SelectedIndex < 0) return;
        ClearModelSelection();
        e.Handled = true;
    }

    private void UpdateHighlight()
    {
        var selected = ModelGrid.SelectedValue as string;
        foreach (var segment in TokenShareStrip.Children.OfType<Border>().Concat(CostShareStrip.Children.OfType<Border>()))
            segment.Opacity = selected is null || Equals(segment.Tag, selected) ? 1 : .26;
    }

    private static string FormatShare(double? share) => share switch
    {
        null => "—",
        > 0 and < .001 => "<0.1%",
        _ => (share.Value * 100).ToString("0.0", CultureInfo.CurrentCulture) + "%"
    };

    private sealed record ModelRow(string ModelId, Brush Color, long Tokens, double TokenShare, string CostText,
        decimal? CostAmount, double? CostShare)
    {
        public string Model => ModelId.Length > 0 ? ModelId : "未识别模型";
        public string TokenText => UsageDisplayFormatting.TokenAdaptive(Tokens);
        public string TokenShareText => FormatShare(TokenShare);
        public string CostShareText => FormatShare(CostShare);
    }
}
