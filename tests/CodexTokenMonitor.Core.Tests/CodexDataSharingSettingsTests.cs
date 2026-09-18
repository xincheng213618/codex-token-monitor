using Xunit;

namespace CodexTokenMonitor.Tests;

public sealed class CodexDataSharingSettingsTests : IDisposable
{
    private readonly IDisposable cacheScope;
    private readonly string root;

    public CodexDataSharingSettingsTests()
    {
        root = Path.Combine(Path.GetTempPath(), $"DataSharingSettingsTests-{Guid.NewGuid():N}");
        cacheScope = MonitorCachePaths.PushLocalAppDataRoot(root);
    }

    public void Dispose()
    {
        cacheScope.Dispose();
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch
        {
            // Best-effort cleanup of the temporary tree.
        }
    }

    [Fact]
    public void Load_WithoutFile_ReturnsSafeDefaults()
    {
        var settings = CodexDataSharingSettings.Load();

        Assert.Equal(CodexDataSharingSettings.DefaultPort, settings.Port);
        Assert.True(settings.AutoStart);
        Assert.False(settings.AutoUploadToday);
        Assert.Equal(CodexDataSharingSettings.DefaultAutoUploadIntervalHours, settings.AutoUploadIntervalHours);
        Assert.False(string.IsNullOrWhiteSpace(settings.AccessKey));
    }

    [Fact]
    public void SaveThenLoad_RoundTripsAllEditableFields()
    {
        var saved = new CodexDataSharingSettings
        {
            Port = 4711,
            AutoStart = false,
            AutoUploadToday = true,
            AutoUploadIntervalHours = 6,
            AccessKey = "key-1234",
            ServerAddress = "http://192.168.1.20:36666",
            ServerAccessKey = "remote-key"
        };
        saved.Save();

        var loaded = CodexDataSharingSettings.Load();

        Assert.Equal(4711, loaded.Port);
        Assert.False(loaded.AutoStart);
        Assert.True(loaded.AutoUploadToday);
        Assert.Equal(6, loaded.AutoUploadIntervalHours);
        Assert.Equal("key-1234", loaded.AccessKey);
        Assert.Equal("http://192.168.1.20:36666", loaded.ServerAddress);
        Assert.Equal("remote-key", loaded.ServerAccessKey);
    }

    [Fact]
    public void Load_CorruptFile_ReturnsDefaultsInsteadOfFailing()
    {
        new CodexDataSharingSettings().Save();
        File.WriteAllText(
            Path.Combine(MonitorCachePaths.LocalAppData, "CodexTokenMonitor", "data-sharing-v1.json"),
            "{ not json");

        var settings = CodexDataSharingSettings.Load();

        Assert.Equal(CodexDataSharingSettings.DefaultPort, settings.Port);
        Assert.False(string.IsNullOrWhiteSpace(settings.AccessKey));
    }

    [Fact]
    public void Load_OutOfRangeValues_AreClampedBackToDefaults()
    {
        var path = Path.Combine(MonitorCachePaths.LocalAppData, "CodexTokenMonitor");
        Directory.CreateDirectory(path);
        File.WriteAllText(
            Path.Combine(path, "data-sharing-v1.json"),
            """{"Port": 99999, "AutoUploadIntervalHours": 0, "AccessKey": ""}""");

        var settings = CodexDataSharingSettings.Load();

        Assert.Equal(CodexDataSharingSettings.DefaultPort, settings.Port);
        Assert.Equal(CodexDataSharingSettings.DefaultAutoUploadIntervalHours, settings.AutoUploadIntervalHours);
        Assert.False(string.IsNullOrWhiteSpace(settings.AccessKey));
    }
}
