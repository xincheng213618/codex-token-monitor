using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WpfBinding = System.Windows.Data.Binding;

namespace CodexTokenMonitor;

internal sealed class BreakdownGridAdapter
{
    private readonly DataGrid grid;

    public BreakdownGridAdapter(DataGrid grid)
    {
        this.grid = grid;
    }

    public void ConfigureInitialColumns()
    {
        var now = DateTimeOffset.UtcNow.ToOffset(CodexUsageReader.BeijingOffset);
        ApplyColumns(
            new SelectedRange(now, now, "", "", RangeMode.Day),
            eventBreakdown: false,
            Array.Empty<PricePreset>(),
            includeQuota: true);
    }

    public BreakdownScrollAnchor? CaptureAnchor()
    {
        if (grid.Items.Count == 0)
        {
            return null;
        }

        var index = 0;
        if (FindVisualChild<ScrollViewer>(grid) is { } scrollViewer)
        {
            index = Math.Clamp((int)Math.Floor(scrollViewer.VerticalOffset), 0, grid.Items.Count - 1);
        }

        return grid.Items[index] is BreakdownRow row
            ? new BreakdownScrollAnchor(index, row.Label)
            : null;
    }

    public void ApplyRows(
        SelectedRange range,
        bool eventBreakdown,
        IReadOnlyList<PricePreset> tablePresets,
        bool includeQuota,
        IReadOnlyList<BreakdownRow> rows)
    {
        var anchor = CaptureAnchor();
        ApplyColumns(range, eventBreakdown, tablePresets, includeQuota);
        grid.ItemsSource = new ObservableCollection<BreakdownRow>(rows);
        RestoreAnchor(anchor);
    }

    private void ApplyColumns(
        SelectedRange range,
        bool eventBreakdown,
        IReadOnlyList<PricePreset> tablePresets,
        bool includeQuota)
    {
        var expected = BuildColumnDefinitions(range, eventBreakdown, tablePresets, includeQuota);
        if (ColumnsMatch(expected))
        {
            ApplyColumnWidths(expected);
            return;
        }

        grid.Columns.Clear();
        foreach (var definition in expected)
        {
            var column = new DataGridTextColumn
            {
                Header = definition.Title,
                Binding = new WpfBinding(definition.BindingPath),
                Width = definition.Width
            };
            if (definition.RightAlign)
            {
                column.ElementStyle = RightAlignedTextStyle();
            }

            grid.Columns.Add(column);
        }
    }

    private static IReadOnlyList<BreakdownColumnDefinition> BuildColumnDefinitions(
        SelectedRange range,
        bool eventBreakdown,
        IReadOnlyList<PricePreset> tablePresets,
        bool includeQuota)
    {
        var columns = new List<BreakdownColumnDefinition>
        {
            new(eventBreakdown ? "时间" : "日期", nameof(BreakdownRow.Label), FirstColumnWidth(range, eventBreakdown), false),
            new("Total", nameof(BreakdownRow.Total), 96, true),
            new("Input", nameof(BreakdownRow.Input), 96, true),
            new("Cached", nameof(BreakdownRow.Cached), 96, true),
            new("Uncached", nameof(BreakdownRow.Uncached), 104, true),
            new("Output", nameof(BreakdownRow.Output), 88, true)
        };

        for (var i = 0; i < tablePresets.Count; i++)
        {
            var bindingPath = i switch
            {
                0 => nameof(BreakdownRow.Price1),
                1 => nameof(BreakdownRow.Price2),
                _ => nameof(BreakdownRow.Price3)
            };
            columns.Add(new BreakdownColumnDefinition(FormatPresetColumnTitle(tablePresets[i], $"价格{i + 1}"), bindingPath, 108, true));
        }

        if (includeQuota)
        {
            columns.Add(new BreakdownColumnDefinition("额度(5h/7d)", nameof(BreakdownRow.Quota), 126, true));
        }

        return columns;
    }

    private bool ColumnsMatch(IReadOnlyList<BreakdownColumnDefinition> expected)
    {
        if (grid.Columns.Count != expected.Count)
        {
            return false;
        }

        for (var i = 0; i < expected.Count; i++)
        {
            if (!string.Equals(grid.Columns[i].Header?.ToString(), expected[i].Title, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private void ApplyColumnWidths(IReadOnlyList<BreakdownColumnDefinition> definitions)
    {
        for (var i = 0; i < definitions.Count && i < grid.Columns.Count; i++)
        {
            grid.Columns[i].Width = definitions[i].Width;
        }
    }

    private void RestoreAnchor(BreakdownScrollAnchor? anchor)
    {
        if (anchor is null || grid.Items.Count == 0)
        {
            return;
        }

        var index = FindAnchorIndex(anchor);
        if (index < 0 || index >= grid.Items.Count)
        {
            return;
        }

        grid.Dispatcher.BeginInvoke(() =>
        {
            if (index >= 0 && index < grid.Items.Count)
            {
                grid.ScrollIntoView(grid.Items[index]);
            }
        });
    }

    private int FindAnchorIndex(BreakdownScrollAnchor anchor)
    {
        var count = grid.Items.Count;
        if (count == 0)
        {
            return -1;
        }

        var clampedIndex = Math.Clamp(anchor.Index, 0, count - 1);
        if (ItemLabel(clampedIndex) == anchor.Label)
        {
            return clampedIndex;
        }

        for (var distance = 1; distance < count; distance++)
        {
            var before = clampedIndex - distance;
            if (before >= 0 && ItemLabel(before) == anchor.Label)
            {
                return before;
            }

            var after = clampedIndex + distance;
            if (after < count && ItemLabel(after) == anchor.Label)
            {
                return after;
            }
        }

        return clampedIndex;
    }

    private string? ItemLabel(int index)
    {
        return grid.Items[index] is BreakdownRow row ? row.Label : null;
    }

    private static double FirstColumnWidth(SelectedRange range, bool eventBreakdown)
    {
        if (eventBreakdown && range.Mode != RangeMode.Day && !range.IsCustomStart)
        {
            return 134;
        }

        if (range.IsCustomStart)
        {
            return 150;
        }

        return range.Mode == RangeMode.Day ? 86 : 104;
    }

    private static Style RightAlignedTextStyle()
    {
        return new Style(typeof(TextBlock))
        {
            Setters =
            {
                new Setter(TextBlock.TextAlignmentProperty, TextAlignment.Right),
                new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis)
            }
        };
    }

    private static string FormatPresetColumnTitle(PricePreset preset, string fallback)
    {
        var text = string.IsNullOrWhiteSpace(preset.Provider) ? preset.Model : $"{preset.Provider} {preset.Model}";
        return string.IsNullOrWhiteSpace(text) ? fallback : ShortenColumnTitle(text);
    }

    private static string ShortenColumnTitle(string text)
    {
        return text.Length <= 18 ? text : $"{text[..16]}...";
    }

    private static T? FindVisualChild<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                return match;
            }

            var nested = FindVisualChild<T>(child);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }

    private sealed record BreakdownColumnDefinition(
        string Title,
        string BindingPath,
        double Width,
        bool RightAlign);

    public sealed record BreakdownScrollAnchor(int Index, string Label);
}
