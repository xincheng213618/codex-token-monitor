using System.Windows;
using System.Windows.Threading;

namespace CodexTokenMonitor;

public partial class MainWindow
{
    private CodexDataSharingServer? dataSharingServer;
    private DataSharingWindow? dataSharingWindow;
    private CodexHistorySharingStore? historySharingStore;
    private readonly SemaphoreSlim dataSharingClientGate = new(1, 1);
    private readonly DispatcherTimer automaticTodayUploadTimer = new();
    private bool automaticTodayUploadTimerInitialized;
    private Task automaticTodayUploadTask = Task.CompletedTask;

    private void EnsureDataSharingServer()
    {
        historySharingStore ??= new CodexHistorySharingStore(cacheGate: usageQueryGate,
            imported: result => NotifySharedDataImported(result, refresh: false), runtime: runtime);
        dataSharingServer ??= new CodexDataSharingServer(
            ExportSharedUsageAsync,
            ImportSharedUsageAsync,
            historySharingStore,
            ExportSharedUsageTodayAsync,
            ImportSharedUsageTodayAsync);
    }

    private async Task StartDataSharingOnLaunchAsync()
    {
        try
        {
            var settings = CodexDataSharingSettings.Load();
            ConfigureAutomaticTodayUpload();
            if (!settings.AutoStart) return;
            // Persist a newly generated key before opening the listener.
            settings.Save();
            EnsureDataSharingServer();
            await dataSharingServer!.StartAsync(settings.Port, settings.AccessKey, runtime.LifetimeToken);
        }
        catch (OperationCanceledException) when (runtime.IsStopping) { }
        catch (Exception ex)
        {
            if (!isClosed) SetStatus($"共享服务自动开启失败：{ex.Message}；可在数据管理 → 局域网共享中重试。");
        }
    }

    private void DataSharingButton_Click(object sender, RoutedEventArgs e)
    {
        if (dataSharingWindow is not null)
        {
            dataSharingWindow.Activate();
            return;
        }

        EnsureDataSharingServer();
        dataSharingWindow = new DataSharingWindow(
            dataSharingServer!,
            ExportSharedUsageAsync,
            ImportSharedUsageAsync,
            ExportSharedUsageTodayAsync,
            ImportSharedUsageTodayAsync,
            historySharingStore!,
            dataSharingClientGate,
            ConfigureAutomaticTodayUpload,
            runtime.LifetimeToken,
            runtime)
        {
            Owner = this
        };
        dataSharingWindow.Closed += (_, _) => dataSharingWindow = null;
        dataSharingWindow.Show();
    }

    private void ConfigureAutomaticTodayUpload()
    {
        if (!automaticTodayUploadTimerInitialized)
        {
            automaticTodayUploadTimer.Tick += AutomaticTodayUploadTimer_Tick;
            automaticTodayUploadTimerInitialized = true;
        }

        automaticTodayUploadTimer.Stop();
        var settings = CodexDataSharingSettings.Load();
        if (!settings.AutoUploadToday || runtime.IsStopping || isClosed)
        {
            return;
        }

        try
        {
            _ = CodexDataSharingProtocol.ParseServerAddress(settings.ServerAddress);
            CodexDataSharingProtocol.ValidateAccessKey(settings.ServerAccessKey);
            automaticTodayUploadTimer.Interval = TimeSpan.FromHours(settings.AutoUploadIntervalHours);
            automaticTodayUploadTimer.Start();
        }
        catch (ArgumentException ex)
        {
            ReportAutomaticTodayUpload($"自动上传未启动：{ex.Message}");
        }
    }

    private async void AutomaticTodayUploadTimer_Tick(object? sender, EventArgs e)
    {
        if (!automaticTodayUploadTask.IsCompleted || runtime.IsStopping || isClosed)
        {
            return;
        }

        var settings = CodexDataSharingSettings.Load();
        if (!settings.AutoUploadToday)
        {
            automaticTodayUploadTimer.Stop();
            return;
        }

        automaticTodayUploadTask = RunAutomaticTodayUploadAsync(settings);
        try
        {
            await automaticTodayUploadTask;
        }
        catch (OperationCanceledException) when (runtime.IsStopping || isClosed) { }
        catch (Exception ex)
        {
            ReportAutomaticTodayUpload($"自动上传今天数据失败：{ex.Message}；将在下个周期重试。");
        }
    }

