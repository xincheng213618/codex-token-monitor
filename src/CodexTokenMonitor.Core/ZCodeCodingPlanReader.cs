using System.Diagnostics;

namespace CodexTokenMonitor;

/// <summary>The BigModel coding-plan credential: account id plus api key.</summary>
internal sealed record ZCodeCodingPlanAccount(string AccountId, string ApiKey);

/// <summary>
/// Reads the BigModel coding plan (GLM Coding Lite/Pro/Max). This plan is
/// metered as percentage windows — a rolling 5-hour window and a weekly
/// window, Codex-style — through open.bigmodel.cn's monitor endpoint, not the
/// zcode-plan balance endpoint. Auth is the coding-plan API key the desktop
/// app stores in the shared credentials file when the plan is connected.
/// </summary>
internal sealed class ZCodeCodingPlanReader
{
    public static readonly ZCodeCodingPlanReader Shared = new();

    private const string DefaultApiOrigin = "https://open.bigmodel.cn";
    private const string PlanId = "bigmodel-individual-coding-plan";
    private const string CredentialKeyPrefix = "account-provider:coding-plan:account:bigmodel-";
    private const string CredentialKeySuffix = ":api-key";

    private readonly Func<string> locateCredentialsFile;
    private readonly Func<HttpMessageInvoker> createHttpSender;
    private readonly TimeSpan requestTimeout;
    private HttpMessageInvoker? cachedHttpSender;

