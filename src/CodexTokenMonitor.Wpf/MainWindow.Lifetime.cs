using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;

namespace CodexTokenMonitor;

public partial class MainWindow
{
    private bool shutdownComplete;
    private Task? shutdownTask;

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (shutdownComplete) return;
        e.Cancel = true;
        if (shutdownTask is not null) return;

        // Keep the dispatcher alive while work releases locks and cancels I/O.
        // Closed is too late: WPF can already be shutting the application down.
        isClosed = true;
        displayViewModel.Stop();
        IsEnabled = false;
        refreshTimer.Stop();
        StopAutomaticTodayUpload();
        backgroundCacheWarmer.Dispose();
        shutdownTask = Task.CompletedTask;
        shutdownTask = ShutdownAsync();
    }

    private async Task ShutdownAsync()
    {
        try
        {
            foreach (var child in OwnedWindows.Cast<Window>().ToArray())
            {
                try { child.Close(); }
                catch (Exception ex) { Trace.TraceError($"Close owned window: {ex}"); }
            }

            // Cleanup is admitted before sealing the runtime. These operations
            // intentionally finish independently of the cancelled lifetime token.
            _ = runtime.Run("关闭资源", _ => Task.WhenAll(
                dataSharingServer?.StopAsync() ?? Task.CompletedTask,
                backgroundCacheWarmer.Completion,
                usageRefreshRunner.Completion,
                cycleRefreshTask,
                LastDisplayStore.FlushAsync()));
            var result = await runtime.StopAsync(TimeSpan.FromSeconds(3));
            if (!result.Completed)
            {
                Trace.TraceWarning("Monitor shutdown exceeded its wait budget.");
            }
        }
        catch (Exception ex)
        {
            Trace.TraceError($"Monitor shutdown: {ex}");
            runtime.BeginStop();
        }
        finally
        {
            // Even a synchronous drain must leave the first Closing handler.
            await Dispatcher.Yield(DispatcherPriority.Background);
            shutdownComplete = true;
            Close();
        }
    }
}
