using System.Globalization;
using System.Text;
using System.Windows;

namespace CodexTokenMonitor;

public partial class MainWindow
{
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
        await RunUiActionAsync(ExportCurrentCsvAsync);
    }

    private async Task ExportCurrentCsvAsync()
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
                runtime.LifetimeToken);
            await WriteTextAtomicallyAsync(dialog.FileName, csv, runtime.LifetimeToken);
            if (!isClosed)
            {
                SetStatus($"已导出当前明细 CSV：{result.BreakdownRows.Count:N0} 行");
            }
        }
        catch (OperationCanceledException) when (runtime.IsStopping)
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

    private Task ExportDataAsync(CodexDataExportScope scope) => RunUiActionAsync(() => ExportDataCoreAsync(scope));

    private async Task ExportDataCoreAsync(CodexDataExportScope scope)
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
            await usageQueryGate.WaitAsync(runtime.LifetimeToken);
            try
            {
                result = await Task.Run(
                    () => CodexDataTransferService.Export(dialog.FileName, scope, runtime.LifetimeToken),
                    runtime.LifetimeToken);
            }
            finally
            {
                usageQueryGate.Release();
            }

            if (isClosed) return;
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
        catch (OperationCanceledException) when (runtime.IsStopping)
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

    private Task ImportDataAsync(IReadOnlyList<string> filePaths) => RunUiActionAsync(() => ImportDataCoreAsync(filePaths));

    private async Task ImportDataCoreAsync(IReadOnlyList<string> filePaths)
    {
        Interlocked.Increment(ref quotaRefreshVersion);
        backgroundCacheWarmer.CancelCurrent();
        SetBusy(true);
        SetStatus($"正在导入 {filePaths.Count:N0} 个数据包...");
        try
        {
            CodexDataImportResult result;
            await usageQueryGate.WaitAsync(runtime.LifetimeToken);
            try
            {
                result = await Task.Run(
                    () => CodexDataTransferService.Import(filePaths, runtime.LifetimeToken),
                    runtime.LifetimeToken);
            }
            finally
            {
                usageQueryGate.Release();
            }

            if (isClosed) return;
            var codexModule = CurrentCodexModule();
            codexModule.CurrentQuotaEstimate = null;
            UsageSourceReaders.Codex.Cycles.InvalidateCache();
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
        catch (OperationCanceledException) when (runtime.IsStopping)
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
}
