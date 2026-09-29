using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;

namespace CodexTokenMonitor;

internal partial class PriceSettingsWindow : Window
{
    private readonly AnalysisQuerySession querySession;
    private readonly Dictionary<string, List<PriceDisplaySlotRow>> groupRows = new(StringComparer.OrdinalIgnoreCase);
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
            loadedSettings = result.Value.Clone();
            string? modelWarning = null;
            try
            {
                var models = await querySession.RunAsync("读取已发现模型", _ => UsageCacheStore.Load().GetModelIds());
                if (querySession.IsStopping) return;
                if (models.CacheWarnings.Count == 0) CodexModelCost.AddMissingPresets(loadedSettings, models.Value);
                else modelWarning = CacheFailureText.Detail(models.CacheWarnings);
            }
            catch (OperationCanceledException) when (querySession.IsStopping) { return; }
            catch (Exception ex) { modelWarning = ex.ToString(); }
            BuildRows();
            hasLoadedSettings = true;
            SetStatus(modelWarning is null ? "修改在点击保存后生效。" : "价格已载入；模型缓存暂不可读，可继续编辑。", modelWarning);
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

    private bool CanEdit => hasLoadedSettings && !isLoading && !isSaving && !querySession.IsStopping;
    private void UpdateEditingState()
    {
        PriceEditor.IsEnabled = CanEdit;
        ModelPricesButton.IsEnabled = CanEdit;
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
    private async void RetryLoadButton_Click(object sender, RoutedEventArgs e) => await LoadAsync();
    private string CurrentGroup() => SourceTabs.SelectedItem is TabItem { Tag: UsageSource source }
        ? PricePresetGroups.ForSource(source) : PricePresetGroups.Codex;

    private void BuildRows()
    {
        if (loadedSettings is null) return;
        groupRows.Clear();
        foreach (var group in PricePresetGroups.All)
        {
            var catalog = loadedSettings.PresetsForGroup(group);
            groupRows[group] = loadedSettings.DisplaySlotsForGroup(group)
                .Select((key, index) => new PriceDisplaySlotRow(index + 1, catalog, key)).ToList();
        }
        UpdateCurrentView();
    }
    private void UpdateCurrentView()
    {
        if (!groupRows.TryGetValue(CurrentGroup(), out var rows)) return;
        SlotGrid.ItemsSource = rows;
        GroupDescription.Text = $"{CurrentGroup()} · 10 个展示位置";
    }
    private void SourceTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ReferenceEquals(e.Source, SourceTabs)) UpdateCurrentView();
    }
    private PriceSettings CollectSettings()
    {
        var settings = loadedSettings!.Clone();
        foreach (var group in PricePresetGroups.All)
            settings.DisplaySlots[group] = groupRows[group].Select(row => row.SelectedModel?.SelectionKey ?? "").ToList();
        return settings;
    }
    private void ModelPricesButton_Click(object sender, RoutedEventArgs e)
    {
        if (!CanEdit) return;
        var settings = CollectSettings();
        var window = new ModelPricesWindow(CurrentGroup(), querySession.Runtime, settings) { Owner = this };
        if (window.ShowDialog() == true && window.EditedSettings is { } edited)
        {
            loadedSettings = edited;
            BuildRows();
            SetStatus("模型价格已更新，点击保存设置后生效。");
        }
    }
    private void ClearSlotButton_Click(object sender, RoutedEventArgs e)
    {
        if (CanEdit && sender is FrameworkElement { DataContext: PriceDisplaySlotRow row }) row.Clear();
    }
    private void MoveUpButton_Click(object sender, RoutedEventArgs e) => MoveSlot(sender, -1);
    private void MoveDownButton_Click(object sender, RoutedEventArgs e) => MoveSlot(sender, 1);
    private void MoveSlot(object sender, int direction)
    {
        if (!CanEdit || sender is not FrameworkElement { DataContext: PriceDisplaySlotRow row }) return;
        var rows = groupRows[CurrentGroup()];
        var index = rows.IndexOf(row);
        var target = index + direction;
        if (target < 0 || target >= rows.Count) return;
        var model = row.SelectedModel;
        row.Select(rows[target].SelectedModel);
        rows[target].Select(model);
    }
    private void RestoreButton_Click(object sender, RoutedEventArgs e)
    {
        if (!CanEdit || loadedSettings is null) return;
        var group = CurrentGroup();
        loadedSettings = CollectSettings();
        loadedSettings.DisplaySlots[group] = PriceSettingsStore.Defaults().DisplaySlotsForGroup(group);
        BuildRows();
        SetStatus("已恢复当前来源的默认展示位置，点击保存设置后生效。");
    }
    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (!CanEdit || loadedSettings is null) return;
        try
        {
            var settings = CollectSettings();
            isSaving = true;
            UpdateEditingState();
            SetStatus("正在保存价格设置…");
            await querySession.RunAsync("保存价格设置", _ => { PriceSettingsStore.Save(settings); return true; }, requiresSharedIo: true);
            if (!querySession.IsStopping) DialogResult = true;
        }
        catch (OperationCanceledException) when (querySession.IsStopping) { }
        catch (Exception ex)
        {
            if (!querySession.IsStopping) SetStatus("保存失败，已保留当前编辑。修复后可再次点击保存。", ex.ToString());
        }
        finally
        {
            isSaving = false;
            if (!querySession.IsStopping) UpdateEditingState();
        }
    }
    protected override void OnClosed(EventArgs e)
    {
        querySession.Dispose();
        base.OnClosed(e);
    }
}

