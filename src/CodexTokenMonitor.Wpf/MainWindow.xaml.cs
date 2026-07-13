using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using MediaColor = System.Windows.Media.Color;
using WpfBinding = System.Windows.Data.Binding;
using System.Windows.Media;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace CodexTokenMonitor;

public partial class MainWindow : Window
{
    private const double CostCardWidth = 190;
    private const double CostCardRightMargin = 12;
    private static readonly TimeSpan DayTimelineInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan MultiDayBreakdownInterval = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan MonthTimelineInterval = TimeSpan.FromHours(1);

    private readonly IReadOnlyDictionary<UsageSource, UsageSourceModule> usageModules = UsageSourceModules.Create();
    private readonly SemaphoreSlim usageQueryGate = new(1, 1);
    private readonly DispatcherTimer refreshTimer = new();
    private readonly BackgroundCacheWarmer backgroundCacheWarmer;
    private readonly ResetOpportunitySynchronizer resetOpportunitySynchronizer = new();
    private readonly BreakdownGridAdapter breakdownGridAdapter;
    private readonly object usageRefreshSync = new();
    private UsageSource activeSource = UsageSource.Codex;
    private bool initializing = true;
    private bool suppressRangeRefresh;
    private bool suppressDateRefresh;
    private bool suppressCycleRefresh;
    private bool suppressWeekTimeRefresh;
    private bool suppressStartTimeRefresh;
    private bool isRefreshing;
    private bool isQuotaRefreshing;
    private bool isClosed;
    private bool usageRefreshLoopRunning;
    private bool usageRefreshPending;
    private bool pendingCacheOnly;
    private long usageRefreshVersion;
    private Task usageRefreshLoopTask = Task.CompletedTask;
    private int lastVisibleCostColumnCount = -1;

    public MainWindow()
    {
        InitializeComponent();
        backgroundCacheWarmer = new BackgroundCacheWarmer(CurrentSource, () => isRefreshing, usageQueryGate, SetBackgroundStatus);
        breakdownGridAdapter = new BreakdownGridAdapter(BreakdownGrid);
        ConfigureBreakdownGrid();
        SyncRangeModeItems(CurrentModule());
        RangeModeBox.SelectedIndex = 0;
        DatePicker.SelectedDate = DateTime.Today;
        refreshTimer.Interval = TimeSpan.FromSeconds(30);
        refreshTimer.Tick += async (_, _) =>
        {
            if (isRefreshing || usageRefreshLoopRunning || isQuotaRefreshing || AutoRefreshBox.IsChecked != true)
            {
                return;
            }

            var range = GetSelectedRange();
            if (ShouldIncludeLiveToday(range))
            {
                await RefreshUsageAsync();
            }
        };
        refreshTimer.Start();
        initializing = false;
        Loaded += async (_, _) =>
        {
            UpdateRangeControls();
            var restored = TryRestoreLastDisplay();
            if (!restored)
            {
                await RefreshUsageAsync(cacheOnly: true);
            }

            await RefreshUsageAsync();
            _ = SyncResetOpportunitiesFromCodexAsync(showError: false);
            backgroundCacheWarmer.Start();
        };
        Closed += (_, _) =>
        {
            isClosed = true;
            refreshTimer.Stop();
            backgroundCacheWarmer.Dispose();
            LastDisplayStore.Flush();
        };
    }

    private async void WeekPickerButton_Click(object sender, RoutedEventArgs e)
    {
        await OpenWeekPickerAsync();
    }

    private async void RefreshDayButton_Click(object sender, RoutedEventArgs e)
    {
        await RefreshSelectedDayFromCacheAsync();
    }

    private void CostCardsViewport_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (initializing)
        {
            return;
        }

