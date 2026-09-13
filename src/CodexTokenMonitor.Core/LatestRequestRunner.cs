namespace CodexTokenMonitor;

/// <summary>
/// Runs one request at a time, replacing any waiting request with the latest one.
/// Callers should provide immutable request snapshots and use <see cref="IsCurrent"/>
/// before publishing results that an intervening request may have superseded.
/// </summary>
internal sealed class LatestRequestRunner<TRequest>
{
    private readonly object sync = new();
    private readonly Func<TRequest, long, CancellationToken, Task> execute;
    private readonly CancellationToken lifetimeCancellation;
    private PendingRequest? pending;
    private bool isRunning;
    private long version;
    private Task completion = Task.CompletedTask;

    public LatestRequestRunner(
        Func<TRequest, long, CancellationToken, Task> execute,
        CancellationToken lifetimeCancellation = default)
    {
        ArgumentNullException.ThrowIfNull(execute);
        this.execute = execute;
        this.lifetimeCancellation = lifetimeCancellation;
    }

    public bool IsRunning
    {
        get { lock (sync) return isRunning; }
    }

    public Task Completion
    {
        get { lock (sync) return completion; }
    }

    public long Version
    {
        get { lock (sync) return version; }
    }

    public bool IsCurrent(long requestVersion)
    {
        lock (sync)
            return !lifetimeCancellation.IsCancellationRequested && requestVersion == version;
    }

    /// <summary>
    /// Returns the completion of this entire run, including a coalesced successor.
    /// Each callback starts on the SynchronizationContext of its request, if present.
    /// The callback must itself preserve that context when awaiting before UI work.
    /// A failure or cancellation discards pending work and is propagated to callers;
    /// the runner can be reused unless its lifetime has been cancelled.
    /// </summary>
    public Task RequestAsync(TRequest request)
    {
        TaskCompletionSource runCompletion;
        lock (sync)
        {
            if (lifetimeCancellation.IsCancellationRequested)
                return Task.FromCanceled(lifetimeCancellation);

            pending = new PendingRequest(request, ++version, SynchronizationContext.Current);
            if (isRunning)
                return completion;

            isRunning = true;
            runCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            completion = runCompletion.Task;
        }

        // Store the completion before invoking user code: synchronous completion
        // and reentrant requests must see the same, already-established run.
        _ = RunAsync(runCompletion);
        return runCompletion.Task;
    }

    private async Task RunAsync(TaskCompletionSource runCompletion)
    {
        try
        {
            while (true)
            {
                PendingRequest next;
                lock (sync)
                {
                    lifetimeCancellation.ThrowIfCancellationRequested();
                    if (pending is null)
                    {
                        // Become idle atomically with observing an empty queue.
                        // Otherwise a concurrent request could be lost at shutdown.
                        isRunning = false;
                        runCompletion.TrySetResult();
                        return;
                    }

                    next = pending;
                    pending = null;
                }

                await ExecuteOnRequestContextAsync(next);
            }
        }
        catch (OperationCanceledException ex)
        {
            lock (sync)
            {
                pending = null;
                isRunning = false;
                runCompletion.TrySetCanceled(ex.CancellationToken);
            }
        }
        catch (Exception ex)
        {
            lock (sync)
            {
                pending = null;
                isRunning = false;
                runCompletion.TrySetException(ex);
            }
        }
    }

    private Task ExecuteOnRequestContextAsync(PendingRequest request)
    {
        if (request.Context is null || ReferenceEquals(request.Context, SynchronizationContext.Current))
            return execute(request.Value, request.Version, lifetimeCancellation);

        var callbackCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        request.Context.Post(_ => { _ = ExecutePostedAsync(request, callbackCompletion); }, null);
        return callbackCompletion.Task;
    }

    private async Task ExecutePostedAsync(PendingRequest request, TaskCompletionSource callbackCompletion)
    {
        try
        {
            lifetimeCancellation.ThrowIfCancellationRequested();
            await execute(request.Value, request.Version, lifetimeCancellation);
            callbackCompletion.TrySetResult();
        }
        catch (OperationCanceledException ex)
        {
            callbackCompletion.TrySetCanceled(ex.CancellationToken);
        }
        catch (Exception ex)
        {
            callbackCompletion.TrySetException(ex);
        }
    }

    private sealed record PendingRequest(TRequest Value, long Version, SynchronizationContext? Context);
}
