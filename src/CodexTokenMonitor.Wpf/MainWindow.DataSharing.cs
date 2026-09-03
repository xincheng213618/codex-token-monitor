using System.Windows;

namespace CodexTokenMonitor;

public partial class MainWindow
{
    private CodexDataSharingServer? dataSharingServer;
    private DataSharingWindow? dataSharingWindow;

    private void DataSharingButton_Click(object sender, RoutedEventArgs e)
    {
        if (dataSharingWindow is not null)
        {
            dataSharingWindow.Activate();
            return;
        }

        dataSharingServer ??= new CodexDataSharingServer(ExportSharedWeekAsync, ImportSharedWeekAsync);
        dataSharingWindow = new DataSharingWindow(
            dataSharingServer, ExportSharedWeekAsync, ImportSharedWeekAsync, lifetimeCancellation.Token)
        {
            Owner = this
        };
        dataSharingWindow.Closed += (_, _) => dataSharingWindow = null;
        dataSharingWindow.Show();
    }

    private async Task<CodexDataExportResult> ExportSharedWeekAsync(string path, CancellationToken cancellationToken)
    {
        await usageQueryGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => CodexDataTransferService.Export(
                path, CodexDataExportScope.ThisWeek, cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            usageQueryGate.Release();
        }
    }

    private async Task<CodexDataImportResult> ImportSharedWeekAsync(string path, CancellationToken cancellationToken)
    {
        CodexDataImportResult result;
        await usageQueryGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            result = await Task.Run(() => CodexDataTransferService.ImportThisWeek(
                path, cancellationToken), cancellationToken).ConfigureAwait(false);
            CodexQuotaCycleReader.InvalidateCache();
        }
        finally
        {
            usageQueryGate.Release();
        }

        // Queue UI work after releasing the cache gate. Completing the remote
        // upload must not depend on a main-window refresh or a modal dialog.
        if (!Dispatcher.HasShutdownStarted && !lifetimeCancellation.IsCancellationRequested)
        {
            _ = Dispatcher.BeginInvoke(new Action(async () =>
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

                    await RefreshUsageAsync(cacheOnly: true);
                    if (!isClosed)
                    {
                        SetStatus($"共享数据已合并：新增 {result.AddedUsageEventCount:N0} 条用量 · {result.AddedQuotaSnapshotCount:N0} 条额度快照");
                    }
                }
                catch (OperationCanceledException) when (isClosed || lifetimeCancellation.IsCancellationRequested) { }
                catch (Exception ex)
                {
                    if (!isClosed)
                    {
                        SetStatus($"共享数据已保存，刷新显示失败：{ex.Message}");
                    }
                }
            }));
        }

        return result;
    }
}
