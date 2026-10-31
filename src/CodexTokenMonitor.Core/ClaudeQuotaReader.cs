using System.ComponentModel;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodexTokenMonitor;

// Keep bearer values out of generated record ToString(), diagnostics and persisted snapshots.
internal sealed class ClaudeOAuthCredential(string token)
{
    public string Token { get; } = token;
    public override string ToString() => "Claude OAuth credential";
}

internal static class ClaudeOAuthCredentials
{
    public static ClaudeOAuthCredential? ReadCurrent()
    {
        // Test/probe scopes must never discover the user's real login or call its API.
        if (MonitorCachePaths.IsIsolated) return null;
        var now = DateTimeOffset.UtcNow;
        foreach (var root in DesktopRoots())
        {
            try
            {
                using var config = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "config.json")));
                var account = config.RootElement.TryGetProperty("lastKnownAccountUuid", out var id) ? id.GetString() : null;
                using var state = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "Local State")));
                var wrapped = Convert.FromBase64String(state.RootElement.GetProperty("os_crypt").GetProperty("encrypted_key").GetString()!);
                if (!wrapped.AsSpan().StartsWith("DPAPI"u8)) continue;
                var key = Unprotect(wrapped[5..]);
                try
                {
                    foreach (var name in new[] { "oauth:tokenCacheV2", "oauth:tokenCache" })
                    {
                        if (!config.RootElement.TryGetProperty(name, out var encrypted) || encrypted.ValueKind != JsonValueKind.String) continue;
                        var plain = DecryptElectron(Convert.FromBase64String(encrypted.GetString()!), key);
                        try
                        {
                            var credential = ParseDesktop(Encoding.UTF8.GetString(plain), account, now);
                            if (credential is not null) return credential;
                        }
                        finally { CryptographicOperations.ZeroMemory(plain); }
                    }
                }
                finally { CryptographicOperations.ZeroMemory(key); }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or
                CryptographicException or Win32Exception or FormatException or InvalidOperationException or KeyNotFoundException or ArgumentException)
            {
                // Another Desktop version or login may be usable. Never surface credential contents.
            }
        }

        try
        {
            var root = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
            if (string.IsNullOrWhiteSpace(root)) root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
            return ParseCli(File.ReadAllText(Path.Combine(root, ".credentials.json")), now);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    internal static ClaudeOAuthCredential? ParseDesktop(string json, string? activeAccount, DateTimeOffset now)
    {
        // V2 keys bind account, client, organization, API origin and scopes. Do not
        // accidentally use an old account's credential or one issued for another host.
        if (string.IsNullOrWhiteSpace(activeAccount)) return null;
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
        const string hostMarker = ":https://api.anthropic.com:";
        var candidates = document.RootElement.EnumerateObject()
            .Where(entry => entry.Name.StartsWith($"acct:{activeAccount}|", StringComparison.Ordinal) &&
                            entry.Name.Contains(hostMarker, StringComparison.Ordinal) &&
                            entry.Name[(entry.Name.IndexOf(hostMarker, StringComparison.Ordinal) + hostMarker.Length)..]
                                .Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("user:inference"))
            .OrderByDescending(entry => entry.Name.Contains("user:sessions:claude_code", StringComparison.Ordinal));
        foreach (var entry in candidates)
        {
            if (ReadToken(entry.Value, "token", now) is { } token) return new ClaudeOAuthCredential(token);
        }
        return null;
    }

    internal static ClaudeOAuthCredential? ParseCli(string json, DateTimeOffset now)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("claudeAiOauth", out var oauth) || oauth.ValueKind != JsonValueKind.Object) return null;
        if (!oauth.TryGetProperty("scopes", out var scopes) || scopes.ValueKind != JsonValueKind.Array ||
            !scopes.EnumerateArray().Any(scope => scope.ValueKind == JsonValueKind.String && scope.GetString() == "user:inference")) return null;
        return ReadToken(oauth, "accessToken", now) is { } token ? new ClaudeOAuthCredential(token) : null;
    }

    private static string? ReadToken(JsonElement entry, string property, DateTimeOffset now) =>
        entry.ValueKind == JsonValueKind.Object && entry.TryGetProperty(property, out var token) && token.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(token.GetString()) && entry.TryGetProperty("expiresAt", out var expiry) &&
        expiry.ValueKind == JsonValueKind.Number && expiry.TryGetInt64(out var ms) && ms > now.ToUnixTimeMilliseconds() + 30_000 ? token.GetString() : null;

    private static IEnumerable<string> DesktopRoots()
    {
        if (!OperatingSystem.IsWindows()) yield break;
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var packages = Path.Combine(local, "Packages");
        if (Directory.Exists(packages))
        {
            foreach (var package in Directory.EnumerateDirectories(packages, "Claude_*"))
            {
                var root = Path.Combine(package, "LocalCache", "Roaming", "Claude");
                if (File.Exists(Path.Combine(root, "config.json"))) yield return root;
            }
        }
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude");
    }

    internal static byte[] DecryptElectron(byte[] encrypted, byte[] key)
    {
        if (encrypted.Length < 31 || !encrypted.AsSpan(0, 3).SequenceEqual("v10"u8)) throw new CryptographicException();
        var plain = new byte[encrypted.Length - 31];
        using var aes = new AesGcm(key, 16);
        aes.Decrypt(encrypted.AsSpan(3, 12), encrypted.AsSpan(15, plain.Length), encrypted.AsSpan(encrypted.Length - 16), plain);
        return plain;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Blob { public int Length; public IntPtr Data; }

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy,
        IntPtr reserved, IntPtr prompt, int flags, out Blob output);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    private static byte[] Unprotect(byte[] encrypted)
    {
        var input = new Blob { Length = encrypted.Length, Data = Marshal.AllocHGlobal(encrypted.Length) };
        try
        {
            Marshal.Copy(encrypted, 0, input.Data, encrypted.Length);
            if (!CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out var output))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                var bytes = new byte[output.Length];
                Marshal.Copy(output.Data, bytes, 0, output.Length);
                return bytes;
            }
            finally
            {
                unsafe { new Span<byte>((void*)output.Data, output.Length).Clear(); }
                LocalFree(output.Data);
            }
        }
        finally { Marshal.FreeHGlobal(input.Data); }
    }
}

