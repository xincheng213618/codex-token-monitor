using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace CodexTokenMonitor;

public partial class MainWindow : Window
{
    private readonly UsageDisplayViewModel displayViewModel = new();
    private const double CostCardWidth = 190;
    private const double CostCardRightMargin = 10;
    private static readonly TimeSpan DayTimelineInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan MultiDayBreakdownInterval = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan MonthTimelineInterval = TimeSpan.FromHours(1);

    private readonly IReadOnlyDictionary<UsageSource, UsageSourceModule> usageModules = UsageSourceModules.Create();
    private readonly MonitorRuntime runtime = new();
    private SemaphoreSlim usageQueryGate => runtime.SharedIoGate;
    private readonly DispatcherTimer refreshTimer = new();
    private readonly BackgroundCacheWarmer backgroundCacheWarmer;
    private CacheDetailsWindow? cacheDetailsWindow;
    private readonly ResetOpportunitySynchronizer resetOpportunitySynchronizer = new();
    private readonly BreakdownGridAdapter breakdownGridAdapter;
    private readonly UsageQueryService usageQueryService = new();
    private readonly LatestRequestRunner<UsageRefreshRequest> usageRefreshRunner;
    private readonly object cycleRefreshSync = new();
    private UsageSource activeSource = UsageSource.Codex;
    private readonly UiEventSuppressor suppressUiEvents = new();
    private bool isRefreshing;
    private bool isQuotaRefreshing;
    private bool isClosed;
    private long quotaRefreshVersion;
    private Task cycleRefreshTask = Task.CompletedTask;
    private int lastVisibleCostColumnCount = -1;

    public MainWindow()
    {
        // Controls raise change events while the constructor assigns their
        // initial values; hold one suppression scope across the whole setup.
        using var setupScope = suppressUiEvents.Begin();
        InitializeComponent();
        DataContext = displayViewModel;
        foreach (var definition in UsageSourceRegistry.All)
            SourceTabs.Items.Add(new TabItem { Header = definition.Title, Tag = definition.Source });
        SourceTabs.SelectedIndex = 0;
        usageRefreshRunner = new LatestRequestRunner<UsageRefreshRequest>(
            (request, version, _) => runtime.Run("用量刷新", _ => RefreshUsageOnceAsync(request.CacheOnly, request.IsAutomaticRefresh, version)),
            runtime.LifetimeToken);
        backgroundCacheWarmer = new BackgroundCacheWarmer(
            CurrentSource,
            () => isRefreshing || isQuotaRefreshing,
            usageQueryGate,
            SetBackgroundStatus, runtime);
        breakdownGridAdapter = new BreakdownGridAdapter(BreakdownGrid);
        ConfigureBreakdownGrid();
        SyncRangeModeItems(CurrentModule());
        RangeModeBox.SelectedIndex = 0;
        DatePicker.SelectedDate = BeijingClock.Today;
        refreshTimer.Interval = TimeSpan.FromSeconds(30);
        refreshTimer.Tick += async (_, _) => await RunUiActionAsync(async () =>
        {
            if (isRefreshing || usageRefreshRunner.IsRunning || isQuotaRefreshing || AutoRefreshBox.IsChecked != true)
            {
                return;
            }

            var module = CurrentModule();
            var range = GetSelectedRange();
            var action = UsageRangePolicy.GetAutomaticRefreshAction(
                module.Mode, range, module.LastRange, DateTimeOffset.UtcNow);
            if (action == AutomaticRefreshAction.AdvanceCurrentPeriod)
            {
                MoveToCurrentPeriod(module);
                UpdateRangeControls();
                await RefreshUsageAsync(isAutomaticRefresh: true);
                return;
            }

            if (action == AutomaticRefreshAction.RefreshQuotaOnly)
            {
                if (module is CodexUsageModule)
                {
                    await RefreshQuotaSummaryAsync();
                }

                return;
            }

            await RefreshUsageAsync(isAutomaticRefresh: true);
        });
        refreshTimer.Start();
        Loaded += async (_, _) => await RunUiActionAsync(async () =>
        {
            await StartDataSharingOnLaunchAsync();
            UpdateRangeControls();
            _ = RefreshSettingsSummaryAsync();
            var startupSource = CurrentSource();
            var priceWarnings = await Task.Run(
                () => ReadPriceSettingsForUsage(startupSource, null, runtime.LifetimeToken),
                runtime.LifetimeToken);
            if (isClosed) return;
            var restored = priceWarnings.Count == 0 && TryRestoreLastDisplay();
            if (!restored)
            {
                await RefreshUsageAsync(cacheOnly: true);
                if (isClosed)
                {
                    return;
                }
            }

            await RefreshUsageAsync();
            if (isClosed)
            {
                return;
            }

            _ = SyncResetOpportunitiesFromCodexAsync(
                showError: false,
                cancellationToken: runtime.LifetimeToken);
            backgroundCacheWarmer.Start();
        });
        Closing += MainWindow_Closing;
        Closed += (_, _) =>
        {
            breakdownGridAdapter.Dispose();
            runtime.Dispose();
        };
    }

    private async Task RunUiActionAsync(Func<Task> action)
    {
        try
        {
            if (!isClosed) await runtime.Run("界面操作", _ => action());
        }
        catch (OperationCanceledException) when (runtime.IsStopping || isClosed)
        {
            // Closing the main window cancels active I/O. Event handlers are
            // fire-and-forget by WPF, so observe that cancellation here rather
            // than allowing it to reach the dispatcher as an unhandled fault.
        }
        catch (Exception ex)
        {
            if (!isClosed)
            {
                SetStatus($"操作失败：{ex.Message}");
                System.Windows.MessageBox.Show(this, ex.Message, "Codex Token 额度监控器", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private async void WeekPickerButton_Click(object sender, RoutedEventArgs e)
    {
        await RunUiActionAsync(OpenWeekPickerAsync);
    }

    private async void RefreshDayButton_Click(object sender, RoutedEventArgs e)
    {
        await RunUiActionAsync(RefreshSelectedDayFromCacheAsync);
    }

    private void CostCardsViewport_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        ResizeCostCards();
        if (suppressUiEvents.IsSuppressing)
        {
            return;
        }

        ReflowCostColumnsForCurrentDisplay();
    }

    private async void PreviousButton_Click(object sender, RoutedEventArgs e)
    {
        await RunUiActionAsync(() => ShiftPeriodAsync(-1));
    }

    private async void NextButton_Click(object sender, RoutedEventArgs e)
    {
        await RunUiActionAsync(() => ShiftPeriodAsync(1));
    }

    private async void CurrentButton_Click(object sender, RoutedEventArgs e)
    {
        await RunUiActionAsync(JumpToCurrentPeriodAsync);
    }

    private async void StartNowButton_Click(object sender, RoutedEventArgs e)
    {
        await RunUiActionAsync(async () =>
        {
            var module = CurrentModule();
            module.CustomStartLocal = DateTimeOffset.UtcNow.ToOffset(CodexUsageReader.BeijingOffset);
            UpdateStartNowButtonState();
            await RefreshUsageAsync();
        });
    }

    private async void RangeModeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (suppressUiEvents.IsSuppressing)
        {
            return;
        }

        await RunUiActionAsync(RangeModeChangedAsync);
    }

    private async void DatePicker_SelectedDateChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (suppressUiEvents.IsSuppressing || DatePicker.SelectedDate is null)
        {
            return;
        }

        await RunUiActionAsync(async () =>
        {
            var module = CurrentModule();
            var previous = module.PickerValue;
            module.PickerValue = DatePicker.SelectedDate.Value.Date + previous.TimeOfDay;
            if (module.Mode == RangeMode.Week && DatePicker.SelectedDate.Value.Date == BeijingClock.Today)
            {
                module.PickerValue = BeijingClock.DateTimeNow;
            }

            ClearCustomStart();
            UpdateRangeControls();
            await RefreshUsageAsync();
        });
    }

    private async void WeekEndPicker_ValueChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (suppressUiEvents.IsSuppressing || CurrentModule().Mode != RangeMode.Week || WeekEndPicker.Value is not DateTime selected)
        {
            return;
        }

        await RunUiActionAsync(async () =>
        {
            var module = CurrentModule();
            module.PickerValue = selected;
            ClearCustomStart();
            UpdateRangeControls();
            await RefreshUsageAsync();
        });
    }

