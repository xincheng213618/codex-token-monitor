using System.Globalization;
using System.Text.Json;

namespace CodexTokenMonitor;

internal enum DshBalanceFailureKind
{
    NotSignedIn,
    HttpError,
    NetworkError,
    ParseError,
    Unknown
}

internal sealed record DshBalanceFailure(
    DshBalanceFailureKind Kind,
    int? StatusCode = null,
    string? Message = null);

/// <summary>One Platform wallet balance, kept as the server's decimal string.</summary>
internal sealed record DshWallet(string Currency, decimal Amount);

/// <summary>
/// The DeepSeek account wallet state behind the dsh account: the recharge
/// wallets, the separate bonus (gift) wallets, and the account's lifetime cost
/// the Platform summary reports alongside them.
/// </summary>
internal sealed record DshAccountBalance(
    IReadOnlyList<DshWallet> RechargeWallets,
    IReadOnlyList<DshWallet> BonusWallets,
    IReadOnlyList<DshWallet> TotalCosts,
    DateTimeOffset SnapshotLocal)
{
    /// <summary>The wallet the panel headlines: CNY when present, else the first.</summary>
    public DshWallet? PrimaryRecharge =>
        RechargeWallets.FirstOrDefault(wallet => wallet.Currency == "CNY") ?? RechargeWallets.FirstOrDefault();

    public DshWallet? PrimaryBonus =>
        BonusWallets.FirstOrDefault(wallet => wallet.Currency == "CNY") ?? BonusWallets.FirstOrDefault();

    public DshWallet? PrimaryCost =>
        TotalCosts.FirstOrDefault(wallet => wallet.Currency == "CNY") ?? TotalCosts.FirstOrDefault();
}

internal sealed record DshBalanceReadResult(
    DshAccountBalance? Balance,
    DshBalanceFailure? Failure);

/// <summary>
/// Reads the DeepSeek account balance that the dsh desktop app shows in its
/// account settings, straight from the Platform summary endpoint the app uses:
///
///   GET {platformOrigin}/api/v0/users/get_user_summary
///   x-dsh-auth-token: &lt;stored grant&gt;
///
/// The only source of the grant is dsh's own credential document
/// (<c>~/.dsh/.credentials.yaml</c>), so a signed-in dsh install is required;
/// the token stays in memory and is never logged or persisted by the monitor.
/// Bonus wallets are deliberately kept apart from recharge balances, exactly as
/// the Platform API reports them.
/// </summary>
internal sealed class DshBalanceReader
{
    public static readonly DshBalanceReader Shared = new();

    private const string DefaultPlatformOrigin = "https://platform.deepseek.com";
    private const string SummaryPath = "/api/v0/users/get_user_summary";
    private const string CredentialOwner = "deepseek-account-platform/default";
    private static readonly TimeSpan SuccessCacheDuration = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan FailureCacheDuration = TimeSpan.FromSeconds(30);

    private readonly object syncRoot = new();
    private readonly Func<string> locateCredentialsFile;
    private readonly Func<string> platformOrigin;
    private readonly Func<HttpMessageInvoker> createHttpSender;
    private readonly TimeSpan requestTimeout;
    private HttpMessageInvoker? cachedHttpSender;
    private DateTimeOffset lastAttemptLocal = DateTimeOffset.MinValue;
    private DshAccountBalance? cachedBalance;
    private DshBalanceFailure? cachedFailure;

