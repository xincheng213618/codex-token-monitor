using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace CodexTokenMonitor;

internal partial class PriceSettingsWindow : Window
{
    private readonly Dictionary<string, ObservableCollection<PricePresetRow>> groupRows = new(StringComparer.OrdinalIgnoreCase);
    private ICollectionView? activeView;
    private System.Windows.Point dragStartPoint;
    private PricePresetRow? draggedRow;
    private DateTime lastDragScrollUtc = DateTime.MinValue;

    public PriceSettingsWindow(string initialGroup)
    {
        InitializeComponent();
        LoadSettings(PriceSettingsStore.Current);
        SourceTabs.SelectedIndex = Math.Max(0, PricePresetGroups.All
            .Select((group, index) => (group, index))
            .FirstOrDefault(item => string.Equals(item.group, PricePresetGroups.Normalize(initialGroup), StringComparison.OrdinalIgnoreCase))
            .index);
        UpdateCurrentView();
    }

    private void LoadSettings(PriceSettings settings)
    {
        groupRows.Clear();
        foreach (var group in PricePresetGroups.All)
        {
            var rows = new ObservableCollection<PricePresetRow>(settings.PresetsForGroup(group)
                .Select(item => new PricePresetRow(item.Clone())));
            UpdateRanks(rows);
            groupRows[group] = rows;
        }
    }

    private void SourceTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (groupRows.Count == 0 || !ReferenceEquals(e.Source, SourceTabs))
        {
            return;
        }

