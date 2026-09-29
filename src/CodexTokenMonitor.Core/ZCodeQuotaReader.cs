using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace CodexTokenMonitor;

/// <summary>
/// One entitlement bucket of the ZCode plan balance: the server meters tokens
/// per model entitlement, so a plan can expose several balances at once.
/// Window-scaled buckets (BigModel coding plan) carry percentages instead of
/// tokens: usage/remaining scaled so 10000 ≙ 100%.
/// </summary>
internal sealed record ZCodeQuotaBalance(
    string ModelName,
    string? ModelId,
    long TotalUnits,
    long UsedUnits,
    long RemainingUnits,
    long? AvailableUnits,
    DateTimeOffset? ExpiresAtLocal,
    DateTimeOffset? PeriodStartLocal,
    DateTimeOffset? PeriodEndLocal,
    string? PlanId = null,
    string? UserPlanId = null,
    bool IsWindowScaled = false)
{
    public decimal? UsedPercent => TotalUnits > 0
        ? decimal.Round(100m * Math.Min(UsedUnits, TotalUnits) / TotalUnits, 1)
        : null;

    public decimal? RemainingPercent => TotalUnits > 0
        ? decimal.Round(100m * Math.Max(0m, TotalUnits - UsedUnits) / TotalUnits, 1)
        : null;
}

/// <summary>
/// One purchased plan. The account can hold several concurrently (the balance
/// endpoint lists them all with status "active"), each metering its own buckets.
/// </summary>
internal sealed record ZCodeQuotaPlan(
    string PlanId,
    string? UserPlanId,
    string Name,
    string? Description,
    string Status,
    DateTimeOffset? StartsAtLocal,
    DateTimeOffset? EndsAtLocal,
    IReadOnlyList<ZCodeQuotaBalance> Balances)
{
    /// <summary>Stable identity of this plan instance, used to persist the UI selection.</summary>
    public string SelectionKey => UserPlanId ?? PlanId;

    public bool IsActive => string.Equals(Status, "active", StringComparison.OrdinalIgnoreCase);

    /// <summary>The balance shown as the headline number: the first bucket that still carries units.</summary>
    public ZCodeQuotaBalance? PrimaryBalance =>
        Balances.FirstOrDefault(item => item.RemainingUnits > 0) ??
        Balances.FirstOrDefault(item => item.TotalUnits > 0) ??
        Balances.FirstOrDefault();
}

internal sealed record ZCodeQuotaSnapshot(
    DateTimeOffset SnapshotLocal,
    IReadOnlyList<ZCodeQuotaPlan> Plans,
    DateTimeOffset? ServerTimeLocal)
{
    /// <summary>
    /// The plan a fresh session lands on: an active one that still carries
    /// units, else the first active one, else the server's first entry.
    /// </summary>
    public ZCodeQuotaPlan DefaultPlan =>
        Plans.FirstOrDefault(item => item.IsActive && item.PrimaryBalance?.RemainingUnits > 0) ??
        Plans.FirstOrDefault(item => item.IsActive) ??
        Plans[0];

    /// <summary>Resolves a persisted selection; user_plan_id wins, then plan_id.</summary>
    public ZCodeQuotaPlan? FindPlan(string? userPlanId, string? planId)
    {
        if (!string.IsNullOrWhiteSpace(userPlanId))
        {
            var match = Plans.FirstOrDefault(item =>
                string.Equals(item.UserPlanId, userPlanId, StringComparison.Ordinal));
            if (match is not null)
            {
                return match;
            }
        }

        if (!string.IsNullOrWhiteSpace(planId))
        {
            var match = Plans.FirstOrDefault(item =>
                string.Equals(item.PlanId, planId, StringComparison.Ordinal));
            if (match is not null)
            {
                return match;
            }
        }

        return null;
    }
}

internal enum ZCodeQuotaFailureKind
{
    NotSignedIn,
    HttpError,
    NetworkError,
    ParseError,
    Unknown
}

internal sealed record ZCodeQuotaFailure(
    ZCodeQuotaFailureKind Kind,
    int? StatusCode = null,
    string? Message = null,
    TimeSpan? RetryAfter = null);

