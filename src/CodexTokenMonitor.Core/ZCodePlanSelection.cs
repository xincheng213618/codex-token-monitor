namespace CodexTokenMonitor;

/// <summary>The plan the ZCode panel is currently showing, persisted across restarts.</summary>
internal sealed record ZCodePlanSelection(string? UserPlanId, string? PlanId);

/// <summary>
/// Stores the selected plan as one small JSON file. user_plan_id identifies the
/// purchased plan instance; plan_id is kept as a fallback because a repurchase
/// mints a new user_plan_id for the same plan template.
/// </summary>
internal static class ZCodePlanSelectionStore
{
    private static string SettingsPath => Path.Combine(
        MonitorCachePaths.LocalAppData, "CodexTokenMonitor", "zcode-plan-selection-v1.json");

    public static ZCodePlanSelection? Load()
    {
        try
        {
            return JsonSerializer.Deserialize<ZCodePlanSelection>(File.ReadAllText(SettingsPath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public static void Save(ZCodePlanSelection selection)
    {
        try
        {
            var path = SettingsPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporaryPath, JsonSerializer.Serialize(selection));
                File.Move(temporaryPath, path, overwrite: true);
            }
            finally
            {
                File.Delete(temporaryPath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A failed persistence must not break the quota panel; the next
            // selection change simply retries.
        }
    }
}
