namespace CodexTokenMonitor;

/// <summary>
/// The account's Zhipu coding-plan reset cards plus the latest usage history,
/// as reported by the desktop app's reset endpoint. Expired cards never appear:
/// the server lists only the currently available ones.
/// </summary>
internal sealed record ZCodeResetCreditStatus(
    int FiveHourCount,
    int WeekCount,
    IReadOnlyList<DateTimeOffset> FiveHourExpiresLocal,
    IReadOnlyList<DateTimeOffset> WeekExpiresLocal,
    DateTimeOffset? LatestFiveHourUsedLocal,
    DateTimeOffset? LatestWeekUsedLocal,
    bool HasUnreadHistory,
    DateTimeOffset SnapshotLocal)
{
    public int TotalCount => FiveHourCount + WeekCount;

    public DateTimeOffset? EarliestExpiresLocal => FiveHourExpiresLocal
        .Concat(WeekExpiresLocal)
        .OrderBy(item => item)
        .Cast<DateTimeOffset?>()
        .FirstOrDefault();
}

/// <summary>
/// Reads the GLM coding-plan reset cards (额度重置卡) from the same
/// zcode.z.ai endpoint the desktop client uses for its in-app reset prompt.
/// Auth mirrors that client: the ZCode JWT plus the account family's OAuth
/// token, both from the shared credentials file, so a signed-in desktop
/// install is required. Any failure yields null; this card never blocks the
/// quota panel.
/// </summary>
internal sealed class ZCodeResetCreditReader
{
    public static readonly ZCodeResetCreditReader Shared = new();

    private const string DefaultApiOrigin = "https://zcode.z.ai";
    private const string StatusPath = "/api/v1/coding-plan/reset/status";
    private const string ZCodeJwtKey = "zcodejwttoken";
    private const string ZaiTokenKey = "oauth:zai:access_token";
    private const string BigModelTokenKey = "oauth:bigmodel:access_token";

    private readonly object syncRoot = new();
    private readonly Func<string> locateCredentialsFile;
    private readonly Func<HttpMessageInvoker> createHttpSender;
    private readonly TimeSpan requestTimeout;
    private static readonly TimeSpan SuccessCacheDuration = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan FailureCacheDuration = TimeSpan.FromSeconds(30);
    private HttpMessageInvoker? cachedHttpSender;
    private DateTimeOffset lastAttemptUtc = DateTimeOffset.MinValue;
    private ZCodeResetCreditStatus? cachedStatus;

