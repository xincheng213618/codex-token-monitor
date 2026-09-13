namespace CodexTokenMonitor;

internal static class MonitorCachePaths
{
    private static readonly AsyncLocal<CachePathScope?> CurrentScope = new();

    public static string LocalAppData =>
        CurrentScope.Value?.Root ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    // Dispose nested scopes in LIFO order within each execution context.
    internal static IDisposable PushLocalAppDataRoot(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var scope = new CachePathScope(Path.GetFullPath(path), CurrentScope.Value);
        CurrentScope.Value = scope;
        return scope;
    }

    private sealed class CachePathScope(string root, CachePathScope? previous) : IDisposable
    {
        public string Root { get; } = root;

        public void Dispose()
        {
            if (ReferenceEquals(CurrentScope.Value, this)) CurrentScope.Value = previous;
        }
    }
}
