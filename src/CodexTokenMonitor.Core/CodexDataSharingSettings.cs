using System.Security.Cryptography;

namespace CodexTokenMonitor;

internal sealed class CodexDataSharingSettings
{
    public const int DefaultPort = 36666;
    public const int DefaultAutoUploadIntervalHours = 1;
    public const int MaxAutoUploadIntervalHours = 24;
    public int Port { get; set; } = DefaultPort;
    public bool AutoStart { get; set; } = true;
    public bool AutoUploadToday { get; set; }
    public int AutoUploadIntervalHours { get; set; } = DefaultAutoUploadIntervalHours;
    public string AccessKey { get; set; } = CreateAccessKey();
    public string ServerAddress { get; set; } = "";
    public string ServerAccessKey { get; set; } = "";

    public static string CreateAccessKey() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

    private static string SettingsPath => Path.Combine(
        MonitorCachePaths.LocalAppData, "CodexTokenMonitor", "data-sharing-v1.json");

    public static CodexDataSharingSettings Load()
    {
        try
        {
            var settings = JsonSerializer.Deserialize<CodexDataSharingSettings>(File.ReadAllText(SettingsPath)) ?? new();
            if (settings.Port is < 1 or > 65535)
            {
                settings.Port = DefaultPort;
            }

            if (settings.AutoUploadIntervalHours is < 1 or > MaxAutoUploadIntervalHours)
            {
                settings.AutoUploadIntervalHours = DefaultAutoUploadIntervalHours;
            }

            if (string.IsNullOrWhiteSpace(settings.AccessKey))
            {
                settings.AccessKey = CreateAccessKey();
            }

            return settings;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new();
        }
    }

    public void Save()
    {
        var path = SettingsPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }
}