internal sealed class PriceDisplaySlotRow : INotifyPropertyChanged
{
    private const string Hidden = "不显示";
    private readonly IReadOnlyList<PricePreset> catalog;
    private string selectedProvider = Hidden;
    private PricePreset? selectedModel;
    public PriceDisplaySlotRow(int position, IReadOnlyList<PricePreset> catalog, string key)
    {
        PositionLabel = position == 1 ? "1 · 主价格" : position.ToString();
        this.catalog = catalog;
        Providers = new[] { Hidden }.Concat(catalog.Select(ProviderLabel).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(provider => provider)).ToList();
        Select(catalog.FirstOrDefault(item => string.Equals(item.SelectionKey, key, StringComparison.OrdinalIgnoreCase)));
    }
    public string PositionLabel { get; }
    public IReadOnlyList<string> Providers { get; }
    public IReadOnlyList<PricePreset> Models => catalog.Where(item => ProviderLabel(item) == SelectedProvider).ToList();
    public string SelectedProvider
    {
        get => selectedProvider;
        set
        {
            if (value is null || selectedProvider == value) return;
            selectedProvider = value;
            selectedModel = catalog.FirstOrDefault(item => ProviderLabel(item) == value);
            NotifyAll();
        }
    }
    public PricePreset? SelectedModel
    {
        get => selectedModel;
        set
        {
            // ComboBox clears selection while replacing its provider-filtered ItemsSource.
            if (ReferenceEquals(selectedModel, value) || value is null && selectedModel is not null) return;
            selectedModel = value;
            NotifyAll();
        }
    }
    public string PriceSummary => selectedModel is { } model
        ? $"{model.UncachedInput:0.####} / {model.CachedInput:0.####} / {model.EffectiveCacheWriteInput:0.####} / {model.Output:0.####}  {model.UnitLabel}"
        : "—";
    public string PriceSource => selectedModel is { } model ? $"{model.UnitLabel}\n{model.Source}" : "";
    public void Clear() => Select(null);
    public void Select(PricePreset? model)
    {
        selectedModel = model;
        selectedProvider = model is null ? Hidden : ProviderLabel(model);
        NotifyAll();
    }
    private static string ProviderLabel(PricePreset preset) => string.IsNullOrWhiteSpace(preset.Provider) ? "未命名供应商" : preset.Provider;
    public event PropertyChangedEventHandler? PropertyChanged;
    private void NotifyAll() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
}