    private async void CustomStartPicker_ValueChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        var module = CurrentModule();
        if (suppressUiEvents.IsSuppressing || module.CustomStartLocal is null || CustomStartPicker.Value is not DateTime selected)
        {
            return;
        }

        await RunUiActionAsync(async () =>
        {
            module.CustomStartLocal = ToBeijingOffset(selected);
            UpdateStartNowButtonState();
            await RefreshUsageAsync();
        });
    }

    private async void CycleBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (suppressUiEvents.IsSuppressing || CurrentModule() is not CodexUsageModule codexModule)
        {
            return;
        }

        await RunUiActionAsync(async () =>
        {
            codexModule.SelectedCycle = CycleBox.SelectedItem as CodexQuotaCycle;
            ClearCustomStart();
            UpdateRangeControls();
            await RefreshUsageAsync();
        });
    }

    private void AnalyzeSelectedCycleMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedCycle() is not { } cycle)
        {
            return;
        }

        var codexModule = CurrentCodexModule();
        var previousPeriod = codexModule.QuotaCycles
            .Where(item => item.PeriodStart < cycle.PeriodStart)
            .OrderByDescending(item => item.PeriodStart)
            .FirstOrDefault();
        var window = new QuotaCycleAnalysisWindow(
            cycle,
            codexModule.CurrentQuotaEstimate?.Week,
            previousPeriod,
            runtime)
        {
            Owner = this
        };
        window.Show();
    }

    private async void SourceTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (suppressUiEvents.IsSuppressing || !ReferenceEquals(e.Source, SourceTabs))
        {
            return;
        }

        await RunUiActionAsync(async () =>
        {
            SaveActiveModuleState();
            activeSource = CurrentSource();
            var module = CurrentModule();
            RestoreModuleControls(module);
            if (module.TryGetDisplay(out var range, out var result))
            {
                ApplySummary(range, result, module);
                SetStatus($"已切换 {module.Title}，正在刷新...");
            }
            else
            {
                ApplyEmptyModuleState(module);
                SetStatus($"正在读取 {module.Title}...");
            }

            await RefreshUsageAsync();
        });
    }

    private void PriceSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var window = new PriceSettingsWindow(PricePresetGroups.ForSource(activeSource), runtime)
        {
            Owner = this
        };
        if (window.ShowDialog() == true)
        {
            CurrentCodexModule().CurrentQuotaEstimate = null;
            foreach (var module in usageModules.Values)
            {
                module.ClearDisplay();
            }

            _ = RefreshSettingsSummaryAsync(refreshAfterCurrent: true);
            _ = RefreshUsageAsync();
        }
    }

    private void CopySummaryButton_Click(object sender, RoutedEventArgs e)
    {
        var module = CurrentModule();
        if (!module.TryGetDisplay(out var range, out var result) || !HasUsage(result))
        {
            SetStatus("当前没有可复制的统计结果");
            UpdateCopySummaryState();
            return;
        }

        try
        {
            System.Windows.Clipboard.SetText(BuildClipboardSummary(module, range, result));
            SetStatus($"已复制 {module.Title} 摘要 · {range.Title}");
        }
        catch (Exception ex)
        {
            SetStatus($"复制摘要失败：{ex.Message}");
        }
    }

    private void ResetSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentModule() is not CodexUsageModule)
        {
            return;
        }

        var window = new ResetOpportunityWindow(runtime) { Owner = this };
        window.ShowDialog();
        _ = RefreshSettingsSummaryAsync(refreshAfterCurrent: true);
    }

    private Task SyncResetOpportunitiesFromCodexAsync(bool showError, CancellationToken cancellationToken = default)
    {
        return runtime.Run("重置机会同步", async token =>
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, cancellationToken);
            await SyncResetOpportunitiesFromCodexCoreAsync(showError, linked.Token);
        });
    }

    private async Task SyncResetOpportunitiesFromCodexCoreAsync(bool showError, CancellationToken cancellationToken)
    {
        var result = await resetOpportunitySynchronizer.SyncAsync(cancellationToken);
        if (result is null || isClosed)
        {
            return;
        }

        if (result.Success)
        {
            await RefreshSettingsSummaryAsync(refreshAfterCurrent: true);
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

        var window = new SubscriptionPlanWindow(runtime) { Owner = this };
        if (window.ShowDialog() == true)
        {
            _ = RefreshSettingsSummaryAsync(refreshAfterCurrent: true);
            SetStatus("套餐设置已保存");
        }
    }

    private async void QuotaEstimateButton_Click(object sender, RoutedEventArgs e)
    {
        var codexModule = CurrentCodexModule();
        var quota = codexModule.CurrentQuotaEstimate;
        if (quota is null)
        {
            return;
        }

        await RefreshCycleOptionsAsync(keepSelection: true);
        if (isClosed) return;
        quota = codexModule.CurrentQuotaEstimate;
        if (quota is null)
        {
            return;
        }

        var window = new QuotaEstimateWindow(quota, codexModule.QuotaCycles, runtime)
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
            await RefreshCycleOptionsAsync(keepSelection: true);
            if (CycleBox.Items.Count == 0)
            {
                return;
            }

            var targetIndex = Math.Clamp(CycleBox.SelectedIndex - delta, 0, CycleBox.Items.Count - 1);
            if (targetIndex == CycleBox.SelectedIndex)
            {
                return;
            }

            using (suppressUiEvents.Begin())
            {
                CycleBox.SelectedIndex = targetIndex;
            }

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
            await RefreshCycleOptionsAsync(keepSelection: false);
            codexModule.SelectedCycle = CycleBox.SelectedItem as CodexQuotaCycle;
            UpdateRangeControls();
            await RefreshUsageAsync();
            return;
        }

        module.PickerValue = module.Mode == RangeMode.Week ? BeijingClock.DateTimeNow : BeijingClock.Today;
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
            using (suppressUiEvents.Begin())
            {
                RangeModeBox.SelectedIndex = 0;
            }
        }

        module.Mode = mode;
        if (mode == RangeMode.Week && module.PickerValue.Date == BeijingClock.Today)
        {
            module.PickerValue = BeijingClock.DateTimeNow;
        }

        if (mode == RangeMode.Cycle)
        {
            await RefreshCycleOptionsAsync(keepSelection: false);
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
        var maintenance = UsageSourceRegistry.For(module.Source).CacheMaintenance;
        var selectedDay = DateOnly.FromDateTime(module.PickerValue.Date);
        SetStatus($"清除 {module.Title} {selectedDay:yyyy-MM-dd} 缓存...");

        bool deleted;
        await usageQueryGate.WaitAsync(runtime.LifetimeToken);
        try
        {
            deleted = await Task.Run(
                () => maintenance.RefreshCachedDay(selectedDay, runtime.LifetimeToken),
                runtime.LifetimeToken);
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

    private sealed record UsageRefreshRequest(bool CacheOnly, bool IsAutomaticRefresh);

    private Task RefreshUsageAsync(bool cacheOnly = false, bool isAutomaticRefresh = false)
    {
        return usageRefreshRunner.RequestAsync(new UsageRefreshRequest(cacheOnly, isAutomaticRefresh));
    }

    private async Task RefreshUsageOnceAsync(bool cacheOnly, bool isAutomaticRefresh, long requestVersion)
    {
        // Do not queue timer refreshes behind a long historical scan and disable
        // navigation. A manual refresh still cancels warmup; the next timer retries.
        if (isAutomaticRefresh && usageQueryGate.CurrentCount == 0)
        {
            return;
        }

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var resumeBackgroundCache = false;
        var module = CurrentModule();
        isRefreshing = true;
        _ = RefreshSettingsSummaryAsync();

        try
        {
            SetBusy(true);
            SetStatus(cacheOnly ? "正在读取缓存..." : "正在刷新...");
            var source = module.Source;
            var range = GetSelectedRange();
            var includeLiveToday = !cacheOnly && ShouldIncludeLiveToday(range);
            var cachedQuota = module is CodexUsageModule codexModule ? codexModule.CurrentQuotaEstimate : null;
            if (range.Mode == RangeMode.Cycle && !includeLiveToday && module.TryGetCachedDisplay(range, out var cachedResult))
            {
                if (!usageRefreshRunner.IsCurrent(requestVersion) || isClosed)
                {
                    return;
                }

                module.StoreDisplay(range, cachedResult);
                if (CurrentSource() == source)
                {
                    ApplySummary(range, cachedResult, module);
                    SetStatus($"周期结果缓存命中 {BeijingClock.DateTimeNow:HH:mm:ss} · {stopwatch.ElapsedMilliseconds:N0}ms");
                    if (!cacheOnly && ShouldRefreshQuota(module))
                    {
                        _ = RefreshQuotaSummaryAsync();
                    }
                }

                return;
            }

            // A historical query can repair missing details and materialize quota
            // anchors. Only the display-cache hit above is safe to bypass the gate.
            if (!isAutomaticRefresh && backgroundCacheWarmer.IsRunning)
            {
                resumeBackgroundCache = true;
                backgroundCacheWarmer.CancelCurrent();
                SetStatus("正在刷新...");
            }

            var definition = UsageSourceRegistry.For(source);
            var cachedQueries = definition.CachedQueries;
            // Capture the chosen capability and immutable request on the UI
            // thread. A cache request has no source-log scanning methods.
            Func<CancellationToken, UsageQueryResult> executeQuery;
            if (cacheOnly)
            {
                var request = new UsageCacheQueryRequest(cachedQueries, range, cachedQuota);
                executeQuery = token => usageQueryService.ExecuteCached(request, token);
            }
            else
            {
                var request = new UsageQueryRequest(definition.Queries, range, false, includeLiveToday, cachedQuota);
                executeQuery = token => usageQueryService.Execute(request, token);
            }
            UsageQueryResult result;
            bool canCacheCycleResult;
            await usageQueryGate.WaitAsync(runtime.LifetimeToken);
            try
            {
                // Navigation can supersede a request while it waits for warmup.
                // Avoid scanning a range whose result can no longer be displayed.
                if (!usageRefreshRunner.IsCurrent(requestVersion) || isClosed)
                {
                    return;
                }

                (result, canCacheCycleResult) = await Task.Run(() =>
                {
                    var queried = executeQuery(runtime.LifetimeToken);
                    var priceWarnings = queried.CacheWarnings.Count == 0
                        ? ReadPriceSettingsForUsage(source, queried, runtime.LifetimeToken)
                        : Array.Empty<CacheWarning>();
                    using var diagnostics = CacheOperationDiagnostics.Begin();
                    var canCache = queried.CacheWarnings.Count == 0 && priceWarnings.Count == 0 && usageQueryService.CanCacheCycleResult(
                        cachedQueries, range, includeLiveToday, runtime.LifetimeToken);
                    var warnings = queried.CacheWarnings.Concat(priceWarnings).Concat(diagnostics.Warnings).Distinct().ToArray();
                    return (queried with { CacheWarnings = warnings }, canCache && warnings.Length == 0);
                }, runtime.LifetimeToken);
            }
            finally
            {
                usageQueryGate.Release();
            }

            if (!usageRefreshRunner.IsCurrent(requestVersion) || isClosed)
            {
                return;
            }

            if (result.CacheWarnings.Count > 0)
            {
                if (CurrentSource() == source) ShowCacheWarnings(module, result.CacheWarnings);
                return;
            }

            module.StoreDisplay(range, result);
            if (canCacheCycleResult)
            {
                module.CacheDisplay(range, result);
            }

            if (CurrentSource() == source)
            {
                ApplySummary(range, result, module);
                SetStatus(includeLiveToday
                    ? $"已刷新 {BeijingClock.DateTimeNow:HH:mm:ss} · {stopwatch.ElapsedMilliseconds:N0}ms"
                    : $"缓存命中 {BeijingClock.DateTimeNow:HH:mm:ss} · {stopwatch.ElapsedMilliseconds:N0}ms");
                if (!cacheOnly && ShouldRefreshQuota(module))
                {
                    _ = RefreshQuotaSummaryAsync();
                }
            }
        }
        catch (Exception ex)
        {
            if (usageRefreshRunner.IsCurrent(requestVersion) && !isClosed && CurrentSource() == module.Source)
            {
                ShowUsageReadFailure(module, ex.Message, ex.Message);
                System.Windows.MessageBox.Show(this, ex.Message, "Codex Token 额度监控器", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        finally
        {
            isRefreshing = false;
            if (!isClosed)
            {
                SetBusy(false);
            }

            if (resumeBackgroundCache && !isClosed)
            {
                _ = ResumeBackgroundCacheAsync();
            }
        }
    }

    private Task ResumeBackgroundCacheAsync() => runtime.Run("恢复缓存预热", _ => ResumeBackgroundCacheCoreAsync());

    private async Task ResumeBackgroundCacheCoreAsync()
    {
        try
        {
            for (var attempt = 0; attempt < 30 && backgroundCacheWarmer.IsRunning && !isClosed; attempt++)
            {
                await Task.Delay(100, runtime.LifetimeToken);
            }

            runtime.LifetimeToken.ThrowIfCancellationRequested();
            if (!isClosed)
            {
                await backgroundCacheWarmer.WarmNowAsync();
            }
        }
        catch (OperationCanceledException) when (runtime.IsStopping || isClosed)
        {
            // A close can interrupt the short hand-off wait; no new warm cycle
            // should be started after the window lifetime has ended.
        }
    }

    private static bool ShouldRefreshQuota(UsageSourceModule module)
    {
        return module.SupportsQuota || module is ZCodeUsageModule;
    }

    private Task RefreshQuotaSummaryAsync() => runtime.Run("额度刷新", _ => CurrentModule() is ZCodeUsageModule
        ? RefreshZCodeQuotaCoreAsync()
        : RefreshQuotaSummaryCoreAsync());

    private async Task RefreshZCodeQuotaCoreAsync()
    {
        if (isQuotaRefreshing || CurrentModule() is not ZCodeUsageModule zcodeModule)
        {
            return;
        }

        isQuotaRefreshing = true;
        try
        {
            var snapshot = await Task.Run(
                () => ZCodeQuotaReader.Shared.ReadCurrent(runtime.LifetimeToken),
                runtime.LifetimeToken);
            if (!isClosed && CurrentModule() is ZCodeUsageModule current && ReferenceEquals(current, zcodeModule))
            {
                zcodeModule.CurrentQuotaSnapshot = snapshot;
                ApplyZCodeQuotaSummary(zcodeModule);
            }
        }
        catch (OperationCanceledException) when (runtime.IsStopping || isClosed)
        {
            // Window shutdown cancels the shared quota read; the closing path
            // owns the message surface in that case.
        }
        catch (Exception ex)
        {
            if (!isClosed)
            {
                SetStatus($"ZCode 额度刷新失败：{ex.Message}");
            }
        }
        finally
        {
            isQuotaRefreshing = false;
        }
    }

    private async Task RefreshQuotaSummaryCoreAsync()
    {
        if (isQuotaRefreshing || CurrentModule() is not CodexUsageModule codexModule)
        {
            return;
        }

        var requestVersion = Volatile.Read(ref quotaRefreshVersion);
        isQuotaRefreshing = true;
        _ = RefreshSettingsSummaryAsync();
        try
        {
            if (isClosed || requestVersion != Volatile.Read(ref quotaRefreshVersion))
            {
                return;
            }

            await usageQueryGate.WaitAsync(runtime.LifetimeToken);
            CodexQuotaEstimate? cachedQuota;
            CodexQuotaEstimate? quota;
            IReadOnlyList<CacheWarning> quotaWarnings;
            try
            {
                if (isClosed || requestVersion != Volatile.Read(ref quotaRefreshVersion))
                {
                    return;
                }

                var previousQuota = codexModule.CurrentQuotaEstimate;
                (cachedQuota, quota, quotaWarnings) = await Task.Run(() =>
                {
                    using var diagnostics = CacheOperationDiagnostics.Begin();
                    var cached = FreshQuotaOrNull(UsageSourceReaders.Codex.ReadCachedQuotaEstimate(runtime.LifetimeToken));
                    var establishedQuota = LatestFreshQuota(previousQuota, cached);
                    var current = UsageSourceReaders.Codex.ReadQuotaEstimate(establishedQuota, runtime.LifetimeToken);
                    return (cached, current, diagnostics.Warnings);
                }, runtime.LifetimeToken);
            }
            finally
            {
                usageQueryGate.Release();
            }

            if (isClosed || requestVersion != Volatile.Read(ref quotaRefreshVersion))
            {
                return;
            }

            if (quotaWarnings.Count > 0)
            {
                SetStatus("额度缓存读取失败；保留上次额度，下次刷新将重试。");
                return;
            }

            if (cachedQuota is not null)
            {
                codexModule.CurrentQuotaEstimate = LatestFreshQuota(codexModule.CurrentQuotaEstimate, cachedQuota);
                if (CurrentModule() is CodexUsageModule)
                {
                    ApplyQuotaSummary(codexModule, codexModule.CurrentQuotaEstimate);
                    ApplyQuotaToCurrentBreakdown(codexModule, codexModule.CurrentQuotaEstimate);
                }
            }

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
                    await RefreshCycleOptionsAsync(keepSelection: true);
                }
            }
        }
        catch (OperationCanceledException) when (runtime.IsStopping || isClosed)
        {
            // Window shutdown cancels the shared quota read. Do not replace a
            // normal closing path with an error message.
        }
        catch (Exception ex)
        {
            // Automatic quota refresh is auxiliary to the token view, but a
            // silent failure leaves stale percentages looking authoritative.
            // Only report the error while this refresh request is still the
            // active one, so a late failure from an obsolete request cannot
            // overwrite a newer status message.
            if (!isClosed && requestVersion == Volatile.Read(ref quotaRefreshVersion))
            {
                SetStatus($"额度刷新失败：{ex.Message}");
            }
        }
        finally
        {
            isQuotaRefreshing = false;
            if (!isClosed && requestVersion != Volatile.Read(ref quotaRefreshVersion))
            {
                _ = RefreshQuotaSummaryAsync();
            }
        }
    }

    private void ApplySummary(SelectedRange range, UsageQueryResult result, UsageSourceModule module)
    {
        var hasUsage = HasUsage(result);
        displayViewModel.ShowResult(module.Source, module.Title, range, result);
        displayViewModel.SetBusy(isRefreshing);

        var displayPresets = PriceSettingsStore.DisplayPresetsForSource(module.Source, count: 0).ToList();
        if (hasUsage)
        {
            ApplyCostCards(displayPresets, result.Summary);
        }
        else
        {
            CostCardsPanel.Children.Clear();
        }
        if (module is CodexUsageModule codexModule)
        {
            codexModule.CurrentQuotaSnapshots = result.QuotaSnapshots;
        }

        ApplyQuotaSummary(module, result.Quota);
        if (module.Source == UsageSource.Codex && range.Mode == RangeMode.Cycle)
        {
            _ = RefreshCycleOptionsAsync(keepSelection: true);
        }

        var showTimeline = hasUsage && result.BreakdownRows.Count > 0;
        if (showTimeline)
        {
            SetTimelineVisible(true);
            Timeline.SetData(range.Start, range.End, GetTimelineRows(range, result), GetTimelineInterval(range.Mode));
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

        // The restored source and range are programmatic assignments; keep
        // their change events from enqueueing refreshes (and release the scope
        // even if a restore step throws).
        using var restoreScope = suppressUiEvents.Begin();
        SourceTabs.SelectedIndex = SourceToTabIndex(snapshot.Source);
        activeSource = snapshot.Source;
        RestoreModuleRange(module, snapshot.Range);
        AlignRestoredCurrentRange(module, snapshot.Range);
        if (module is CodexUsageModule codexModule)
        {
            codexModule.CurrentQuotaEstimate = snapshot.Result.Quota;
            codexModule.CurrentQuotaSnapshots = snapshot.Result.QuotaSnapshots;
        }

        module.StoreDisplay(snapshot.Range, snapshot.Result);
        RestoreModuleControls(module);
        restoreScope.Dispose();
        ApplySummary(snapshot.Range, snapshot.Result, module);
        SetStatus("已恢复上次显示，正在刷新...");
        return true;
    }

    private static void AlignRestoredCurrentRange(UsageSourceModule module, SelectedRange range)
    {
        if (!range.FollowsCurrent ||
            module.Mode is not (RangeMode.Day or RangeMode.Week or RangeMode.Month) ||
            UsageRangePolicy.ShouldReadLiveToday(range, DateTimeOffset.UtcNow))
        {
            return;
        }

        module.PickerValue = module.Mode == RangeMode.Week ? BeijingClock.DateTimeNow : BeijingClock.Today;
    }

    private static int SourceToTabIndex(UsageSource source)
    {
        return UsageSourceRegistry.IndexOf(source);
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
        displayViewModel.ShowLoading(module.Source, module.Title);
        CostCardsPanel.Children.Clear();
        SetTimelineVisible(false);
        ApplyQuotaSummary(module, module is CodexUsageModule codexModule ? codexModule.CurrentQuotaEstimate : null);
        ApplyBreakdownRows(GetSelectedRange(), Array.Empty<TokenUsageBucket>(), Array.Empty<CodexQuotaSnapshot>(), module.Source, PriceSettingsStore.DisplayPresetsForSource(module.Source, count: 0).ToList());
    }

    private void ApplyCostCards(IReadOnlyList<PricePreset> presets, TokenUsageSummary summary)
    {
        CostCardsPanel.Children.Clear();
        var source = CurrentModule().Source;
        if (source == UsageSource.Codex)
        {
            CostCardsPanel.Children.Add(CreateCostCard(new PricePreset { Provider = "OpenAI", Model = "实际模型 · 标准 API 等价" }, summary, actual: true));
        }
        else if (summary.ModelUsage.Count > 0)
        {
            // Non-Codex sources price their actual model ids against the
            // source's own price group, in that group's currency.
            var provider = source == UsageSource.ZCode ? "智谱/Z.AI" : UsageSourceRegistry.For(source).Title;
            CostCardsPanel.Children.Add(CreateCostCard(
                new PricePreset { Provider = provider, Model = source == UsageSource.Kimi ? "实际模型 · 参考价估算" : "实际模型 · 标准 API 等价" }, summary,
                actual: true, priceGroup: PricePresetGroups.ForSource(source)));
        }
        foreach (var preset in presets.Take(GetVisibleCostColumnCount(presets.Count)))
        {
            CostCardsPanel.Children.Add(CreateCostCard(preset, summary, comparison: CurrentModule().Source == UsageSource.Codex));
        }
        if (CostCardsPanel.Children.Count > 0 && CostCardsPanel.Children[^1] is FrameworkElement lastCard)
            lastCard.Margin = new Thickness(0);
        ResizeCostCards();
    }

    private void ResizeCostCards()
    {
        var count = CostCardsPanel.Children.Count;
        if (count == 0) return;
        var width = Math.Max(CostCardWidth,
            (CostCardsViewport.ActualWidth - CostCardRightMargin * (count - 1)) / count);
        foreach (FrameworkElement card in CostCardsPanel.Children)
            card.Width = width;
    }

    private void ReflowCostColumnsForCurrentDisplay()
    {
        var module = CurrentModule();
        if (!module.TryGetDisplay(out var range, out var result))
        {
            return;
        }

        if (!HasUsage(result))
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

    private static bool HasUsage(UsageQueryResult result) => UsageDisplayViewModel.ContainsUsage(result);

    private static string BuildClipboardSummary(
        UsageSourceModule module,
        SelectedRange range,
        UsageQueryResult result)
    {
        var summary = result.Summary;
        var builder = new StringBuilder();
        builder.AppendLine($"Codex Token Monitor · {module.Title}");
        builder.AppendLine($"范围：{range.Title}");
        builder.AppendLine($"时间：{summary.StartLocal:yyyy-MM-dd HH:mm:ss} - {summary.EndLocal:yyyy-MM-dd HH:mm:ss} GMT+8");
        builder.AppendLine();
        builder.AppendLine($"Total Tokens：{FormatTokenMillions(summary.TotalTokens)}");
        builder.AppendLine($"Input：{FormatTokenMillions(summary.InputTokens)}");
        builder.AppendLine($"Cached：{FormatTokenMillions(summary.CachedInputTokens)} ({summary.CacheRatioPercent:N2}%)");
        builder.AppendLine($"Cache Write：{FormatTokenMillions(summary.CacheWriteInputTokens)}");
        builder.AppendLine($"Uncached：{FormatTokenMillions(summary.UncachedInputTokens)}");
        builder.AppendLine($"Output：{FormatTokenAdaptive(summary.OutputTokens)}");
        builder.AppendLine($"Reasoning：{FormatTokenAdaptive(summary.ReasoningOutputTokens)}");
        builder.AppendLine($"Events：{summary.Events:N0}");
        builder.AppendLine($"Coding Time：{FormatDuration(result.CodingTime)}");

        var presets = PriceSettingsStore.DisplayPresetsForSource(module.Source, count: 3);
        if (presets.Count > 0)
        {
            builder.AppendLine();
            if (module.Source == UsageSource.Codex)
            {
                builder.AppendLine($"实际模型 API 等价费用：{CodexModelCost.Estimate(summary).Format()}");
                builder.AppendLine(BuildModelCostDetails(summary));
            }
            else if (summary.ModelUsage.Count > 0)
            {
                var priceGroup = PricePresetGroups.ForSource(module.Source);
                builder.AppendLine($"实际模型 API 等价费用：{FormatActualBucketCost(module.Source, summary)}");
                builder.AppendLine(BuildModelCostDetails(summary, priceGroup));
            }
            builder.AppendLine("相同 Token 换模型费用估算：");
            foreach (var preset in presets)
            {
                var label = string.IsNullOrWhiteSpace(preset.Provider)
                    ? preset.Model
                    : $"{preset.Provider} · {preset.Model}";
                builder.AppendLine($"- {label}：{FormatPresetCost(summary, preset)}");
            }
        }

        if (result.Quota is { } quota)
        {
            builder.AppendLine();
            builder.AppendLine("Codex 额度：");
            builder.AppendLine($"- 5h 剩余：{FormatQuotaRemaining(quota.FiveHour)}");
            builder.AppendLine($"- 7d 剩余：{FormatQuotaRemaining(quota.Week)}");
        }

        return builder.ToString().TrimEnd();
    }

    private static string FormatQuotaRemaining(CodexQuotaWindowEstimate? window)
    {
        if (window is null)
        {
            return "-";
        }

        var remaining = Math.Max(0m, 100m - window.UsedPercent);
        var reset = window.ResetAtLocal is { } resetAt
            ? $"，重置 {resetAt:yyyy-MM-dd HH:mm}"
            : "";
        return $"{remaining:N0}%{reset}";
    }

    private static string BuildModelCostDetails(TokenUsageBucket usage, string? priceGroup = null)
    {
        var isCodex = string.IsNullOrWhiteSpace(priceGroup) ||
                      PricePresetGroups.Normalize(priceGroup) == PricePresetGroups.Codex;
        var estimate = isCodex
            ? CodexModelCost.Estimate(usage)
            : CodexModelCost.Estimate(usage, priceGroup!);
        var lines = new List<string>
        {
            isCodex
                ? "按日志模型与当前价格库估算 API 等价费用；不是订阅账单。"
                : "按日志模型与当前来源价格库估算；不同币种或计价单位分别显示，不做汇率或单位换算。"
        };
        if (isCodex)
        {
            lines.Add(estimate.SpeedDescription);
            lines.Add($"订阅基准折算（含 Fast）：{estimate.FormatQuotaCost()}");
        }
        else if (priceGroup == PricePresetGroups.Kimi)
        {
            lines.Add("Kimi 费用仅按参考单价折算，不代表官方账单或会员额度。");
            var presets = PriceSettingsStore.Current.KimiPresets;
            foreach (var model in estimate.Models)
            {
                var preset = presets.FirstOrDefault(preset => CodexModelCost.NormalizeModelId(model.ModelId) ==
                    CodexModelCost.NormalizeModelId(preset.ModelId));
                if (preset is not null) lines.Add($"{model.ModelId} 价格来源：{preset.Source}");
            }
        }
        foreach (var model in estimate.Models)
        {
            var formattedCost = model.Cost is { } cost
                ? ModelCostEstimate.FormatAmount(cost, model.CurrencySymbol, "N4")
                : CodexModelCost.HasNoPublicPrice(model.ModelId)
                    ? "暂无公开 API 单价"
                    : "0x · 待填写";
            lines.Add($"{model.ModelId}{(CodexModelCost.IsReserveModel(model.ModelId) ? "（按 GPT-5.6 Luna）" : "")}: Input {model.Usage.InputTokens:N0} / Cached {model.Usage.CachedInputTokens:N0} / Cache Write {model.Usage.CacheWriteInputTokens:N0} / Output {model.Usage.OutputTokens:N0} · {formattedCost}");
        }
        if (!estimate.IsComplete)
        {
            lines.Add(estimate.MissingPriceDescription);
            lines.Add("未计价记录不计入对应单位金额；缺模型需从原日志补全，缺价格可在价格设置按模型 ID 填写。");
        }
        return string.Join(Environment.NewLine, lines);
    }

    private static bool SupportsModelCost(UsageSource source)
    {
        return source is UsageSource.Codex or UsageSource.ZCode or UsageSource.WorkBuddy or UsageSource.ClaudeCode or UsageSource.Kimi;
    }

    private static string FormatActualBucketCost(UsageSource source, TokenUsageBucket bucket)
    {
        var estimate = source == UsageSource.Codex
            ? CodexModelCost.Estimate(bucket)
            : CodexModelCost.Estimate(bucket, PricePresetGroups.ForSource(source));
        return source == UsageSource.Kimi && !estimate.IsComplete && estimate.Models.All(item => item.Cost is null)
            ? "待填价格"
            : estimate.Format("N4");
    }

    private static string FormatPresetCost(TokenUsageBucket bucket, PricePreset preset)
    {
        if (CodexModelCost.IsPending(preset)) return "待填价格";
        var profile = preset.ToProfile();
        return FormatCost(bucket.EstimateCost(profile), profile);
    }

    private static UIElement CreateCostCard(PricePreset preset, TokenUsageSummary summary, bool actual = false, bool comparison = false, string? priceGroup = null)
    {
        var actualCost = priceGroup is null
            ? CodexModelCost.Estimate(summary).Format()
            : priceGroup == PricePresetGroups.Kimi
                ? FormatActualBucketCost(UsageSource.Kimi, summary)
                : CodexModelCost.Estimate(summary, priceGroup).Format();
        return new CostCardControl(
            preset.Source == PricePreset.KimiPreviewPriceSource ? "B.AI 第三方参考价" : string.IsNullOrWhiteSpace(preset.Provider) ? preset.Model : preset.Provider,
            comparison ? $"换用 {preset.Model}" : preset.Model,
            actual ? actualCost : FormatPresetCost(summary, preset),
            actual ? BuildModelCostDetails(summary, priceGroup) : preset.Source == PricePreset.KimiPreviewPriceSource
                ? $"{preset.Source}\n仅用于参考估算，不代表 Kimi 官方账单或会员额度。"
                : comparison ? "按相同输入、缓存和输出 Token 换算，实际换模型后的用量可能不同。" : null,
            actual)
        {
            Width = CostCardWidth,
            Margin = new Thickness(0, 0, CostCardRightMargin, 0)
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
                Model = SupportsModelCost(source) ? CodexModelCost.DescribeModels(bucket) : "",
                ActualCost = SupportsModelCost(source) ? FormatActualBucketCost(source, bucket) : "",
                Total = FormatBreakdownToken(bucket.TotalTokens, rowIsEvent),
                Input = FormatBreakdownToken(bucket.InputTokens, rowIsEvent),
                Cached = FormatBreakdownToken(bucket.CachedInputTokens, rowIsEvent),
                CacheWrite = FormatBreakdownToken(bucket.CacheWriteInputTokens, rowIsEvent),
                Uncached = FormatBreakdownToken(bucket.UncachedInputTokens, rowIsEvent),
                Output = FormatTokenAdaptive(bucket.OutputTokens),
                Prices = tablePresets.Select(preset => FormatPresetCost(bucket, preset)).ToArray(),
                Quota = quotaLookup is not null ? FormatQuotaSnapshotForBucket(range, bucket, quotaLookup, rowIsEvent) : "-"
            });
        }

        breakdownGridAdapter.ApplyRows(
            range,
            eventBreakdown,
            tablePresets,
            includeModels: SupportsModelCost(source),
            includeQuota: source == UsageSource.Codex,
            rows);
    }

    private void ApplyQuotaSummary(CodexQuotaEstimate? quota)
    {
        ApplyQuotaSummary(CurrentModule(), quota);
    }

    private void ApplyQuotaSummary(UsageSourceModule module, CodexQuotaEstimate? quota)
    {
        var show = module is CodexUsageModule;
        QuotaPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        ZCodeQuotaPanel.Visibility = module is ZCodeUsageModule ? Visibility.Visible : Visibility.Collapsed;
        ResetSettingsButton.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        PlanSettingsButton.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        ClearReserveUsageDisplay();
        if (module is ZCodeUsageModule zcodeModule)
        {
            ApplyZCodeQuotaSummary(zcodeModule);
        }

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
        ApplyReserveUsage(effectiveQuota);
        if (effectiveQuota is null)
        {
            Quota5hValue.Text = "--";
            Quota5hDetail.Text = "等待 Codex 实时额度";
            QuotaWeekValue.Text = "--";
            QuotaWeekDetail.Text = "暂未读取到周额度";
            return;
        }

        ApplyQuotaWindow(Quota5hValue, Quota5hDetail, effectiveQuota.FiveHour, QuotaWindowDisplayMode.FiveHour);
        ApplyQuotaWindow(QuotaWeekValue, QuotaWeekDetail, effectiveQuota.Week, QuotaWindowDisplayMode.Week);
    }

    private void ApplyZCodeQuotaSummary(ZCodeUsageModule module)
    {
        var balance = module.CurrentQuotaSnapshot?.PrimaryBalance;
        if (module.CurrentQuotaSnapshot is not { } snapshot || balance is null)
        {
            ZCodeQuotaRemainingValue.Text = "--";
            ZCodeQuotaRemainingDetail.Text = "等待 ZCode 额度";
            ZCodeQuotaUsedValue.Text = "--";
            ZCodeQuotaUsedDetail.Text = "需要已登录的 ZCode 桌面端";
            ZCodeQuotaPlanValue.Text = "-";
            ZCodeQuotaPlanDetail.Text = null;
            ZCodeQuotaExpiryValue.Text = "-";
            ZCodeQuotaExpiryDetail.Text = null;
            return;
        }

        var usedPercent = balance.UsedPercent;
        var remainingPercent = usedPercent is null
            ? (decimal?)null
            : Math.Max(0m, 100m - usedPercent.Value);
        ZCodeQuotaRemainingValue.Text = remainingPercent is null ? "--" : $"{remainingPercent:N1}%";
        ZCodeQuotaRemainingDetail.Text =
            $"剩余 {FormatTokenMillions(balance.RemainingUnits)} / {FormatTokenMillions(balance.TotalUnits)}";
        ZCodeQuotaUsedValue.Text = usedPercent is null ? "--" : $"{usedPercent:N1}%";
        ZCodeQuotaUsedDetail.Text =
            $"已用 {FormatTokenMillions(balance.UsedUnits)} · {balance.ModelName} · 数据 {snapshot.SnapshotLocal:HH:mm}";

        ZCodeQuotaPlanValue.Text = snapshot.PlanName;
        ZCodeQuotaPlanDetail.Text = string.IsNullOrWhiteSpace(snapshot.PlanDescription)
            ? snapshot.PlanStatus
            : snapshot.PlanDescription;

        var expiry = balance.ExpiresAtLocal ?? balance.PeriodEndLocal;
        if (expiry is { } expiryLocal)
        {
            ZCodeQuotaExpiryValue.Text = expiryLocal.ToString("MM-dd HH:mm");
            var remainingTime = expiryLocal - BeijingClock.Now;
            ZCodeQuotaExpiryDetail.Text = remainingTime > TimeSpan.Zero
                ? $"还有 {remainingTime.TotalHours:N1} 小时"
                : "已过期";
        }
        else
        {
            ZCodeQuotaExpiryValue.Text = "-";
            ZCodeQuotaExpiryDetail.Text = null;
        }
    }

    private void ApplyReserveUsage(CodexQuotaEstimate? quota)
    {
        var reserve = FindReserveUsage(quota);
        if (reserve is null)
        {
            return;
        }

        var estimate = new TokenUsageBucket
        {
            ModelUsage = new Dictionary<string, TokenUsageBucket>(StringComparer.OrdinalIgnoreCase)
            {
                [CodexModelCost.ReserveModelId] = reserve.Value.Usage
            }
        };
        var cost = CodexModelCost.Estimate(estimate).KnownCost;
        ReserveUsageValue.Text = FormatTokenMillions(reserve.Value.Usage.TotalTokens);
        ReserveUsageDetail.Text = $"{reserve.Value.WindowLabel} · {reserve.Value.Usage.Events:N0} 条 · 按 Luna ≈ ${cost:N4}";
        ReserveUsageDetail.ToolTip = "仅统计额度窗口内的 gpt-reserve 用量；价格按 GPT-5.6 Luna：输入 $0.20、缓存读取 $0.02、缓存写入 $0.25、输出 $1.20（每百万 Token）。";
        ReserveUsagePanel.Visibility = Visibility.Visible;
    }

    private static (TokenUsageBucket Usage, string WindowLabel)? FindReserveUsage(CodexQuotaEstimate? quota)
    {
        if (quota is null)
        {
            return null;
        }

        foreach (var candidate in new[]
        {
            (Usage: quota.FiveHour?.Usage, WindowLabel: "5h"),
            (Usage: quota.Week?.Usage, WindowLabel: "7d")
        })
        {
            if (candidate.Usage is null)
            {
                continue;
            }

            var reserve = CodexModelCost.FindModelUsage(candidate.Usage, CodexModelCost.ReserveModelId);
            if (reserve is not null && reserve.TotalTokens > 0)
            {
                return (reserve, candidate.WindowLabel);
            }
        }

        return null;
    }

    private void ClearReserveUsageDisplay()
    {
        ReserveUsagePanel.Visibility = Visibility.Collapsed;
        ReserveUsageValue.Text = "-";
        ReserveUsageDetail.Text = "";
        ReserveUsageDetail.ToolTip = null;
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
        var modelCost = CodexModelCost.Estimate(window.Usage);
        detailBlock.ToolTip = "剩余百分比与重置时间来自官方额度。多台电脑需先同步用量；美元金额按本周期模型组合和价格库折算，不代表官方扣减规则或固定额度。";
        if (!modelCost.IsComplete)
            detailBlock.ToolTip += "\n" + modelCost.MissingPriceDescription + "\n可在价格设置中按模型 ID 补充报价；重扫日志不会生成价格。";
        valueBlock.Text = $"{remainingPercent:N0}%";
        detailBlock.Text = mode == QuotaWindowDisplayMode.FiveHour
            ? $"5h {resetAt:HH:mm} · 已用 {modelCost.Format()}"
            : $"周 {resetAt:MM-dd HH:mm} · {FormatQuotaLimit(window)}";
    }

    private SelectedRange GetSelectedRange()
    {
        var module = CurrentModule();
        return UsageRangePolicy.ResolveSelectedRange(
            module.Mode,
            module.PickerValue,
            module.CustomStartLocal,
            SelectedCycle(),
            DateTimeOffset.UtcNow.ToOffset(CodexUsageReader.BeijingOffset));
    }
    private static void MoveToCurrentPeriod(UsageSourceModule module)
    {
        module.PickerValue = module.Mode == RangeMode.Week ? BeijingClock.DateTimeNow : BeijingClock.Today;
        module.CustomStartLocal = null;
    }

    private static bool ShouldIncludeLiveToday(SelectedRange range)
    {
        return UsageRangePolicy.ShouldReadLiveToday(range, DateTimeOffset.UtcNow);
    }

    private void UpdateRangeControls()
    {
        var module = CurrentModule();
        SyncRangeModeItems(module);
        // The three pickers and the cycle list are mirror images of the module
        // state; suppress the handlers for the whole mirror pass so a partially
        // updated control set never enqueues its own refresh.
        using var mirrorScope = suppressUiEvents.Begin();
        RangeModeBox.SelectedIndex = ModeToIndex(module.Mode);

        DatePicker.SelectedDate = module.PickerValue.Date;

        WeekEndPicker.Value = module.PickerValue;

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
        UpdateCopySummaryState();
    }

    private void UpdateCycleOptions(bool keepSelection)
    {
        if (CurrentModule() is not CodexUsageModule codexModule)
        {
            using (suppressUiEvents.Begin())
            {
                CycleBox.ItemsSource = null;
            }

            return;
        }

        ApplyCycleOptions(codexModule, codexModule.QuotaCycles, keepSelection);
    }

    private Task RefreshCycleOptionsAsync(bool keepSelection)
    {
        lock (cycleRefreshSync)
        {
            if (!cycleRefreshTask.IsCompleted)
            {
                return cycleRefreshTask;
            }

            cycleRefreshTask = runtime.Run("额度周期", _ => RefreshCycleOptionsCoreAsync(keepSelection));
            return cycleRefreshTask;
        }
    }

    private async Task RefreshCycleOptionsCoreAsync(bool keepSelection)
    {
        using var diagnostics = CacheOperationDiagnostics.Begin();
        if (CurrentModule() is not CodexUsageModule codexModule)
        {
            return;
        }

        var quota = codexModule.CurrentQuotaEstimate;
        if (quota is null)
        {
            codexModule.QuotaCycles = Array.Empty<CodexQuotaCycle>();
            ApplyCycleOptions(codexModule, codexModule.QuotaCycles, keepSelection);
            return;
        }

        try
        {
            IReadOnlyList<CodexQuotaCycle> cycles;
            await usageQueryGate.WaitAsync(runtime.LifetimeToken);
            try
            {
                if (isClosed)
                {
                    return;
                }

                var now = DateTimeOffset.UtcNow.ToOffset(CodexUsageReader.BeijingOffset);
                cycles = await Task.Run(
                    () => UsageSourceReaders.Codex.Cycles.ReadWeeklyCycles(quota, now, runtime.LifetimeToken),
                    runtime.LifetimeToken);
            }
            finally
            {
                usageQueryGate.Release();
            }

            if (isClosed || !ReferenceEquals(CurrentModule(), codexModule) ||
                !ReferenceEquals(codexModule.CurrentQuotaEstimate, quota))
            {
                return;
            }

            if (diagnostics.Warnings.Count > 0)
            {
                SetStatus("额度周期缓存读取失败；保留上次周期，下次刷新将重试。");
                return;
            }

            codexModule.QuotaCycles = cycles;
            ApplyCycleOptions(codexModule, cycles, keepSelection);
            if (codexModule.Mode == RangeMode.Cycle)
            {
                UpdateRangeControls();
            }
        }
        catch (OperationCanceledException) when (isClosed || runtime.IsStopping)
        {
        }
        catch (Exception ex)
        {
            if (!isClosed) SetStatus($"额度周期读取失败：{ex.Message}");
        }
    }

    private void ApplyCycleOptions(
        CodexUsageModule codexModule,
        IReadOnlyList<CodexQuotaCycle> cycles,
        bool keepSelection)
    {
        var selected = keepSelection ? codexModule.SelectedCycle ?? SelectedCycle() : null;
        using (suppressUiEvents.Begin())
        {
            CycleBox.ItemsSource = cycles;
            if (cycles.Count > 0)
            {
                var selectedItem = selected is null
                    ? cycles[0]
                    : cycles.FirstOrDefault(item => SameCycle(item, selected)) ?? cycles[0];
                CycleBox.SelectedItem = selectedItem;
                codexModule.SelectedCycle = selectedItem;
            }
            else
            {
                codexModule.SelectedCycle = null;
            }
        }
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
        var module = CurrentModule();
        var customStart = module.CustomStartLocal;
        var isCurrentDay = module.Mode == RangeMode.Day && module.PickerValue.Date == BeijingClock.Today;
        var enabled = isCurrentDay || customStart is not null;
        StartNowButton.IsEnabled = enabled && !isRefreshing;
        StartNowButton.Content = customStart is null ? "从当前算" : "重设起点";
        StartNowButton.SetResourceReference(BackgroundProperty, customStart is null ? "SubtleBrush" : "AccentBrush");
        StartNowButton.SetResourceReference(ForegroundProperty, customStart is null ? "TextBrush" : "SurfaceBrush");

        using (suppressUiEvents.Begin())
        {
            CustomStartPicker.Value = customStart?.DateTime;
            CustomStartPicker.Visibility = customStart is null ? Visibility.Collapsed : Visibility.Visible;
        }
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
        displayViewModel.SetBusy(busy);
        isRefreshing = busy;
        RangeModeBox.IsEnabled = !busy;
        DatePicker.IsEnabled = !busy;
        WeekEndPicker.IsEnabled = !busy;
        CycleBox.IsEnabled = !busy;
        PreviousButton.IsEnabled = !busy;
        NextButton.IsEnabled = !busy;
        CurrentButton.IsEnabled = !busy;
        SourceTabs.IsEnabled = !busy;
        DataTransferButton.IsEnabled = !busy;
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

    private void UpdateCopySummaryState()
    {
        var module = CurrentModule();
        displayViewModel.SetCopySourceAvailable(module.TryGetDisplay(out _, out var result) && HasUsage(result));
        displayViewModel.SetBusy(isRefreshing);
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
                TimelineRow.Height = new GridLength(1, GridUnitType.Star);
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
        if (isClosed) return;
        displayViewModel.SetStatus(text);
    }

    private void SetBackgroundStatus(CacheWarmStatus status)
    {
        if (isClosed)
        {
            return;
        }

        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(() =>
            {
                if (!isClosed)
                {
                    SetBackgroundStatus(status);
                }
            });
            return;
        }

        CacheStatusButton.Content = status.Summary;
        CacheStatusButton.ToolTip = $"{status.Phase}\n点击查看缓存详情";
    }

    private void CacheStatusButton_Click(object sender, RoutedEventArgs e)
    {
        if (cacheDetailsWindow is { IsLoaded: true })
        {
            cacheDetailsWindow.Activate();
            return;
        }

        cacheDetailsWindow = new CacheDetailsWindow(backgroundCacheWarmer)
        {
            Owner = this
        };
        cacheDetailsWindow.Closed += (_, _) => cacheDetailsWindow = null;
        cacheDetailsWindow.Show();
    }

    private UsageSource CurrentSource()
    {
        return SourceTabs.SelectedItem is TabItem { Tag: UsageSource source }
            ? UsageSourceRegistry.For(source).Source
            : UsageSourceRegistry.Default.Source;
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

        using (suppressUiEvents.Begin())
        {
            RangeModeBox.Items.Clear();
            foreach (var label in labels)
            {
                RangeModeBox.Items.Add(label);
            }
        }
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

    private static TimeSpan GetQuotaBucketInterval(SelectedRange range)
    {
        return UsageQueryService.GetQuotaBucketInterval(range);
    }

    private static IReadOnlyList<TokenUsageBucket> GetTimelineRows(SelectedRange range, UsageQueryResult result)
    {
        if (range.Mode != RangeMode.Month)
        {
            return result.BreakdownRows;
        }

        // A restored or LRU-cached display intentionally drops full detail rows.
        // Do not reopen SQLite while applying that display on the UI thread;
        // the next background refresh will supply the higher-resolution rows.
        return result.DetailRows.Count > 0 ? result.DetailRows : result.BreakdownRows;
    }

    private static bool UsesEventBreakdown(SelectedRange range, IReadOnlyList<TokenUsageBucket> rows)
    {
        return range.IsCustomStart || range.Mode == RangeMode.Day || rows.Any(row => IsEventBucket(range, row));
    }

    private static bool IsEventBucket(SelectedRange range, TokenUsageBucket bucket)
    {
        return UsageQueryService.IsEventBucket(range, bucket);
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
        var snapshot = lookup.Select(
            range,
            bucket,
            eventBreakdown,
            eventBreakdown ? null : GetQuotaBucketInterval(range));
        return snapshot is null ||
               snapshot.FiveHourUsedPercent is null && snapshot.WeekUsedPercent is null
            ? "-"
            : snapshot.IsAnomaly
                ? "异常"
            : $"{FormatQuotaRemaining(snapshot.FiveHourUsedPercent)} / {FormatQuotaRemaining(snapshot.WeekUsedPercent)}";
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

        var estimatedLimit = CodexModelCost.EstimateQuotaValue(window.Usage, window.UsedPercent);
        var cost = CodexModelCost.Estimate(window.Usage);
        return estimatedLimit is { } limit
            ? $"订阅折算 ≈${limit:N0}" + (cost.FastEvents > 0 ? " · 含 Fast" : "") +
                (!cost.IsComplete ? " · 部分未计价" : "")
            : "费用超出范围";
    }

    private int GetVisibleCostColumnCount(int presetCount)
    {
        if (presetCount <= 0)
        {
            return 0;
        }

        var availableWidth = CostCardsViewport.ActualWidth;
        var actualModelColumns = CurrentModule().Source == UsageSource.Codex ? 1 : 0;
        if (double.IsNaN(availableWidth) || availableWidth <= 0)
        {
            return Math.Min(3 - actualModelColumns, presetCount);
        }

        var visible = (int)Math.Floor((availableWidth + CostCardRightMargin) / (CostCardWidth + CostCardRightMargin));
        return Math.Clamp(visible - actualModelColumns, 0, presetCount);
    }

    private static string FormatTokenMillions(long value)
    {
        return UsageDisplayFormatting.TokenMillions(value);
    }

    private static string FormatTokenAdaptive(long value)
    {
        return UsageDisplayFormatting.TokenAdaptive(value);
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
        return UsageDisplayFormatting.Duration(value);
    }

    private enum QuotaWindowDisplayMode
    {
        FiveHour,
        Week
    }

}
