namespace CodexTokenMonitor;

internal static class MonitorSettingsDatabase
{
    // Purchases and reset-card settings are user data, not rebuildable log caches.
    public static string Path => System.IO.Path.Combine(
        MonitorCachePaths.LocalAppData, "CodexTokenMonitor", "monitor-settings.sqlite3");
}