internal sealed record ZCodeQuotaReadResult(
    ZCodeQuotaSnapshot? Snapshot,
    ZCodeQuotaFailure? Failure);

/// <summary>
/// Parses the <c>zcode-plan/billing/balance</c> envelope. Kept pure so tests can
/// pin the server contract without network access.
/// </summary>
internal static class ZCodeQuotaParser
{
    public static ZCodeQuotaSnapshot? Parse(string json, DateTimeOffset snapshotLocal)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!IsSuccessfulEnvelope(root) || !root.TryGetProperty("data", out var data) ||
                data.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var plans = ReadPlans(data);
            var balances = ReadBalances(data);
            if (plans.Count == 0 && balances.Count == 0)
            {
                return null;
            }

            return new ZCodeQuotaSnapshot(
                snapshotLocal,
                AttachBalances(plans, balances),
                ReadUnixSeconds(data, "server_time"));
        }
        catch (JsonException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static bool IsSuccessfulEnvelope(JsonElement root)
    {
        // The server wraps failures as HTTP 200 envelopes; code 0/absent means ok.
        if (root.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (root.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.Number)
        {
            return code.TryGetInt64(out var value) && value == 0;
        }

        return !root.TryGetProperty("success", out var success) || success.ValueKind != JsonValueKind.False;
    }

    private static List<ZCodeQuotaPlan> ReadPlans(JsonElement data)
    {
        var result = new List<ZCodeQuotaPlan>();
        if (!data.TryGetProperty("plans", out var plans) || plans.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var candidate in plans.EnumerateArray())
        {
            if (candidate.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var planId = GetString(candidate, "plan_id") ?? "zcode";
            result.Add(new ZCodeQuotaPlan(
                planId,
                GetString(candidate, "user_plan_id"),
                GetString(candidate, "name") ?? planId,
                GetString(candidate, "description"),
                GetString(candidate, "status") ?? "unknown",
                ReadUnixSeconds(candidate, "starts_at"),
                ReadUnixSeconds(candidate, "ends_at"),
                Array.Empty<ZCodeQuotaBalance>()));
        }

        return result;
    }

    private static IReadOnlyList<ZCodeQuotaBalance> ReadBalances(JsonElement data)
    {
        if (!data.TryGetProperty("balances", out var balances) || balances.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<ZCodeQuotaBalance>();
        }

        var result = new List<ZCodeQuotaBalance>();
        foreach (var item in balances.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var total = GetInt64(item, "total_units");
            var used = GetInt64(item, "used_units");
            var remaining = GetInt64(item, "remaining_units");
            if (total == 0 && used == 0 && remaining == 0)
            {
                continue;
            }

            result.Add(new ZCodeQuotaBalance(
                GetString(item, "show_name") ?? "unknown",
                ReadCapabilityModelId(item),
                total,
                used,
                remaining,
                GetInt64OrNull(item, "available_units"),
                ReadUnixSeconds(item, "expires_at"),
                ReadUnixSeconds(item, "period_start"),
                ReadUnixSeconds(item, "period_end"),
                GetString(item, "plan_id"),
                GetString(item, "user_plan_id")));
        }

        return result;
    }

    /// <summary>
    /// Attributes each metered bucket to its purchased plan (user_plan_id first,
    /// plan_id as the legacy fallback). The account may hold several plans at
    /// once — merging their buckets would show one plan's name over another
    /// plan's balance. Buckets whose plan record vanished stay visible under a
    /// synthetic entry so no metered quota silently disappears.
    /// </summary>
    private static IReadOnlyList<ZCodeQuotaPlan> AttachBalances(
        List<ZCodeQuotaPlan> plans, IReadOnlyList<ZCodeQuotaBalance> balances)
    {
        var unclaimed = balances.ToList();
        var result = new List<ZCodeQuotaPlan>(plans.Count);
        foreach (var plan in plans)
        {
            IReadOnlyList<ZCodeQuotaBalance> owned;
            if (plan.UserPlanId is { } userPlanId)
            {
                owned = unclaimed.Where(item => string.Equals(item.UserPlanId, userPlanId, StringComparison.Ordinal)).ToList();
            }
            else
            {
                owned = unclaimed.Where(item => item.UserPlanId is null &&
                                                string.Equals(item.PlanId, plan.PlanId, StringComparison.Ordinal)).ToList();
            }

            foreach (var item in owned)
            {
                unclaimed.Remove(item);
            }

            result.Add(plan with { Balances = OrderBalances(owned) });
        }

        foreach (var group in unclaimed.GroupBy(item => item.UserPlanId ?? item.PlanId ?? "zcode"))
        {
            var first = group.First();
            result.Add(new ZCodeQuotaPlan(
                first.PlanId ?? group.Key,
                first.UserPlanId,
                first.PlanId ?? group.Key,
                null,
                "unknown",
                null,
                null,
                OrderBalances(group.ToList())));
        }

        return result;
    }

    private static IReadOnlyList<ZCodeQuotaBalance> OrderBalances(IReadOnlyList<ZCodeQuotaBalance> balances)
    {
        return balances.OrderByDescending(item => item.RemainingUnits).ToList();
    }

    private static string? ReadCapabilityModelId(JsonElement item)
    {
        if (!item.TryGetProperty("capabilities", out var capabilities) ||
            capabilities.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var capability in capabilities.EnumerateArray())
        {
            if (capability.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var value = capability.GetString();
            if (!string.IsNullOrWhiteSpace(value) && value.StartsWith("model:", StringComparison.Ordinal))
            {
                return value["model:".Length..].Trim();
            }
        }

        return null;
    }

    private static DateTimeOffset? ReadUnixSeconds(JsonElement element, string propertyName)
    {
        var seconds = GetInt64OrNull(element, propertyName);
        return seconds is > 0
            ? DateTimeOffset.FromUnixTimeSeconds(seconds.Value).ToOffset(CodexUsageReader.BeijingOffset)
            : null;
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static long GetInt64(JsonElement element, string propertyName)
    {
        return GetInt64OrNull(element, propertyName) ?? 0;
    }

    private static long? GetInt64OrNull(JsonElement element, string propertyName)
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
}

/// <summary>
/// Reverses the desktop app's at-rest credential encryption. Values in
/// <c>~/.zcode/v2/credentials.json</c> are stored as
/// <c>enc:v1:&lt;iv&gt;.&lt;tag&gt;.&lt;ciphertext&gt;</c> (AES-256-GCM, base64url),
/// keyed by SHA-256 of the app's credential secret; plaintext values pass through.
/// </summary>
internal static class ZCodeCredentialProtector
{
    private const string Prefix = "enc:v1:";
    private const int TagSizeBytes = 16;
    private const string SecretEnvVar = "ZCODE_CREDENTIAL_SECRET";

    public static string? Decrypt(string? stored)
    {
        return DecryptWithSecret(stored, BuildSecret());
    }

    internal static string? DecryptWithSecret(string? stored, string secret)
    {
        if (string.IsNullOrEmpty(stored) || !stored.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return stored;
        }

        var parts = stored[Prefix.Length..].Split('.');
        if (parts.Length != 3 || parts.Any(string.IsNullOrEmpty))
        {
            return null;
        }

        byte[] key;
        byte[] nonce;
        byte[] tag;
        byte[] ciphertext;
        try
        {
            key = SHA256.HashData(Encoding.UTF8.GetBytes(secret));
            nonce = Base64UrlDecode(parts[0]);
            tag = Base64UrlDecode(parts[1]);
            ciphertext = Base64UrlDecode(parts[2]);
        }
        catch (FormatException)
        {
            return null;
        }

        if (nonce.Length == 0 || ciphertext.Length == 0)
        {
            return null;
        }

        try
        {
            using var aes = new AesGcm(key, TagSizeBytes);
            var plaintext = new byte[ciphertext.Length];
            aes.Decrypt(nonce, ciphertext, tag, plaintext);
            return Encoding.UTF8.GetString(plaintext);
        }
        catch (CryptographicException)
        {
            // Wrong secret, tampered payload, or a foreign encrypted value.
            return null;
        }
    }

    /// <summary>Mirrors the desktop app's fallback secret: env override or platform/home/user chain.</summary>
    private static string BuildSecret()
    {
        var fromEnvironment = Environment.GetEnvironmentVariable(SecretEnvVar);
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            return fromEnvironment;
        }

        return $"zcode-credential-fallback:{NodePlatformName()}:" +
               $"{Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)}:" +
               $"{Environment.UserName}";
    }

    private static string NodePlatformName()
    {
        return OperatingSystem.IsWindows() ? "win32" :
            OperatingSystem.IsMacOS() ? "darwin" : "linux";
    }

    private static byte[] Base64UrlDecode(string value)
    {
        return Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/').PadRight(
            value.Length + (4 - value.Length % 4) % 4, '='));
    }
}

/// <summary>
/// Reads the current ZCode plan balance from the same endpoint the desktop app
/// uses for its settings page. The shared credential file is the only source of
/// the bearer token, so a signed-in desktop install is required.
/// </summary>
internal sealed class ZCodeQuotaReader
{
    public static readonly ZCodeQuotaReader Shared = new();

    private const string DefaultApiOrigin = "https://zcode.z.ai";
    private const string FallbackAppVersion = "unknown";
    private static readonly TimeSpan SuccessCacheDuration = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan FailureCacheDuration = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan StaleSnapshotReuseDuration = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan MaxRateLimitCacheDuration = TimeSpan.FromMinutes(10);

    private readonly object syncRoot = new();
    private readonly Func<string> locateCredentialsFile;
    private readonly Func<string?> locateDeviceMidFile;
    private readonly Func<string?> locateAppVersion;
    private readonly Func<HttpMessageInvoker> createHttpSender;
    private readonly Func<CancellationToken, ZCodeQuotaPlan?> readCodingPlan;
    private readonly TimeSpan requestTimeout;
    private HttpMessageInvoker? cachedHttpSender;
    private DateTimeOffset lastAttemptUtc = DateTimeOffset.MinValue;
    private ZCodeQuotaSnapshot? cachedSnapshot;
    private ZCodeQuotaFailure? cachedFailure;
    private ZCodeQuotaSnapshot? lastGoodSnapshot;

    public ZCodeQuotaReader(
        Func<string>? locateCredentialsFile = null,
        Func<string?>? locateDeviceMidFile = null,
        Func<string?>? locateAppVersion = null,
        Func<HttpMessageInvoker>? createHttpSender = null,
        TimeSpan? requestTimeout = null,
        Func<CancellationToken, ZCodeQuotaPlan?>? readCodingPlan = null)
    {
        this.locateCredentialsFile = locateCredentialsFile ?? LocateCredentialsFile;
        this.locateDeviceMidFile = locateDeviceMidFile ?? LocateDeviceMidFile;
        this.locateAppVersion = locateAppVersion ?? LocateAppVersion;
        this.createHttpSender = createHttpSender ?? (() => new HttpClient(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(10)
        }));
        this.readCodingPlan = readCodingPlan ?? (token => ZCodeCodingPlanReader.Shared.ReadCurrent(token));
        this.requestTimeout = requestTimeout ?? DefaultRequestTimeout;
    }

    /// <summary>
    /// A property on purpose: the <see cref="Shared"/> singleton is initialized
    /// before any other static field of this type, so a static readonly field
    /// here would be captured as TimeSpan.Zero by that first construction.
    /// </summary>
    private static TimeSpan DefaultRequestTimeout => TimeSpan.FromSeconds(15);

    /// <summary>Effective per-request timeout, surfaced for tests and diagnostics.</summary>
    internal TimeSpan RequestTimeout => requestTimeout;

    public ZCodeQuotaSnapshot? ReadCurrent(CancellationToken cancellationToken = default)
    {
        return ReadCurrentResult(cancellationToken).Snapshot;
    }

    /// <summary>
    /// Read with the cache window applied; the failure explains a null snapshot
    /// so callers can distinguish "not signed in" from a transient outage.
    /// </summary>
    public ZCodeQuotaReadResult ReadCurrentResult(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var lockTaken = false;
        try
        {
            while (!(lockTaken = Monitor.TryEnter(syncRoot, millisecondsTimeout: 100)))
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var nowUtc = DateTimeOffset.UtcNow;
            if (nowUtc - lastAttemptUtc < CacheDuration())
            {
                return new ZCodeQuotaReadResult(cachedSnapshot, cachedFailure);
            }

            lastAttemptUtc = nowUtc;
            try
            {
                var fresh = ReadCurrentUncached(cancellationToken);
                cachedSnapshot = fresh.Snapshot;
                cachedFailure = fresh.Failure;
                if (fresh.Snapshot is not null)
                {
                    lastGoodSnapshot = fresh.Snapshot;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                cachedSnapshot = null;
                cachedFailure = new ZCodeQuotaFailure(ZCodeQuotaFailureKind.Unknown, Message: ex.Message);
            }

            return new ZCodeQuotaReadResult(cachedSnapshot, cachedFailure);
        }
        finally
        {
            if (lockTaken)
            {
                Monitor.Exit(syncRoot);
            }
        }
    }

    /// <summary>Forgets the cached attempt so the next read hits the network again; keeps the last good snapshot for 429 reuse.</summary>
    internal void ResetCacheForTests()
    {
        lock (syncRoot)
        {
            lastAttemptUtc = DateTimeOffset.MinValue;
            cachedSnapshot = null;
            cachedFailure = null;
        }
    }

    /// <summary>
    /// How long the next attempt waits after the last one. A server-provided
    /// Retry-After extends the failure window, capped so a bogus header cannot
    /// freeze the panel for hours.
    /// </summary>
    internal TimeSpan CacheDuration()
    {
        if (cachedSnapshot is not null)
        {
            return SuccessCacheDuration;
        }

        return cachedFailure?.RetryAfter is { } retryAfter && retryAfter > FailureCacheDuration
            ? (retryAfter < MaxRateLimitCacheDuration ? retryAfter : MaxRateLimitCacheDuration)
            : FailureCacheDuration;
    }

    private static TimeSpan? ReadRetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        if (header is null)
        {
            return null;
        }

        if (header.Delta is { } delta)
        {
            return delta > TimeSpan.Zero ? delta : null;
        }

        return header.Date is { } date && date > DateTimeOffset.UtcNow ? date - DateTimeOffset.UtcNow : null;
    }

    private ZCodeQuotaReadResult ReadCurrentUncached(CancellationToken cancellationToken)
    {
        var token = ReadBearerToken();
        if (string.IsNullOrWhiteSpace(token))
        {
            return new ZCodeQuotaReadResult(null, new ZCodeQuotaFailure(ZCodeQuotaFailureKind.NotSignedIn));
        }

        var deviceMid = locateDeviceMidFile();
        var appVersion = locateAppVersion() ?? FallbackAppVersion;
        var request = new HttpRequestMessage(HttpMethod.Get, BuildBalanceUrl(appVersion));
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
        request.Headers.TryAddWithoutValidation("User-Agent", $"ZCode/{appVersion}");
        request.Headers.TryAddWithoutValidation("X-ZCode-App-Version", appVersion);
        if (!string.IsNullOrWhiteSpace(deviceMid))
        {
            request.Headers.TryAddWithoutValidation("X-Device-Mid", deviceMid);
        }

        HttpResponseMessage response;
        // A hung connection must not occupy the panel's refresh slot for the
        // HttpClient default of 100 seconds; bound it well below the cache cycle.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(requestTimeout);
        try
        {
            response = GetHttpSender().SendAsync(request, timeoutCts.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            request.Dispose();
            throw;
        }
        catch (OperationCanceledException)
        {
            request.Dispose();
            return new ZCodeQuotaReadResult(null, new ZCodeQuotaFailure(
                ZCodeQuotaFailureKind.NetworkError,
                Message: $"请求超时（{requestTimeout.TotalSeconds:N0} 秒）"));
        }
        catch (Exception ex)
        {
            request.Dispose();
            return new ZCodeQuotaReadResult(null, new ZCodeQuotaFailure(ZCodeQuotaFailureKind.NetworkError, Message: ex.Message));
        }

        using (request)
        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                // A rate-limited call should not blank out a recently observed
                // balance; the snapshot carries its own timestamp for staleness.
                if ((int)response.StatusCode == 429 && lastGoodSnapshot is not null &&
                    BeijingClock.Now - lastGoodSnapshot.SnapshotLocal <= StaleSnapshotReuseDuration)
                {
                    return new ZCodeQuotaReadResult(lastGoodSnapshot, null);
                }

                return new ZCodeQuotaReadResult(null, new ZCodeQuotaFailure(
                    ZCodeQuotaFailureKind.HttpError,
                    (int)response.StatusCode,
                    RetryAfter: ReadRetryAfter(response)));
            }

            string json;
            try
            {
                json = response.Content
                    .ReadAsStringAsync(timeoutCts.Token)
                    .GetAwaiter()
                    .GetResult();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return new ZCodeQuotaReadResult(null, new ZCodeQuotaFailure(ZCodeQuotaFailureKind.NetworkError, Message: ex.Message));
            }

            var snapshot = ZCodeQuotaParser.Parse(json, BeijingClock.Now);
            snapshot = MergeCodingPlan(snapshot, timeoutCts.Token);
            return snapshot is not null
                ? new ZCodeQuotaReadResult(snapshot, null)
                : new ZCodeQuotaReadResult(null, new ZCodeQuotaFailure(ZCodeQuotaFailureKind.ParseError));
        }
    }

    /// <summary>
    /// Appends the BigModel coding plan (GLM Coding Lite, percentage windows)
    /// to the snapshot so it joins the plan selector. A failed or absent coding
    /// plan never fails the read; a coding plan without balance-endpoint data
    /// still produces a usable snapshot.
    /// </summary>
    private ZCodeQuotaSnapshot? MergeCodingPlan(ZCodeQuotaSnapshot? snapshot, CancellationToken cancellationToken)
    {
        ZCodeQuotaPlan? codingPlan;
        try
        {
            codingPlan = readCodingPlan(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            codingPlan = null;
        }

        if (codingPlan is null)
        {
            return snapshot;
        }

        return snapshot is { } existing
            ? existing with { Plans = existing.Plans.Concat(new[] { codingPlan }).ToList() }
            : new ZCodeQuotaSnapshot(BeijingClock.Now, new[] { codingPlan }, null);
    }

    private HttpMessageInvoker GetHttpSender()
    {
        if (cachedHttpSender is not null)
        {
            return cachedHttpSender;
        }

        var sender = createHttpSender();
        cachedHttpSender = sender;
        return sender;
    }

    internal static string BuildBalanceUrl(string appVersion)
    {
        return $"{DefaultApiOrigin}/api/v1/zcode-plan/billing/balance?app_version=" +
               Uri.EscapeDataString(appVersion);
    }

    private string? ReadBearerToken()
    {
        try
        {
            var path = locateCredentialsFile();
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                return null;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("zcodejwttoken", out var stored) ||
                stored.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            return ZCodeCredentialProtector.Decrypt(stored.GetString());
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static string LocateCredentialsFile()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".zcode", "v2", "credentials.json");
    }

    internal static string? LocateDeviceMidFile()
    {
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".zcode", "v2", "telemetry-state.json");
            if (!File.Exists(path))
            {
                return null;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                   document.RootElement.TryGetProperty("deviceMid", out var mid) &&
                   mid.ValueKind == JsonValueKind.String
                ? mid.GetString()
                : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static string? LocateAppVersion()
    {
        try
        {
            var exe = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs", "ZCode", "ZCode.exe");
            if (!File.Exists(exe))
            {
                return null;
            }

            var version = FileVersionInfo.GetVersionInfo(exe);
            var text = version.ProductVersion;
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            // ProductVersion carries the full four-part build number; the app
            // advertises the first three segments everywhere (app_version, UA).
            var segments = text.Split('.');
            return segments.Length >= 3
                ? string.Join('.', segments[0], segments[1], segments[2])
                : text;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
