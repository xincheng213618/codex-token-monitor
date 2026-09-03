namespace CodexTokenMonitor;

internal static class MonitorCachePaths
{
    private static readonly AsyncLocal<string?> LocalAppDataOverride = new();

    public static string LocalAppData =>
        LocalAppDataOverride.Value ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    internal static IDisposable PushLocalAppDataRoot(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var previous = LocalAppDataOverride.Value;
        LocalAppDataOverride.Value = Path.GetFullPath(path);
        return new CachePathScope(previous);
    }

    private sealed class CachePathScope(string? previous) : IDisposable
    {
        public void Dispose()
        {
            LocalAppDataOverride.Value = previous;
        }
    }
}
