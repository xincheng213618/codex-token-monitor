using System.Windows;

namespace CodexTokenMonitor;

public partial class MainWindow
{
    private CodexDataSharingServer? dataSharingServer;
    private DataSharingWindow? dataSharingWindow;
    private CodexHistorySharingStore? historySharingStore;

    private void EnsureDataSharingServer()
    {
        historySharingStore ??= new CodexHistorySharingStore(cacheGate: usageQueryGate,
            imported: result => NotifySharedDataImported(result, refresh: false), runtime: runtime);
        dataSharingServer ??= new CodexDataSharingServer(ExportSharedUsageAsync, ImportSharedUsageAsync, historySharingStore);
    }

    private async Task StartDataSharingOnLaunchAsync()
    {
        try
        {
            var settings = CodexDataSharingSettings.Load();
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
            dataSharingServer!, ExportSharedUsageAsync, ImportSharedUsageAsync, historySharingStore!, runtime.LifetimeToken, runtime)
        {
            Owner = this
        };
        dataSharingWindow.Closed += (_, _) => dataSharingWindow = null;
        dataSharingWindow.Show();
    }

    private Task<CodexDataExportResult> ExportSharedUsageAsync(string path, CancellationToken cancellationToken)
    {
        return runtime.Run("共享数据导出", async token =>
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, cancellationToken);
            return await ExportSharedUsageCoreAsync(path, linked.Token).ConfigureAwait(false);
        });
    }

    private async Task<CodexDataExportResult> ExportSharedUsageCoreAsync(string path, CancellationToken cancellationToken)
    {
        await usageQueryGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                var now = BeijingClock.Now;
                var range = CodexDataTransferService.GetExportRange(CodexDataExportScope.RecentDays, now);
                var days = Enumerable.Range(0, 7).Select(i => range.StartInclusive!.Value.AddDays(i)).ToArray();
                CodexUsageReader.WarmHistoricalDays(days, cancellationToken);
                CodexUsageReader.ReadRangeFromDetailRows(range.StartInclusive!.Value.AddDays(7), now, cancellationToken: cancellationToken);
                CodexUsageReader.WarmQuotaSnapshotDays(days, cancellationToken);
                CodexUsageReader.ReadCachedAndHistoricalQuotaSnapshots(range.StartInclusive!.Value, now, cancellationToken);
                return CodexDataTransferService.Export(path, CodexDataExportScope.RecentDays, cancellationToken);
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            usageQueryGate.Release();
        }
    }

    private Task<CodexDataImportResult> ImportSharedUsageAsync(string path, CancellationToken cancellationToken)
    {
        return runtime.Run("共享数据导入", async token =>
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, cancellationToken);
            return await ImportSharedUsageCoreAsync(path, linked.Token).ConfigureAwait(false);
        });
    }

    private async Task<CodexDataImportResult> ImportSharedUsageCoreAsync(string path, CancellationToken cancellationToken)
    {
        CodexDataImportResult result;
        await usageQueryGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            result = await Task.Run(() => CodexDataTransferService.ImportRecentDays(
                path, cancellationToken), cancellationToken).ConfigureAwait(false);
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
