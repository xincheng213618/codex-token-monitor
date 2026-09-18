using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using WpfBinding = System.Windows.Data.Binding;

namespace CodexTokenMonitor;

internal sealed class BreakdownGridAdapter : IDisposable
{
    private readonly DataGrid grid;
    private bool disposed;

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
            includeModels: true,
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
        bool includeModels,
        bool includeQuota,
        IReadOnlyList<BreakdownRow> rows)
    {
        if (disposed)
        {
            return;
        }

        var anchor = CaptureAnchor();
        ApplyColumns(range, eventBreakdown, tablePresets, includeModels, includeQuota);
        grid.ItemsSource = rows;
        RestoreAnchor(anchor);
    }

    private void ApplyColumns(
        SelectedRange range,
        bool eventBreakdown,
        IReadOnlyList<PricePreset> tablePresets,
        bool includeModels,
        bool includeQuota)
    {
        var expected = BuildColumnDefinitions(range, eventBreakdown, tablePresets, includeModels, includeQuota);
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
                Width = definition.Width,
                MinWidth = 76,
                HeaderStyle = HeaderStyle(definition.Title),
                ElementStyle = CellTextStyle(definition.RightAlign)
            };

            grid.Columns.Add(column);
        }
    }

    private static IReadOnlyList<BreakdownColumnDefinition> BuildColumnDefinitions(
        SelectedRange range,
        bool eventBreakdown,
        IReadOnlyList<PricePreset> tablePresets,
        bool includeModels,
        bool includeQuota)
    {
        var columns = new List<BreakdownColumnDefinition>
        {
            new(eventBreakdown ? "时间" : "日期", nameof(BreakdownRow.Label), FirstColumnWidth(range, eventBreakdown), false),
            new("Total", nameof(BreakdownRow.Total), 78, true),
            new("Input", nameof(BreakdownRow.Input), 78, true),
            new("Cached", nameof(BreakdownRow.Cached), 82, true),
            new("Cache Write", nameof(BreakdownRow.CacheWrite), 92, true),
            new("Uncached", nameof(BreakdownRow.Uncached), 88, true),
            new("Output", nameof(BreakdownRow.Output), 76, true)
        };

        if (includeModels)
        {
            columns.Insert(1, new("实际模型", nameof(BreakdownRow.Model), 132, false));
            columns.Add(new("模型费用", nameof(BreakdownRow.ActualCost), 112, true));
        }
        for (var i = 0; i < tablePresets.Count; i++)
        {
            var bindingPath = $"{nameof(BreakdownRow.Prices)}[{i}]";
            var title = (includeModels ? "换用 " : "") + FormatPresetColumnTitle(tablePresets[i], $"价格{i + 1}");
            const double width = 112;
            columns.Add(new BreakdownColumnDefinition(title, bindingPath, width, true));
        }

        if (includeQuota)
        {
            columns.Add(new BreakdownColumnDefinition("额度 (5h / 7d)", nameof(BreakdownRow.Quota), 112, true));
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
        if (disposed || anchor is null || grid.Items.Count == 0)
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
            if (!disposed && grid.IsLoaded && index >= 0 && index < grid.Items.Count)
            {
                grid.ScrollIntoView(grid.Items[index]);
            }
        });
    }

    public void Dispose()
    {
        disposed = true;
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
            return 128;
        }

        if (range.IsCustomStart)
        {
            return 144;
        }

        return range.Mode == RangeMode.Day ? 92 : 104;
    }

    private Style HeaderStyle(string title)
    {
        return new Style(typeof(DataGridColumnHeader), grid.TryFindResource(typeof(DataGridColumnHeader)) as Style)
        {
            Setters =
            {
                new Setter(FrameworkElement.ToolTipProperty, title)
            }
        };
    }

    private static Style CellTextStyle(bool rightAlign)
    {
        return new Style(typeof(TextBlock), DataGridTextColumn.DefaultElementStyle)
        {
            Setters =
            {
                new Setter(TextBlock.TextAlignmentProperty, rightAlign ? TextAlignment.Right : TextAlignment.Left),
                new Setter(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center),
                new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis),
                new Setter(FrameworkElement.ToolTipProperty, new WpfBinding(nameof(TextBlock.Text))
                {
                    RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.Self)
                })
            }
        };
    }

    private static string FormatPresetColumnTitle(PricePreset preset, string fallback)
    {
        var text = string.IsNullOrWhiteSpace(preset.Model) ? preset.Provider : preset.Model;
        return string.IsNullOrWhiteSpace(text) ? fallback : text;
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