        UpdateCurrentView();
    }

    private void UpdateCurrentView()
    {
        if (!groupRows.TryGetValue(CurrentGroup(), out var rows))
        {
            return;
        }

        activeView = CollectionViewSource.GetDefaultView(rows);
        activeView.Filter = MatchesSearch;
        PriceGrid.ItemsSource = activeView;
        UpdateCount();
    }

    private bool MatchesSearch(object item)
    {
        if (item is not PricePresetRow row)
        {
            return false;
        }

        var query = SearchBox.Text.Trim();
        return query.Length == 0 ||
               row.Provider.Contains(query, StringComparison.OrdinalIgnoreCase) ||
               row.Model.Contains(query, StringComparison.OrdinalIgnoreCase) ||
               row.Source.Contains(query, StringComparison.OrdinalIgnoreCase) ||
               row.UnitLabel.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        activeView?.Refresh();
        UpdateCount();
    }

    private void UpdateCount()
    {
        if (!groupRows.TryGetValue(CurrentGroup(), out var rows) || activeView is null)
        {
            return;
        }

        var visible = activeView.Cast<object>().Count();
        CountText.Text = string.IsNullOrWhiteSpace(SearchBox.Text)
            ? $"共 {rows.Count} 项；前 3 项优先展示"
            : $"找到 {visible} / {rows.Count} 项";
    }

    private void PinButton_Click(object sender, RoutedEventArgs e)
    {
        if (PriceGrid.SelectedItem is not PricePresetRow selected || !groupRows.TryGetValue(CurrentGroup(), out var rows))
        {
            ShowSelectionHint();
            return;
        }

        var sourceIndex = rows.IndexOf(selected);
        if (sourceIndex > 0)
        {
            rows.Move(sourceIndex, 0);
            FinishReorder(rows, selected);
        }
    }

    private void MoveUpButton_Click(object sender, RoutedEventArgs e)
    {
        MoveSelected(-1);
    }

    private void MoveDownButton_Click(object sender, RoutedEventArgs e)
    {
        MoveSelected(1);
    }

    private void MoveSelected(int direction)
    {
        if (PriceGrid.SelectedItem is not PricePresetRow selected ||
            !groupRows.TryGetValue(CurrentGroup(), out var rows) ||
            activeView is null)
        {
            ShowSelectionHint();
            return;
        }

        var visibleRows = activeView.Cast<PricePresetRow>().ToList();
        var visibleIndex = visibleRows.IndexOf(selected);
        var targetVisibleIndex = visibleIndex + direction;
        if (visibleIndex < 0 || targetVisibleIndex < 0 || targetVisibleIndex >= visibleRows.Count)
        {
            return;
        }

        var sourceIndex = rows.IndexOf(selected);
        var targetIndex = rows.IndexOf(visibleRows[targetVisibleIndex]);
        rows.Move(sourceIndex, targetIndex);
        FinishReorder(rows, selected);
    }

    private void FinishReorder(ObservableCollection<PricePresetRow> rows, PricePresetRow selected)
    {
        UpdateRanks(rows);
        activeView?.Refresh();
        PriceGrid.SelectedItem = selected;
        PriceGrid.ScrollIntoView(selected);
        UpdateCount();
    }

    private void AddButton_Click(object sender, RoutedEventArgs e)
    {
        if (!groupRows.TryGetValue(CurrentGroup(), out var rows))
        {
            return;
        }

        var preset = new PricePreset
        {
            Group = CurrentGroup(),
            CurrencySymbol = "$",
            UnitLabel = "USD / 1M tokens",
            Divisor = 1_000_000m
        };
        var editor = new PricePresetEditorWindow(preset, isNew: true) { Owner = this };
        if (editor.ShowDialog() != true)
        {
            return;
        }

        var added = new PricePresetRow(editor.Result);
        rows.Add(added);
        SearchBox.Clear();
        FinishReorder(rows, added);
    }

    private void EditButton_Click(object sender, RoutedEventArgs e)
    {
        EditSelected();
    }

    private void PriceGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        EditSelected();
    }

    private void PriceGrid_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        dragStartPoint = e.GetPosition(PriceGrid);
        draggedRow = FindVisualParent<DataGridRow>(e.OriginalSource as DependencyObject)?.Item as PricePresetRow;
    }

    private void PriceGrid_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || draggedRow is null)
        {
            return;
        }

        var current = e.GetPosition(PriceGrid);
        if (Math.Abs(current.X - dragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(current.Y - dragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        var row = draggedRow;
        try
        {
            System.Windows.DragDrop.DoDragDrop(PriceGrid, row, System.Windows.DragDropEffects.Move);
        }
        finally
        {
            draggedRow = null;
            PriceGrid.SelectedItem = row;
        }
    }

    private void PriceGrid_DragOver(object sender, System.Windows.DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(typeof(PricePresetRow)) ||
            e.Data.GetData(typeof(PricePresetRow)) is not PricePresetRow source ||
            !groupRows.TryGetValue(CurrentGroup(), out var rows) ||
            !rows.Contains(source))
        {
            e.Effects = System.Windows.DragDropEffects.None;
            e.Handled = true;
            return;
        }

        e.Effects = System.Windows.DragDropEffects.Move;
        var target = FindVisualParent<DataGridRow>(e.OriginalSource as DependencyObject)?.Item as PricePresetRow;
        if (target is not null && !ReferenceEquals(target, source))
        {
            PriceGrid.SelectedItem = target;
        }

        AutoScrollDuringDrag(e.GetPosition(PriceGrid));
        e.Handled = true;
    }

    private void PriceGrid_Drop(object sender, System.Windows.DragEventArgs e)
    {
        if (e.Data.GetData(typeof(PricePresetRow)) is not PricePresetRow source ||
            !groupRows.TryGetValue(CurrentGroup(), out var rows))
        {
            return;
        }

        var sourceIndex = rows.IndexOf(source);
        if (sourceIndex < 0)
        {
            return;
        }

        var targetRow = FindVisualParent<DataGridRow>(e.OriginalSource as DependencyObject);
        if (targetRow?.Item is not PricePresetRow target)
        {
            rows.Move(sourceIndex, rows.Count - 1);
            FinishReorder(rows, source);
            e.Handled = true;
            return;
        }

        if (ReferenceEquals(source, target))
        {
            PriceGrid.SelectedItem = source;
            e.Handled = true;
            return;
        }

        var targetIndex = rows.IndexOf(target);
        var dropAfterTarget = e.GetPosition(targetRow).Y > targetRow.ActualHeight / 2;
        var insertionIndex = targetIndex + (dropAfterTarget ? 1 : 0);
        var destinationIndex = sourceIndex < insertionIndex ? insertionIndex - 1 : insertionIndex;
        destinationIndex = Math.Clamp(destinationIndex, 0, rows.Count - 1);
        if (destinationIndex != sourceIndex)
        {
            rows.Move(sourceIndex, destinationIndex);
            FinishReorder(rows, source);
        }
        else
        {
            PriceGrid.SelectedItem = source;
        }

        e.Handled = true;
    }

    private void AutoScrollDuringDrag(System.Windows.Point position)
    {
        var now = DateTime.UtcNow;
        if (now - lastDragScrollUtc < TimeSpan.FromMilliseconds(45))
        {
            return;
        }

        var scrollViewer = FindVisualChild<ScrollViewer>(PriceGrid);
        if (scrollViewer is null)
        {
            return;
        }

        const double edgeSize = 58;
        if (position.Y < edgeSize)
        {
            scrollViewer.LineUp();
            lastDragScrollUtc = now;
        }
        else if (position.Y > PriceGrid.ActualHeight - edgeSize)
        {
            scrollViewer.LineDown();
            lastDragScrollUtc = now;
        }
    }

    private static T? FindVisualParent<T>(DependencyObject? child) where T : DependencyObject
    {
        while (child is not null)
        {
            if (child is T match)
            {
                return match;
            }

            child = VisualTreeHelper.GetParent(child);
        }

        return null;
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
            {
                return match;
            }

            var descendant = FindVisualChild<T>(child);
            if (descendant is not null)
            {
                return descendant;
            }
        }

        return null;
    }

    private void EditSelected()
    {
        if (PriceGrid.SelectedItem is not PricePresetRow selected)
        {
            ShowSelectionHint();
            return;
        }

        var editor = new PricePresetEditorWindow(selected.Preset, isNew: false) { Owner = this };
        if (editor.ShowDialog() != true)
        {
            return;
        }

        editor.Result.Group = CurrentGroup();
        selected.Replace(editor.Result);
        activeView?.Refresh();
        UpdateCount();
    }

    private void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (PriceGrid.SelectedItem is not PricePresetRow selected || !groupRows.TryGetValue(CurrentGroup(), out var rows))
        {
            ShowSelectionHint();
            return;
        }

        if (IsBuiltInPreset(selected.Preset))
        {
            System.Windows.MessageBox.Show(this, "内置价格不会被删除。你可以编辑它，或恢复当前分组的默认价格。", Title,
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (System.Windows.MessageBox.Show(this, $"确定删除“{selected.DisplayName}”吗？", Title,
                MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
        {
            return;
        }

        rows.Remove(selected);
        UpdateRanks(rows);
        activeView?.Refresh();
        UpdateCount();
    }

    private static bool IsBuiltInPreset(PricePreset preset)
    {
        return PricePreset.Defaults().Any(item =>
            string.Equals(item.Provider, preset.Provider, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(item.Model, preset.Model, StringComparison.OrdinalIgnoreCase));
    }

    private void RestoreButton_Click(object sender, RoutedEventArgs e)
    {
        var group = CurrentGroup();
        if (System.Windows.MessageBox.Show(this, $"恢复 {group} 分组的默认价格和展示顺序？其他分组不会受到影响。", Title,
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
        {
            return;
        }

        var rows = new ObservableCollection<PricePresetRow>(PriceSettingsStore.Defaults().PresetsForGroup(group)
            .Select(item => new PricePresetRow(item.Clone())));
        UpdateRanks(rows);
        groupRows[group] = rows;
        SearchBox.Clear();
        UpdateCurrentView();
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            foreach (var group in PricePresetGroups.All)
            {
                if (!groupRows.TryGetValue(group, out var rows) || rows.Count == 0)
                {
                    throw new InvalidOperationException($"{group} 分组至少需要保留一个价格。");
                }
            }

            var settings = PriceSettingsStore.Current.Clone();
            settings.DisplayOrderVersion = PriceSettingsStore.Defaults().DisplayOrderVersion;
            settings.Presets = new();
            foreach (var group in PricePresetGroups.All)
            {
                settings.SetPresetsForGroup(group, groupRows[group]
                    .Select(row =>
                    {
                        var preset = row.Preset.Clone();
                        preset.Group = group;
                        return preset;
                    })
                    .ToList());
            }

            ApplyLegacyProfiles(settings, groupRows[PricePresetGroups.Codex].Select(row => row.Preset).ToList());
            PriceSettingsStore.Save(settings);
            DialogResult = true;
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static void ApplyLegacyProfiles(PriceSettings settings, IReadOnlyList<PricePreset> codex)
    {
        var current = PriceSettingsStore.Current;
        var gpt = codex.FirstOrDefault(item =>
            item.Provider.Contains("OpenAI", StringComparison.OrdinalIgnoreCase) ||
            item.Model.Contains("GPT", StringComparison.OrdinalIgnoreCase));
        if (gpt is not null)
        {
            settings.GptName = gpt.Model;
            settings.GptUncachedInputPerMillion = gpt.UncachedInput;
            settings.GptCachedInputPerMillion = gpt.CachedInput;
            settings.GptOutputPerMillion = gpt.Output;
        }
        else
        {
            settings.GptName = current.GptName;
            settings.GptUncachedInputPerMillion = current.GptUncachedInputPerMillion;
            settings.GptCachedInputPerMillion = current.GptCachedInputPerMillion;
            settings.GptOutputPerMillion = current.GptOutputPerMillion;
        }

        var deepSeek = codex.FirstOrDefault(item => item.Provider.Contains("DeepSeek", StringComparison.OrdinalIgnoreCase));
        settings.DeepSeekUncachedInputPerMillion = deepSeek?.UncachedInput ?? current.DeepSeekUncachedInputPerMillion;
        settings.DeepSeekCachedInputPerMillion = deepSeek?.CachedInput ?? current.DeepSeekCachedInputPerMillion;
        settings.DeepSeekOutputPerMillion = deepSeek?.Output ?? current.DeepSeekOutputPerMillion;

        var xiaomi = codex.FirstOrDefault(item => item.Provider.Contains("Xiaomi", StringComparison.OrdinalIgnoreCase));
        settings.XiaomiUncachedInputCreditsPerToken = xiaomi?.UncachedInput ?? current.XiaomiUncachedInputCreditsPerToken;
        settings.XiaomiCachedInputCreditsPerToken = xiaomi?.CachedInput ?? current.XiaomiCachedInputCreditsPerToken;
        settings.XiaomiOutputCreditsPerToken = xiaomi?.Output ?? current.XiaomiOutputCreditsPerToken;
    }

    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.F && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
    }

    private void ShowSelectionHint()
    {
        System.Windows.MessageBox.Show(this, "请先在价格表中选择一项。", Title,
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private string CurrentGroup()
    {
        return SourceTabs.SelectedItem is TabItem { Header: string header }
            ? PricePresetGroups.Normalize(header)
            : PricePresetGroups.Codex;
    }

    private static void UpdateRanks(IReadOnlyList<PricePresetRow> rows)
    {
        for (var i = 0; i < rows.Count; i++)
        {
            rows[i].Rank = i + 1;
        }
    }
}

internal sealed class PricePresetRow : INotifyPropertyChanged
{
    private int rank;

    public PricePresetRow(PricePreset preset)
    {
        Preset = preset.Clone();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public PricePreset Preset { get; private set; }
    public string Provider => Preset.Provider;
    public string Model => Preset.Model;
    public string CurrencySymbol => Preset.CurrencySymbol;
    public string UnitLabel => Preset.UnitLabel;
    public decimal UncachedInput => Preset.UncachedInput;
    public decimal CachedInput => Preset.CachedInput;
    public decimal Output => Preset.Output;
    public string Source => Preset.Source;
    public string DisplayName => string.IsNullOrWhiteSpace(Provider) ? Model : $"{Provider} · {Model}";
    public bool IsPriority => Rank <= 3;
    public string PriorityLabel => Rank switch
    {
        1 => "主价格",
        2 => "对比 2",
        3 => "对比 3",
        _ => Rank.ToString()
    };

    public int Rank
    {
        get => rank;
        set
        {
            if (rank == value)
            {
                return;
            }

            rank = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(PriorityLabel));
            OnPropertyChanged(nameof(IsPriority));
        }
    }

    public void Replace(PricePreset preset)
    {
        Preset = preset.Clone();
        OnPropertyChanged(nameof(Preset));
        OnPropertyChanged(nameof(Provider));
        OnPropertyChanged(nameof(Model));
        OnPropertyChanged(nameof(CurrencySymbol));
        OnPropertyChanged(nameof(UnitLabel));
        OnPropertyChanged(nameof(UncachedInput));
        OnPropertyChanged(nameof(CachedInput));
        OnPropertyChanged(nameof(Output));
        OnPropertyChanged(nameof(Source));
        OnPropertyChanged(nameof(DisplayName));
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