    public DshBalanceReader(
        Func<string>? locateCredentialsFile = null,
        Func<string>? platformOrigin = null,
        Func<HttpMessageInvoker>? createHttpSender = null,
        TimeSpan? requestTimeout = null)
    {
        this.locateCredentialsFile = locateCredentialsFile ?? LocateCredentialsFile;
        this.platformOrigin = platformOrigin ?? LocatePlatformOrigin;
        this.createHttpSender = createHttpSender ?? (() => new HttpClient(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(10)
        }));
        this.requestTimeout = requestTimeout ?? DefaultRequestTimeout;
    }

    /// <summary>
    /// A property on purpose: the <see cref="Shared"/> singleton initializes
    /// before the other static fields of this type.
    /// </summary>
    private static TimeSpan DefaultRequestTimeout => TimeSpan.FromSeconds(15);

    /// <summary>Effective per-request timeout, surfaced for tests and diagnostics.</summary>
    internal TimeSpan RequestTimeout => requestTimeout;

    public DshAccountBalance? ReadCurrent(CancellationToken cancellationToken = default)
    {
        return ReadCurrentResult(cancellationToken).Balance;
    }

    /// <summary>
    /// Read with the cache window applied; the failure explains a null balance
    /// so callers can distinguish "not signed in" from a transient outage.
    /// </summary>
    public DshBalanceReadResult ReadCurrentResult(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var lockTaken = false;
        try
        {
            while (!(lockTaken = Monitor.TryEnter(syncRoot, millisecondsTimeout: 100)))
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var nowLocal = BeijingClock.Now;
            if (nowLocal - lastAttemptLocal < CacheDuration())
            {
                return new DshBalanceReadResult(cachedBalance, cachedFailure);
            }

            lastAttemptLocal = nowLocal;
            try
            {
                var fresh = ReadCurrentUncached(cancellationToken);
                cachedBalance = fresh.Balance;
                cachedFailure = fresh.Failure;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                cachedBalance = null;
                cachedFailure = new DshBalanceFailure(DshBalanceFailureKind.Unknown, Message: ex.Message);
            }

            return new DshBalanceReadResult(cachedBalance, cachedFailure);
        }
        finally
        {
            if (lockTaken)
            {
                Monitor.Exit(syncRoot);
            }
        }
    }

    /// <summary>
    /// Forgets the cached attempt so the next read hits the network again; the
    /// panel's manual refresh uses this to bypass the success window.
    /// </summary>
    internal void ResetCache()
    {
        lock (syncRoot)
        {
            lastAttemptLocal = DateTimeOffset.MinValue;
            cachedBalance = null;
            cachedFailure = null;
        }
    }

    private TimeSpan CacheDuration()
    {
        return cachedBalance is not null ? SuccessCacheDuration : FailureCacheDuration;
    }

    private DshBalanceReadResult ReadCurrentUncached(CancellationToken cancellationToken)
    {
        string credentialsText;
        try
        {
            var path = locateCredentialsFile();
            if (!File.Exists(path))
            {
                return new DshBalanceReadResult(null, new DshBalanceFailure(DshBalanceFailureKind.NotSignedIn));
            }

            credentialsText = File.ReadAllText(path);
        }
        catch (Exception ex)
        {
            return new DshBalanceReadResult(
                null,
                new DshBalanceFailure(DshBalanceFailureKind.Unknown, Message: ex.Message));
        }

        var origin = platformOrigin();
        var grant = ParseCredentialStore(credentialsText, CredentialOwner);
        if (grant is null || string.IsNullOrWhiteSpace(grant.Token))
        {
            return new DshBalanceReadResult(null, new DshBalanceFailure(DshBalanceFailureKind.NotSignedIn));
        }

        // dsh deletes a stored grant whose issuer is not the configured origin;
        // mirroring that keeps a stale credential from being sent elsewhere.
        if (!string.IsNullOrWhiteSpace(grant.Issuer) &&
            !string.Equals(grant.Issuer.TrimEnd('/'), origin.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
        {
            return new DshBalanceReadResult(null, new DshBalanceFailure(
                DshBalanceFailureKind.ParseError,
                Message: "凭据签发方与平台地址不一致"));
        }

        var request = new HttpRequestMessage(HttpMethod.Get, origin.TrimEnd('/') + SummaryPath);
        request.Headers.TryAddWithoutValidation("x-dsh-auth-token", grant.Token);
        request.Headers.TryAddWithoutValidation("accept", "application/json");

        HttpResponseMessage response;
        // A hung connection must not occupy the panel's refresh slot for the
        // HttpClient default of 100 seconds.
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
            return new DshBalanceReadResult(null, new DshBalanceFailure(
                DshBalanceFailureKind.NetworkError,
                Message: $"请求超时（{requestTimeout.TotalSeconds:N0} 秒）"));
        }
        catch (Exception ex)
        {
            request.Dispose();
            return new DshBalanceReadResult(
                null,
                new DshBalanceFailure(DshBalanceFailureKind.NetworkError, Message: ex.Message));
        }

        using (request)
        using (response)
        {
            string json;
            try
            {
                json = response.Content.ReadAsStringAsync(timeoutCts.Token).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return new DshBalanceReadResult(
                    null,
                    new DshBalanceFailure(DshBalanceFailureKind.NetworkError, Message: ex.Message));
            }

            if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized)
            {
                return new DshBalanceReadResult(null, new DshBalanceFailure(DshBalanceFailureKind.NotSignedIn));
            }

            if (!response.IsSuccessStatusCode)
            {
                return new DshBalanceReadResult(null, new DshBalanceFailure(
                    DshBalanceFailureKind.HttpError,
                    (int)response.StatusCode));
            }

            var balance = DshBalanceParser.Parse(json, BeijingClock.Now);
            return balance is null
                ? new DshBalanceReadResult(null, new DshBalanceFailure(DshBalanceFailureKind.ParseError))
                : new DshBalanceReadResult(balance, null);
        }
    }

    private HttpMessageInvoker GetHttpSender()
    {
        lock (syncRoot)
        {
            return cachedHttpSender ??= createHttpSender();
        }
    }

    /// <summary>The dsh harness home credential document.</summary>
    internal static string LocateCredentialsFile()
    {
        var home = Environment.GetEnvironmentVariable("DSH_HOME");
        if (!string.IsNullOrWhiteSpace(home))
        {
            return Path.Combine(home, ".credentials.yaml");
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".dsh",
            ".credentials.yaml");
    }

    internal static string LocatePlatformOrigin()
    {
        var configured = Environment.GetEnvironmentVariable("DSH_PLATFORM_ORIGIN");
        return string.IsNullOrWhiteSpace(configured) ? DefaultPlatformOrigin : configured.Trim();
    }

    /// <summary>
    /// Reads one grant record out of the dsh credential document. The document
    /// is a small credential-only YAML file (<c>version</c>/<c>refs</c>/
    /// <c>records</c>, records addressed by <c>&lt;owner&gt;/&lt;id&gt;</c>), so
    /// only the two scalar fields this reader needs are extracted; an
    /// unrecognized document simply reports "not signed in".
    /// </summary>
    internal static DshCredentialGrant? ParseCredentialStore(string text, string owner)
    {
        ArgumentNullException.ThrowIfNull(text);

        var lines = text.Split('\n');
        var inRecords = false;
        var currentOwner = "";
        var token = "";
        var issuer = "";
        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Length == 0 || line.TrimStart().StartsWith('#'))
            {
                continue;
            }

            var indent = line.Length - line.TrimStart(' ').Length;
            var trimmed = line.Trim();
            if (indent == 0)
            {
                inRecords = string.Equals(trimmed, "records:", StringComparison.Ordinal);
                currentOwner = "";
                continue;
            }

            if (!inRecords || trimmed.StartsWith("- ", StringComparison.Ordinal))
            {
                continue;
            }

            var separator = trimmed.IndexOf(':');
            if (separator <= 0)
            {
                continue;
            }

            var key = trimmed[..separator].Trim();
            var value = trimmed[(separator + 1)..].Trim().Trim('"', '\'');
            if (indent == 2 && value.Length == 0)
            {
                currentOwner = key;
                continue;
            }

            if (!string.Equals(currentOwner, owner, StringComparison.Ordinal))
            {
                continue;
            }

            if (value.Length == 0)
            {
                continue;
            }

            if (string.Equals(key, "token", StringComparison.Ordinal))
            {
                token = value;
            }
            else if (string.Equals(key, "issuer", StringComparison.Ordinal))
            {
                issuer = value;
            }
        }

        return token.Length == 0 && issuer.Length == 0 ? null : new DshCredentialGrant(token, issuer);
    }
}

