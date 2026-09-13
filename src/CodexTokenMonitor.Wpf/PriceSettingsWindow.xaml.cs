using System.Collections.ObjectModel;
using System.Collections.Specialized;
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
    private readonly AnalysisQuerySession querySession;
    private PriceSettings? loadedSettings;
    private bool hasLoadedSettings;
    private bool isLoading;
    private bool isSaving;

    public PriceSettingsWindow(string initialGroup, MonitorRuntime? runtime = null)
    {
        querySession = new AnalysisQuerySession(runtime);
        InitializeComponent();
        foreach (var definition in UsageSourceRegistry.All)
            SourceTabs.Items.Add(new TabItem { Header = definition.PriceGroup, Tag = definition.Source });
        SourceTabs.SelectedIndex = UsageSourceRegistry.IndexOf(UsageSourceRegistry.ForPriceGroup(initialGroup).Source);
        UpdateEditingState();
        Loaded += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        if (isLoading || isSaving || querySession.IsStopping) return;
        isLoading = true;
        hasLoadedSettings = false;
        UpdateEditingState();
        SetStatus("正在读取价格设置…");
        try
        {
            var result = await querySession.RunAsync("读取价格设置", _ => PriceSettingsStore.Load(forceReload: true));
            if (querySession.IsStopping) return;
            if (result.CacheWarnings.Count > 0)
            {
                SetStatus("价格设置读取失败，编辑和保存已停用。修复后点击“重新读取”。", CacheFailureText.Detail(result.CacheWarnings));
                return;
            }

            var settings = result.Value.Clone();
            var discoveredCount = 0;
            string? modelWarning = null;
            try
            {
                var models = await querySession.RunAsync("读取已发现模型", _ => UsageCacheStore.Load().GetModelIds());
                if (querySession.IsStopping) return;
                if (models.CacheWarnings.Count == 0)
                    discoveredCount = CodexModelCost.AddMissingPresets(settings, models.Value);
                else
                    modelWarning = CacheFailureText.Detail(models.CacheWarnings);
            }
            catch (OperationCanceledException) when (querySession.IsStopping) { return; }
            catch (Exception ex) { modelWarning = ex.ToString(); }

            loadedSettings = settings;
            LoadSettings(settings);
            UpdateCurrentView();
            hasLoadedSettings = true;
            SetStatus(modelWarning is not null
                ? "价格已载入；模型缓存暂不可读，可继续编辑价格。"
                : discoveredCount > 0
                    ? $"已补充 {discoveredCount} 个模型价格，点击保存后生效。"
                    : "修改在点击保存后生效。", modelWarning);
        }
        catch (OperationCanceledException) when (querySession.IsStopping) { }
        catch (Exception ex)
        {
            if (!querySession.IsStopping)
                SetStatus("价格设置读取失败，编辑和保存已停用。修复后点击“重新读取”。", ex.ToString());
        }
        finally
        {
            isLoading = false;
            if (!querySession.IsStopping) UpdateEditingState();
        }
    }

    private async void RetryLoadButton_Click(object sender, RoutedEventArgs e) => await LoadAsync();

    private bool CanEdit => hasLoadedSettings && !isLoading && !isSaving && !querySession.IsStopping;

    private void UpdateEditingState()
    {
        PriceEditor.IsEnabled = CanEdit;
        RestoreButton.IsEnabled = CanEdit;
        SaveButton.IsEnabled = CanEdit;
        RetryLoadButton.Visibility = hasLoadedSettings ? Visibility.Collapsed : Visibility.Visible;
        RetryLoadButton.IsEnabled = !isLoading && !isSaving && !querySession.IsStopping;
    }

    private void SetStatus(string text, string? detail = null)
    {
        StatusText.Text = text;
        StatusText.ToolTip = detail;
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

        if (activeView is not null)
        {
            activeView.CollectionChanged -= ActiveView_CollectionChanged;
        }

        activeView = CollectionViewSource.GetDefaultView(rows);
        activeView.Filter = MatchesSearch;
        activeView.CollectionChanged += ActiveView_CollectionChanged;
        PriceGrid.ItemsSource = activeView;
        UpdateCount();
    }

    private void ActiveView_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Column sorting preserves SelectedItem and does not raise SelectionChanged.
        UpdateSelectionActions();
    }

    protected override void OnClosed(EventArgs e)
    {
        querySession.Dispose();
        if (activeView is not null)
        {
            activeView.CollectionChanged -= ActiveView_CollectionChanged;
        }

        base.OnClosed(e);
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
        var hasSearch = !string.IsNullOrWhiteSpace(SearchBox.Text);
        CountText.Text = hasSearch ? $"找到 {visible} / {rows.Count} 条价格" : $"共 {rows.Count} 条价格";
        ClearSearchButton.Visibility = hasSearch ? Visibility.Visible : Visibility.Collapsed;
        EmptyStatePanel.Visibility = visible == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyStateTitle.Text = hasSearch ? "没有找到匹配的价格" : "这个来源还没有价格";
        EmptyStateDescription.Text = hasSearch
            ? "换个关键词，或清除搜索查看当前来源的全部价格。"
            : "点击上方“新增价格”，添加第一个模型的计价信息。";
        ClearEmptyFilterButton.Visibility = hasSearch ? Visibility.Visible : Visibility.Collapsed;
        UpdateSelectionActions();
    }

    private void ClearSearchButton_Click(object sender, RoutedEventArgs e)
    {
        SearchBox.Clear();
        SearchBox.Focus();
    }

    private void PriceGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateSelectionActions();
    }

    private void UpdateSelectionActions()
    {
        if (EditPriceButton is null || activeView is null)
        {
            return;
        }

        var selected = PriceGrid.SelectedItem as PricePresetRow;
        var visibleRows = activeView.Cast<PricePresetRow>().ToList();
        var index = selected is null ? -1 : visibleRows.IndexOf(selected);
        EditPriceButton.IsEnabled = index >= 0;
        DeletePriceButton.IsEnabled = index >= 0;
        PinPriceButton.IsEnabled = index >= 0 && selected!.Rank > 1;
        MovePriceUpButton.IsEnabled = index > 0;
        MovePriceDownButton.IsEnabled = index >= 0 && index < visibleRows.Count - 1;
    }

    private void PinButton_Click(object sender, RoutedEventArgs e)
    {
        if (!CanEdit) return;
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
        if (!CanEdit) return;
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
        if (!CanEdit) return;
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
        if (!CanEdit) return;
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
        if (!CanEdit) return;
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
        if (!CanEdit) return;
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
        if (!CanEdit) return;
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
        if (!CanEdit) return;
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

    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (!CanEdit || loadedSettings is null) return;
        try
        {
            foreach (var group in PricePresetGroups.All)
            {
                if (!groupRows.TryGetValue(group, out var rows) || rows.Count == 0)
                {
                    throw new InvalidOperationException($"{group} 分组至少需要保留一个价格。");
                }
            }

            var settings = loadedSettings.Clone();
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
            isSaving = true;
            UpdateEditingState();
            SetStatus("正在保存价格设置…");
            await querySession.RunAsync("保存价格设置", _ =>
            {
                PriceSettingsStore.Save(settings);
                return true;
            }, requiresSharedIo: true);
            if (querySession.IsStopping) return;
            DialogResult = true;
        }
        catch (OperationCanceledException) when (querySession.IsStopping) { }
        catch (Exception ex)
        {
            if (!querySession.IsStopping)
            {
                if (isSaving)
                    SetStatus("保存失败，已保留当前编辑。修复后可再次点击保存。", ex.ToString());
                else
                    System.Windows.MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        finally
        {
            isSaving = false;
            if (!querySession.IsStopping) UpdateEditingState();
        }
    }

    private static void ApplyLegacyProfiles(PriceSettings settings, IReadOnlyList<PricePreset> codex)
    {
        var current = settings.Clone();
        var gpt = codex.FirstOrDefault(item =>
            item.Provider.Contains("OpenAI", StringComparison.OrdinalIgnoreCase) ||
            item.Model.Contains("GPT", StringComparison.OrdinalIgnoreCase));
        if (gpt is not null)
        {
            settings.GptName = gpt.Model;
            settings.GptUncachedInputPerMillion = gpt.UncachedInput;
            settings.GptCachedInputPerMillion = gpt.CachedInput;
            settings.GptCacheWriteInputPerMillion = gpt.CacheWriteInput;
            settings.GptOutputPerMillion = gpt.Output;
        }
        else
        {
            settings.GptName = current.GptName;
            settings.GptUncachedInputPerMillion = current.GptUncachedInputPerMillion;
            settings.GptCachedInputPerMillion = current.GptCachedInputPerMillion;
            settings.GptCacheWriteInputPerMillion = current.GptCacheWriteInputPerMillion;
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
        return SourceTabs.SelectedItem is TabItem { Tag: UsageSource source }
            ? PricePresetGroups.ForSource(source)
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
    public string ScheduleLabel => Preset.ScheduleLabel;
    public decimal UncachedInput => Preset.UncachedInput;
    public decimal CachedInput => Preset.CachedInput;
    public decimal CacheWriteInput => Preset.EffectiveCacheWriteInput;
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
        OnPropertyChanged(nameof(ScheduleLabel));
        OnPropertyChanged(nameof(UncachedInput));
        OnPropertyChanged(nameof(CachedInput));
        OnPropertyChanged(nameof(CacheWriteInput));
        OnPropertyChanged(nameof(Output));
        OnPropertyChanged(nameof(Source));
        OnPropertyChanged(nameof(DisplayName));
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