        ReflowCostColumnsForCurrentDisplay();
    }

    private async void PreviousButton_Click(object sender, RoutedEventArgs e)
    {
        await ShiftPeriodAsync(-1);
    }

    private async void NextButton_Click(object sender, RoutedEventArgs e)
    {
        await ShiftPeriodAsync(1);
    }

    private async void CurrentButton_Click(object sender, RoutedEventArgs e)
    {
        await JumpToCurrentPeriodAsync();
    }

    private async void StartNowButton_Click(object sender, RoutedEventArgs e)
    {
        var module = CurrentModule();
        module.CustomStartLocal = DateTimeOffset.UtcNow.ToOffset(CodexUsageReader.BeijingOffset);
        UpdateStartNowButtonState();
        await RefreshUsageAsync();
    }

    private async void RangeModeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (initializing || suppressRangeRefresh)
        {
            return;
        }

        await RangeModeChangedAsync();
    }

    private async void DatePicker_SelectedDateChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (initializing || suppressDateRefresh || DatePicker.SelectedDate is null)
        {
            return;
        }

        var module = CurrentModule();
        var previous = module.PickerValue;
        module.PickerValue = DatePicker.SelectedDate.Value.Date + previous.TimeOfDay;
        if (module.Mode == RangeMode.Week && DatePicker.SelectedDate.Value.Date == DateTime.Today)
        {
            module.PickerValue = DateTime.Now;
        }

        ClearCustomStart();
        UpdateRangeControls();
        await RefreshUsageAsync();
    }

    private async void WeekEndPicker_ValueChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (initializing || suppressWeekTimeRefresh || CurrentModule().Mode != RangeMode.Week || WeekEndPicker.Value is not DateTime selected)
        {
            return;
        }

        var module = CurrentModule();
        module.PickerValue = selected;
        ClearCustomStart();
        UpdateRangeControls();
        await RefreshUsageAsync();
    }

    private async void CustomStartPicker_ValueChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        var module = CurrentModule();
        if (initializing || suppressStartTimeRefresh || module.CustomStartLocal is null || CustomStartPicker.Value is not DateTime selected)
        {
            return;
        }

        module.CustomStartLocal = ToBeijingOffset(selected);
        UpdateStartNowButtonState();
        await RefreshUsageAsync();
    }

    private async void CycleBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (initializing || suppressCycleRefresh || CurrentModule() is not CodexUsageModule codexModule)
        {
            return;
        }

        codexModule.SelectedCycle = CycleBox.SelectedItem as CodexQuotaCycle;
        ClearCustomStart();
        UpdateRangeControls();
        await RefreshUsageAsync();
    }

    private void SourceTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (initializing || !ReferenceEquals(e.Source, SourceTabs))
        {
            return;
        }

        SaveActiveModuleState();
        activeSource = CurrentSource();
        var module = CurrentModule();
        RestoreModuleControls(module);
        if (module.TryGetDisplay(out var range, out var result))
        {
            ApplySummary(range, result, module);
            SetStatus($"已切换 {module.Title}");
            return;
        }

        ApplyEmptyModuleState(module);
        SetStatus($"{module.Title} 未刷新");
    }

    private void PriceSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        using var form = new PriceSettingsForm();
        if (form.ShowDialog() == Forms.DialogResult.OK)
        {
            CurrentCodexModule().CurrentQuotaEstimate = null;
            foreach (var module in usageModules.Values)
            {
                module.ClearDisplay();
            }

            _ = RefreshUsageAsync();
        }
    }

    private void ResetSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentModule() is not CodexUsageModule)
        {
            return;
        }

        using var form = new ResetOpportunityForm();
        form.ShowDialog();
        ApplyResetOpportunitySummary();
    }

    private async Task SyncResetOpportunitiesFromCodexAsync(bool showError)
    {
        var result = await resetOpportunitySynchronizer.SyncAsync();
        if (result is null || isClosed)
        {
            return;
        }

        if (result.Success)
        {
            ApplyResetOpportunitySummary();
            return;
        }

        if (showError)
        {
            System.Windows.MessageBox.Show(this, result.Message, "Codex Token 额度监控器", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void PlanSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentModule() is not CodexUsageModule)
        {
            return;
        }

        using var form = new SubscriptionPlanForm();
        if (form.ShowDialog() == Forms.DialogResult.OK)
        {
            ApplyCurrentPlanSummary();
            SetStatus("套餐设置已保存");
        }
    }

    private void QuotaEstimateButton_Click(object sender, RoutedEventArgs e)
    {
        var codexModule = CurrentCodexModule();
        var quota = codexModule.CurrentQuotaEstimate;
        if (quota is null)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow.ToOffset(CodexUsageReader.BeijingOffset);
        codexModule.QuotaCycles = CodexQuotaCycleReader.ReadWeeklyCycles(quota, now);
        var window = new QuotaEstimateWindow(quota, codexModule.QuotaCycles)
        {
            Owner = this
        };
        window.Show();
    }

    private async Task ShiftPeriodAsync(int delta)
    {
        ClearCustomStart();
        var module = CurrentModule();
        if (module.Mode == RangeMode.Cycle && module is CodexUsageModule codexModule)
        {
            UpdateCycleOptions(keepSelection: true);
            if (CycleBox.Items.Count == 0)
            {
                return;
            }

            var targetIndex = Math.Clamp(CycleBox.SelectedIndex - delta, 0, CycleBox.Items.Count - 1);
            if (targetIndex == CycleBox.SelectedIndex)
            {
                return;
            }

            suppressCycleRefresh = true;
            CycleBox.SelectedIndex = targetIndex;
            suppressCycleRefresh = false;
            codexModule.SelectedCycle = CycleBox.SelectedItem as CodexQuotaCycle;
            UpdateRangeControls();
            await RefreshUsageAsync();
            return;
        }

        module.PickerValue = module.Mode switch
        {
            RangeMode.Day => module.PickerValue.Date.AddDays(delta),
            RangeMode.Week => module.PickerValue.AddDays(delta * 7),
            RangeMode.Month => module.PickerValue.Date.AddMonths(delta),
            _ => module.PickerValue
        };
        UpdateRangeControls();
        await RefreshUsageAsync();
    }

    private async Task JumpToCurrentPeriodAsync()
    {
        ClearCustomStart();
        var module = CurrentModule();
        if (module.Mode == RangeMode.Cycle && module is CodexUsageModule codexModule)
        {
            UpdateCycleOptions(keepSelection: false);
            codexModule.SelectedCycle = CycleBox.SelectedItem as CodexQuotaCycle;
            UpdateRangeControls();
            await RefreshUsageAsync();
            return;
        }

        module.PickerValue = module.Mode == RangeMode.Week ? DateTime.Now : DateTime.Today;
        UpdateRangeControls();
        await RefreshUsageAsync();
    }

    private async Task RangeModeChangedAsync()
    {
        var module = CurrentModule();
        var mode = CurrentMode();
        ClearCustomStart();
        if (mode == RangeMode.Cycle && !module.SupportsCycle)
        {
            mode = RangeMode.Day;
            suppressRangeRefresh = true;
            RangeModeBox.SelectedIndex = 0;
            suppressRangeRefresh = false;
        }

        module.Mode = mode;
        if (mode == RangeMode.Week && module.PickerValue.Date == DateTime.Today)
        {
            module.PickerValue = DateTime.Now;
        }

        if (mode == RangeMode.Cycle)
        {
            UpdateCycleOptions(keepSelection: false);
        }

        UpdateRangeControls();
        await RefreshUsageAsync();
    }

    private async Task OpenWeekPickerAsync()
    {
        var module = CurrentModule();
        if (module.Mode != RangeMode.Week)
        {
            return;
        }

        var range = GetSelectedRange();
        if (WeekWindowPicker.TryPickEndDate(this, range.End.DateTime.Date, out var selectedEnd))
        {
            module.PickerValue = selectedEnd.Date + module.PickerValue.TimeOfDay;
            UpdateRangeControls();
            await RefreshUsageAsync();
        }
    }

    private async Task RefreshSelectedDayFromCacheAsync()
    {
        if (isRefreshing || CurrentModule().Mode != RangeMode.Day)
        {
            return;
        }

        backgroundCacheWarmer.CancelCurrent();
        var module = CurrentModule();
        var selectedDay = DateOnly.FromDateTime(module.PickerValue.Date);
        SetStatus($"清除 {module.Title} {selectedDay:yyyy-MM-dd} 缓存...");

        bool deleted;
        await usageQueryGate.WaitAsync();
        try
        {
            deleted = await Task.Run(() => module.Reader.RefreshCachedDay(selectedDay));
        }
        finally
        {
            usageQueryGate.Release();
        }

        module.ClearDisplay();
        SetStatus(deleted ? $"已清除 {selectedDay:yyyy-MM-dd}，正在重新解析..." : $"{selectedDay:yyyy-MM-dd} 无缓存，正在解析...");
        await RefreshUsageAsync();
        _ = backgroundCacheWarmer.WarmNowAsync();
    }

    private Task RefreshUsageAsync(bool cacheOnly = false)
    {
        lock (usageRefreshSync)
        {
            pendingCacheOnly = cacheOnly;
            usageRefreshPending = true;
            usageRefreshVersion++;
            if (!usageRefreshLoopRunning)
            {
                usageRefreshLoopRunning = true;
                usageRefreshLoopTask = RunUsageRefreshLoopAsync();
            }

            return usageRefreshLoopTask;
        }
    }

    private async Task RunUsageRefreshLoopAsync()
    {
        while (true)
        {
            bool cacheOnly;
            long requestVersion;
            lock (usageRefreshSync)
            {
                if (!usageRefreshPending)
                {
                    usageRefreshLoopRunning = false;
                    return;
                }

                cacheOnly = pendingCacheOnly;
                requestVersion = usageRefreshVersion;
                usageRefreshPending = false;
            }

            await RefreshUsageOnceAsync(cacheOnly, requestVersion);
        }
    }

    private async Task RefreshUsageOnceAsync(bool cacheOnly, long requestVersion)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        isRefreshing = true;
        SetBusy(true);
        SetStatus(cacheOnly ? "正在读取缓存..." : "正在刷新...");
        var module = CurrentModule();
        var source = module.Source;

        try
        {
            var range = GetSelectedRange();
            var includeLiveToday = !cacheOnly && ShouldIncludeLiveToday(range);
            var cachedQuota = module is CodexUsageModule codexModule ? codexModule.CurrentQuotaEstimate : null;
            if (backgroundCacheWarmer.IsRunning)
            {
                backgroundCacheWarmer.CancelCurrent();
                SetStatus("正在刷新...");
            }

            UsageQueryResult result;
            await usageQueryGate.WaitAsync();
            try
            {
                result = await Task.Run(() =>
                {
                    if (range.IsCustomStart)
                    {
                        var transientRows = module.Reader.ReadTransientDetailRows(range.Start, range.End);
                        var transientSummary = CreateSummaryFromRows(range, transientRows);
                        var transientQuota = ReadQuotaForRefresh(module.Reader, includeLiveToday, cachedQuota);
                        var transientQuotaSnapshots = module.Reader.SupportsQuota
                            ? ReadQuotaSnapshotsForRefresh(range, transientRows, includeLiveToday, transientQuota)
                            : Array.Empty<CodexQuotaSnapshot>();
                        return new UsageQueryResult(
                            transientSummary,
                            transientRows,
                            UsageBreakdownBuilder.EstimateCodingTime(transientRows),
                            transientQuota,
                            transientQuotaSnapshots)
                        {
                            DetailRows = transientRows
                        };
                    }

                    if (range.Mode == RangeMode.Day)
                    {
                        var dayUsage = module.Reader.ReadDay(range.Start, range.End, includeLiveToday);
                        var dayQuota = ReadQuotaForRefresh(module.Reader, includeLiveToday, cachedQuota);
                        var dayQuotaSnapshots = module.Reader.SupportsQuota
                            ? ReadQuotaSnapshotsForRefresh(range, dayUsage.Rows, includeLiveToday, dayQuota)
                            : Array.Empty<CodexQuotaSnapshot>();
                        return new UsageQueryResult(
                            dayUsage.Summary,
                            dayUsage.Rows,
                            UsageBreakdownBuilder.EstimateCodingTime(dayUsage.Rows),
                            dayQuota,
                            dayQuotaSnapshots)
                        {
                            DetailRows = dayUsage.Rows
                        };
                    }

                    var summary = includeLiveToday
                        ? module.Reader.ReadRange(range.Start, range.End, includeLiveToday)
                        : module.Reader.ReadCachedRange(range.Start, range.End);
                    var detailRows = module.Reader.ReadCachedDetailRows(range.Start, range.End);
                    var rows = UsageBreakdownBuilder.Build(range, summary, detailRows, MultiDayBreakdownInterval);
                    var codingTime = UsageBreakdownBuilder.EstimateCodingTimeForRange(
                        module.Reader,
                        range,
                        rows,
                        detailRows,
                        includeLiveToday,
                        !includeLiveToday);
                    var quota = ReadQuotaForRefresh(module.Reader, includeLiveToday, cachedQuota);
                    var quotaSnapshots = module.Reader.SupportsQuota
                        ? ReadQuotaSnapshotsForRefresh(range, rows, includeLiveToday, quota)
                        : Array.Empty<CodexQuotaSnapshot>();
                    return new UsageQueryResult(summary, rows, codingTime, quota, quotaSnapshots)
                    {
                        DetailRows = detailRows
                    };
                });
            }
            finally
            {
                usageQueryGate.Release();
            }

            if (requestVersion != Volatile.Read(ref usageRefreshVersion) || isClosed)
            {
                return;
            }

            module.StoreDisplay(range, result);
            if (CurrentSource() == source)
            {
                ApplySummary(range, result, module);
                SetStatus(includeLiveToday
                    ? $"已刷新 {DateTime.Now:HH:mm:ss} · {stopwatch.ElapsedMilliseconds:N0}ms"
                    : $"缓存命中 {DateTime.Now:HH:mm:ss} · {stopwatch.ElapsedMilliseconds:N0}ms");
                if (!cacheOnly && module.Reader.SupportsQuota)
                {
                    _ = RefreshQuotaSummaryAsync();
                }
            }
        }
        catch (Exception ex)
        {
            if (requestVersion == Volatile.Read(ref usageRefreshVersion) && !isClosed)
            {
                SetStatus("读取失败");
                System.Windows.MessageBox.Show(this, ex.Message, "Codex Token 额度监控器", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        finally
        {
            isRefreshing = false;
            SetBusy(false);
        }
    }

    private async Task RefreshQuotaSummaryAsync()
    {
        if (isQuotaRefreshing || CurrentModule() is not CodexUsageModule codexModule)
        {
            return;
        }

        isQuotaRefreshing = true;
        try
        {
            var cachedQuota = FreshQuotaOrNull(CodexUsageReader.ReadCachedQuotaEstimate());
            if (cachedQuota is not null)
            {
                codexModule.CurrentQuotaEstimate = LatestFreshQuota(codexModule.CurrentQuotaEstimate, cachedQuota);
                if (CurrentModule() is CodexUsageModule)
                {
                    ApplyQuotaSummary(codexModule, codexModule.CurrentQuotaEstimate);
                    ApplyQuotaToCurrentBreakdown(codexModule, codexModule.CurrentQuotaEstimate);
                }
            }

            var quota = await Task.Run(CodexUsageReader.ReadQuotaEstimate);
            var freshQuota = FreshQuotaOrNull(quota);
            if (freshQuota is not null)
            {
                codexModule.CurrentQuotaEstimate = LatestFreshQuota(codexModule.CurrentQuotaEstimate, freshQuota);
            }

            if (CurrentModule() is CodexUsageModule)
            {
                ApplyQuotaSummary(codexModule, codexModule.CurrentQuotaEstimate);
                ApplyQuotaToCurrentBreakdown(codexModule, codexModule.CurrentQuotaEstimate);
                if (CurrentModule().Mode == RangeMode.Cycle)
                {
                    UpdateCycleOptions(keepSelection: true);
                }
            }
        }
        catch
        {
        }
        finally
        {
            isQuotaRefreshing = false;
        }
    }

    private void ApplySummary(SelectedRange range, UsageQueryResult result, UsageSourceModule module)
    {
        Title = $"{module.Title} Token 额度监控器 - {range.Title}";
        TotalValue.Text = FormatTokenMillions(result.Summary.TotalTokens);
        PeriodValue.Text = $"{result.Summary.StartLocal:yyyy-MM-dd HH:mm} - {result.Summary.EndLocal:yyyy-MM-dd HH:mm:ss}  GMT+8";
        InputValue.Text = FormatTokenMillions(result.Summary.InputTokens);
        CachedValue.Text = FormatTokenMillions(result.Summary.CachedInputTokens);
        UncachedValue.Text = FormatTokenMillions(result.Summary.UncachedInputTokens);
        OutputValue.Text = FormatTokenAdaptive(result.Summary.OutputTokens);
        ReasoningValue.Text = FormatTokenAdaptive(result.Summary.ReasoningOutputTokens);
        CacheRatioValue.Text = result.Summary.InputTokens > 0 ? $"{result.Summary.CacheRatioPercent:N2}%" : "0.00%";
        EventsValue.Text = result.Summary.Events.ToString("N0");
        CodingTimeValue.Text = FormatDuration(result.CodingTime);

        var displayPresets = PriceSettingsStore.DisplayPresetsForSource(module.Source, count: 0).ToList();
        ApplyCostCards(displayPresets, result.Summary);
        if (module is CodexUsageModule codexModule)
        {
            codexModule.CurrentQuotaSnapshots = result.QuotaSnapshots;
        }

        ApplyQuotaSummary(module, result.Quota);
        if (module.Source == UsageSource.Codex && range.Mode == RangeMode.Cycle)
        {
            UpdateCycleOptions(keepSelection: true);
        }

        var showTimeline = result.BreakdownRows.Count > 0;
        if (showTimeline)
        {
            SetTimelineVisible(true);
            Timeline.SetData(range.Start, range.End, GetTimelineRows(module.Reader, range, result), GetTimelineInterval(range.Mode));
        }
        else
        {
            SetTimelineVisible(false);
        }

        ApplyBreakdownRows(range, result.BreakdownRows, result.QuotaSnapshots, module.Source, displayPresets);
        LastDisplayStore.Save(module.Source, range, result);
    }

    private void ApplyQuotaToCurrentBreakdown(CodexUsageModule module, CodexQuotaEstimate? quota)
    {
        if (!module.TryGetDisplay(out var range, out var result))
        {
            return;
        }

        var snapshots = MergeQuotaSnapshot(result.QuotaSnapshots, range, quota);
        var updatedResult = result with { Quota = quota, QuotaSnapshots = snapshots };
        module.CurrentQuotaSnapshots = snapshots;
        module.StoreDisplay(range, updatedResult);
        var displayPresets = PriceSettingsStore.DisplayPresetsForSource(module.Source, count: 0).ToList();
        ApplyBreakdownRows(range, result.BreakdownRows, snapshots, module.Source, displayPresets);
        LastDisplayStore.Save(module.Source, range, updatedResult);
    }

    private bool TryRestoreLastDisplay()
    {
        var snapshot = LastDisplayStore.Load();
        if (snapshot is null || !usageModules.TryGetValue(snapshot.Source, out var module))
        {
            return false;
        }

        var previousInitializing = initializing;
        initializing = true;
        SourceTabs.SelectedIndex = SourceToTabIndex(snapshot.Source);
        activeSource = snapshot.Source;
        RestoreModuleRange(module, snapshot.Range);
        if (module is CodexUsageModule codexModule)
        {
            codexModule.CurrentQuotaEstimate = snapshot.Result.Quota;
            codexModule.CurrentQuotaSnapshots = snapshot.Result.QuotaSnapshots;
        }

        module.StoreDisplay(snapshot.Range, snapshot.Result);
        RestoreModuleControls(module);
        initializing = previousInitializing;
        ApplySummary(snapshot.Range, snapshot.Result, module);
        SetStatus("已恢复上次显示，正在刷新...");
        return true;
    }

    private static int SourceToTabIndex(UsageSource source)
    {
        return source switch
        {
            UsageSource.ClaudeCode => 1,
            UsageSource.ZCode => 2,
            UsageSource.WorkBuddy => 3,
            _ => 0
        };
    }

    private static void RestoreModuleRange(UsageSourceModule module, SelectedRange range)
    {
        module.Mode = range.Mode;
        module.CustomStartLocal = range.IsCustomStart ? range.Start : null;
        module.PickerValue = range.Mode switch
        {
            RangeMode.Week => range.End.DateTime,
            RangeMode.Month => range.Start.DateTime,
            _ => range.Start.DateTime
        };
    }

    private void ApplyEmptyModuleState(UsageSourceModule module)
    {
        Title = $"{module.Title} Token 额度监控器";
        TotalValue.Text = "-";
        PeriodValue.Text = "-";
        InputValue.Text = "-";
        CachedValue.Text = "-";
        UncachedValue.Text = "-";
        OutputValue.Text = "-";
        ReasoningValue.Text = "-";
        CacheRatioValue.Text = "-";
        EventsValue.Text = "-";
        CodingTimeValue.Text = "-";
        CostCardsPanel.Children.Clear();
        SetTimelineVisible(false);
        ApplyQuotaSummary(module, module is CodexUsageModule codexModule ? codexModule.CurrentQuotaEstimate : null);
        ApplyBreakdownRows(GetSelectedRange(), Array.Empty<TokenUsageBucket>(), Array.Empty<CodexQuotaSnapshot>(), module.Source, PriceSettingsStore.DisplayPresetsForSource(module.Source, count: 0).ToList());
    }

    private void ApplyCostCards(IReadOnlyList<PricePreset> presets, TokenUsageSummary summary)
    {
        CostCardsPanel.Children.Clear();
        foreach (var preset in presets.Take(GetVisibleCostColumnCount(presets.Count)))
        {
            CostCardsPanel.Children.Add(CreateCostCard(preset, summary));
        }
    }

    private void ReflowCostColumnsForCurrentDisplay()
    {
        var module = CurrentModule();
        if (!module.TryGetDisplay(out var range, out var result))
        {
            return;
        }

        var displayPresets = PriceSettingsStore.DisplayPresetsForSource(module.Source, count: 0).ToList();
        var visibleCostColumnCount = GetVisibleCostColumnCount(displayPresets.Count);
        if (visibleCostColumnCount == lastVisibleCostColumnCount)
        {
            return;
        }

        ApplyCostCards(displayPresets, result.Summary);
        ApplyBreakdownRows(range, result.BreakdownRows, result.QuotaSnapshots, module.Source, displayPresets);
    }

    private static UIElement CreateCostCard(PricePreset preset, TokenUsageSummary summary)
    {
        var profile = preset.ToProfile();
        var card = new Grid();
        card.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        card.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        card.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var title = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(preset.Provider) ? preset.Model : preset.Provider,
            Foreground = new SolidColorBrush(MediaColor.FromRgb(92, 105, 122)),
            FontWeight = FontWeights.SemiBold,
            FontSize = 11.5,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetRow(title, 0);
        card.Children.Add(title);

        var subtitle = new TextBlock
        {
            Text = preset.Model,
            Foreground = new SolidColorBrush(MediaColor.FromRgb(101, 114, 130)),
            TextTrimming = TextTrimming.CharacterEllipsis,
            FontSize = 11.5,
            Margin = new Thickness(0, 1, 0, 2)
        };
        Grid.SetRow(subtitle, 1);
        card.Children.Add(subtitle);

        var value = new TextBlock
        {
            Text = FormatCost(summary.EstimateCost(profile), profile),
            Foreground = new SolidColorBrush(MediaColor.FromRgb(31, 41, 55)),
            FontSize = 22,
            FontWeight = FontWeights.Bold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetRow(value, 2);
        card.Children.Add(value);

        return new Border
        {
            Width = CostCardWidth,
            Height = 96,
            Padding = new Thickness(10, 5, 8, 3),
            Margin = new Thickness(0, 0, CostCardRightMargin, 0),
            Background = System.Windows.Media.Brushes.Transparent,
            Child = card
        };
    }

    private void ApplyBreakdownRows(
        SelectedRange range,
        IReadOnlyList<TokenUsageBucket> buckets,
        IReadOnlyList<CodexQuotaSnapshot> quotaSnapshots,
        UsageSource source,
        IReadOnlyList<PricePreset> displayPresets)
    {
        var tablePresets = displayPresets.Take(GetVisibleCostColumnCount(displayPresets.Count)).ToList();
        var tableProfiles = tablePresets.Select(preset => preset.ToProfile()).ToArray();
        var quotaLookup = source == UsageSource.Codex
            ? new QuotaSnapshotLookup(quotaSnapshots)
            : null;
        lastVisibleCostColumnCount = tablePresets.Count;
        var eventBreakdown = UsesEventBreakdown(range, buckets);
        var rows = new List<BreakdownRow>(buckets.Count);
        foreach (var bucket in buckets)
        {
            var rowIsEvent = IsEventBucket(range, bucket);
            rows.Add(new BreakdownRow
            {
                Label = FormatBucketLabel(range, bucket.StartLocal, rowIsEvent),
                Total = FormatBreakdownToken(bucket.TotalTokens, rowIsEvent),
                Input = FormatBreakdownToken(bucket.InputTokens, rowIsEvent),
                Cached = FormatBreakdownToken(bucket.CachedInputTokens, rowIsEvent),
                Uncached = FormatBreakdownToken(bucket.UncachedInputTokens, rowIsEvent),
                Output = FormatTokenAdaptive(bucket.OutputTokens),
                Price1 = tableProfiles.Length > 0 ? FormatCost(bucket.EstimateCost(tableProfiles[0]), tableProfiles[0]) : "-",
                Price2 = tableProfiles.Length > 1 ? FormatCost(bucket.EstimateCost(tableProfiles[1]), tableProfiles[1]) : "-",
                Price3 = tableProfiles.Length > 2 ? FormatCost(bucket.EstimateCost(tableProfiles[2]), tableProfiles[2]) : "-",
                Quota = quotaLookup is not null ? FormatQuotaSnapshotForBucket(range, bucket, quotaLookup, rowIsEvent) : "-"
            });
        }

        breakdownGridAdapter.ApplyRows(range, eventBreakdown, tablePresets, source == UsageSource.Codex, rows);
    }

    private void ApplyQuotaSummary(CodexQuotaEstimate? quota)
    {
        ApplyQuotaSummary(CurrentModule(), quota);
    }

    private void ApplyQuotaSummary(UsageSourceModule module, CodexQuotaEstimate? quota)
    {
        var show = module is CodexUsageModule;
        QuotaPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        ResetSettingsButton.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        PlanSettingsButton.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show)
        {
            return;
        }

        var codexModule = (CodexUsageModule)module;
        var effectiveQuota = LatestFreshQuota(codexModule.CurrentQuotaEstimate, quota);
        codexModule.CurrentQuotaEstimate = effectiveQuota;
        QuotaEstimateButton.IsEnabled = effectiveQuota is not null;
        ApplyCurrentPlanSummary();
        ApplyResetOpportunitySummary();
        ApplyResetPaceSummary(effectiveQuota?.Week);
        if (effectiveQuota is null)
        {
            Quota5hValue.Text = "5h";
            Quota5hDetail.Text = "";
            QuotaWeekValue.Text = "周";
            QuotaWeekDetail.Text = "";
            return;
        }

        ApplyQuotaWindow(Quota5hValue, Quota5hDetail, effectiveQuota.FiveHour, QuotaWindowDisplayMode.FiveHour);
        ApplyQuotaWindow(QuotaWeekValue, QuotaWeekDetail, effectiveQuota.Week, QuotaWindowDisplayMode.Week);
    }

    private void ApplyCurrentPlanSummary()
    {
        var now = DateTimeOffset.UtcNow.ToOffset(CodexUsageReader.BeijingOffset);
        var active = SubscriptionPlanStore.Load()
            .Where(item => item.StartLocal <= now && item.EndLocal > now)
            .OrderByDescending(item => item.StartLocal)
            .ToList();
        if (active.Count == 0)
        {
            PlanSpendValue.Text = "-";
            PlanSpendDetail.Text = "";
            return;
        }

        PlanSpendValue.Text = string.Join(" / ", active.Select(item => item.PlanName).Distinct());
        PlanSpendDetail.Text = FormatCny(active.Sum(item => item.AmountCny));
    }

    private void ApplyResetOpportunitySummary()
    {
        var now = DateTimeOffset.UtcNow.ToOffset(CodexUsageReader.BeijingOffset);
        var summary = ResetOpportunityStore.Summarize(now);
        ResetOpportunityValue.Text = ResetOpportunityFormatter.FormatCompactSummary(summary);
        ResetOpportunityDetail.Text = summary.AvailableRecords.Count == 0 ? "" : $"{summary.AvailableCount:N0} 张可用";
    }

    private void ApplyResetPaceSummary(CodexQuotaWindowEstimate? week)
    {
        if (week is null)
        {
            ResetPaceValue.Text = "-";
            ResetPaceDetail.Text = "";
            return;
        }

        var now = DateTimeOffset.UtcNow.ToOffset(CodexUsageReader.BeijingOffset);
        var report = QuotaPaceAnalyzer.Analyze(week, ResetOpportunityStore.Summarize(now));
        ResetPaceValue.Text = report.Rating;
        ResetPaceDetail.Text =
            $"已{report.UsedPercent:N0}% / 应{report.ExpectedUsedPercent:N0}% · " +
            $"{QuotaPaceAnalyzer.FormatPaceDelta(report.DeltaPercent)} · {report.DetailText}";
    }

    private static void ApplyQuotaWindow(TextBlock valueBlock, TextBlock detailBlock, CodexQuotaWindowEstimate? window, QuotaWindowDisplayMode mode)
    {
        if (window is null)
        {
            valueBlock.Text = mode == QuotaWindowDisplayMode.FiveHour ? "暂不限" : "周";
            detailBlock.Text = mode == QuotaWindowDisplayMode.FiveHour
                ? "当前未返回 5h 限制"
                : "当前未返回周额度";
            return;
        }

        var remainingPercent = Math.Max(0m, 100m - window.UsedPercent);
        var resetAt = window.ResetAtLocal ?? window.WindowEndLocal;
        var profile = PriceProfiles.PrimaryCodex;
        var usedCost = window.Usage.EstimateCost(profile);
        valueBlock.Text = $"{remainingPercent:N0}%";
        detailBlock.Text = mode == QuotaWindowDisplayMode.FiveHour
            ? $"5h {resetAt:HH:mm} · ≈ {FormatMoney(usedCost, profile)}"
            : $"周 {resetAt:MM-dd HH:mm} · {FormatQuotaLimit(window)}";
    }

    private SelectedRange GetSelectedRange()
    {
        var module = CurrentModule();
        var now = DateTimeOffset.UtcNow.ToOffset(CodexUsageReader.BeijingOffset);
        var selected = module.PickerValue;
        var selectedDay = new DateTimeOffset(selected.Year, selected.Month, selected.Day, 0, 0, 0, CodexUsageReader.BeijingOffset);
        var selectedDateTime = new DateTimeOffset(selected.Year, selected.Month, selected.Day, selected.Hour, selected.Minute, selected.Second, CodexUsageReader.BeijingOffset);

        if (module.CustomStartLocal is not null)
        {
            var startFromNow = module.CustomStartLocal.Value;
            var customEnd = now < startFromNow ? startFromNow : now;
            return new SelectedRange(startFromNow, customEnd, $"当前起算 {startFromNow:MM-dd HH:mm:ss}", "事件明细（起点后）", RangeMode.Day, true);
        }

        if (module.Mode == RangeMode.Cycle)
        {
            var cycle = SelectedCycle();
            if (cycle is null)
            {
                return new SelectedRange(now, now, "额度周期", "按天明细（额度周期）", RangeMode.Cycle);
            }

            var cycleEnd = cycle.PeriodEnd > now ? now : cycle.PeriodEnd;
            if (cycleEnd < cycle.PeriodStart)
            {
                cycleEnd = cycle.PeriodStart;
            }

            return new SelectedRange(cycle.PeriodStart, cycleEnd, cycle.IsCurrent ? "当前周期" : $"周期 {cycle.PeriodStart:MM-dd HH:mm}", "按天明细（额度周期）", RangeMode.Cycle);
        }

        DateTimeOffset start;
        DateTimeOffset periodEnd;
        string title;
        string breakdownTitle;
        switch (module.Mode)
        {
            case RangeMode.Week:
                periodEnd = selectedDateTime > now ? now : selectedDateTime;
                start = periodEnd.AddDays(-7);
                title = periodEnd >= now.AddSeconds(-2) ? "近一周" : $"7天至 {periodEnd:MM-dd HH:mm}";
                breakdownTitle = "按天明细（7天窗口）";
                break;
            case RangeMode.Month:
                start = new DateTimeOffset(selectedDay.Year, selectedDay.Month, 1, 0, 0, 0, CodexUsageReader.BeijingOffset);
                periodEnd = start.AddMonths(1);
                title = start.Year == now.Year && start.Month == now.Month ? "本月" : start.ToString("yyyy-MM");
                breakdownTitle = "按天明细（本月）";
                break;
            default:
                start = selectedDay;
                periodEnd = start.AddDays(1);
                title = start.Date == now.Date ? "今天" : start.ToString("yyyy-MM-dd");
                breakdownTitle = "事件明细（当天）";
                break;
        }

        var end = periodEnd > now ? now : periodEnd;
        if (end < start)
        {
            end = start;
        }

        return new SelectedRange(start, end, title, breakdownTitle, module.Mode);
    }

    private static bool ShouldIncludeLiveToday(SelectedRange range)
    {
        var now = DateTimeOffset.UtcNow.ToOffset(CodexUsageReader.BeijingOffset);
        var todayStart = new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, CodexUsageReader.BeijingOffset);
        return range.IsCustomStart ||
               range.Mode == RangeMode.Day && range.Start == todayStart ||
               range.Mode == RangeMode.Cycle && range.End >= now.AddSeconds(-2);
    }

    private void UpdateRangeControls()
    {
        var module = CurrentModule();
        SyncRangeModeItems(module);
        suppressRangeRefresh = true;
        RangeModeBox.SelectedIndex = ModeToIndex(module.Mode);
        suppressRangeRefresh = false;

        suppressDateRefresh = true;
        DatePicker.SelectedDate = module.PickerValue.Date;
        suppressDateRefresh = false;

        suppressWeekTimeRefresh = true;
        WeekEndPicker.Value = module.PickerValue;
        suppressWeekTimeRefresh = false;

        if (module.Mode == RangeMode.Cycle)
        {
            UpdateCycleOptions(keepSelection: true);
        }

        CurrentButton.Content = module.Mode switch
        {
            RangeMode.Week => "近一周",
            RangeMode.Month => "本月",
            RangeMode.Cycle => "当前周期",
            _ => "今天"
        };
        DatePicker.Visibility = module.Mode is RangeMode.Cycle or RangeMode.Week ? Visibility.Collapsed : Visibility.Visible;
        CycleBox.Visibility = module.Mode == RangeMode.Cycle ? Visibility.Visible : Visibility.Collapsed;
        var now = DateTimeOffset.UtcNow.ToOffset(CodexUsageReader.BeijingOffset);
        var range = GetSelectedRange();
        if (module.Mode == RangeMode.Cycle)
        {
            PreviousButton.IsEnabled = CycleBox.SelectedIndex >= 0 && CycleBox.SelectedIndex < CycleBox.Items.Count - 1;
            NextButton.IsEnabled = CycleBox.SelectedIndex > 0;
            CurrentButton.IsEnabled = CycleBox.SelectedIndex != 0 && CycleBox.Items.Count > 0;
        }
        else
        {
            var currentStart = module.Mode switch
            {
                RangeMode.Week => now.AddDays(-7),
                RangeMode.Month => new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, CodexUsageReader.BeijingOffset),
                _ => new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, CodexUsageReader.BeijingOffset)
            };
            PreviousButton.IsEnabled = true;
            NextButton.IsEnabled = module.Mode == RangeMode.Week
                ? range.End < now.AddSeconds(-2)
                : range.Start < currentStart;
            CurrentButton.IsEnabled = true;
        }

        UpdateStartNowButtonState();
        UpdateWeekPickerState();
        UpdateRefreshDayButtonState();
        UpdateAutoRefreshState();
    }

    private void UpdateCycleOptions(bool keepSelection)
    {
        if (CurrentModule() is not CodexUsageModule codexModule)
        {
            suppressCycleRefresh = true;
            CycleBox.ItemsSource = null;
            suppressCycleRefresh = false;
            return;
        }

        var selected = keepSelection ? codexModule.SelectedCycle ?? SelectedCycle() : null;
        var now = DateTimeOffset.UtcNow.ToOffset(CodexUsageReader.BeijingOffset);
        codexModule.QuotaCycles = CodexQuotaCycleReader.ReadWeeklyCycles(codexModule.CurrentQuotaEstimate, now);
        suppressCycleRefresh = true;
        CycleBox.ItemsSource = codexModule.QuotaCycles;
        if (codexModule.QuotaCycles.Count > 0)
        {
            var selectedItem = selected is null
                ? codexModule.QuotaCycles[0]
                : codexModule.QuotaCycles.FirstOrDefault(item => SameCycle(item, selected)) ?? codexModule.QuotaCycles[0];
            CycleBox.SelectedItem = selectedItem;
            codexModule.SelectedCycle = selectedItem;
        }
        else
        {
            codexModule.SelectedCycle = null;
        }

        suppressCycleRefresh = false;
    }

    private CodexQuotaCycle? SelectedCycle()
    {
        return CycleBox.SelectedItem as CodexQuotaCycle;
    }

    private static bool SameCycle(CodexQuotaCycle first, CodexQuotaCycle second)
    {
        return first.PeriodStart == second.PeriodStart &&
               first.PeriodEnd == second.PeriodEnd &&
               first.ResetAt == second.ResetAt;
    }

    private static DateTimeOffset ToBeijingOffset(DateTime value)
    {
        return new DateTimeOffset(
            value.Year,
            value.Month,
            value.Day,
            value.Hour,
            value.Minute,
            value.Second,
            CodexUsageReader.BeijingOffset);
    }

    private void ClearCustomStart()
    {
        var module = CurrentModule();
        if (module.CustomStartLocal is null)
        {
            UpdateStartNowButtonState();
            return;
        }

        module.CustomStartLocal = null;
        UpdateStartNowButtonState();
    }

    private void UpdateStartNowButtonState()
    {
        var customStart = CurrentModule().CustomStartLocal;
        var enabled = CurrentModule().Mode == RangeMode.Day || customStart is not null;
        StartNowButton.IsEnabled = enabled && !isRefreshing;
        StartNowButton.Content = customStart is null ? "从当前算" : "重设起点";
        StartNowButton.Background = new SolidColorBrush(customStart is null
            ? MediaColor.FromRgb(229, 234, 242)
            : MediaColor.FromRgb(21, 128, 106));
        StartNowButton.Foreground = new SolidColorBrush(customStart is null
            ? MediaColor.FromRgb(55, 65, 81)
            : MediaColor.FromRgb(255, 255, 255));

        suppressStartTimeRefresh = true;
        CustomStartPicker.Value = customStart?.DateTime;
        CustomStartPicker.Visibility = customStart is null ? Visibility.Collapsed : Visibility.Visible;
        suppressStartTimeRefresh = false;
    }

    private void UpdateWeekPickerState()
    {
        var show = CurrentModule().Mode == RangeMode.Week && CurrentModule().CustomStartLocal is null;
        WeekEndPicker.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        WeekPickerButton.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        WeekPickerButton.IsEnabled = show && !isRefreshing;
    }

    private void UpdateRefreshDayButtonState()
    {
        var show = CurrentModule().Mode == RangeMode.Day;
        RefreshDayButton.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        RefreshDayButton.IsEnabled = show && !isRefreshing;
    }

    private void SetBusy(bool busy)
    {
        isRefreshing = busy;
        RangeModeBox.IsEnabled = !busy;
        DatePicker.IsEnabled = !busy;
        WeekEndPicker.IsEnabled = !busy;
        CycleBox.IsEnabled = !busy;
        PreviousButton.IsEnabled = !busy;
        NextButton.IsEnabled = !busy;
        CurrentButton.IsEnabled = !busy;
        SourceTabs.IsEnabled = !busy;
        if (busy)
        {
            StartNowButton.IsEnabled = false;
            WeekPickerButton.IsEnabled = false;
            RefreshDayButton.IsEnabled = false;
        }
        else
        {
            UpdateRangeControls();
        }

        UpdateAutoRefreshState();
    }

    private void UpdateAutoRefreshState()
    {
        AutoRefreshBox.IsEnabled = ShouldIncludeLiveToday(GetSelectedRange()) && !isRefreshing;
    }

    private void SetTimelineVisible(bool visible)
    {
        var wasVisible = TimelineHost.Visibility == Visibility.Visible;
        TimelineHost.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        TimelineSplitter.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        TimelineSplitterRow.Height = visible ? new GridLength(10) : new GridLength(0);
        if (visible)
        {
            if (!wasVisible || TimelineRow.Height.Value <= 0)
            {
                TimelineRow.Height = new GridLength(2, GridUnitType.Star);
                BreakdownRow.Height = new GridLength(1, GridUnitType.Star);
            }
        }
        else
        {
            TimelineRow.Height = new GridLength(0);
            BreakdownRow.Height = new GridLength(1, GridUnitType.Star);
        }

        if (!visible)
        {
            Timeline.ClearData();
        }
    }

    private void SetStatus(string text)
    {
        StatusText.Text = text;
    }

    private void SetBackgroundStatus(string text)
    {
        StatusText.Text = text;
        QueryStatusText.Text = text;
    }

    private UsageSource CurrentSource()
    {
        return SourceTabs.SelectedIndex switch
        {
            1 => UsageSource.ClaudeCode,
            2 => UsageSource.ZCode,
            3 => UsageSource.WorkBuddy,
            _ => UsageSource.Codex
        };
    }

    private UsageSourceModule CurrentModule()
    {
        return usageModules[CurrentSource()];
    }

    private CodexUsageModule CurrentCodexModule()
    {
        return (CodexUsageModule)usageModules[UsageSource.Codex];
    }

    private RangeMode CurrentMode()
    {
        return RangeModeBox.SelectedIndex switch
        {
            1 => RangeMode.Week,
            2 => RangeMode.Month,
            3 => RangeMode.Cycle,
            _ => RangeMode.Day
        };
    }

    private void SaveActiveModuleState()
    {
        if (!usageModules.TryGetValue(activeSource, out var module))
        {
            return;
        }

        module.Mode = CurrentMode();
        if (module.Mode == RangeMode.Week && WeekEndPicker.Value is { } weekEnd)
        {
            module.PickerValue = weekEnd;
        }
        else if (DatePicker.SelectedDate is { } selectedDate)
        {
            module.PickerValue = selectedDate.Date + module.PickerValue.TimeOfDay;
        }

        if (module is CodexUsageModule codexModule)
        {
            codexModule.SelectedCycle = SelectedCycle();
        }
    }

    private void RestoreModuleControls(UsageSourceModule module)
    {
        SyncRangeModeItems(module);
        UpdateRangeControls();
    }

    private void SyncRangeModeItems(UsageSourceModule module)
    {
        var labels = module.SupportsCycle
            ? new[] { "按天", "按周", "按月", "按周期" }
            : new[] { "按天", "按周", "按月" };
        if (RangeModeBox.Items.Count == labels.Length)
        {
            return;
        }

        suppressRangeRefresh = true;
        RangeModeBox.Items.Clear();
        foreach (var label in labels)
        {
            RangeModeBox.Items.Add(label);
        }

        suppressRangeRefresh = false;
    }

    private static int ModeToIndex(RangeMode mode)
    {
        return mode switch
        {
            RangeMode.Week => 1,
            RangeMode.Month => 2,
            RangeMode.Cycle => 3,
            _ => 0
        };
    }

    private void ConfigureBreakdownGrid()
    {
        breakdownGridAdapter.ConfigureInitialColumns();
    }

    private static CodexQuotaEstimate? ReadQuotaForRefresh(IUsageSourceReader reader, bool includeLiveToday, CodexQuotaEstimate? cachedQuota)
    {
        if (!reader.SupportsQuota)
        {
            return null;
        }

        _ = includeLiveToday;
        return FreshQuotaOrNull(cachedQuota) ?? CodexUsageReader.ReadCachedQuotaEstimate();
    }

    private static CodexQuotaEstimate? FreshQuotaOrNull(CodexQuotaEstimate? quota)
    {
        if (quota is null)
        {
            return null;
        }

        var now = DateTimeOffset.UtcNow.ToOffset(CodexUsageReader.BeijingOffset);
        return QuotaFreshness.IsFresh(quota.SnapshotLocal, now) ? quota : null;
    }

    private static CodexQuotaEstimate? LatestFreshQuota(CodexQuotaEstimate? first, CodexQuotaEstimate? second)
    {
        var freshFirst = FreshQuotaOrNull(first);
        var freshSecond = FreshQuotaOrNull(second);
        if (freshFirst is null)
        {
            return freshSecond;
        }

        if (freshSecond is null)
        {
            return freshFirst;
        }

        return freshFirst.SnapshotLocal >= freshSecond.SnapshotLocal ? freshFirst : freshSecond;
    }

    private static IReadOnlyList<CodexQuotaSnapshot> ReadQuotaSnapshotsForRefresh(
        SelectedRange range,
        IReadOnlyList<TokenUsageBucket> rows,
        bool includeLiveToday,
        CodexQuotaEstimate? quota)
    {
        var anchors = rows
            .Select(bucket =>
            {
                if (IsEventBucket(range, bucket))
                {
                    return bucket.StartLocal;
                }

                var bucketEnd = bucket.StartLocal.AddDays(1);
                return (bucketEnd < range.End ? bucketEnd : range.End).AddTicks(-1);
            })
            .Where(anchor => anchor >= range.Start && anchor < range.End)
            .Distinct()
            .ToList();
        var supplemental = quota is null
            ? Array.Empty<CodexQuotaSnapshot>()
            : new[]
            {
                new CodexQuotaSnapshot(
                    quota.SnapshotLocal,
                    quota.LimitId,
                    quota.LimitName,
                    quota.FiveHour?.UsedPercent,
                    quota.FiveHour?.ResetAtLocal,
                    quota.Week?.UsedPercent,
                    quota.Week?.ResetAtLocal)
            };

        return CodexUsageReader.ReadMaterializedQuotaTimeline(
            anchors,
            supplemental,
            refreshExisting: false);
    }

    private static IReadOnlyList<CodexQuotaSnapshot> FilterQuotaSnapshotsForQuota(
        IEnumerable<CodexQuotaSnapshot> snapshots,
        CodexQuotaEstimate? quota)
    {
        var filtered = snapshots
            .Where(CodexUsageReader.IsGeneralCodexQuotaSnapshot)
            .OrderBy(item => item.SnapshotLocal)
            .ToList();

        return CodexQuotaCycleReader.MarkTransientResetOutliers(
            RemoveShadowedZeroQuotaSnapshots(filtered));
    }

    private static IReadOnlyList<CodexQuotaSnapshot> RemoveShadowedZeroQuotaSnapshots(IReadOnlyList<CodexQuotaSnapshot> snapshots)
    {
        if (snapshots.Count == 0)
        {
            return snapshots;
        }

        var usefulSnapshots = snapshots.Where(HasNonZeroQuotaUsage).ToList();
        if (usefulSnapshots.Count == 0)
        {
            return snapshots;
        }

        return snapshots
            .Where(snapshot =>
                !IsZeroQuotaSnapshot(snapshot) ||
                !usefulSnapshots.Any(useful =>
                    Math.Abs((useful.SnapshotLocal - snapshot.SnapshotLocal).TotalMinutes) <= 10 &&
                    QuotaWindowsOverlap(snapshot, useful)))
            .ToList();
    }

    private static bool IsZeroQuotaSnapshot(CodexQuotaSnapshot snapshot)
    {
        return snapshot.FiveHourUsedPercent == 0m && snapshot.WeekUsedPercent == 0m;
    }

    private static bool HasNonZeroQuotaUsage(CodexQuotaSnapshot snapshot)
    {
        return (snapshot.FiveHourUsedPercent ?? 0m) > 0m ||
               (snapshot.WeekUsedPercent ?? 0m) > 0m;
    }

    private static bool QuotaWindowsOverlap(CodexQuotaSnapshot first, CodexQuotaSnapshot second)
    {
        return SameQuotaReset(first.FiveHourResetAtLocal, second.FiveHourResetAtLocal) ||
               SameQuotaReset(first.WeekResetAtLocal, second.WeekResetAtLocal);
    }

    private static bool SameQuotaReset(DateTimeOffset? first, DateTimeOffset? second)
    {
        if (first is null || second is null)
        {
            return false;
        }

        return Math.Abs((first.Value - second.Value).TotalMinutes) <= 10;
    }

    private static IReadOnlyList<CodexQuotaSnapshot> MergeQuotaSnapshot(
        IReadOnlyList<CodexQuotaSnapshot> snapshots,
        SelectedRange range,
        CodexQuotaEstimate? quota)
    {
        var filtered = FilterQuotaSnapshotsForQuota(snapshots, quota);
        if (quota is null || quota.SnapshotLocal < range.Start || quota.SnapshotLocal >= range.End)
        {
            return filtered;
        }

        var merged = filtered
            .Append(new CodexQuotaSnapshot(
                quota.SnapshotLocal,
                quota.LimitId,
                quota.LimitName,
                quota.FiveHour?.UsedPercent,
                quota.FiveHour?.ResetAtLocal,
                quota.Week?.UsedPercent,
                quota.Week?.ResetAtLocal));

        return FilterQuotaSnapshotsForQuota(merged, quota)
            .GroupBy(item => $"{item.SnapshotLocal:O}|{item.LimitId ?? ""}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(item => item.WeekUsedPercent ?? -1m).First())
            .OrderBy(item => item.SnapshotLocal)
            .ToList();
    }

    private static TokenUsageSummary CreateSummaryFromRows(SelectedRange range, IReadOnlyList<TokenUsageBucket> rows)
    {
        var summary = new TokenUsageSummary
        {
            StartLocal = range.Start,
            EndLocal = range.End
        };

        foreach (var row in rows)
        {
            summary.Add(row.StartLocal, row.InputTokens, row.CachedInputTokens, row.OutputTokens, row.ReasoningOutputTokens, row.TotalTokens);
        }

        return summary;
    }

    private static TimeSpan? GetTimelineInterval(RangeMode mode)
    {
        return mode switch
        {
            RangeMode.Day => DayTimelineInterval,
            RangeMode.Week or RangeMode.Cycle => MultiDayBreakdownInterval,
            RangeMode.Month => MonthTimelineInterval,
            _ => null
        };
    }

    private static IReadOnlyList<TokenUsageBucket> GetTimelineRows(IUsageSourceReader reader, SelectedRange range, UsageQueryResult result)
    {
        if (range.Mode != RangeMode.Month)
        {
            return result.BreakdownRows;
        }

        var detailRows = result.DetailRows.Count > 0
            ? result.DetailRows
            : reader.ReadCachedDetailRows(range.Start, range.End);
        return detailRows.Count > 0 ? detailRows : result.BreakdownRows;
    }

    private static bool UsesEventBreakdown(SelectedRange range, IReadOnlyList<TokenUsageBucket> rows)
    {
        return range.IsCustomStart || range.Mode == RangeMode.Day || rows.Any(row => IsEventBucket(range, row));
    }

    private static bool IsEventBucket(SelectedRange range, TokenUsageBucket bucket)
    {
        if (range.IsCustomStart || range.Mode == RangeMode.Day)
        {
            return true;
        }

        if (range.Mode is RangeMode.Week or RangeMode.Cycle)
        {
            return bucket.LastTokenEventLocal is null || bucket.LastTokenEventLocal.Value < bucket.StartLocal.Add(MultiDayBreakdownInterval);
        }

        return false;
    }

    private static void AddBucketValues(TokenUsageBucket target, TokenUsageBucket source)
    {
        target.MergeFrom(source);
    }

    private static string FormatBucketLabel(SelectedRange range, DateTimeOffset start, bool eventBreakdown)
    {
        if (range.IsCustomStart)
        {
            return start.ToString("yyyy-MM-dd HH:mm:ss");
        }

        if (range.Mode == RangeMode.Day)
        {
            return start.ToString("HH:mm:ss");
        }

        return eventBreakdown ? start.ToString("MM-dd HH:mm") : start.ToString("yyyy-MM-dd");
    }

    private static string FormatQuotaSnapshotForBucket(SelectedRange range, TokenUsageBucket bucket, QuotaSnapshotLookup lookup, bool eventBreakdown)
    {
        var snapshot = lookup.Select(range, bucket, eventBreakdown);
        return snapshot is null ||
               snapshot.FiveHourUsedPercent is null && snapshot.WeekUsedPercent is null
            ? "-"
            : snapshot.IsAnomaly
                ? "异常"
            : $"{FormatQuotaRemaining(snapshot.FiveHourUsedPercent)} / {FormatQuotaRemaining(snapshot.WeekUsedPercent)}";
    }

    private static CodexQuotaSnapshot? SelectQuotaSnapshotForBucket(SelectedRange range, TokenUsageBucket bucket, IReadOnlyList<CodexQuotaSnapshot> snapshots, bool eventBreakdown)
    {
        if (snapshots.Count == 0)
        {
            return null;
        }

        var ordered = snapshots.OrderBy(item => item.SnapshotLocal).ToList();
        if (eventBreakdown)
        {
            var eventTolerance = range.Mode == RangeMode.Day || range.IsCustomStart
                ? TimeSpan.FromMinutes(2)
                : TimeSpan.FromMinutes(10);

            return InterpolateQuotaSnapshotForAnchor(ordered, bucket.StartLocal, eventTolerance);
        }

        var bucketEnd = bucket.StartLocal.AddDays(1);

        if (!eventBreakdown)
        {
            var inBucket = ordered
                .Where(item => item.SnapshotLocal >= bucket.StartLocal && item.SnapshotLocal < bucketEnd)
                .LastOrDefault();
            if (inBucket is not null)
            {
                return inBucket;
            }
        }

        var anchor = eventBreakdown ? bucket.StartLocal : bucketEnd;
        var snapshot = ordered
            .Where(item => item.SnapshotLocal <= anchor)
            .LastOrDefault();
        if (snapshot is not null)
        {
            return snapshot;
        }

        var fallbackTolerance = TimeSpan.FromMinutes(2);
        return ordered
            .Where(item => item.SnapshotLocal > anchor && item.SnapshotLocal <= anchor.Add(fallbackTolerance))
            .FirstOrDefault();
    }

    private static CodexQuotaSnapshot? InterpolateQuotaSnapshotForAnchor(
        IReadOnlyList<CodexQuotaSnapshot> ordered,
        DateTimeOffset anchor,
        TimeSpan nearestTolerance)
    {
        var nearest = ordered
            .Where(item => Math.Abs((item.SnapshotLocal - anchor).TotalSeconds) <= nearestTolerance.TotalSeconds)
            .OrderBy(item => Math.Abs((item.SnapshotLocal - anchor).TotalSeconds))
            .ThenByDescending(HasNonZeroQuotaUsage)
            .ThenByDescending(item => item.SnapshotLocal <= anchor)
            .FirstOrDefault();

        if (nearest?.IsAnomaly == true &&
            Math.Abs((nearest.SnapshotLocal - anchor).TotalSeconds) <= 2)
        {
            return nearest;
        }

        var trusted = ordered.Where(item => !item.IsAnomaly).ToList();

        var before = trusted.LastOrDefault(item => item.SnapshotLocal <= anchor);
        var after = trusted.FirstOrDefault(item => item.SnapshotLocal >= anchor);
        if (before is not null && after is not null && before.SnapshotLocal != after.SnapshotLocal)
        {
            var fiveHourUsed = InterpolateUsedPercent(
                before,
                after,
                anchor,
                item => item.FiveHourUsedPercent,
                item => item.FiveHourResetAtLocal);
            var weekUsed = InterpolateUsedPercent(
                before,
                after,
                anchor,
                item => item.WeekUsedPercent,
                item => item.WeekResetAtLocal);

            if (fiveHourUsed is not null || weekUsed is not null)
            {
                return new CodexQuotaSnapshot(
                    anchor,
                    !string.IsNullOrWhiteSpace(before.LimitId) ? before.LimitId : after.LimitId,
                    !string.IsNullOrWhiteSpace(before.LimitName) ? before.LimitName : after.LimitName,
                    fiveHourUsed,
                    SelectInterpolatedReset(before.FiveHourResetAtLocal, after.FiveHourResetAtLocal),
                    weekUsed,
                    SelectInterpolatedReset(before.WeekResetAtLocal, after.WeekResetAtLocal));
            }
        }

        if (nearest is not null && !nearest.IsAnomaly)
        {
            return nearest;
        }

        return trusted
            .OrderBy(item => Math.Abs((item.SnapshotLocal - anchor).TotalSeconds))
            .FirstOrDefault(item => Math.Abs((item.SnapshotLocal - anchor).TotalSeconds) <= nearestTolerance.TotalSeconds);
    }

    private static decimal? InterpolateUsedPercent(
        CodexQuotaSnapshot before,
        CodexQuotaSnapshot after,
        DateTimeOffset anchor,
        Func<CodexQuotaSnapshot, decimal?> usedSelector,
        Func<CodexQuotaSnapshot, DateTimeOffset?> resetSelector)
    {
        var beforeUsed = usedSelector(before);
        var afterUsed = usedSelector(after);
        if (beforeUsed is null || afterUsed is null)
        {
            return beforeUsed ?? afterUsed;
        }

        if (!SameQuotaReset(resetSelector(before), resetSelector(after)))
        {
            return null;
        }

        if (after.SnapshotLocal <= before.SnapshotLocal)
        {
            return ClampPercent(beforeUsed.Value);
        }

        if (afterUsed.Value + 1m < beforeUsed.Value)
        {
            return null;
        }

        var ratio = (decimal)((anchor - before.SnapshotLocal).TotalSeconds / (after.SnapshotLocal - before.SnapshotLocal).TotalSeconds);
        return ClampPercent(beforeUsed.Value + ((afterUsed.Value - beforeUsed.Value) * ratio));
    }

    private static DateTimeOffset? SelectInterpolatedReset(DateTimeOffset? before, DateTimeOffset? after)
    {
        return SameQuotaReset(before, after) ? after ?? before : null;
    }

    private static decimal ClampPercent(decimal value)
    {
        return Math.Max(0m, Math.Min(100m, value));
    }

    private static string FormatQuotaRemaining(decimal? usedPercent)
    {
        return usedPercent is null ? "-" : $"{Math.Max(0m, 100m - usedPercent.Value):N0}%";
    }

    private static string FormatQuotaLimit(CodexQuotaWindowEstimate? window)
    {
        if (window is null || window.UsedPercent <= 0m)
        {
            return "-";
        }

        var profile = PriceProfiles.PrimaryCodex;
        var usedCost = window.Usage.EstimateCost(profile);
        return $"≈{FormatMoney(usedCost / (window.UsedPercent / 100m), profile)}";
    }

    private static string FormatPresetColumnTitle(PricePreset? preset, string fallback)
    {
        if (preset is null)
        {
            return fallback;
        }

        var text = string.IsNullOrWhiteSpace(preset.Provider) ? preset.Model : $"{preset.Provider} {preset.Model}";
        return text.Length <= 18 ? text : $"{text[..16]}...";
    }

    private int GetVisibleCostColumnCount(int presetCount)
    {
        if (presetCount <= 0)
        {
            return 0;
        }

        var availableWidth = CostCardsViewport.ActualWidth;
        if (double.IsNaN(availableWidth) || availableWidth <= 0)
        {
            return Math.Min(3, presetCount);
        }

        var visible = (int)Math.Floor((availableWidth + CostCardRightMargin) / (CostCardWidth + CostCardRightMargin));
        return Math.Clamp(visible, 1, presetCount);
    }

    private static string FormatTokenMillions(long value)
    {
        return $"{value / 1_000_000d:N3}M";
    }

    private static string FormatTokenAdaptive(long value)
    {
        if (value >= 1_000_000)
        {
            return FormatTokenMillions(value);
        }

        if (value >= 10_000)
        {
            return $"{value / 1_000d:N1}K";
        }

        if (value >= 1_000)
        {
            return $"{value / 1_000d:N2}K";
        }

        return value.ToString("N0");
    }

    private static string FormatBreakdownToken(long value, bool useEventScale)
    {
        return useEventScale ? FormatTokenAdaptive(value) : FormatTokenMillions(value);
    }

    private static string FormatMoney(decimal value, PriceProfile profile)
    {
        return value switch
        {
            >= 100 => $"{profile.CurrencySymbol}{value:N0}",
            >= 10 => $"{profile.CurrencySymbol}{value:N2}",
            >= 1 => $"{profile.CurrencySymbol}{value:N3}",
            _ => $"{profile.CurrencySymbol}{value:N4}"
        };
    }

    private static string FormatCost(decimal value, PriceProfile profile)
    {
        return string.Equals(profile.CurrencySymbol, "Credits", StringComparison.OrdinalIgnoreCase)
            ? FormatCredits(value)
            : FormatMoney(value, profile);
    }

    private static string FormatCredits(decimal value)
    {
        if (value >= 100_000_000m)
        {
            return $"{value / 100_000_000m:N2}亿";
        }

        if (value >= 1_000_000m)
        {
            return $"{value / 1_000_000m:N2}M";
        }

        return $"{value:N0}";
    }

    private static string FormatCny(decimal value)
    {
        return value switch
        {
            >= 100 => $"¥{value:N0}",
            >= 10 => $"¥{value:N2}",
            >= 1 => $"¥{value:N2}",
            _ => $"¥{value:N4}"
        };
    }

    private static string FormatDuration(TimeSpan value)
    {
        if (value <= TimeSpan.Zero)
        {
            return "-";
        }

        return value.TotalHours >= 1
            ? $"{(int)value.TotalHours}h {value.Minutes}m"
            : $"{Math.Max(1, (int)Math.Round(value.TotalMinutes))}m";
    }

    private enum QuotaWindowDisplayMode
    {
        FiveHour,
        Week
    }

}
