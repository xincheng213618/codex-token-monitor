using System.Runtime.CompilerServices;

namespace CodexTokenMonitor;

internal enum CacheWarningKind
{
    Locked,
    Corrupt,
    AccessDenied,
    Unavailable,
    StorageFailure
}

internal sealed record CacheWarning(string Path, string Operation, CacheWarningKind Kind, string Message);

/// <summary>
/// Collects cache failures for one application operation. Execution-context
/// propagation lets existing readers retain their fallback return contracts
/// while callers distinguish unavailable data from a successful zero result.
/// </summary>
internal sealed class CacheOperationDiagnostics : IDisposable
{
    private static readonly AsyncLocal<CacheOperationDiagnostics?> CurrentScope = new();
    private static long nextOperationId;
    private readonly CacheOperationDiagnostics? previous;
    private readonly bool propagateToParent;
    private readonly object sync = new();
    private readonly List<CacheWarning> warnings = new();
    private bool disposed;

    private CacheOperationDiagnostics(bool propagateToParent)
    {
        previous = CurrentScope.Value;
        this.propagateToParent = propagateToParent;
        OperationId = Interlocked.Increment(ref nextOperationId);
        CurrentScope.Value = this;
    }

    internal long OperationId { get; }
    internal static long CurrentOperationId => CurrentScope.Value?.OperationId ?? 0;
    internal static CacheOperationDiagnostics? CurrentOperation => CurrentScope.Value;

    public static CacheOperationDiagnostics Begin(bool propagateToParent = false) => new(propagateToParent);

    public IReadOnlyList<CacheWarning> Warnings
    {
        get
        {
            lock (sync) return warnings.ToArray();
        }
    }

    public static void Report(string path, string operation, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (exception is OperationCanceledException) return;
        var scope = CurrentScope.Value;
        if (scope is null) return;

        var kind = exception switch
        {
            SqliteException { SqliteErrorCode: 5 or 6 } => CacheWarningKind.Locked,
            SqliteException { SqliteErrorCode: 11 or 26 } => CacheWarningKind.Corrupt,
            JsonException => CacheWarningKind.Corrupt,
            UnauthorizedAccessException => CacheWarningKind.AccessDenied,
            IOException or SqliteException { SqliteErrorCode: 14 } => CacheWarningKind.Unavailable,
            _ => CacheWarningKind.StorageFailure
        };
        // Store only the exception message, never its stack or SQL command.
        var warning = new CacheWarning(path, operation, kind, exception.Message);
        scope.Add(warning);
    }

    private void Add(CacheWarning warning)
    {
        lock (sync)
        {
            if (!disposed && !warnings.Contains(warning)) warnings.Add(warning);
        }
    }

    public void Dispose()
    {
        CacheWarning[] forwarded;
        lock (sync)
        {
            if (disposed) return;
            disposed = true;
            forwarded = propagateToParent ? warnings.ToArray() : Array.Empty<CacheWarning>();
        }
        CurrentScope.Value = previous;
        foreach (var warning in forwarded) previous?.Add(warning);
    }
}

internal sealed class CacheUnavailableException(string path, string operation, Exception innerException)
    : IOException($"Cache operation '{operation}' is unavailable: {path}", innerException)
{
    public string CachePath { get; } = path;
    public string Operation { get; } = operation;
}

/// <summary>
/// Serializes database initialization and retries after a failed operation.
/// A failing query tries initialization only once; a new query may recover
/// without replacing or deleting the existing database or store instance.
/// </summary>
internal sealed class CacheDatabaseState(string path)
{
    private readonly object sync = new();
    private bool available;
    private Exception? lastFailure;
    private long failedOperationId;

    public bool EnsureAvailable(
        Action initialize,
        bool propagateErrors = false,
        [CallerMemberName] string operation = "")
    {
        lock (sync)
        {
            if (available) return true;

            var currentOperationId = CacheOperationDiagnostics.CurrentOperationId;
            if (lastFailure is null || currentOperationId == 0 || failedOperationId != currentOperationId)
            {
                try
                {
                    initialize();
                    available = true;
                    lastFailure = null;
                    return true;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    lastFailure = ex;
                    failedOperationId = currentOperationId;
                }
            }

            CacheOperationDiagnostics.Report(path, operation, lastFailure!);
            if (propagateErrors) throw new CacheUnavailableException(path, operation, lastFailure!);
            return false;
        }
    }

    public void ReportFailure(Exception exception, [CallerMemberName] string operation = "")
    {
        if (exception is OperationCanceledException) return;
        lock (sync)
        {
            available = false;
            lastFailure = exception;
            failedOperationId = CacheOperationDiagnostics.CurrentOperationId;
        }
        CacheOperationDiagnostics.Report(path, operation, exception);
    }
}