    private Task RunAutomaticTodayUploadAsync(CodexDataSharingSettings settings)
    {
        return runtime.Run("自动上传今天数据", async token =>
        {
            await dataSharingClientGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                using var client = new CodexDataSharingClient(settings.ServerAddress, settings.ServerAccessKey);
                var peer = await client.TestConnectionAsync(token).ConfigureAwait(false);
                if (!peer.SupportsToday)
                {
                    throw new InvalidDataException("主机版本不支持今天快速同步，请先更新主机程序");
                }

                using var upload = new CodexSharingTemporaryFile();
                await ExportSharedUsageCoreAsync(upload.FilePath, CodexDataExportScope.Today, token).ConfigureAwait(false);
                var result = await client.UploadTodayAsync(upload.FilePath, token).ConfigureAwait(false);
                ReportAutomaticTodayUpload(
                    $"自动上传完成：主机新增 {result.AddedUsageEventCount:N0} 条用量、{result.AddedQuotaSnapshotCount:N0} 条额度快照。下一次约 {settings.AutoUploadIntervalHours} 小时后执行。");
            }
            finally
            {
                dataSharingClientGate.Release();
            }
        });
    }

    private void StopAutomaticTodayUpload() => automaticTodayUploadTimer.Stop();

    private void ReportAutomaticTodayUpload(string message)
    {
        if (Dispatcher.HasShutdownStarted || runtime.IsStopping || isClosed)
        {
            return;
        }

        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(new Action(() => ReportAutomaticTodayUpload(message)));
            return;
        }

        dataSharingWindow?.ReportAutomaticUpload(message);
        SetStatus(message);
    }

    private Task<CodexDataExportResult> ExportSharedUsageAsync(string path, CancellationToken cancellationToken)
        => ExportSharedUsageAsync(path, CodexDataExportScope.RecentDays, cancellationToken);

    private Task<CodexDataExportResult> ExportSharedUsageTodayAsync(string path, CancellationToken cancellationToken)
        => ExportSharedUsageAsync(path, CodexDataExportScope.Today, cancellationToken);

    private Task<CodexDataExportResult> ExportSharedUsageAsync(
        string path,
        CodexDataExportScope scope,
        CancellationToken cancellationToken)
    {
        return runtime.Run("共享数据导出", async token =>
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, cancellationToken);
            return await ExportSharedUsageCoreAsync(path, scope, linked.Token).ConfigureAwait(false);
        });
    }

    private async Task<CodexDataExportResult> ExportSharedUsageCoreAsync(
        string path,
        CodexDataExportScope scope,
        CancellationToken cancellationToken)
    {
        await usageQueryGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                var now = BeijingClock.Now;
                var range = CodexDataTransferService.GetExportRange(scope, now);
                var today = CodexDataTransferService.GetExportRange(CodexDataExportScope.Today, now).StartInclusive!.Value;
                var historicalDayCount = Math.Max(0, (today - range.StartInclusive!.Value).Days);
                var days = Enumerable.Range(0, historicalDayCount)
                    .Select(i => range.StartInclusive.Value.AddDays(i))
                    .ToArray();
                if (days.Length > 0)
                {
                    CodexUsageReader.WarmHistoricalDays(days, cancellationToken);
                    CodexUsageReader.WarmQuotaSnapshotDays(days, cancellationToken);
                }
                CodexUsageReader.ReadRangeFromDetailRows(today, now, cancellationToken: cancellationToken);
                CodexUsageReader.ReadCachedAndHistoricalQuotaSnapshots(range.StartInclusive!.Value, now, cancellationToken);
                return CodexDataTransferService.Export(path, scope, cancellationToken);
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            usageQueryGate.Release();
        }
    }

    private Task<CodexDataImportResult> ImportSharedUsageAsync(string path, CancellationToken cancellationToken)
        => ImportSharedUsageAsync(path, CodexDataExportScope.RecentDays, cancellationToken);

    private Task<CodexDataImportResult> ImportSharedUsageTodayAsync(string path, CancellationToken cancellationToken)
        => ImportSharedUsageAsync(path, CodexDataExportScope.Today, cancellationToken);

    private Task<CodexDataImportResult> ImportSharedUsageAsync(
        string path,
        CodexDataExportScope scope,
        CancellationToken cancellationToken)
    {
        return runtime.Run("共享数据导入", async token =>
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, cancellationToken);
            return await ImportSharedUsageCoreAsync(path, scope, linked.Token).ConfigureAwait(false);
        });
    }

    private async Task<CodexDataImportResult> ImportSharedUsageCoreAsync(
        string path,
        CodexDataExportScope scope,
        CancellationToken cancellationToken)
    {
        CodexDataImportResult result;
        await usageQueryGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            result = await Task.Run(() => scope == CodexDataExportScope.Today
                ? CodexDataTransferService.ImportToday(path, cancellationToken)
                : CodexDataTransferService.ImportRecentDays(path, cancellationToken), cancellationToken).ConfigureAwait(false);
            CodexQuotaCycleReader.InvalidateCache();
        }
        finally
        {
            usageQueryGate.Release();
        }

        NotifySharedDataImported(result);
        return result;
    }

    private void NotifySharedDataImported(CodexDataImportResult result, bool refresh = true)
    {
        CodexQuotaCycleReader.InvalidateCache();
        // Queue UI work after releasing the cache gate. Completing the remote
        // upload must not depend on a main-window refresh or a modal dialog.
        if (!Dispatcher.HasShutdownStarted && !runtime.IsStopping)
        {
            _ = Dispatcher.BeginInvoke(new Action(async () => await RunUiActionAsync(async () =>
            {
                if (isClosed)
                {
                    return;
                }

                try
                {
                    Interlocked.Increment(ref quotaRefreshVersion);
                    CurrentCodexModule().CurrentQuotaEstimate = null;
                    foreach (var module in usageModules.Values)
                    {
                        module.ClearDisplay();
                    }

                    UpdateCopySummaryState();

                    if (refresh) await RefreshUsageAsync(cacheOnly: true);
                    if (!isClosed)
                    {
                        SetStatus($"共享数据已合并：新增 {result.AddedUsageEventCount:N0} 条用量 · {result.AddedQuotaSnapshotCount:N0} 条额度快照");
                    }
                }
                catch (OperationCanceledException) when (isClosed || runtime.IsStopping) { }
                catch (Exception ex)
                {
                    if (!isClosed)
                    {
                        SetStatus($"共享数据已保存，刷新显示失败：{ex.Message}");
                    }
                }
            })));
        }

    }
}
