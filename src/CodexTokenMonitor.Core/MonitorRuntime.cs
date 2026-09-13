namespace CodexTokenMonitor;

internal sealed record MonitorOperationFailure(string OperationName, Exception Exception);

internal sealed record MonitorShutdownResult(
    bool Completed,
    IReadOnlyList<string> PendingOperations,
    IReadOnlyList<MonitorOperationFailure> Failures);

/// <summary>
/// Owns the monitor's shared I/O gate and application lifetime. Register work
/// through Run before it starts; UI timers and dispatcher work remain with WPF.
/// Call StopAsync from outside registered operations so shutdown never waits on
/// itself. Dispose requests resource cleanup after all registered work has ended.
/// </summary>
internal sealed class MonitorRuntime : IDisposable
{
    private const int FailureHistoryLimit = 128;
    private readonly object sync = new();
    private readonly CancellationTokenSource lifetimeCancellation = new();
    private readonly CancellationToken lifetimeToken;
    private readonly Dictionary<long, string> operations = new();
    private readonly List<MonitorOperationFailure> failures = new();
    private readonly TaskCompletionSource drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task<MonitorShutdownResult>? shutdownTask;
    private long nextOperationId;
    private bool isStopping;
    private bool cancellationFinished;
    private bool disposeRequested;
    private bool resourcesDisposed;

    public MonitorRuntime()
    {
        lifetimeToken = lifetimeCancellation.Token;
    }

    public SemaphoreSlim SharedIoGate { get; } = new(1, 1);

    public CancellationToken LifetimeToken => lifetimeToken;

    public bool IsStopping
    {
        get { lock (sync) return isStopping; }
    }

    /// <summary>The latest 128 failures, including errors arriving after a shutdown timeout.</summary>
    public IReadOnlyList<MonitorOperationFailure> Failures
    {
        get { lock (sync) return failures.ToArray(); }
    }

    public Task Run(string name, Func<CancellationToken, Task> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return Run<object?>(name, async token =>
        {
            await operation(token).ConfigureAwait(false);
            return null;
        });
    }

    /// <summary>
    /// Registers work before invoking the callback on the caller's thread and
    /// SynchronizationContext. Callbacks own their own UI context after awaits.
    /// The returned task retains failures/cancellation for the calling UI; the
    /// runtime also observes failures when callers intentionally do not await it.
    /// </summary>
    public Task<T> Run<T>(string name, Func<CancellationToken, Task<T>> operation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(operation);
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        long operationId;
        lock (sync)
        {
            if (isStopping)
            {
                // BeginStop marks the boundary before invoking cancellation
                // callbacks. Another caller can reach here during that handoff.
                completion.TrySetCanceled(lifetimeToken);
                return completion.Task;
            }

            operationId = ++nextOperationId;
            operations.Add(operationId, name);
        }

        _ = ExecuteAsync(operationId, name, operation, completion);
        return completion.Task;
    }

    public void BeginStop()
    {
        lock (sync)
        {
            if (isStopping) return;
            isStopping = true;
        }

        // CancelAsync sets the token immediately without synchronously running
        // user callbacks on the UI thread. Their completion is part of draining.
        _ = ObserveCancellationAsync(lifetimeCancellation.CancelAsync());
    }

    private async Task ObserveCancellationAsync(Task cancellation)
    {
        try
        {
            await cancellation.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // A faulty cancellation callback must not prevent other registered
            // operations from draining or the window from completing shutdown.
            RecordFailure("Lifetime cancellation", ex);
        }
        finally
        {
            lock (sync)
                cancellationFinished = true;
            CompleteDrainAndDisposeIfRequested();
        }
    }

    /// <summary>
    /// The first call establishes one total timeout for all registered work.
    /// Repeated calls share its result. A timeout does not dispose resources still
    /// in use; Failures continues recording any errors from late completions.
    /// </summary>
    public Task<MonitorShutdownResult> StopAsync(TimeSpan timeout)
    {
        if (timeout < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout));

        TaskCompletionSource<MonitorShutdownResult> completion;
        lock (sync)
        {
            if (shutdownTask is not null) return shutdownTask;
            completion = new TaskCompletionSource<MonitorShutdownResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            shutdownTask = completion.Task;
        }

        _ = StopCoreAsync(timeout, completion);
        return completion.Task;
    }

    private async Task StopCoreAsync(TimeSpan timeout, TaskCompletionSource<MonitorShutdownResult> completion)
    {
        try
        {
            var startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
            BeginStop();
            var remaining = timeout - System.Diagnostics.Stopwatch.GetElapsedTime(startedAt);
            if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;
            try
            {
                await drained.Task.WaitAsync(remaining).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // Work that cannot finish within the total shutdown budget stays
                // registered and keeps its gate until its own completion.
            }

            lock (sync)
            {
                completion.TrySetResult(new MonitorShutdownResult(
                    drained.Task.IsCompleted,
                    cancellationFinished
                        ? operations.Values.ToArray()
                        : operations.Values.Append("Lifetime cancellation").ToArray(),
                    failures.ToArray()));
            }
        }
        catch (Exception ex)
        {
            completion.TrySetException(ex);
            _ = completion.Task.Exception;
        }
    }

    private async Task ExecuteAsync<T>(
        long operationId,
        string name,
        Func<CancellationToken, Task<T>> operation,
        TaskCompletionSource<T> completion)
    {
        try
        {
            lifetimeToken.ThrowIfCancellationRequested();
            var result = await operation(lifetimeToken).ConfigureAwait(false);
            completion.TrySetResult(result);
        }
        catch (OperationCanceledException ex)
        {
            completion.TrySetCanceled(ex.CancellationToken);
        }
        catch (Exception ex)
        {
            RecordFailure(name, ex);
            completion.TrySetException(ex);
            _ = completion.Task.Exception;
        }
        finally
        {
            lock (sync)
                operations.Remove(operationId);
            CompleteDrainAndDisposeIfRequested();
        }
    }

    private void RecordFailure(string name, Exception exception)
    {
        lock (sync)
        {
            if (failures.Count == FailureHistoryLimit) failures.RemoveAt(0);
            failures.Add(new MonitorOperationFailure(name, exception));
        }
    }

    public void Dispose()
    {
        lock (sync)
            disposeRequested = true;
        BeginStop();
        CompleteDrainAndDisposeIfRequested();
    }

    private void CompleteDrainAndDisposeIfRequested()
    {
        lock (sync)
        {
            if (!isStopping || !cancellationFinished || operations.Count != 0) return;
            drained.TrySetResult();
            if (!disposeRequested || resourcesDisposed) return;
            resourcesDisposed = true;
        }

        // Never dispose while cancellation callbacks or registered operations
        // are still using these resources, even after StopAsync has timed out.
        SharedIoGate.Dispose();
        lifetimeCancellation.Dispose();
    }
}
