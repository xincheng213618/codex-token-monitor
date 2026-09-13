namespace CodexTokenMonitor;

internal static class UsageLogPaths
{
    private static readonly AsyncLocal<LogPathScope?> CurrentScope = new();

    internal static string? GetOverrideRoot(UsageSource source)
    {
        return CurrentScope.Value is { } scope ? Path.Combine(scope.Root, source.ToString()) : null;
    }

    // Cache isolation alone does not isolate scans that repair incomplete days.
    // Keep log overrides local to the execution context so parallel tests cannot
    // redirect another reader back to the user's actual history. Nested scopes
    // are disposed in LIFO order within each execution context.
    internal static IDisposable PushRoot(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var scope = new LogPathScope(Path.GetFullPath(path), CurrentScope.Value);
        CurrentScope.Value = scope;
        return scope;
    }

    private sealed class LogPathScope(string root, LogPathScope? previous) : IDisposable
    {
        public string Root { get; } = root;

        public void Dispose()
        {
            if (ReferenceEquals(CurrentScope.Value, this)) CurrentScope.Value = previous;
        }
    }
}
