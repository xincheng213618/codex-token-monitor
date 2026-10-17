using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace CodexTokenMonitor.Wpf.Probes;

internal static partial class MainWindowProbe
{
    private static async Task CheckQuotaEstimateOpeningAsync(MainWindow window, CodexUsageModule module)
    {
        var previousQuota = module.CurrentQuotaEstimate;
        var previousPeriods = module.QuotaCycles;
        var now = BeijingClock.Now;
        var start = now.AddDays(-2);
        var week = new CodexQuotaWindowEstimate("7d", 20, 10080, start, now, start.AddDays(7),
            new TokenUsageSummary { StartLocal = start, EndLocal = now }, 0, null, null);
        module.CurrentQuotaEstimate = new(now, "codex", "Codex", null, week);
        module.QuotaCycles = Array.Empty<CodexQuotaCycle>();
        // Create only a native owner handle; keep the main Loaded lifecycle and
        // its startup network requests out of this isolated regression suite.
        new WindowInteropHelper(window).EnsureHandle();
        var gate = Get<SemaphoreSlim>(window, "usageQueryGate");
        QuotaEstimateWindow? estimate = null;
        await gate.WaitAsync();
        try
        {
            var timer = Stopwatch.StartNew();
            Invoke(window, "QuotaEstimateButton_Click", window, new RoutedEventArgs());
            estimate = window.OwnedWindows.Cast<Window>().OfType<QuotaEstimateWindow>().SingleOrDefault();
            Require(estimate is not null, "quota estimate opens immediately while the main I/O gate is held");
            var openMilliseconds = timer.Elapsed.TotalMilliseconds;
            estimate!.Left = estimate.Top = -20000;
            estimate.ShowInTaskbar = false;
            await estimate.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            while (Get<bool>(estimate, "isLoading"))
            {
                Require(timer.Elapsed < TimeSpan.FromSeconds(20), "quota estimate finishes behind the busy main gate");
                await Task.Delay(10);
            }
            Require(GetObject(estimate, "loadedEstimateResult") is QuotaEstimateLoadResult { PeriodCount: > 0 },
                "quota estimate publishes its result behind the busy main gate");
            Results.Add(new { check = "quota-estimate-opens-behind-busy-gate", openMilliseconds,
                loadMilliseconds = timer.Elapsed.TotalMilliseconds, mainGateHeld = true });
        }
        finally
        {
            estimate?.Close();
            gate.Release();
            module.CurrentQuotaEstimate = previousQuota;
            module.QuotaCycles = previousPeriods;
        }
    }
}