/// <summary>One stored dsh Platform grant: the bearer token and its issuer origin.</summary>
internal sealed record DshCredentialGrant(string Token, string Issuer);

/// <summary>
/// Parses the Platform <c>get_user_summary</c> envelope. Kept pure so tests can
/// pin the server contract without network access.
/// </summary>
internal static class DshBalanceParser
{
    public static DshAccountBalance? Parse(string json, DateTimeOffset snapshotLocal)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!root.TryGetProperty("code", out var code) ||
                code.ValueKind != JsonValueKind.Number ||
                !code.TryGetInt32(out var codeValue) ||
                codeValue != 0)
            {
                return null;
            }

            if (!root.TryGetProperty("data", out var data) ||
                data.ValueKind != JsonValueKind.Object ||
                !data.TryGetProperty("biz_code", out var bizCode) ||
                bizCode.ValueKind != JsonValueKind.Number ||
                !bizCode.TryGetInt32(out var bizCodeValue) ||
                bizCodeValue != 0 ||
                !data.TryGetProperty("biz_data", out var bizData) ||
                bizData.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            return new DshAccountBalance(
                ReadWallets(bizData, "normal_wallets"),
                ReadWallets(bizData, "bonus_wallets"),
                ReadWallets(bizData, "total_costs"),
                snapshotLocal);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static IReadOnlyList<DshWallet> ReadWallets(JsonElement bizData, string propertyName)
    {
        if (!bizData.TryGetProperty(propertyName, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var wallets = new List<DshWallet>();
        foreach (var element in array.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object ||
                !element.TryGetProperty("currency", out var currencyElement) ||
                currencyElement.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var currency = currencyElement.GetString();
            if (string.IsNullOrWhiteSpace(currency))
            {
                continue;
            }

            // Platform reports the recharge balance as `balance` and the
            // lifetime cost as `amount`; both are decimal strings.
            var amountText = ReadDecimalString(element, "balance") ?? ReadDecimalString(element, "amount");
            if (amountText is null ||
                !decimal.TryParse(amountText, NumberStyles.Float, CultureInfo.InvariantCulture, out var amount))
            {
                continue;
            }

            wallets.Add(new DshWallet(currency.Trim(), amount));
        }

        return wallets;
    }

    private static string? ReadDecimalString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null
        };
    }
}
