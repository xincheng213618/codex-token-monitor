namespace CodexTokenMonitor;

internal sealed record AnalysisQueryResult<T>(T Value, IReadOnlyList<CacheWarning> CacheWarnings);

/// <summary>
/// Tracks a window's analysis work in both its own lifetime and the application
/// runtime. Closing the window cancels only its work; application shutdown still
/// waits for queries that have not finished releasing their resources.
/// </summary>
internal sealed class AnalysisQuerySession : IDisposable
{
    private readonly MonitorRuntime localRuntime = new();
    private readonly MonitorRuntime? parentRuntime;

    public AnalysisQuerySession(MonitorRuntime? runtime = null)
    {
        parentRuntime = runtime;
    }

    public MonitorRuntime Runtime => parentRuntime ?? localRuntime;

    public bool IsStopping => localRuntime.IsStopping || parentRuntime?.IsStopping == true;

    /// <summary>
    /// Runs a synchronous query on a worker and returns operation-scoped cache
    /// warnings. Query inputs must be captured before calling this method; the
    /// query must not access WPF objects. Callers select the shared I/O gate for
    /// queries that scan logs or write application caches. Independent read-only
    /// queries can proceed while the foreground or warmer owns that gate.
    /// </summary>
    public Task<AnalysisQueryResult<T>> RunAsync<T>(
        string name,
        Func<CancellationToken, T> query,
        bool requiresSharedIo = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(query);

        return localRuntime.Run(name, windowToken => parentRuntime is null
            ? ExecuteAsync(query, requiresSharedIo, windowToken, CancellationToken.None, cancellationToken)
            : parentRuntime.Run(name, parentToken =>
                ExecuteAsync(query, requiresSharedIo, windowToken, parentToken, cancellationToken)));
    }

    private async Task<AnalysisQueryResult<T>> ExecuteAsync<T>(
        Func<CancellationToken, T> query,
        bool requiresSharedIo,
        CancellationToken windowToken,
        CancellationToken parentToken,
        CancellationToken requestToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(windowToken, parentToken, requestToken);
        var token = linked.Token;
        void ThrowIfCancellationRequested()
        {
            // CancelAsync marks the source token before invoking registrations.
            // The linked token can therefore lag behind a closed window or a
            // stopped application while cancellation callbacks are still queued.
            windowToken.ThrowIfCancellationRequested();
            parentToken.ThrowIfCancellationRequested();
            requestToken.ThrowIfCancellationRequested();
            token.ThrowIfCancellationRequested();
            if (IsStopping) throw new OperationCanceledException("Analysis session is stopping.", windowToken);
        }
        ThrowIfCancellationRequested();

        if (requiresSharedIo)
            await Runtime.SharedIoGate.WaitAsync(token).ConfigureAwait(false);

        try
        {
            var result = await Task.Run(() =>
            {
                ThrowIfCancellationRequested();
                using var diagnostics = CacheOperationDiagnostics.Begin();
                var value = query(token);
                ThrowIfCancellationRequested();
                return new AnalysisQueryResult<T>(value, diagnostics.Warnings);
            }, token).ConfigureAwait(false);
            ThrowIfCancellationRequested();
            return result;
        }
        finally
        {
            if (requiresSharedIo) Runtime.SharedIoGate.Release();
        }
    }

    public void Dispose() => localRuntime.Dispose();
}
