namespace CodexTokenMonitor;

/// <summary>A local record of the balance and expiry shown in Claude's usage settings.</summary>
internal sealed record ClaudeFableResetReminder(int Remaining, DateTimeOffset ExpiresAt)
{
    private static string SettingsPath =>
        Path.Combine(MonitorCachePaths.LocalAppData, "ClaudeCodeTokenMonitor", "fable-reset-reminder.json");

    public string Status(DateTimeOffset now) => ExpiresAt <= now ? "已过期" : $"{Remaining:N0} 次剩余";

    public static ClaudeFableResetReminder? Load()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return null;
            var reminder = JsonSerializer.Deserialize<ClaudeFableResetReminder>(File.ReadAllText(SettingsPath));
            return reminder is { Remaining: >= 0 } && reminder.ExpiresAt != default ? reminder : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public void Save()
    {
        if (Remaining < 0 || ExpiresAt == default)
            throw new ArgumentException("请填写有效的重置次数和到期时间。");

        var path = SettingsPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(this));
            File.Move(temporary, path, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }
}
