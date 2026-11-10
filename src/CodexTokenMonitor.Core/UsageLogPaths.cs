namespace CodexTokenMonitor;

internal static class UsageLogPaths
{
    private static readonly AsyncLocal<LogPathScope?> CurrentScope = new();

    internal static string? GetOverrideRoot(UsageSource source)
    {
        return CurrentScope.Value is { } scope ? Path.Combine(scope.Root, source.ToString()) : null;
    }

    internal static string GetCodexHome()
    {
        var home = GetOverrideRoot(UsageSource.Codex) ?? Environment.GetEnvironmentVariable("CODEX_HOME");
        return string.IsNullOrWhiteSpace(home)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex")
            : home;
    }

    internal static IEnumerable<string> GetCodexLogRoots(string home)
    {
        foreach (var name in new[] { "sessions", "archived_sessions" })
        {
            var path = Path.Combine(home, name);
            if (Directory.Exists(path)) yield return path;
        }
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
