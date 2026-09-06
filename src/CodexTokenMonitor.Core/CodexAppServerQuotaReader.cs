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
    private static CodexCliCommand? selectedCommand;

    public static CodexQuotaSnapshot? ReadCurrent(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var lockTaken = false;
        try
        {
            while (!(lockTaken = Monitor.TryEnter(SyncRoot, millisecondsTimeout: 100)))
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            cancellationToken.ThrowIfCancellationRequested();
            var nowUtc = DateTimeOffset.UtcNow;
            var cacheDuration = cachedSnapshot is null ? FailureCacheDuration : SuccessCacheDuration;
            if (nowUtc - lastAttemptUtc < cacheDuration)
            {
                return cachedSnapshot;
            }

            lastAttemptUtc = nowUtc;
            try
            {
                cachedSnapshot = ReadCurrentAsync(cancellationToken).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                cachedSnapshot = null;
            }

            return cachedSnapshot;
        }
        finally
        {
            if (lockTaken)
            {
                Monitor.Exit(SyncRoot);
            }
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

    private static async Task<CodexQuotaSnapshot?> ReadCurrentAsync(CancellationToken cancellationToken)
    {
        var failedCommand = selectedCommand;
        if (failedCommand is not null)
        {
            var cachedPathSnapshot = await TryReadCurrentAsync(failedCommand, cancellationToken);
            if (cachedPathSnapshot is not null)
            {
                return cachedPathSnapshot;
            }

            selectedCommand = null;
        }

        foreach (var command in CodexCliLocator.FindAll())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (command == failedCommand)
            {
                continue;
            }

            var snapshot = await TryReadCurrentAsync(command, cancellationToken);
            if (snapshot is not null)
            {
                selectedCommand = command;
                return snapshot;
            }
        }

        return null;
    }

    private static async Task<CodexQuotaSnapshot?> TryReadCurrentAsync(
        CodexCliCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            return await ReadCurrentAsync(command, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Multiple Codex installations can coexist. A packaged PATH entry
            // may be visible but not launchable, so rediscover and switch only
            // after the selected command actually fails.
            return null;
        }
    }

    private static async Task<CodexQuotaSnapshot?> ReadCurrentAsync(
        CodexCliCommand command,
        CancellationToken cancellationToken)
    {
        var response = await ReadAccountResponseAsync(command, "account/rateLimits/read", null, cancellationToken);
        return response is null ? null : ParseRateLimitsResponse(response, BeijingClock.Now);
    }

    public static async Task<string?> ReadPlanTypeAsync(CancellationToken cancellationToken = default)
    {
        foreach (var command in CodexCliLocator.FindAll())
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var response = await ReadAccountResponseAsync(command, "account/read", new { refreshToken = false }, cancellationToken);
                var plan = ParsePlanTypeResponse(response);
                if (plan is not null) return plan;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch { /* Try another installed CLI, as with quota discovery. */ }
        }
        return null;
    }

    internal static string? ParsePlanTypeResponse(string? response)
    {
        if (response is null) return null;
        try
        {
            using var document = JsonDocument.Parse(response);
            if (document.RootElement.TryGetProperty("result", out var result) &&
                result.ValueKind == JsonValueKind.Object && result.TryGetProperty("account", out var account) &&
                account.ValueKind == JsonValueKind.Object && account.TryGetProperty("type", out var type) &&
                type.GetString() == "chatgpt" && account.TryGetProperty("planType", out var plan) &&
                plan.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(plan.GetString()))
                return plan.GetString();
        }
        catch (JsonException) { }
        catch (InvalidOperationException) { }
        return null;
    }

    private static async Task<string?> ReadAccountResponseAsync(
        CodexCliCommand command, string method, object? parameters,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var process = new Process
        {
            StartInfo = CreateStartInfo(command)
        };
        if (!process.Start())
        {
            return null;
        }

        // Drain stderr while the protocol is active so diagnostics cannot
        // fill the pipe and block the app-server. Keep the task so its final
        // completion/exception is observed after the exact process tree is
        // stopped below.
        var standardErrorTask = process.StandardError.ReadToEndAsync();
        try
        {
            await WriteLineAsync(
                process.StandardInput,
                "{\"id\":1,\"method\":\"initialize\",\"params\":{\"clientInfo\":{\"name\":\"codex-token-monitor\",\"version\":\"1.0.0\"},\"capabilities\":{\"experimentalApi\":true}}}",
                cancellationToken);

            using (var initializeTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                initializeTimeout.CancelAfter(TimeSpan.FromSeconds(5));
                if (await ReadResponseLineAsync(process.StandardOutput, InitializeRequestId, initializeTimeout.Token) is null)
                {
                    return null;
                }
            }

            await WriteLineAsync(process.StandardInput, "{\"method\":\"initialized\"}", cancellationToken);
            await WriteLineAsync(
                process.StandardInput,
                JsonSerializer.Serialize(new { id = RateLimitsRequestId, method, @params = parameters }),
                cancellationToken);

            using var rateLimitsTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            rateLimitsTimeout.CancelAfter(TimeSpan.FromSeconds(10));
            var response = await ReadResponseLineAsync(
                process.StandardOutput,
                RateLimitsRequestId,
                rateLimitsTimeout.Token);
            if (response is null)
            {
                return null;
            }

            return response;
        }
        finally
        {
            StopProcess(process);
            try
            {
                await standardErrorTask;
            }
            catch
            {
                // stderr is diagnostic-only; process cleanup has already
                // completed and the protocol result remains authoritative.
            }
        }
    }

    private static ProcessStartInfo CreateStartInfo(CodexCliCommand command)
    {
        ProcessStartInfo startInfo;
        if (command.RequiresCommandShell)
        {
            var commandProcessor = Environment.GetEnvironmentVariable("ComSpec");
            if (string.IsNullOrWhiteSpace(commandProcessor))
            {
                commandProcessor = "cmd.exe";
            }

            var shellCommand = $"\"{command.FilePath}\" app-server --stdio";
            startInfo = new ProcessStartInfo
            {
                FileName = commandProcessor,
                Arguments = $"/d /s /c \"{shellCommand}\""
            };
        }
        else
        {
            startInfo = new ProcessStartInfo
            {
                FileName = command.FilePath,
                Arguments = "app-server --stdio"
            };
        }

        startInfo.UseShellExecute = false;
        startInfo.RedirectStandardInput = true;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.CreateNoWindow = true;
        startInfo.WindowStyle = ProcessWindowStyle.Hidden;
        return startInfo;
    }

    private static async Task WriteLineAsync(
        StreamWriter writer,
        string line,
        CancellationToken cancellationToken)
    {
        await writer.WriteLineAsync(line.AsMemory(), cancellationToken);
        await writer.FlushAsync(cancellationToken);
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
        }
        catch
        {
            // The short-lived helper may already have exited. Continue to
            // the termination check even when closing stdin fails.
        }

        try
        {
            if (!process.HasExited && !process.WaitForExit(1_000))
            {
                // The app-server may be a child of the cmd.exe helper. Kill
                // the exact temporary process tree so a timed-out quota read
                // cannot accumulate orphaned app-server processes.
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // The process may have exited between the checks. A final bounded
            // wait below still gives the OS time to reap it when possible.
        }

        try
        {
            if (!process.HasExited)
            {
                process.WaitForExit(1_000);
            }
        }
        catch
        {
            // Best-effort cleanup for a short-lived helper process.
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
        if (!QuotaPercentRules.IsValid(usedPercent))
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
            usedPercent!.Value,
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
