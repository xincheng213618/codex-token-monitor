using System.Diagnostics;

namespace CodexTokenMonitor;

/// <summary>
/// Reads the current account quota through Codex's local app-server protocol.
/// This endpoint is independent from session token events, so it still works
/// when no conversation has produced a fresh token_count log entry.
/// </summary>
internal static class CodexAppServerQuotaReader
{
    private const int InitializeRequestId = 1;
    private const int RateLimitsRequestId = 2;
    private static readonly object SyncRoot = new();
    private static readonly TimeSpan SuccessCacheDuration = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan FailureCacheDuration = TimeSpan.FromSeconds(10);
    private static DateTimeOffset lastAttemptUtc = DateTimeOffset.MinValue;
    private static CodexQuotaSnapshot? cachedSnapshot;

    public static CodexQuotaSnapshot? ReadCurrent()
    {
        lock (SyncRoot)
        {
            var nowUtc = DateTimeOffset.UtcNow;
            var cacheDuration = cachedSnapshot is null ? FailureCacheDuration : SuccessCacheDuration;
            if (nowUtc - lastAttemptUtc < cacheDuration)
            {
                return cachedSnapshot;
            }

            lastAttemptUtc = nowUtc;
            try
            {
                cachedSnapshot = ReadCurrentAsync().GetAwaiter().GetResult();
            }
            catch
            {
                cachedSnapshot = null;
            }

            return cachedSnapshot;
        }
    }

    internal static CodexQuotaSnapshot? ParseRateLimitsResponse(string line, DateTimeOffset snapshotLocal)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (!root.TryGetProperty("result", out var result))
            {
                return null;
            }

            if (!TrySelectGeneralCodexLimits(result, out var rateLimits))
            {
                return null;
            }

            var primary = TryReadWindow(rateLimits, "primary");
            var secondary = TryReadWindow(rateLimits, "secondary");
            if (primary is null && secondary is null)
            {
                return null;
            }

            var (fiveHour, week) = ClassifyWindows(primary, secondary, snapshotLocal);
            if (fiveHour is null && week is null)
            {
                return null;
            }

