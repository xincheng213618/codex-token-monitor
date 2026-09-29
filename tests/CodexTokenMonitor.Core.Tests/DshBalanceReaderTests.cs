using System.Net;
using System.Net.Http;
using System.Text;
using Xunit;

namespace CodexTokenMonitor.Tests;

public sealed class DshBalanceReaderTests
{
    private static readonly DateTimeOffset Snapshot =
        new(2026, 9, 30, 2, 10, 0, TimeSpan.FromHours(8));

    private const string CredentialDocument = """
        version: 1
        refs:
          DEEPSEEK_API_KEY: sk-example
        records:
          client-connection/browser-session:
            kind: grant
            payload:
              version: 1
            secret: browser-secret

          deepseek-account-platform/default:
            kind: grant
            payload:
              version: 1
              token: stored-grant-token
              issuer: https://platform.deepseek.com
        """;

    private const string SummaryJson = """
        {"code":0,"msg":"","data":{"biz_code":0,"biz_msg":"","biz_data":{
          "normal_wallets":[{"currency":"CNY","balance":"17.6895218000000000","token_estimation":"0"}],
          "bonus_wallets":[{"currency":"CNY","balance":"2.0976498400000000","token_estimation":"0"}],
          "total_costs":[{"currency":"CNY","amount":"36.2972123600000000"}]}}}
        """;

    [Fact]
    public void ParsesStoredGrantFromCredentialDocument()
    {
        var grant = DshBalanceReader.ParseCredentialStore(CredentialDocument, "deepseek-account-platform/default");

        Assert.NotNull(grant);
        Assert.Equal("stored-grant-token", grant!.Token);
        Assert.Equal("https://platform.deepseek.com", grant.Issuer);
    }

    [Fact]
    public void MissingGrantIsNotSignedIn()
    {
        Assert.Null(DshBalanceReader.ParseCredentialStore("version: 1\nrefs:\nrecords:\n", "deepseek-account-platform/default"));
    }

    [Fact]
    public void AnotherOwnersGrantIsNotUsed()
    {
        var grant = DshBalanceReader.ParseCredentialStore(
            CredentialDocument,
            "deepseek-account-platform/other");

        Assert.Null(grant);
    }

    [Fact]
    public void ParsesWalletBucketsFromPlatformSummary()
    {
        var balance = DshBalanceParser.Parse(SummaryJson, Snapshot);

        Assert.NotNull(balance);
        Assert.Equal(17.6895218000000000m, balance!.PrimaryRecharge!.Amount);
        Assert.Equal("CNY", balance.PrimaryRecharge.Currency);
        Assert.Equal(2.0976498400000000m, balance.PrimaryBonus!.Amount);
        Assert.Equal(36.2972123600000000m, balance.PrimaryCost!.Amount);
        Assert.Equal(Snapshot, balance.SnapshotLocal);
    }

