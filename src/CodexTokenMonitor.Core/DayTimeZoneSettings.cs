namespace CodexTokenMonitor;

// Fixed UTC offsets define reporting days; source timestamps and cache dates
// keep their existing storage convention.
internal sealed class DayTimeZoneSettings
{
    public int OffsetMinutes { get; set; } = 480;
    public TimeSpan Offset => TimeSpan.FromMinutes(OffsetMinutes);
    public static bool IsValid(int minutes) => minutes is >= -720 and <= 840 && minutes % 15 == 0;

    public static string Format(TimeSpan offset)
    {
        if (offset == TimeSpan.Zero) return "UTC";
        var minutes = Math.Abs((int)offset.TotalMinutes);
        return $"UTC{(offset < TimeSpan.Zero ? "-" : "+")}{minutes / 60}" +
            (minutes % 60 == 0 ? "" : $":{minutes % 60:00}");
    }

    private static string SettingsPath => Path.Combine(
        MonitorCachePaths.LocalAppData, "CodexTokenMonitor", "day-time-zone-v1.json");

    public static DayTimeZoneSettings Load()
    {
        try
        {
            var settings = JsonSerializer.Deserialize<DayTimeZoneSettings>(File.ReadAllText(SettingsPath));
            return settings is not null && IsValid(settings.OffsetMinutes) ? settings : new();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new();
        }
    }

    public void Save()
    {
        if (!IsValid(OffsetMinutes)) throw new ArgumentOutOfRangeException(nameof(OffsetMinutes));
        var path = SettingsPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(this));
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally { File.Delete(temporaryPath); }
    }
}
