using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
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
    private readonly CancellationTokenSource lifetimeCancellation = new();
    private CacheDetailsWindow? cacheDetailsWindow;
    private readonly ResetOpportunitySynchronizer resetOpportunitySynchronizer = new();
    private readonly BreakdownGridAdapter breakdownGridAdapter;
    private readonly object usageRefreshSync = new();
    private readonly object cycleRefreshSync = new();
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
    private bool pendingIsAutomaticRefresh;
    private long usageRefreshVersion;
    private long quotaRefreshVersion;
    private Task usageRefreshLoopTask = Task.CompletedTask;
    private Task cycleRefreshTask = Task.CompletedTask;
    private int lastVisibleCostColumnCount = -1;

    public MainWindow()
    {
        InitializeComponent();
        backgroundCacheWarmer = new BackgroundCacheWarmer(
            CurrentSource,
            () => isRefreshing || isQuotaRefreshing,
            usageQueryGate,
            SetBackgroundStatus);
        breakdownGridAdapter = new BreakdownGridAdapter(BreakdownGrid);
        ConfigureBreakdownGrid();
        SyncRangeModeItems(CurrentModule());
        RangeModeBox.SelectedIndex = 0;
        DatePicker.SelectedDate = BeijingClock.Today;
        refreshTimer.Interval = TimeSpan.FromSeconds(30);
        refreshTimer.Tick += async (_, _) => await RunUiActionAsync(async () =>
        {
            if (isRefreshing || usageRefreshLoopRunning || isQuotaRefreshing || AutoRefreshBox.IsChecked != true)
            {
                return;
            }

            var range = GetSelectedRange();
            if (!ShouldIncludeLiveToday(range))
            {
                if (CurrentModule() is CodexUsageModule)
                {
                    await RefreshQuotaSummaryAsync();
                }

                return;
            }

            var module = CurrentModule();
            if (ShouldAdvanceToCurrentPeriod(module, range))
            {
                MoveToCurrentPeriod(module);
                UpdateRangeControls();
                await RefreshUsageAsync(isAutomaticRefresh: true);
                return;
            }

            await RefreshUsageAsync(isAutomaticRefresh: true);
        });
        refreshTimer.Start();
        initializing = false;
        Loaded += async (_, _) => await RunUiActionAsync(async () =>
        {
            await StartDataSharingOnLaunchAsync();
            UpdateRangeControls();
            var restored = TryRestoreLastDisplay();
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
                cancellationToken: lifetimeCancellation.Token);
            backgroundCacheWarmer.Start();
        });
        Closed += async (_, _) =>
        {
            isClosed = true;
            lifetimeCancellation.Cancel();
            var sharingShutdown = dataSharingServer?.StopAsync() ?? Task.CompletedTask;
            refreshTimer.Stop();
            breakdownGridAdapter.Dispose();
            backgroundCacheWarmer.Dispose();
            LastDisplayStore.Flush();
            await ObserveShutdownTaskAsync(sharingShutdown);
            await backgroundCacheWarmer.WaitForCompletionAsync(TimeSpan.FromSeconds(2));
            await ObserveShutdownTaskAsync(usageRefreshLoopTask);
            await ObserveShutdownTaskAsync(cycleRefreshTask);
        };
    }

    private static async Task ObserveShutdownTaskAsync(Task task)
    {
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // Closing is best effort. Cancellation and any already-reported
            // refresh error must not escape an async Closed handler.
        }
    }

    private async Task RunUiActionAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (OperationCanceledException) when (lifetimeCancellation.IsCancellationRequested || isClosed)
        {
            // Closing the main window cancels active I/O. Event handlers are
            // fire-and-forget by WPF, so observe that cancellation here rather
            // than allowing it to reach the dispatcher as an unhandled fault.
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
        if (initializing)
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
        if (initializing || suppressRangeRefresh)
        {
            return;
        }

        await RunUiActionAsync(RangeModeChangedAsync);
    }

    private async void DatePicker_SelectedDateChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (initializing || suppressDateRefresh || DatePicker.SelectedDate is null)
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
        if (initializing || suppressWeekTimeRefresh || CurrentModule().Mode != RangeMode.Week || WeekEndPicker.Value is not DateTime selected)
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
        if (initializing || suppressStartTimeRefresh || module.CustomStartLocal is null || CustomStartPicker.Value is not DateTime selected)
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
        if (initializing || suppressCycleRefresh || CurrentModule() is not CodexUsageModule codexModule)
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

        var window = new QuotaCycleAnalysisWindow(cycle)
        {
            Owner = this
        };
        window.Show();
    }

    private async void SourceTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (initializing || !ReferenceEquals(e.Source, SourceTabs))
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
        var window = new PriceSettingsWindow(PricePresetGroups.ForSource(activeSource))
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

    private void DataTransferButton_Click(object sender, RoutedEventArgs e)
    {
        if (DataTransferButton.ContextMenu is not { } menu)
        {
            return;
        }

        menu.PlacementTarget = DataTransferButton;
        menu.IsOpen = true;
    }

    private async void ExportTodayDataButton_Click(object sender, RoutedEventArgs e)
    {
        await ExportDataAsync(CodexDataExportScope.Today);
    }

    private async void ExportThisWeekDataButton_Click(object sender, RoutedEventArgs e)
    {
        await ExportDataAsync(CodexDataExportScope.ThisWeek);
    }

    private async void ExportDataButton_Click(object sender, RoutedEventArgs e)
    {
        await ExportDataAsync(CodexDataExportScope.All);
    }

    private async void ExportCurrentCsvButton_Click(object sender, RoutedEventArgs e)
    {
        var module = CurrentModule();
        if (!module.TryGetDisplay(out var range, out var result) || !HasUsage(result))
        {
            SetStatus("当前没有可导出的统计结果");
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = $"导出 {module.Title} 当前明细 CSV",
            Filter = "CSV 文件 (*.csv)|*.csv|所有文件 (*.*)|*.*",
            DefaultExt = ".csv",
            AddExtension = true,
            FileName = $"{SanitizeFileName(module.Title)}-usage-{range.Start:yyyyMMdd-HHmm}-{range.End:yyyyMMdd-HHmm}.csv"
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        backgroundCacheWarmer.CancelCurrent();
        SetBusy(true);
        SetStatus($"正在导出 {module.Title} 当前明细...");
        try
        {
            var csv = await Task.Run(
                () => BuildCurrentCsv(module, range, result),
                lifetimeCancellation.Token);
            await WriteTextAtomicallyAsync(dialog.FileName, csv, lifetimeCancellation.Token);
            if (!isClosed)
            {
                SetStatus($"已导出当前明细 CSV：{result.BreakdownRows.Count:N0} 行");
            }
        }
        catch (OperationCanceledException) when (lifetimeCancellation.IsCancellationRequested)
        {
            if (!isClosed)
            {
                SetStatus("已取消当前明细 CSV 导出");
            }
        }
        catch (Exception ex)
        {
            if (!isClosed)
            {
                SetStatus("当前明细 CSV 导出失败");
                System.Windows.MessageBox.Show(this, ex.Message, "CSV 导出", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        finally
        {
            if (!isClosed)
            {
                SetBusy(false);
                _ = backgroundCacheWarmer.WarmNowAsync();
            }
        }
    }

    private static string BuildCurrentCsv(
        UsageSourceModule module,
        SelectedRange range,
        UsageQueryResult result)
    {
        var presets = PriceSettingsStore.DisplayPresetsForSource(module.Source, count: 0).ToList();
        var quotaLookup = module.Source == UsageSource.Codex
            ? new QuotaSnapshotLookup(result.QuotaSnapshots)
            : null;
        var headers = new List<string?>
        {
            "来源",
            "范围",
            "分桶开始(GMT+8)",
            "分桶类型",
            "Events",
            "Total Tokens",
            "Input",
            "Cached",
            "Cache Write",
            "Uncached",
            "Output",
            "Reasoning",
            "Long Context Events",
            "Long Context Input",
            "Long Context Cached",
            "Long Context Cache Write",
            "Long Context Output",
            "Last Token Event"
        };
        if (module.Source == UsageSource.Codex)
            headers.AddRange(new[] { "Actual Models", "Standard API Cost USD (known subtotal)", "Unpriced Tokens", "Unpriced Events",
                "Fast Events", "Unknown Speed Events", "Quota Reference USD (including Fast)" });
        headers.AddRange(presets.Select(item => $"换用费用 · {item.DisplayName} ({item.CurrencySymbol})"));
        if (quotaLookup is not null)
        {
            headers.Add("5h Used %");
            headers.Add("5h Reset");
            headers.Add("7d Used %");
            headers.Add("7d Reset");
            headers.Add("Quota Anomaly");
        }

        var rows = new List<IEnumerable<string?>> { headers };
        foreach (var bucket in result.BreakdownRows)
        {
            var rowIsEvent = IsEventBucket(range, bucket);
            var values = new List<string?>
            {
                module.Title,
                range.Title,
                FormatCsvDate(bucket.StartLocal),
                rowIsEvent ? "event" : "bucket",
                FormatCsvNumber(bucket.Events),
                FormatCsvNumber(bucket.TotalTokens),
                FormatCsvNumber(bucket.InputTokens),
                FormatCsvNumber(bucket.CachedInputTokens),
                FormatCsvNumber(bucket.CacheWriteInputTokens),
                FormatCsvNumber(bucket.UncachedInputTokens),
                FormatCsvNumber(bucket.OutputTokens),
                FormatCsvNumber(bucket.ReasoningOutputTokens),
                FormatCsvNumber(bucket.LongContextEvents),
                FormatCsvNumber(bucket.LongContextInputTokens),
                FormatCsvNumber(bucket.LongContextCachedInputTokens),
                FormatCsvNumber(bucket.LongContextCacheWriteInputTokens),
                FormatCsvNumber(bucket.LongContextOutputTokens),
                bucket.LastTokenEventLocal is { } lastEvent ? FormatCsvDate(lastEvent) : ""
            };
            if (module.Source == UsageSource.Codex)
            {
                var modelCost = CodexModelCost.Estimate(bucket);
                values.AddRange(new[] { CodexModelCost.DescribeModels(bucket), FormatCsvCost(modelCost.KnownCost),
                    FormatCsvNumber(modelCost.UnpricedTokens), FormatCsvNumber(modelCost.UnpricedEvents),
                    FormatCsvNumber(modelCost.FastEvents), FormatCsvNumber(modelCost.UnknownTierEvents), FormatCsvCost(modelCost.QuotaEquivalentCost) });
            }
            values.AddRange(presets.Select(item => FormatCsvCost(bucket.EstimateCost(item.ToProfile()))));

            if (quotaLookup is not null)
            {
                var quota = quotaLookup.Select(
                    range,
                    bucket,
                    rowIsEvent,
                    rowIsEvent ? null : GetQuotaBucketInterval(range));
                values.Add(quota?.FiveHourUsedPercent is { } fiveHour ? FormatCsvDecimal(fiveHour) : "");
                values.Add(quota?.FiveHourResetAtLocal is { } fiveHourReset ? FormatCsvDate(fiveHourReset) : "");
                values.Add(quota?.WeekUsedPercent is { } week ? FormatCsvDecimal(week) : "");
                values.Add(quota?.WeekResetAtLocal is { } weekReset ? FormatCsvDate(weekReset) : "");
                values.Add(quota?.IsAnomaly == true ? "true" : "false");
            }

            rows.Add(values);
        }

        return CsvWriter.Build(rows);
    }

    private static async Task WriteTextAtomicallyAsync(
        string filePath,
        string content,
        CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(filePath);
        var temporaryPath = $"{fullPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(
                temporaryPath,
                content,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Concat(value.Select(character => invalid.Contains(character) ? '_' : character));
    }

    private static string FormatCsvDate(DateTimeOffset value)
    {
        return value.ToOffset(CodexUsageReader.BeijingOffset).ToString("yyyy-MM-dd HH:mm:sszzz", CultureInfo.InvariantCulture);
    }

    private static string FormatCsvNumber(long value)
    {
        return value.ToString(CultureInfo.InvariantCulture);
    }

    private static string FormatCsvDecimal(decimal value)
    {
        return value.ToString("0.########", CultureInfo.InvariantCulture);
    }

    private static string FormatCsvCost(decimal value)
    {
        return value.ToString("0.########", CultureInfo.InvariantCulture);
    }

    private async Task ExportDataAsync(CodexDataExportScope scope)
    {
        var (scopeLabel, fileScope) = scope switch
        {
            CodexDataExportScope.Today => ("今天", "today"),
            CodexDataExportScope.ThisWeek => ("本周", "week"),
            _ => ("全部", "all")
        };
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = $"导出{scopeLabel} Codex 统计数据",
            Filter = "Codex 监控器数据包 (*.codex.json)|*.codex.json|JSON 文件 (*.json)|*.json",
            DefaultExt = ".codex.json",
            AddExtension = true,
            FileName = $"codex-data-{Environment.MachineName}-{fileScope}-{BeijingClock.DateTimeNow:yyyyMMdd-HHmmss}.codex.json"
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        backgroundCacheWarmer.CancelCurrent();
        SetBusy(true);
        SetStatus($"正在导出{scopeLabel} Codex 数据...");
        try
        {
            CodexDataExportResult result;
            await usageQueryGate.WaitAsync(lifetimeCancellation.Token);
            try
            {
                result = await Task.Run(
                    () => CodexDataTransferService.Export(dialog.FileName, scope, lifetimeCancellation.Token),
                    lifetimeCancellation.Token);
            }
            finally
            {
                usageQueryGate.Release();
            }

            SetStatus($"已导出{scopeLabel}数据：{result.UsageEventCount:N0} 条用量 · {result.QuotaSnapshotCount:N0} 条额度快照");
            System.Windows.MessageBox.Show(
                this,
                $"已导出到：\n{result.FilePath}\n\n" +
                $"数据范围：{scopeLabel}\n" +
                $"用量事件：{result.UsageEventCount:N0}\n" +
                $"额度快照：{result.QuotaSnapshotCount:N0}",
                "Codex 数据导出",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (OperationCanceledException) when (lifetimeCancellation.IsCancellationRequested)
        {
            if (!isClosed)
            {
                SetStatus($"已取消导出{scopeLabel}数据");
            }
        }
        catch (Exception ex)
        {
            if (!isClosed)
            {
                SetStatus("数据导出失败");
                System.Windows.MessageBox.Show(this, ex.Message, "Codex 数据导出", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        finally
        {
            if (!isClosed)
            {
                SetBusy(false);
                _ = backgroundCacheWarmer.WarmNowAsync();
            }
        }
    }

    private async void ImportDataButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "导入其他电脑的 Codex 统计数据",
            Filter = "Codex 监控器数据包 (*.codex.json)|*.codex.json|JSON 文件 (*.json)|*.json",
            DefaultExt = ".codex.json",
            Multiselect = true,
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        await ImportDataAsync(dialog.FileNames);
    }

    private void MainWindow_PreviewDragOver(object sender, System.Windows.DragEventArgs e)
    {
        e.Effects = !isRefreshing && GetDroppedCodexDataPackages(e.Data).Count > 0
            ? System.Windows.DragDropEffects.Copy
            : System.Windows.DragDropEffects.None;
        e.Handled = true;
    }

    private async void MainWindow_PreviewDrop(object sender, System.Windows.DragEventArgs e)
    {
        e.Handled = true;
        if (isRefreshing)
        {
            System.Windows.MessageBox.Show(
                this,
                "当前正在刷新或处理数据，请稍后再拖入数据包。",
                "Codex 数据导入",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var filePaths = GetDroppedCodexDataPackages(e.Data);
        if (filePaths.Count == 0)
        {
            System.Windows.MessageBox.Show(
                this,
                "没有识别到 Codex 数据包。\n\n请拖入 *.codex.json 或旧版 *.codex-data.json 文件。",
                "Codex 数据导入",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var fileList = string.Join("\n", filePaths.Take(8).Select(path => $"• {Path.GetFileName(path)}"));
        if (filePaths.Count > 8)
        {
            fileList += $"\n• 另有 {filePaths.Count - 8:N0} 个数据包";
        }

        var prompt = filePaths.Count == 1
            ? $"是否合并这个 Codex 数据包？\n\n{fileList}"
            : $"是否合并这 {filePaths.Count:N0} 个 Codex 数据包？\n\n{fileList}";
        var confirmation = System.Windows.MessageBox.Show(
            this,
            prompt + "\n\n请确认它们来自同一个 Codex 账户。重复导入不会重复计数。",
            "确认合并 Codex 数据",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No);
        if (confirmation != MessageBoxResult.Yes)
        {
            SetStatus("已取消合并数据包");
            return;
        }

        await ImportDataAsync(filePaths);
    }

    private static IReadOnlyList<string> GetDroppedCodexDataPackages(System.Windows.IDataObject data)
    {
        if (!data.GetDataPresent(System.Windows.DataFormats.FileDrop) ||
            data.GetData(System.Windows.DataFormats.FileDrop) is not string[] filePaths)
        {
            return Array.Empty<string>();
        }

        return filePaths
            .Where(File.Exists)
            .Where(IsCodexDataPackagePath)
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool IsCodexDataPackagePath(string filePath)
    {
        return filePath.EndsWith(".codex.json", StringComparison.OrdinalIgnoreCase) ||
               filePath.EndsWith(".codex-data.json", StringComparison.OrdinalIgnoreCase);
    }

    private async Task ImportDataAsync(IReadOnlyList<string> filePaths)
    {
        Interlocked.Increment(ref quotaRefreshVersion);
        backgroundCacheWarmer.CancelCurrent();
        SetBusy(true);
        SetStatus($"正在导入 {filePaths.Count:N0} 个数据包...");
        try
        {
            CodexDataImportResult result;
            await usageQueryGate.WaitAsync(lifetimeCancellation.Token);
            try
            {
                result = await Task.Run(
                    () => CodexDataTransferService.Import(filePaths, lifetimeCancellation.Token),
                    lifetimeCancellation.Token);
            }
            finally
            {
                usageQueryGate.Release();
            }

            var codexModule = CurrentCodexModule();
            codexModule.CurrentQuotaEstimate = null;
            CodexQuotaCycleReader.InvalidateCache();
            foreach (var module in usageModules.Values)
            {
                module.ClearDisplay();
            }

            SetStatus($"已导入 {result.AddedUsageEventCount:N0} 条新用量 · {result.AddedQuotaSnapshotCount:N0} 条新额度快照");
            System.Windows.MessageBox.Show(
                this,
                $"已合并 {result.FileCount:N0} 个数据包（{result.DeviceCount:N0} 台电脑）。\n\n" +
                $"新增用量事件：{result.AddedUsageEventCount:N0}\n" +
                $"已存在用量事件：{result.ExistingUsageEventCount:N0}\n" +
                $"新增额度快照：{result.AddedQuotaSnapshotCount:N0}\n" +
                $"已存在额度快照：{result.ExistingQuotaSnapshotCount:N0}\n\n" +
                "请只合并同一 Codex 账户的数据。重复导入不会重复计数。",
                "Codex 数据导入",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (OperationCanceledException) when (lifetimeCancellation.IsCancellationRequested)
        {
            if (!isClosed)
            {
                SetStatus("已取消数据导入");
            }
        }
        catch (Exception ex)
        {
            if (!isClosed)
            {
                SetStatus("数据导入失败");
                System.Windows.MessageBox.Show(this, ex.Message, "Codex 数据导入", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        finally
        {
            if (!isClosed)
            {
                SetBusy(false);
            }
        }

        if (isClosed)
        {
            return;
        }

        await RefreshUsageAsync();
        _ = backgroundCacheWarmer.WarmNowAsync();
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

    private async Task SyncResetOpportunitiesFromCodexAsync(bool showError, CancellationToken cancellationToken = default)
    {
        var result = await resetOpportunitySynchronizer.SyncAsync(cancellationToken);
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

    private async void QuotaEstimateButton_Click(object sender, RoutedEventArgs e)
    {
        var codexModule = CurrentCodexModule();
        var quota = codexModule.CurrentQuotaEstimate;
        if (quota is null)
        {
            return;
        }

        await RefreshCycleOptionsAsync(keepSelection: true);
        quota = codexModule.CurrentQuotaEstimate;
        if (quota is null)
        {
            return;
        }

        var window = new QuotaEstimateWindow(quota, codexModule.QuotaCycles, usageQueryGate)
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
            suppressRangeRefresh = true;
            RangeModeBox.SelectedIndex = 0;
            suppressRangeRefresh = false;
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
        var selectedDay = DateOnly.FromDateTime(module.PickerValue.Date);
        SetStatus($"清除 {module.Title} {selectedDay:yyyy-MM-dd} 缓存...");

        bool deleted;
        await usageQueryGate.WaitAsync(lifetimeCancellation.Token);
        try
        {
            deleted = await Task.Run(
                () => module.Reader.RefreshCachedDay(selectedDay, lifetimeCancellation.Token),
                lifetimeCancellation.Token);
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

    private Task RefreshUsageAsync(bool cacheOnly = false, bool isAutomaticRefresh = false)
    {
        lock (usageRefreshSync)
        {
            pendingCacheOnly = cacheOnly;
            pendingIsAutomaticRefresh = isAutomaticRefresh;
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
            bool isAutomaticRefresh;
            long requestVersion;
            lock (usageRefreshSync)
            {
                if (!usageRefreshPending)
                {
                    usageRefreshLoopRunning = false;
                    return;
                }

                cacheOnly = pendingCacheOnly;
                isAutomaticRefresh = pendingIsAutomaticRefresh;
                requestVersion = usageRefreshVersion;
                usageRefreshPending = false;
            }

            await RefreshUsageOnceAsync(cacheOnly, isAutomaticRefresh, requestVersion);
        }
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
            if (range.Mode == RangeMode.Cycle && !includeLiveToday && module.TryGetCachedDisplay(range, out var cachedResult))
            {
                if (requestVersion != Volatile.Read(ref usageRefreshVersion) || isClosed)
                {
                    return;
                }

                module.StoreDisplay(range, cachedResult);
                if (CurrentSource() == source)
                {
                    ApplySummary(range, cachedResult, module);
                    SetStatus($"周期结果缓存命中 {BeijingClock.DateTimeNow:HH:mm:ss} · {stopwatch.ElapsedMilliseconds:N0}ms");
                    if (!cacheOnly && module.Reader.SupportsQuota)
                    {
                        _ = RefreshQuotaSummaryAsync();
                    }
                }

                return;
            }

            var usesCachedCycleData = range.Mode == RangeMode.Cycle && !includeLiveToday;
            // Automatic refresh waits for the current cache item through the shared
            // gate; isRefreshing keeps the warmer from starting its next item.
            if (!isAutomaticRefresh && !usesCachedCycleData && backgroundCacheWarmer.IsRunning)
            {
                resumeBackgroundCache = true;
                backgroundCacheWarmer.CancelCurrent();
                SetStatus("正在刷新...");
            }

            UsageQueryResult QueryUsage()
            {
                if (range.IsCustomStart)
                {
                    var transientRows = module.Reader.ReadTransientDetailRows(
                        range.Start,
                        range.End,
                        lifetimeCancellation.Token);
                    var transientSummary = CreateSummaryFromRows(range, transientRows);
                    var transientQuota = ReadQuotaForRefresh(
                        module.Reader,
                        includeLiveToday,
                        cachedQuota,
                        lifetimeCancellation.Token);
                    var transientQuotaSnapshots = module.Reader.SupportsQuota
                        ? ReadQuotaSnapshotsForRefresh(
                            range,
                            transientRows,
                            includeLiveToday,
                            transientQuota,
                            lifetimeCancellation.Token)
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
                    var dayUsage = module.Reader.ReadDay(
                        range.Start,
                        range.End,
                        includeLiveToday,
                        lifetimeCancellation.Token);
                    var dayRows = dayUsage.Rows;
                    var daySummary = dayUsage.Summary;
                    var historicalCacheNeedsRepair = !cacheOnly &&
                                                     !includeLiveToday &&
                                                     (daySummary.Events > 0 || dayRows.Count > 0) &&
                                                     HasIncompleteHistoricalCache(
                                                         module.Reader,
                                                         range,
                                                         lifetimeCancellation.Token);
                    if (historicalCacheNeedsRepair ||
                        (daySummary.Events > 0 &&
                         UsageBreakdownBuilder.CountEvents(dayRows) < daySummary.Events))
                    {
                        dayRows = module.Reader.ReadDetailRows(
                            range.Start,
                            range.End,
                            includeLiveToday: false,
                            cancellationToken: lifetimeCancellation.Token);
                        daySummary = UsageSummaryBuilder.FromRows(range.Start, range.End, dayRows);
                    }
                    var dayQuota = ReadQuotaForRefresh(
                        module.Reader,
                        includeLiveToday,
                        cachedQuota,
                        lifetimeCancellation.Token);
                    var dayQuotaSnapshots = module.Reader.SupportsQuota
                        ? ReadQuotaSnapshotsForRefresh(
                            range,
                            dayRows,
                            includeLiveToday,
                            dayQuota,
                            lifetimeCancellation.Token)
                        : Array.Empty<CodexQuotaSnapshot>();
                    return new UsageQueryResult(
                        daySummary,
                        dayRows,
                        UsageBreakdownBuilder.EstimateCodingTime(dayRows),
                        dayQuota,
                        dayQuotaSnapshots)
                    {
                        DetailRows = dayRows
                    };
                }

                var summary = includeLiveToday
                    ? module.Reader.ReadRange(
                        range.Start,
                        range.End,
                        includeLiveToday,
                        lifetimeCancellation.Token)
                    : module.Reader.ReadCachedRange(
                        range.Start,
                        range.End,
                        lifetimeCancellation.Token);
                var detailRows = module.Reader.ReadCachedDetailRows(
                    range.Start,
                    range.End,
                    lifetimeCancellation.Token);
                if (!cacheOnly &&
                    !includeLiveToday &&
                    (summary.Events > 0 || detailRows.Count > 0) &&
                    HasIncompleteHistoricalCache(
                        module.Reader,
                        range,
                        lifetimeCancellation.Token))
                {
                    summary = module.Reader.ReadRange(
                        range.Start,
                        range.End,
                        includeLiveToday: false,
                        cancellationToken: lifetimeCancellation.Token);
                    detailRows = module.Reader.ReadCachedDetailRows(
                        range.Start,
                        range.End,
                        lifetimeCancellation.Token);
                }
                if (!cacheOnly &&
                    summary.Events > 0 &&
                    UsageBreakdownBuilder.CountEvents(detailRows) < summary.Events)
                {
                    detailRows = UsageBreakdownBuilder.ReadDetailRowsForRange(
                        range.Start,
                        range.End,
                        (dayStart, dayEnd) => module.Reader.ReadDetailRows(
                            dayStart,
                            dayEnd,
                            includeLiveToday,
                            lifetimeCancellation.Token));
                }
                var rows = UsageBreakdownBuilder.Build(range, summary, detailRows, MultiDayBreakdownInterval);
                var codingTime = UsageBreakdownBuilder.EstimateCodingTimeForRange(
                    module.Reader,
                    range,
                    rows,
                    detailRows,
                    includeLiveToday,
                    !includeLiveToday,
                    lifetimeCancellation.Token);
                var quota = ReadQuotaForRefresh(
                    module.Reader,
                    includeLiveToday,
                    cachedQuota,
                    lifetimeCancellation.Token);
                var quotaSnapshots = module.Reader.SupportsQuota
                    ? ReadQuotaSnapshotsForRefresh(
                        range,
                        rows,
                        includeLiveToday,
                        quota,
                        lifetimeCancellation.Token)
                    : Array.Empty<CodexQuotaSnapshot>();
                return new UsageQueryResult(summary, rows, codingTime, quota, quotaSnapshots)
                {
                    DetailRows = detailRows
                };
            }

            UsageQueryResult result;
            if (usesCachedCycleData)
            {
                result = await Task.Run(QueryUsage, lifetimeCancellation.Token);
            }
            else
            {
                await usageQueryGate.WaitAsync(lifetimeCancellation.Token);
                try
                {
                    result = await Task.Run(QueryUsage, lifetimeCancellation.Token);
                }
                finally
                {
                    usageQueryGate.Release();
                }
            }

            if (requestVersion != Volatile.Read(ref usageRefreshVersion) || isClosed)
            {
                return;
            }

            var canCacheCycleResult = false;
            if (range.Mode == RangeMode.Cycle &&
                !includeLiveToday &&
                !range.IsCustomStart &&
                range.Start < range.End)
            {
                await usageQueryGate.WaitAsync(lifetimeCancellation.Token);
                try
                {
                    canCacheCycleResult = await Task.Run(
                        () => CanCacheCycleResult(
                            module.Reader,
                            range,
                            includeLiveToday,
                            lifetimeCancellation.Token),
                        lifetimeCancellation.Token);
                }
                finally
                {
                    usageQueryGate.Release();
                }
            }

            if (requestVersion != Volatile.Read(ref usageRefreshVersion) || isClosed)
            {
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

    private static bool CanCacheCycleResult(
        IUsageSourceReader reader,
        SelectedRange range,
        bool includeLiveToday,
        CancellationToken cancellationToken = default)
    {
        if (includeLiveToday || range.Mode != RangeMode.Cycle || range.IsCustomStart || range.Start >= range.End)
        {
            return false;
        }

        var now = DateTimeOffset.UtcNow.ToOffset(CodexUsageReader.BeijingOffset);
        var todayStart = new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, CodexUsageReader.BeijingOffset);
        var lastIncluded = range.End.AddTicks(-1);
        return lastIncluded < todayStart &&
               reader.GetIncompleteHistoricalDays(range.Start, lastIncluded, cancellationToken).Count == 0;
    }

    private async Task ResumeBackgroundCacheAsync()
    {
        try
        {
            for (var attempt = 0; attempt < 30 && backgroundCacheWarmer.IsRunning && !isClosed; attempt++)
            {
                await Task.Delay(100, lifetimeCancellation.Token);
            }

            lifetimeCancellation.Token.ThrowIfCancellationRequested();
            if (!isClosed)
            {
                await backgroundCacheWarmer.WarmNowAsync();
            }
        }
        catch (OperationCanceledException) when (lifetimeCancellation.IsCancellationRequested || isClosed)
        {
            // A close can interrupt the short hand-off wait; no new warm cycle
            // should be started after the window lifetime has ended.
        }
    }

    private async Task RefreshQuotaSummaryAsync()
    {
        if (isQuotaRefreshing || CurrentModule() is not CodexUsageModule codexModule)
        {
            return;
        }

        var requestVersion = Volatile.Read(ref quotaRefreshVersion);
        isQuotaRefreshing = true;
        try
        {
            if (isClosed || requestVersion != Volatile.Read(ref quotaRefreshVersion))
            {
                return;
            }

            await usageQueryGate.WaitAsync(lifetimeCancellation.Token);
            CodexQuotaEstimate? cachedQuota;
            CodexQuotaEstimate? quota;
            try
            {
                if (isClosed || requestVersion != Volatile.Read(ref quotaRefreshVersion))
                {
                    return;
                }

                cachedQuota = await Task.Run(
                    () => FreshQuotaOrNull(CodexUsageReader.ReadCachedQuotaEstimate(lifetimeCancellation.Token)),
                    lifetimeCancellation.Token);
                quota = await Task.Run(
                    () => CodexUsageReader.ReadQuotaEstimate(lifetimeCancellation.Token),
                    lifetimeCancellation.Token);
            }
            finally
            {
                usageQueryGate.Release();
            }

            if (isClosed || requestVersion != Volatile.Read(ref quotaRefreshVersion))
            {
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
        catch (OperationCanceledException) when (lifetimeCancellation.IsCancellationRequested || isClosed)
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
        if (module.Source == UsageSource.Codex)
        {
            var settings = PriceSettingsStore.Current.Clone();
            if (CodexModelCost.AddMissingPresets(settings, result.Summary.ModelUsage.Keys) > 0)
                PriceSettingsStore.Save(settings);
        }
        Title = $"{module.Title} Token 额度监控器 - {range.Title}";
        var hasUsage = HasUsage(result);
        ApplyUsageContentState(range, module, hasUsage);
        TotalValue.Text = FormatTokenMillions(result.Summary.TotalTokens);
        PeriodValue.Text = $"{result.Summary.StartLocal:yyyy-MM-dd HH:mm} - {result.Summary.EndLocal:yyyy-MM-dd HH:mm:ss}  GMT+8";
        InputValue.Text = FormatTokenMillions(result.Summary.InputTokens);
        CachedValue.Text = FormatTokenMillions(result.Summary.CachedInputTokens);
        CacheWriteValue.Text = FormatTokenMillions(result.Summary.CacheWriteInputTokens);
        UncachedValue.Text = FormatTokenMillions(result.Summary.UncachedInputTokens);
        OutputValue.Text = FormatTokenAdaptive(result.Summary.OutputTokens);
        ReasoningValue.Text = FormatTokenAdaptive(result.Summary.ReasoningOutputTokens);
        CacheRatioValue.Text = result.Summary.InputTokens > 0 ? $"{result.Summary.CacheRatioPercent:N2}%" : "0.00%";
        EventsValue.Text = result.Summary.Events.ToString("N0");
        CodingTimeValue.Text = FormatDuration(result.CodingTime);
        CopySummaryButton.IsEnabled = hasUsage && !isRefreshing;

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

        var previousInitializing = initializing;
        initializing = true;
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
        initializing = previousInitializing;
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
        return source switch
        {
            UsageSource.ClaudeCode => 1,
            UsageSource.ZCode => 2,
            UsageSource.WorkBuddy => 3,
            UsageSource.Dsh => 4,
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
        UsageSummaryPanel.Visibility = Visibility.Collapsed;
        UsageMetricsPanel.Visibility = Visibility.Collapsed;
        UsageDetailsPanel.Visibility = Visibility.Collapsed;
        EmptyUsagePanel.Visibility = Visibility.Visible;
        EmptyUsageTitle.Text = $"正在读取 {module.Title} 用量";
        EmptyUsagePeriod.Text = "";
        EmptyUsageHint.Text = "完成后会自动显示当前时段的统计结果。";
        TotalValue.Text = "-";
        PeriodValue.Text = "-";
        InputValue.Text = "-";
        CachedValue.Text = "-";
        CacheWriteValue.Text = "-";
        UncachedValue.Text = "-";
        OutputValue.Text = "-";
        ReasoningValue.Text = "-";
        CacheRatioValue.Text = "-";
        EventsValue.Text = "-";
        CodingTimeValue.Text = "-";
        CostCardsPanel.Children.Clear();
        CopySummaryButton.IsEnabled = false;
        SetTimelineVisible(false);
        ApplyQuotaSummary(module, module is CodexUsageModule codexModule ? codexModule.CurrentQuotaEstimate : null);
        ApplyBreakdownRows(GetSelectedRange(), Array.Empty<TokenUsageBucket>(), Array.Empty<CodexQuotaSnapshot>(), module.Source, PriceSettingsStore.DisplayPresetsForSource(module.Source, count: 0).ToList());
    }

    private void ApplyCostCards(IReadOnlyList<PricePreset> presets, TokenUsageSummary summary)
    {
        UsageSummaryPanel.Height = 112;
        CostCardsPanel.Children.Clear();
        if (CurrentModule().Source == UsageSource.Codex)
            CostCardsPanel.Children.Add(CreateCostCard(new PricePreset { Provider = "OpenAI", Model = "实际模型 · 标准 API 等价" }, summary, actual: true));
        foreach (var preset in presets.Take(GetVisibleCostColumnCount(presets.Count)))
        {
            CostCardsPanel.Children.Add(CreateCostCard(preset, summary, comparison: CurrentModule().Source == UsageSource.Codex));
        }
        if (CostCardsPanel.Children.Count > 0 && CostCardsPanel.Children[^1] is FrameworkElement lastCard)
            lastCard.Margin = new Thickness(0);
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

    private void ApplyUsageContentState(SelectedRange range, UsageSourceModule module, bool hasUsage)
    {
        UsageSummaryPanel.Visibility = hasUsage ? Visibility.Visible : Visibility.Collapsed;
        UsageMetricsPanel.Visibility = hasUsage ? Visibility.Visible : Visibility.Collapsed;
        UsageDetailsPanel.Visibility = hasUsage ? Visibility.Visible : Visibility.Collapsed;
        EmptyUsagePanel.Visibility = hasUsage ? Visibility.Collapsed : Visibility.Visible;
        if (hasUsage)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow.ToOffset(CodexUsageReader.BeijingOffset);
        var isToday = range.Mode == RangeMode.Day && range.Start.Date == now.Date;
        EmptyUsageTitle.Text = isToday
            ? $"今天还没有 {module.Title} 用量"
            : $"{range.Title}暂无 {module.Title} 用量";
        EmptyUsagePeriod.Text =
            $"{range.Start:yyyy-MM-dd HH:mm} — {range.End:yyyy-MM-dd HH:mm:ss}  GMT+8";
        EmptyUsageHint.Text = module.Source == UsageSource.Codex
            ? "额度会继续独立刷新；产生首条 Codex 用量后，这里会自动恢复统计卡和明细表。"
            : $"产生首条 {module.Title} 用量后，这里会自动恢复统计卡和明细表。";
    }

    private static bool HasUsage(UsageQueryResult result)
    {
        return result.Summary.Events > 0 ||
               result.Summary.TotalTokens > 0 ||
               result.Summary.InputTokens > 0 ||
               result.Summary.OutputTokens > 0 ||
               result.Summary.ReasoningOutputTokens > 0 ||
               result.BreakdownRows.Any(row =>
                   row.Events > 0 ||
                   row.TotalTokens > 0 ||
                   row.InputTokens > 0 ||
                   row.OutputTokens > 0 ||
                   row.ReasoningOutputTokens > 0);
    }

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
            builder.AppendLine("相同 Token 换模型费用估算：");
            foreach (var preset in presets)
            {
                var label = string.IsNullOrWhiteSpace(preset.Provider)
                    ? preset.Model
                    : $"{preset.Provider} · {preset.Model}";
                var profile = preset.ToProfile();
                builder.AppendLine($"- {label}：{FormatCost(summary.EstimateCost(profile), profile)}");
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

    private static string BuildModelCostDetails(TokenUsageBucket usage)
    {
        var estimate = CodexModelCost.Estimate(usage);
        var lines = new List<string> { "按日志模型与当前价格库估算 API 等价费用；不是订阅账单。" };
        lines.Add(estimate.SpeedDescription);
        lines.Add($"订阅基准折算（含 Fast）：{estimate.FormatQuotaCost()}");
        foreach (var model in estimate.Models)
            lines.Add($"{model.ModelId}: Input {model.Usage.InputTokens:N0} / Cached {model.Usage.CachedInputTokens:N0} / Cache Write {model.Usage.CacheWriteInputTokens:N0} / Output {model.Usage.OutputTokens:N0} · {(model.Cost is { } cost ? $"${cost:N4}" : CodexModelCost.HasNoPublicPrice(model.ModelId) ? "暂无公开 API 单价" : "$0 · 0x 待填写")}");
        if (!estimate.IsComplete)
        {
            lines.Add(estimate.MissingPriceDescription);
            lines.Add("未计价记录不计入美元金额；缺模型需从原日志补全，缺价格可在价格设置按模型 ID 填写。");
        }
        return string.Join(Environment.NewLine, lines);
    }

    private static UIElement CreateCostCard(PricePreset preset, TokenUsageSummary summary, bool actual = false, bool comparison = false)
    {
        var profile = preset.ToProfile();
        var card = new Grid
        {
            ToolTip = actual ? BuildModelCostDetails(summary) : comparison ? "按相同输入、缓存和输出 Token 换算，实际换模型后的用量可能不同。" : null
        };
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
            Text = comparison ? $"换用 {preset.Model}" : preset.Model,
            Foreground = new SolidColorBrush(MediaColor.FromRgb(101, 114, 130)),
            TextTrimming = TextTrimming.CharacterEllipsis,
            FontSize = 11.5,
            Margin = new Thickness(0, 1, 0, 2)
        };
        Grid.SetRow(subtitle, 1);
        card.Children.Add(subtitle);

        var value = new TextBlock
        {
            Text = actual ? CodexModelCost.Estimate(summary).Format() : FormatCost(summary.EstimateCost(profile), profile),
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
                Model = source == UsageSource.Codex ? CodexModelCost.DescribeModels(bucket) : "",
                ActualCost = source == UsageSource.Codex ? CodexModelCost.Estimate(bucket).Format("N4") : "",
                Total = FormatBreakdownToken(bucket.TotalTokens, rowIsEvent),
                Input = FormatBreakdownToken(bucket.InputTokens, rowIsEvent),
                Cached = FormatBreakdownToken(bucket.CachedInputTokens, rowIsEvent),
                CacheWrite = FormatBreakdownToken(bucket.CacheWriteInputTokens, rowIsEvent),
                Uncached = FormatBreakdownToken(bucket.UncachedInputTokens, rowIsEvent),
                Output = FormatTokenAdaptive(bucket.OutputTokens),
                Prices = tableProfiles.Select(profile => FormatCost(bucket.EstimateCost(profile), profile)).ToArray(),
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
            Quota5hValue.Text = "--";
            Quota5hDetail.Text = "等待 Codex 实时额度";
            QuotaWeekValue.Text = "--";
            QuotaWeekDetail.Text = "暂未读取到周额度";
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

            var cycleEnd = cycle.IsCurrent ? now : cycle.PeriodEnd;
            if (cycleEnd < cycle.PeriodStart)
            {
                cycleEnd = cycle.PeriodStart;
            }

            return new SelectedRange(
                cycle.PeriodStart,
                cycleEnd,
                cycle.IsCurrent ? "当前周期" : $"周期 {cycle.PeriodStart:MM-dd HH:mm}",
                "按天明细（额度周期）",
                RangeMode.Cycle,
                FollowsCurrent: cycle.IsCurrent);
        }

        DateTimeOffset start;
        DateTimeOffset periodEnd;
        string title;
        string breakdownTitle;
        bool followsCurrent;
        switch (module.Mode)
        {
            case RangeMode.Week:
                periodEnd = selectedDateTime > now ? now : selectedDateTime;
                start = periodEnd.AddDays(-7);
                followsCurrent = periodEnd >= now.AddSeconds(-2);
                title = followsCurrent ? "近一周" : $"7天至 {periodEnd:MM-dd HH:mm}";
                breakdownTitle = "按天明细（7天窗口）";
                break;
            case RangeMode.Month:
                start = new DateTimeOffset(selectedDay.Year, selectedDay.Month, 1, 0, 0, 0, CodexUsageReader.BeijingOffset);
                periodEnd = start.AddMonths(1);
                followsCurrent = start.Year == now.Year && start.Month == now.Month;
                title = followsCurrent ? "本月" : start.ToString("yyyy-MM");
                breakdownTitle = "按天明细（本月）";
                break;
            default:
                start = selectedDay;
                periodEnd = start.AddDays(1);
                followsCurrent = start.Date == now.Date;
                title = followsCurrent ? "今天" : start.ToString("yyyy-MM-dd");
                breakdownTitle = "事件明细（当天）";
                break;
        }

        var end = periodEnd > now ? now : periodEnd;
        if (end < start)
        {
            end = start;
        }

        return new SelectedRange(
            start,
            end,
            title,
            breakdownTitle,
            module.Mode,
            FollowsCurrent: followsCurrent);
    }

    private static bool ShouldAdvanceToCurrentPeriod(UsageSourceModule module, SelectedRange range)
    {
        return module.Mode is (RangeMode.Day or RangeMode.Week or RangeMode.Month) &&
               !range.FollowsCurrent &&
               module.LastRange?.FollowsCurrent == true;
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

    private static bool HasIncompleteHistoricalCache(
        IUsageSourceReader reader,
        SelectedRange range,
        CancellationToken cancellationToken = default)
    {
        if (range.Start >= range.End)
        {
            return false;
        }

        return reader.GetIncompleteHistoricalDays(
            range.Start,
            range.End.AddTicks(-1),
            cancellationToken).Count > 0;
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
        UpdateCopySummaryState();
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

            cycleRefreshTask = RefreshCycleOptionsCoreAsync(keepSelection);
            return cycleRefreshTask;
        }
    }

    private async Task RefreshCycleOptionsCoreAsync(bool keepSelection)
    {
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
            await usageQueryGate.WaitAsync(lifetimeCancellation.Token);
            try
            {
                if (isClosed)
                {
                    return;
                }

                var now = DateTimeOffset.UtcNow.ToOffset(CodexUsageReader.BeijingOffset);
                cycles = await Task.Run(
                    () => CodexQuotaCycleReader.ReadWeeklyCycles(quota, now, lifetimeCancellation.Token),
                    lifetimeCancellation.Token);
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

            codexModule.QuotaCycles = cycles;
            ApplyCycleOptions(codexModule, cycles, keepSelection);
            if (codexModule.Mode == RangeMode.Cycle)
            {
                UpdateRangeControls();
            }
        }
        catch
        {
            // Cycle data is auxiliary; the main usage and quota cards should remain usable.
        }
    }

    private void ApplyCycleOptions(
        CodexUsageModule codexModule,
        IReadOnlyList<CodexQuotaCycle> cycles,
        bool keepSelection)
    {
        var selected = keepSelection ? codexModule.SelectedCycle ?? SelectedCycle() : null;
        suppressCycleRefresh = true;
        try
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
        finally
        {
            suppressCycleRefresh = false;
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
        DataTransferButton.IsEnabled = !busy;
        if (busy)
        {
            StartNowButton.IsEnabled = false;
            WeekPickerButton.IsEnabled = false;
            RefreshDayButton.IsEnabled = false;
            CopySummaryButton.IsEnabled = false;
        }
        else
        {
            UpdateRangeControls();
        }

        UpdateAutoRefreshState();
    }

    private void UpdateCopySummaryState()
    {
        if (CopySummaryButton is null)
        {
            return;
        }

        var module = CurrentModule();
        CopySummaryButton.IsEnabled = !isRefreshing &&
                                      module.TryGetDisplay(out _, out var result) &&
                                      HasUsage(result);
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
        return SourceTabs.SelectedIndex switch
        {
            1 => UsageSource.ClaudeCode,
            2 => UsageSource.ZCode,
            3 => UsageSource.WorkBuddy,
            4 => UsageSource.Dsh,
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

    private static CodexQuotaEstimate? ReadQuotaForRefresh(
        IUsageSourceReader reader,
        bool includeLiveToday,
        CodexQuotaEstimate? cachedQuota,
        CancellationToken cancellationToken = default)
    {
        if (!reader.SupportsQuota)
        {
            return null;
        }

        _ = includeLiveToday;
        return FreshQuotaOrNull(cachedQuota) ?? CodexUsageReader.ReadCachedQuotaEstimate(cancellationToken);
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
        CodexQuotaEstimate? quota,
        CancellationToken cancellationToken = default)
    {
        var anchors = rows
            .Select(bucket =>
            {
                if (IsEventBucket(range, bucket))
                {
                    return bucket.StartLocal;
                }

                var bucketEnd = bucket.StartLocal.Add(GetQuotaBucketInterval(range));
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
            refreshExisting: false,
            cancellationToken: cancellationToken);
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
        return UsageSummaryBuilder.FromRows(range.Start, range.End, rows);
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
        return range.Mode is RangeMode.Week or RangeMode.Cycle
            ? MultiDayBreakdownInterval
            : TimeSpan.FromDays(1);
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