    public ZCodeCodingPlanReader(
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

    /// <summary>
    /// The coding plan as a selectable plan, or null when the desktop app has
    /// no connected BigModel coding plan (no api key, plan lapsed, server says
    /// absent). Any failure yields null: this plan is an addition to the
    /// zcode-plan list and must never break that read.
    /// </summary>
    public ZCodeQuotaPlan? ReadCurrent(CancellationToken cancellationToken = default)
    {
        try
        {
            var account = LocateCodingPlanAccount(locateCredentialsFile());
            if (account is null)
            {
                return null;
            }

            var request = new HttpRequestMessage(HttpMethod.Get, $"{DefaultApiOrigin}/api/monitor/usage/quota/limit");
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {account.ApiKey}");

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
            return ZCodeCodingPlanParser.Parse(json, account.AccountId, BeijingClock.Now);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Per-request timeout only: the plan is simply missing this cycle.
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
    /// Credentials store the key as
    /// <c>account-provider:coding-plan:account:bigmodel-&lt;plan&gt;:account:&lt;id&gt;:api-key</c>.
    /// The account id becomes the plan's stable selection key.
    /// </summary>
    internal static ZCodeCodingPlanAccount? LocateCodingPlanAccount(string? credentialsPath)
    {
        try
        {
            if (string.IsNullOrEmpty(credentialsPath) || !File.Exists(credentialsPath))
            {
                return null;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(credentialsPath));
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            foreach (var property in document.RootElement.EnumerateObject())
            {
                var name = property.Name;
                if (!name.StartsWith(CredentialKeyPrefix, StringComparison.Ordinal) ||
                    !name.EndsWith(CredentialKeySuffix, StringComparison.Ordinal))
                {
                    continue;
                }

                if (property.Value.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var apiKey = ZCodeCredentialProtector.Decrypt(property.Value.GetString());
                if (string.IsNullOrWhiteSpace(apiKey))
                {
                    continue;
                }

                // account-provider:coding-plan:account:<plan>:account:<id>:api-key
                var segments = name.Split(':');
                var accountId = segments.Length >= 6 ? segments[5] : PlanId;
                return new ZCodeCodingPlanAccount(accountId, apiKey);
            }

            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// Parses the <c>/api/monitor/usage/quota/limit</c> envelope:
/// <c>{code:200, data:{level, limits:[{unit, number, usage, remaining, nextResetTime…}]}}</c>.
/// Each limit is one percentage window (unit 3 = hours, 6 = week); usage and
/// remaining are scaled so 10000 ≙ 100%. A "no coding plan" account parses to
/// null, which the caller treats as "plan absent".
/// </summary>
internal static class ZCodeCodingPlanParser
{
    public static ZCodeQuotaPlan? Parse(string json, string accountId, DateTimeOffset snapshotLocal)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("data", out var data) ||
                data.ValueKind != JsonValueKind.Object ||
                !data.TryGetProperty("limits", out var limits) ||
                limits.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var windows = new List<(long Unit, long Number, long Total, long Remaining, DateTimeOffset? ResetAt)>();
            foreach (var limit in limits.EnumerateArray())
            {
                if (limit.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (limit.TryGetProperty("type", out var type) &&
                    type.ValueKind == JsonValueKind.String &&
                    !string.Equals(type.GetString(), "CREDIT_LIMIT", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var total = GetInt64(limit, "usage");
                var remaining = GetInt64(limit, "remaining");
                if (total <= 0 && remaining <= 0)
                {
                    continue;
                }

                windows.Add((
                    GetInt64(limit, "unit"),
                    GetInt64(limit, "number"),
                    total > 0 ? total : remaining,
                    Math.Max(0, remaining),
                    ReadUnixMillis(limit, "nextResetTime")));
            }

            // Shortest window first: the rolling 5-hour window is the headline
            // (Codex-style), the weekly window rides along in the detail line.
            var balances = windows
                .OrderBy(item => item.Unit)
                .ThenBy(item => item.Number)
                .Select(item => new ZCodeQuotaBalance(
                    WindowLabel(item.Unit, item.Number),
                    $"coding-plan-{item.Unit}-{item.Number}",
                    item.Total,
                    Math.Max(0, item.Total - item.Remaining),
                    item.Remaining,
                    item.Remaining,
                    item.ResetAt,
                    PeriodStartLocal: null,
                    PeriodEndLocal: item.ResetAt,
                    PlanId: ZCodeCodingPlanReaderPlanId,
                    UserPlanId: accountId,
                    IsWindowScaled: true))
                .ToList();

            if (balances.Count == 0)
            {
                return null;
            }

            return new ZCodeQuotaPlan(
                ZCodeCodingPlanReaderPlanId,
                accountId,
                PlanDisplayName(GetString(data, "level")),
                "BigModel 编码套餐 · 按窗口百分比计量",
                "active",
                StartsAtLocal: null,
                EndsAtLocal: null,
                balances);
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

    private const string ZCodeCodingPlanReaderPlanId = "bigmodel-individual-coding-plan";

    internal static string WindowLabel(long unit, long number) => unit switch
    {
        2 => number == 1 ? "每分钟窗口" : $"{number} 分钟窗口",
        3 => number == 1 ? "每小时窗口" : $"{number} 小时窗口",
        4 => number == 1 ? "每日窗口" : $"{number} 天窗口",
        6 => "每周窗口",
        _ => $"窗口（unit {unit} × {number}）"
    };

    private static string PlanDisplayName(string? level) => level?.Trim().ToLowerInvariant() switch
    {
        "lite" => "GLM Coding Lite",
        "pro" => "GLM Coding Pro",
        "max" => "GLM Coding Max",
        null or "" => "GLM Coding Plan",
        var other => $"GLM Coding ({other})"
    };

    private static DateTimeOffset? ReadUnixMillis(JsonElement element, string propertyName)
    {
        var value = GetInt64OrNull(element, propertyName);
        if (value is not { } stamp || stamp <= 0)
        {
            return null;
        }

        // The endpoint answers in milliseconds; tolerate second-precision servers.
        return stamp >= 1_000_000_000_000
            ? DateTimeOffset.FromUnixTimeMilliseconds(stamp).ToOffset(CodexUsageReader.BeijingOffset)
            : DateTimeOffset.FromUnixTimeSeconds(stamp).ToOffset(CodexUsageReader.BeijingOffset);
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