    [Fact]
    public void KeepsBonusWalletsSeparateFromRechargeBalances()
    {
        const string json = """
            {"code":0,"data":{"biz_code":0,"biz_data":{
              "normal_wallets":[{"currency":"USD","balance":"1.5"},{"currency":"CNY","balance":"9.25"}],
              "bonus_wallets":[{"currency":"USD","balance":"4.00"}]}}}
            """;

        var balance = DshBalanceParser.Parse(json, Snapshot);

        Assert.NotNull(balance);
        // CNY is the panel headline when both currencies exist, but every
        // wallet stays addressable.
        Assert.Equal("CNY", balance!.PrimaryRecharge!.Currency);
        Assert.Equal(2, balance.RechargeWallets.Count);
        Assert.Equal("USD", Assert.Single(balance.BonusWallets).Currency);
        Assert.Empty(balance.TotalCosts);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"code\":40003,\"msg\":\"Authorization Failed (invalid token)\",\"data\":null}")]
    [InlineData("{\"code\":0,\"data\":{\"biz_code\":1,\"biz_data\":{}}}")]
    [InlineData("{\"code\":0,\"data\":{\"biz_code\":0}}")]
    public void RejectsUnusableEnvelopes(string json)
    {
        Assert.Null(DshBalanceParser.Parse(json, Snapshot));
    }

    [Fact]
    public void SkipsWalletsWithUnparsableAmounts()
    {
        const string json = """
            {"code":0,"data":{"biz_code":0,"biz_data":{
              "normal_wallets":[{"currency":"CNY","balance":"NaN"},{"currency":"USD","balance":"0E-16"}]}}}
            """;

        var balance = DshBalanceParser.Parse(json, Snapshot);

        Assert.NotNull(balance);
        var wallet = Assert.Single(balance!.RechargeWallets);
        Assert.Equal("USD", wallet.Currency);
        Assert.Equal(0m, wallet.Amount);
    }

    [Fact]
    public void ReadsBalanceThroughTheStoredGrant()
    {
        using var handler = new RecordingHandler(SummaryJson, HttpStatusCode.OK);
        var reader = CreateReader(handler, CredentialDocument);

        var result = reader.ReadCurrentResult();

        Assert.Null(result.Failure);
        Assert.Equal(17.6895218000000000m, result.Balance!.PrimaryRecharge!.Amount);
        Assert.Equal("/api/v0/users/get_user_summary", handler.LastRequestUri!.AbsolutePath);
        Assert.Equal("stored-grant-token", handler.LastAuthToken);
    }

    [Fact]
    public void ReportsNotSignedInWithoutCredentialDocument()
    {
        using var handler = new RecordingHandler(SummaryJson, HttpStatusCode.OK);
        var reader = CreateReader(handler, document: null);

        var result = reader.ReadCurrentResult();

        Assert.Null(result.Balance);
        Assert.Equal(DshBalanceFailureKind.NotSignedIn, result.Failure!.Kind);
        Assert.Null(handler.LastRequestUri);
    }

    [Fact]
    public void ReportsNotSignedInWhenPlatformRejectsTheGrant()
    {
        using var handler = new RecordingHandler(
            "{\"code\":40003,\"msg\":\"Authorization Failed (invalid token)\",\"data\":null}",
            HttpStatusCode.Unauthorized);
        var reader = CreateReader(handler, CredentialDocument);

        var result = reader.ReadCurrentResult();

        Assert.Null(result.Balance);
        Assert.Equal(DshBalanceFailureKind.NotSignedIn, result.Failure!.Kind);
    }

    [Fact]
    public void DoesNotSendAGrantIssuedForAnotherOrigin()
    {
        using var handler = new RecordingHandler(SummaryJson, HttpStatusCode.OK);
        var document = CredentialDocument.Replace(
            "issuer: https://platform.deepseek.com",
            "issuer: https://platform.example.com",
            StringComparison.Ordinal);
        var reader = CreateReader(handler, document);

        var result = reader.ReadCurrentResult();

        Assert.Null(result.Balance);
        Assert.Equal(DshBalanceFailureKind.ParseError, result.Failure!.Kind);
        Assert.Null(handler.LastRequestUri);
    }

    [Fact]
    public void ReportsHttpFailuresWithStatusCode()
    {
        using var handler = new RecordingHandler("{}", HttpStatusCode.BadGateway);
        var reader = CreateReader(handler, CredentialDocument);

        var result = reader.ReadCurrentResult();

        Assert.Null(result.Balance);
        Assert.Equal(DshBalanceFailureKind.HttpError, result.Failure!.Kind);
        Assert.Equal(502, result.Failure.StatusCode);
    }

    [Fact]
    public void ReusesTheCachedReadWithinTheSuccessWindow()
    {
        using var handler = new RecordingHandler(SummaryJson, HttpStatusCode.OK);
        var reader = CreateReader(handler, CredentialDocument);

        var first = reader.ReadCurrentResult();
        var second = reader.ReadCurrentResult();

        Assert.NotNull(first.Balance);
        Assert.Same(first.Balance, second.Balance);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public void CancellationStopsTheRead()
    {
        using var handler = new RecordingHandler(SummaryJson, HttpStatusCode.OK);
        var reader = CreateReader(handler, CredentialDocument);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => reader.ReadCurrentResult(cancellation.Token));
    }

    [Fact]
    public void BoundsTheRequestTimeout()
    {
        Assert.Equal(TimeSpan.FromSeconds(15), DshBalanceReader.Shared.RequestTimeout);
    }

    private static DshBalanceReader CreateReader(RecordingHandler handler, string? document)
    {
        var path = Path.Combine(Path.GetTempPath(), $"DshBalance-{Guid.NewGuid():N}.yaml");
        if (document is not null)
        {
            File.WriteAllText(path, document, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        return new DshBalanceReader(
            locateCredentialsFile: () => path,
            platformOrigin: () => "https://platform.deepseek.com",
            createHttpSender: () => new HttpClient(handler),
            requestTimeout: TimeSpan.FromSeconds(5));
    }

    private sealed class RecordingHandler(string body, HttpStatusCode status) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        public Uri? LastRequestUri { get; private set; }

        public string? LastAuthToken { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            LastRequestUri = request.RequestUri;
            LastAuthToken = request.Headers.TryGetValues("x-dsh-auth-token", out var values)
                ? values.FirstOrDefault()
                : null;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }
}
