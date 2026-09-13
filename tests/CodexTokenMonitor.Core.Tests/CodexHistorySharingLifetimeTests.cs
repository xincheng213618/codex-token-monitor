using Xunit;

namespace CodexTokenMonitor.Tests;

public sealed class CodexHistorySharingLifetimeTests
{
    [Fact]
    public async Task RuntimeStopCancelsAndDrainsHistoryOperationWaitingForSharedGate()
    {
        using var cacheScope = MonitorCachePaths.PushLocalAppDataRoot(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        using var logScope = UsageLogPaths.PushRoot(Path.Combine(MonitorCachePaths.LocalAppData, "logs"));
        using var runtime = new MonitorRuntime();
        await runtime.SharedIoGate.WaitAsync();
        try
        {
            var store = new CodexHistorySharingStore(runtime: runtime);
            var dates = store.GetDatesAsync(CancellationToken.None);
            Assert.False(dates.IsCompleted);

            var stopped = await runtime.StopAsync(TimeSpan.FromSeconds(5));
            Assert.True(stopped.Completed);
            Assert.Empty(stopped.PendingOperations);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => dates.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Empty(runtime.Failures);
        }
        finally
        {
            runtime.SharedIoGate.Release();
        }
    }

    [Fact]
    public async Task RuntimeStopRejectsHistoryExportBeforeItCanTouchFiles()
    {
        using var cacheScope = MonitorCachePaths.PushLocalAppDataRoot(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        using var logScope = UsageLogPaths.PushRoot(Path.Combine(MonitorCachePaths.LocalAppData, "logs"));
        using var runtime = new MonitorRuntime();
        await runtime.SharedIoGate.WaitAsync();
        try
        {
            var store = new CodexHistorySharingStore(cacheGate: runtime.SharedIoGate, runtime: runtime);
            runtime.BeginStop();

            var export = store.ExportAsync("unused-output.json",
                new CodexHistoryRange(new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 2)),
                CancellationToken.None);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => export.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.True(export.IsCanceled);
            Assert.Empty(runtime.Failures);
        }
        finally
        {
            runtime.SharedIoGate.Release();
        }
    }

    [Fact]
    public async Task RequestCancellationStopsHistoryWaitWithoutStoppingRuntime()
    {
        using var cacheScope = MonitorCachePaths.PushLocalAppDataRoot(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        using var logScope = UsageLogPaths.PushRoot(Path.Combine(MonitorCachePaths.LocalAppData, "logs"));
        using var runtime = new MonitorRuntime();
        using var request = new CancellationTokenSource();
        await runtime.SharedIoGate.WaitAsync();
        try
        {
            var store = new CodexHistorySharingStore(cacheGate: runtime.SharedIoGate, runtime: runtime);
            var dates = store.GetDatesAsync(request.Token);
            Assert.False(dates.IsCompleted);

            request.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => dates.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.False(runtime.IsStopping);
            Assert.False(runtime.LifetimeToken.IsCancellationRequested);
            Assert.True((await runtime.StopAsync(TimeSpan.FromSeconds(5))).Completed);
        }
        finally
        {
            runtime.SharedIoGate.Release();
        }
    }
}