internal sealed record ClaudeQuotaReadResult(ClaudeRateLimitSnapshot? Snapshot, string? Failure);

/// <summary>Uses the same first-party read-only endpoint as Claude Desktop's plan usage panel.</summary>
internal sealed class ClaudeQuotaReader
{
    private static readonly HttpClient Client = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.All,
        PooledConnectionLifetime = TimeSpan.FromMinutes(10)
    });
    private readonly Func<ClaudeOAuthCredential?> credentials;
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send;
    private readonly Func<DateTimeOffset> clock;
    private readonly SemaphoreSlim gate = new(1);
    private DateTimeOffset nextAttempt;
    private ClaudeQuotaReadResult cached = new(null, null);

    public ClaudeQuotaReader(Func<ClaudeOAuthCredential?>? credentials = null,
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? send = null,
        Func<DateTimeOffset>? clock = null)
    {
        this.credentials = credentials ?? ClaudeOAuthCredentials.ReadCurrent;
        this.send = send ?? ((request, token) => Client.SendAsync(request, token));
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public async Task<ClaudeQuotaReadResult> ReadAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (clock() < nextAttempt) return cached;
            nextAttempt = clock().AddSeconds(30);
            var credential = credentials();
            if (credential is null) return cached = new(null, "未检测到 Claude Desktop / CLI 的有效订阅登录");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.anthropic.com/api/oauth/usage");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.Token);
                request.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20");
                using var response = await send(request, timeout.Token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    var retry = response.Headers.RetryAfter?.Delta ??
                        (response.Headers.RetryAfter?.Date is { } at ? at - clock() : TimeSpan.FromMinutes(2));
                    nextAttempt = clock().AddSeconds(response.StatusCode == HttpStatusCode.TooManyRequests
                        ? Math.Clamp(retry.TotalSeconds, 1, 900) : 30);
                    return cached = new(cached.Snapshot, response.StatusCode switch
                    {
                        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "Claude 登录已失效或没有订阅额度访问权限",
                        HttpStatusCode.TooManyRequests => "Claude 额度接口限流，稍后自动重试",
                        _ => $"Claude 额度接口返回 HTTP {(int)response.StatusCode}"
                    });
                }
                var json = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
                var snapshot = Parse(json, clock());
                if (snapshot is null) return cached = new(cached.Snapshot, "Claude 未返回可用的订阅额度窗口");
                nextAttempt = clock().AddMinutes(1);
                foreach (var window in new[] { snapshot.FiveHour, snapshot.SevenDay })
                    if (window is not null && window.ResetLocal < nextAttempt) nextAttempt = window.ResetLocal;
                ClaudeRateLimitLog.SaveCurrent(snapshot);
                return cached = new(snapshot, null);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return cached = new(cached.Snapshot, "Claude 额度请求超时，稍后自动重试");
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException)
            {
                return cached = new(cached.Snapshot, "Claude 额度读取失败，稍后自动重试");
            }
        }
        finally { gate.Release(); }
    }

    internal static ClaudeRateLimitSnapshot? Parse(string json, DateTimeOffset observed)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return null;
        ClaudeRateLimitWindow? Window(string name)
        {
            if (!root.TryGetProperty(name, out var window) || window.ValueKind != JsonValueKind.Object ||
                !window.TryGetProperty("utilization", out var percent) || percent.ValueKind != JsonValueKind.Number || !percent.TryGetDouble(out var used) || !double.IsFinite(used) || used < 0 || used > 100 ||
                !window.TryGetProperty("resets_at", out var reset) || reset.ValueKind != JsonValueKind.String ||
                !DateTimeOffset.TryParse(reset.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var at)) return null;
            return at > observed ? new ClaudeRateLimitWindow(used, at.ToOffset(CodexUsageReader.BeijingOffset), observed.ToOffset(CodexUsageReader.BeijingOffset)) : null;
        }
        var five = Window("five_hour");
        var week = Window("seven_day");
        return five is null && week is null ? null : new ClaudeRateLimitSnapshot(observed.ToOffset(CodexUsageReader.BeijingOffset), five, week);
    }
}
