namespace CodexTokenMonitor;

internal static class UsageLogPaths
{
    private static readonly AsyncLocal<string?> RootOverride = new();

    internal static string? GetOverrideRoot(UsageSource source)
    {
        return RootOverride.Value is { } root ? Path.Combine(root, source.ToString()) : null;
    }

    // Cache isolation alone does not isolate scans that repair incomplete days.
    // Keep log overrides local to the execution context so parallel tests cannot
    // redirect another reader back to the user's actual history.
    internal static IDisposable PushRoot(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var previous = RootOverride.Value;
        RootOverride.Value = Path.GetFullPath(path);
        return new LogPathScope(previous);
    }

    private sealed class LogPathScope(string? previous) : IDisposable
    {
        public void Dispose()
        {
            RootOverride.Value = previous;
        }
    }
}