            var limitId = GetString(rateLimits, "limitId") ?? "codex";
            var limitName = GetString(rateLimits, "limitName");
            return new CodexQuotaSnapshot(
                snapshotLocal,
                limitId,
                limitName,
                fiveHour?.UsedPercent,
                fiveHour?.ResetAtLocal,
                week?.UsedPercent,
                week?.ResetAtLocal);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<CodexQuotaSnapshot?> ReadCurrentAsync()
    {
        using var process = new Process
        {
            StartInfo = CreateStartInfo()
        };
        if (!process.Start())
        {
            return null;
        }

        _ = process.StandardError.ReadToEndAsync();
        try
        {
            await WriteLineAsync(
                process.StandardInput,
                "{\"id\":1,\"method\":\"initialize\",\"params\":{\"clientInfo\":{\"name\":\"codex-token-monitor\",\"version\":\"1.0.0\"},\"capabilities\":{\"experimentalApi\":true}}}");

            using (var initializeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            {
                if (await ReadResponseLineAsync(process.StandardOutput, InitializeRequestId, initializeTimeout.Token) is null)
                {
                    return null;
                }
            }

            await WriteLineAsync(process.StandardInput, "{\"method\":\"initialized\"}");
            await WriteLineAsync(
                process.StandardInput,
                "{\"id\":2,\"method\":\"account/rateLimits/read\",\"params\":null}");

            using var rateLimitsTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var response = await ReadResponseLineAsync(
                process.StandardOutput,
                RateLimitsRequestId,
                rateLimitsTimeout.Token);
            if (response is null)
            {
                return null;
            }

            var snapshotLocal = DateTimeOffset.UtcNow.ToOffset(CodexUsageReader.BeijingOffset);
            return ParseRateLimitsResponse(response, snapshotLocal);
        }
        finally
        {
            StopProcess(process);
        }
    }

    private static ProcessStartInfo CreateStartInfo()
    {
        var commandProcessor = Environment.GetEnvironmentVariable("ComSpec");
        if (string.IsNullOrWhiteSpace(commandProcessor))
        {
            commandProcessor = "cmd.exe";
        }

        var npmCommand = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "npm",
            "codex.cmd");
        var command = File.Exists(npmCommand)
            ? $"\"{npmCommand}\" app-server --stdio"
            : "codex app-server --stdio";

        return new ProcessStartInfo
        {
            FileName = commandProcessor,
            Arguments = $"/d /s /c \"{command}\"",
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
    }

    private static async Task WriteLineAsync(StreamWriter writer, string line)
    {
        await writer.WriteLineAsync(line);
        await writer.FlushAsync();
    }

    private static async Task<string?> ReadResponseLineAsync(
        StreamReader reader,
        int requestId,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                return null;
            }

            try
            {
                using var document = JsonDocument.Parse(line);
                if (document.RootElement.TryGetProperty("id", out var id) &&
                    id.ValueKind == JsonValueKind.Number &&
                    id.TryGetInt32(out var value) &&
                    value == requestId)
                {
                    return line;
                }
            }
            catch
            {
                // Ignore app-server diagnostics and unrelated notifications.
            }
        }
    }

    private static void StopProcess(Process process)
    {
        try
        {
            process.StandardInput.Close();
            if (!process.HasExited && !process.WaitForExit(1_000))
            {
                // Only terminate the exact helper shell created above. Its
                // stdin has already been closed, so the app-server child also
                // receives EOF and exits without being left resident.
                process.Kill();
                process.WaitForExit(1_000);
            }
        }
        catch
        {
            // The short-lived helper may already have exited.
        }
    }

    private static bool TrySelectGeneralCodexLimits(JsonElement result, out JsonElement rateLimits)
    {
        if (result.TryGetProperty("rateLimitsByLimitId", out var byLimitId) &&
            byLimitId.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in byLimitId.EnumerateObject())
            {
                if (string.Equals(property.Name, "codex", StringComparison.OrdinalIgnoreCase) &&
                    property.Value.ValueKind == JsonValueKind.Object)
                {
                    rateLimits = property.Value;
                    return true;
                }
            }
        }

        if (result.TryGetProperty("rateLimits", out var fallback) &&
            fallback.ValueKind == JsonValueKind.Object)
        {
            var limitId = GetString(fallback, "limitId");
            if (string.IsNullOrWhiteSpace(limitId) ||
                string.Equals(limitId, "codex", StringComparison.OrdinalIgnoreCase))
            {
                rateLimits = fallback;
                return true;
            }
        }

        rateLimits = default;
        return false;
    }

    private static AppServerWindow? TryReadWindow(JsonElement rateLimits, string propertyName)
    {
        if (!rateLimits.TryGetProperty(propertyName, out var window) ||
            window.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        var usedPercent = GetDecimal(window, "usedPercent");
        if (usedPercent is null)
        {
            return null;
        }

        var windowMinutes = GetInt64(window, "windowDurationMins");
        DateTimeOffset? resetAt = null;
        var resetSeconds = GetInt64(window, "resetsAt");
        if (resetSeconds is > 0)
        {
            resetAt = DateTimeOffset.FromUnixTimeSeconds(resetSeconds.Value)
                .ToOffset(CodexUsageReader.BeijingOffset);
        }

        return new AppServerWindow(
            usedPercent.Value,
            windowMinutes is > 0 and <= int.MaxValue ? (int)windowMinutes.Value : null,
            resetAt);
    }

    private static (AppServerWindow? FiveHour, AppServerWindow? Week) ClassifyWindows(
        AppServerWindow? primary,
        AppServerWindow? secondary,
        DateTimeOffset snapshotLocal)
    {
        var windows = new[] { primary, secondary }
            .Where(item => item is not null)
            .Select(item => item!)
            .ToList();
        var fiveHour = windows.FirstOrDefault(item => item.WindowMinutes == 5 * 60);
        var week = windows.FirstOrDefault(item => item.WindowMinutes == 7 * 24 * 60);

        // Older app-server builds may omit windowDurationMins. Preserve the
        // traditional primary=5h / secondary=7d layout, while using reset
        // distance to identify a single weekly-only primary window.
        if (fiveHour is null && primary is { WindowMinutes: null })
        {
            if (secondary is not null || !LooksWeekly(primary, snapshotLocal))
            {
                fiveHour = primary;
            }
        }

        if (week is null)
        {
            if (secondary is { WindowMinutes: null })
            {
                week = secondary;
            }
            else if (primary is { WindowMinutes: null } && LooksWeekly(primary, snapshotLocal))
            {
                week = primary;
            }
        }

        return (fiveHour, week);
    }

    private static bool LooksWeekly(AppServerWindow window, DateTimeOffset snapshotLocal)
    {
        return window.ResetAtLocal is { } resetAt &&
               resetAt - snapshotLocal > TimeSpan.FromDays(1) &&
               resetAt - snapshotLocal <= TimeSpan.FromDays(8);
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static decimal? GetDecimal(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number))
        {
            return number;
        }

        return value.ValueKind == JsonValueKind.String &&
               decimal.TryParse(value.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static long? GetInt64(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
        {
            return number;
        }

        return value.ValueKind == JsonValueKind.String &&
               long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private sealed record AppServerWindow(
        decimal UsedPercent,
        int? WindowMinutes,
        DateTimeOffset? ResetAtLocal);
}