    public ZCodeResetCreditReader(
        Func<string>? locateCredentialsFile = null,
        Func<HttpMessageInvoker>? createHttpSender = null,
        TimeSpan? requestTimeout = null)
    {
        this.locateCredentialsFile = locateCredentialsFile ?? ZCodeQuotaReader.LocateCredentialsFile;
        this.createHttpSender = createHttpSender ?? (() => new HttpClient(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(10)
        }));
        this.requestTimeout = requestTimeout ?? DefaultRequestTimeout;
    }

    /// <summary>
    /// A property on purpose: the <see cref="Shared"/> singleton is initialized
    /// before the other static fields of this type, so a static readonly field
    /// here would be captured as TimeSpan.Zero by that first construction.
    /// </summary>
    private static TimeSpan DefaultRequestTimeout => TimeSpan.FromSeconds(15);

    /// <summary>Effective per-request timeout, surfaced for tests and diagnostics.</summary>
    internal TimeSpan RequestTimeout => requestTimeout;

    public ZCodeResetCreditStatus? ReadCurrent(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (syncRoot)
        {
            var nowUtc = DateTimeOffset.UtcNow;
            var cacheDuration = cachedStatus is null ? FailureCacheDuration : SuccessCacheDuration;
            if (nowUtc - lastAttemptUtc < cacheDuration)
            {
                return cachedStatus;
            }

            lastAttemptUtc = nowUtc;
            cachedStatus = ReadCurrentUncached(cancellationToken);
            return cachedStatus;
        }
    }

    /// <summary>Forgets the cached attempt so the next read hits the network again.</summary>
    internal void ResetCacheForTests()
    {
        lock (syncRoot)
        {
            lastAttemptUtc = DateTimeOffset.MinValue;
            cachedStatus = null;
        }
    }

    private ZCodeResetCreditStatus? ReadCurrentUncached(CancellationToken cancellationToken)
    {
        try
        {
            var authorization = LocateAuthorization(locateCredentialsFile());
            if (authorization is null)
            {
                return null;
            }

            var request = new HttpRequestMessage(HttpMethod.Get, $"{DefaultApiOrigin}{StatusPath}");
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {authorization.Value.ZCodeJwt}");
            request.Headers.TryAddWithoutValidation("X-Bigmodel-Authorization", authorization.Value.MaasToken);
            request.Headers.TryAddWithoutValidation("Bigmodel-Target-Type", "PERSONAL");

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(requestTimeout);
            using var response = GetHttpSender()
                .SendAsync(request, timeoutCts.Token)
                .GetAwaiter()
                .GetResult();
            request.Dispose();
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var json = response.Content
                .ReadAsStringAsync(timeoutCts.Token)
                .GetAwaiter()
                .GetResult();
            return ZCodeResetCreditParser.Parse(json, BeijingClock.Now);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Per-request timeout only: no card state this cycle.
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or HttpRequestException)
        {
            return null;
        }
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

    /// <summary>
    /// The reset endpoint needs both tokens the desktop app stores: the ZCode
    /// JWT and the account family's OAuth access token. A z.ai login wins over
    /// bigmodel when both exist, matching the client's family check.
    /// </summary>
    internal static (string ZCodeJwt, string MaasToken)? LocateAuthorization(string? credentialsPath)
    {
        try
        {
            if (string.IsNullOrEmpty(credentialsPath) || !File.Exists(credentialsPath))
            {
                return null;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(credentialsPath));
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty(ZCodeJwtKey, out var jwtValue) ||
                jwtValue.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var zCodeJwt = ZCodeCredentialProtector.Decrypt(jwtValue.GetString());
            if (string.IsNullOrWhiteSpace(zCodeJwt))
            {
                return null;
            }

            var maasToken = ReadOptionalToken(document.RootElement, ZaiTokenKey) ??
                ReadOptionalToken(document.RootElement, BigModelTokenKey);
            return string.IsNullOrWhiteSpace(maasToken) ? null : (zCodeJwt, maasToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static string? ReadOptionalToken(JsonElement root, string key)
    {
        if (!root.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return ZCodeCredentialProtector.Decrypt(value.GetString());
    }
}

/// <summary>
/// Parses the <c>/api/v1/coding-plan/reset/status</c> envelope:
/// <c>{code, msg, data:{available_five_hour_resets:[{expire_at}], available_week_resets:[...],
/// latest_five_hour_reset_history:{used_at}|null, latest_week_reset_history:..., has_unread_history}}</c>.
/// Timestamps are milliseconds; a non-zero code or missing data parses to null.
/// </summary>
internal static class ZCodeResetCreditParser
{
    public static ZCodeResetCreditStatus? Parse(string json, DateTimeOffset nowLocal)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("data", out var data) ||
                data.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (root.TryGetProperty("code", out var code) &&
                code.ValueKind == JsonValueKind.Number &&
                code.TryGetInt32(out var codeValue) &&
                codeValue != 0)
            {
                return null;
            }

            var fiveHour = ReadExpiries(data, "available_five_hour_resets");
            var week = ReadExpiries(data, "available_week_resets");
            return new ZCodeResetCreditStatus(
                fiveHour.Count,
                week.Count,
                fiveHour,
                week,
                ReadUsedAt(data, "latest_five_hour_reset_history"),
                ReadUsedAt(data, "latest_week_reset_history"),
                data.TryGetProperty("has_unread_history", out var unread) &&
                    unread.ValueKind == JsonValueKind.True,
                nowLocal);
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

    private static IReadOnlyList<DateTimeOffset> ReadExpiries(JsonElement data, string propertyName)
    {
        if (!data.TryGetProperty(propertyName, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<DateTimeOffset>();
        }

        var expiries = new List<DateTimeOffset>();
        foreach (var card in array.EnumerateArray())
        {
            if (card.ValueKind == JsonValueKind.Object &&
                card.TryGetProperty("expire_at", out var expireAt) &&
                expireAt.ValueKind == JsonValueKind.Number &&
                expireAt.TryGetInt64(out var milliseconds) &&
                milliseconds > 0)
            {
                expiries.Add(ReadUnixMillis(milliseconds));
            }
        }

        return expiries.OrderBy(item => item).ToList();
    }

    private static DateTimeOffset? ReadUsedAt(JsonElement data, string propertyName)
    {
        if (!data.TryGetProperty(propertyName, out var history) ||
            history.ValueKind != JsonValueKind.Object ||
            !history.TryGetProperty("used_at", out var usedAt) ||
            usedAt.ValueKind != JsonValueKind.Number ||
            !usedAt.TryGetInt64(out var milliseconds) ||
            milliseconds <= 0)
        {
            return null;
        }

        return ReadUnixMillis(milliseconds);
    }

    private static DateTimeOffset ReadUnixMillis(long milliseconds)
    {
        return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds)
            .ToOffset(CodexUsageReader.BeijingOffset);
    }
}
