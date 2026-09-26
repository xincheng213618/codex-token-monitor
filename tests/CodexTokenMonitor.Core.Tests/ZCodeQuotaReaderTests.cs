using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace CodexTokenMonitor.Tests;

public sealed class ZCodeQuotaReaderTests
{
    private static readonly TimeSpan Beijing = TimeSpan.FromHours(8);
    private static readonly DateTimeOffset SnapshotTime = new(2026, 9, 19, 16, 0, 0, Beijing);

    private const string BalanceEnvelope = """
        {"code":0,"msg":"","data":{"server_time":1789806133,"plans":[{"user_plan_id":"upl_1","plan_id":"zcode-v3-start-plan-wk-0918","name":"ZCode Weekend Build","description":"ZCode 周末活动","priority":101,"status":"active","starts_at":1789730476,"ends_at":1789866000,"entitlements":[{"entitlement_id":"ent-1","show_name":"GLM-5.3-Flash","meter":"model_usage","unit_type":"token","capabilities":["model:glm-5.3-flash"],"grant_units":300000000,"period":"one_time","priority":110,"effective_at":1789743600}]}],"balances":[{"bucket_id":"bucket_1","user_plan_id":"upl_1","plan_id":"zcode-v3-start-plan-wk-0918","entitlement_id":"ent-1","show_name":"GLM-5.3-Flash","meter":"model_usage","unit_type":"token","capabilities":["model:glm-5.3-flash"],"priority":110,"plan_priority":101,"entitlement_priority":110,"total_units":300000000,"used_units":284685448,"remaining_units":15314552,"available_units":15314552,"period_start":1789730476,"period_end":1789866000,"expires_at":1789866000}]}}
        """;

    [Fact]
    public void Parse_ReadsActivePlanAndBalanceFields()
    {
        var snapshot = ZCodeQuotaParser.Parse(BalanceEnvelope, SnapshotTime);

        Assert.NotNull(snapshot);
        Assert.Equal("zcode-v3-start-plan-wk-0918", snapshot.PlanId);
        Assert.Equal("ZCode Weekend Build", snapshot.PlanName);
        Assert.Equal("ZCode 周末活动", snapshot.PlanDescription);
        Assert.Equal("active", snapshot.PlanStatus);
        Assert.Equal(SnapshotTime, snapshot.SnapshotLocal);
        Assert.Equal(
            DateTimeOffset.FromUnixTimeSeconds(1789806133).ToOffset(Beijing),
            snapshot.ServerTimeLocal);

        var balance = Assert.Single(snapshot.Balances);
        Assert.Equal("GLM-5.3-Flash", balance.ModelName);
        Assert.Equal("glm-5.3-flash", balance.ModelId);
        Assert.Equal(300_000_000, balance.TotalUnits);
        Assert.Equal(284_685_448, balance.UsedUnits);
        Assert.Equal(15_314_552, balance.RemainingUnits);
        Assert.Equal(15_314_552, balance.AvailableUnits);
        Assert.Equal(
            DateTimeOffset.FromUnixTimeSeconds(1789866000).ToOffset(Beijing),
            balance.ExpiresAtLocal);
        Assert.Equal(94.9m, balance.UsedPercent);
        Assert.Same(balance, snapshot.PrimaryBalance);
    }

    [Fact]
    public void Parse_SelectsRemainingBalanceAsPrimaryAndOrdersByRemaining()
    {
        var response = """
            {"code":0,"data":{"server_time":100,"plans":[{"plan_id":"p","name":"Plan","status":"active"}],"balances":[
                {"show_name":"A","total_units":100,"used_units":100,"remaining_units":0},
                {"show_name":"B","total_units":200,"used_units":150,"remaining_units":50}
            ]}}
            """;

        var snapshot = ZCodeQuotaParser.Parse(response, SnapshotTime);

        Assert.NotNull(snapshot);
        Assert.Equal(2, snapshot.Balances.Count);
        Assert.Equal("B", snapshot.PrimaryBalance!.ModelName);
        Assert.True(snapshot.Balances[0].RemainingUnits >= snapshot.Balances[1].RemainingUnits);
    }

    [Fact]
    public void Parse_RejectsFailedEnvelopeAndMissingBalances()
    {
        Assert.Null(ZCodeQuotaParser.Parse("{\"code\":3001,\"msg\":\"parameter error\"}", SnapshotTime));
        Assert.Null(ZCodeQuotaParser.Parse("{\"code\":0,\"data\":{\"plans\":[],\"balances\":[]}}", SnapshotTime));
        Assert.Null(ZCodeQuotaParser.Parse("invalid", SnapshotTime));
        Assert.Null(ZCodeQuotaParser.Parse("{}", SnapshotTime));
    }

    [Fact]
    public void Parse_TreatsInactivePlanAsFallbackWithoutDroppingBalances()
    {
        var response = """
            {"code":0,"data":{"plans":[{"plan_id":"p2","name":"Upcoming","status":"pending"}],"balances":[
                {"show_name":"M","total_units":10,"used_units":1,"remaining_units":9}
            ]}}
            """;

        var snapshot = ZCodeQuotaParser.Parse(response, SnapshotTime);

        Assert.NotNull(snapshot);
        Assert.Equal("p2", snapshot.PlanId);
        Assert.Single(snapshot.Balances);
    }

    [Fact]
    public void Decrypt_RoundTripsDesktopEncryptionFormat()
    {
        const string secret = "zcode-credential-test-secret";
        var key = SHA256.HashData(Encoding.UTF8.GetBytes(secret));
        var nonce = new byte[12] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 };
        var plaintext = "test-token-value"u8.ToArray();

        using var aes = new AesGcm(key, 16);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];
        aes.Encrypt(nonce, plaintext, ciphertext, tag);
        var stored = "enc:v1:" + ToBase64Url(nonce) + "." + ToBase64Url(tag) + "." + ToBase64Url(ciphertext);

        Assert.Equal("test-token-value", ZCodeCredentialProtector.DecryptWithSecret(stored, secret));
    }

    [Fact]
    public void Decrypt_PassesPlaintextThroughAndRejectsGarbage()
    {
        Assert.Equal("plain-value", ZCodeCredentialProtector.Decrypt("plain-value"));
        Assert.Null(ZCodeCredentialProtector.Decrypt("enc:v1:not-three-parts"));
        Assert.Null(ZCodeCredentialProtector.Decrypt("enc:v1:...."));
        Assert.Null(ZCodeCredentialProtector.Decrypt(null));
    }

    [Fact]
    public void BuildBalanceUrl_EscapesAppVersion()
    {
        Assert.Equal(
            "https://zcode.z.ai/api/v1/zcode-plan/billing/balance?app_version=3.12.3",
            ZCodeQuotaReader.BuildBalanceUrl("3.12.3"));
        Assert.EndsWith("app_version=unknown", ZCodeQuotaReader.BuildBalanceUrl("unknown"));
    }

    [Fact]
    public void ReadCurrent_ThrowsImmediatelyWhenCancelled()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => ZCodeQuotaReader.Shared.ReadCurrent(cancellation.Token));
    }

    [Fact]
    public void ReadCurrentResult_ClassifiesMissingCredentialsAsNotSignedIn()
    {
        var reader = new ZCodeQuotaReader(
            locateCredentialsFile: () => Path.Combine(Path.GetTempPath(), "missing-zcode-credentials.json"),
            locateDeviceMidFile: () => null,
            locateAppVersion: () => "3.12.3",
            createHttpSender: () => throw new InvalidOperationException("network must not be reached"));

        var result = reader.ReadCurrentResult();

        Assert.Null(result.Snapshot);
        Assert.Equal(ZCodeQuotaFailureKind.NotSignedIn, result.Failure!.Kind);
        Assert.Null(reader.ReadCurrent());
    }

    [Fact]
    public void ReadCurrentResult_ClassifiesHttpErrorWithStatusCode()
    {
        var credentialsPath = WriteTempCredentials();
        try
        {
            using var handler = new StubHttpHandler(() => new HttpResponseMessage(HttpStatusCode.InternalServerError));
            var reader = CreateReaderWithCredentials(handler, credentialsPath);

            var result = reader.ReadCurrentResult();

            Assert.Null(result.Snapshot);
            Assert.Equal(ZCodeQuotaFailureKind.HttpError, result.Failure!.Kind);
            Assert.Equal(500, result.Failure.StatusCode);
        }
        finally
        {
            File.Delete(credentialsPath);
        }
    }

    [Fact]
    public void ReadCurrentResult_ClassifiesBadEnvelopeAsParseError()
    {
        var credentialsPath = WriteTempCredentials();
        try
        {
            using var handler = new StubHttpHandler(
                () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
            var reader = CreateReaderWithCredentials(handler, credentialsPath);

            var result = reader.ReadCurrentResult();

            Assert.Null(result.Snapshot);
            Assert.Equal(ZCodeQuotaFailureKind.ParseError, result.Failure!.Kind);
        }
        finally
        {
            File.Delete(credentialsPath);
        }
    }

    [Fact]
    public void ReadCurrentResult_ClassifiesTransportExceptionAsNetworkError()
    {
        var credentialsPath = WriteTempCredentials();
        try
        {
            using var handler = new ThrowingHttpHandler(new HttpRequestException("connection refused"));
            var reader = CreateReaderWithCredentials(handler, credentialsPath);

            var result = reader.ReadCurrentResult();

            Assert.Null(result.Snapshot);
            Assert.Equal(ZCodeQuotaFailureKind.NetworkError, result.Failure!.Kind);
        }
        finally
        {
            File.Delete(credentialsPath);
        }
    }

    [Fact]
    public void ReadCurrentResult_ReusesStaleSnapshotWhenRateLimited()
    {
        var credentialsPath = WriteTempCredentials();
        try
        {
            var statuses = new Queue<HttpStatusCode>(new[] { HttpStatusCode.OK, HttpStatusCode.TooManyRequests });
            using var handler = new StubHttpHandler(() => new HttpResponseMessage(statuses.Dequeue())
            {
                Content = new StringContent(BalanceEnvelope)
            });
            var reader = CreateReaderWithCredentials(handler, credentialsPath);

            var first = reader.ReadCurrentResult();
            reader.ResetCacheForTests();
            var second = reader.ReadCurrentResult();

            Assert.NotNull(first.Snapshot);
            Assert.Same(first.Snapshot, second.Snapshot);
            Assert.Null(second.Failure);
            Assert.Equal(2, handler.CallCount);
        }
        finally
        {
            File.Delete(credentialsPath);
        }
    }

    [Fact]
    public void ReadCurrentResult_CachesFailureAndReusesSender()
    {
        var credentialsPath = WriteTempCredentials();
        try
        {
            using var handler = new StubHttpHandler(() => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            var senderCreations = 0;
            var reader = new ZCodeQuotaReader(
                locateCredentialsFile: () => credentialsPath,
                locateDeviceMidFile: () => "device-mid",
                locateAppVersion: () => "3.12.3",
                createHttpSender: () =>
                {
                    senderCreations++;
                    return new HttpClient(handler);
                });

            var first = reader.ReadCurrentResult();
            var second = reader.ReadCurrentResult();

            Assert.Equal(ZCodeQuotaFailureKind.HttpError, first.Failure!.Kind);
            Assert.Same(first.Failure, second.Failure);
            Assert.Equal(1, handler.CallCount);
            Assert.Equal(1, senderCreations);
        }
        finally
        {
            File.Delete(credentialsPath);
        }
    }

    private static ZCodeQuotaReader CreateReaderWithCredentials(HttpMessageHandler handler, string credentialsPath)
    {
        return new ZCodeQuotaReader(
            locateCredentialsFile: () => credentialsPath,
            locateDeviceMidFile: () => "device-mid",
            locateAppVersion: () => "3.12.3",
            createHttpSender: () => new HttpClient(handler));
    }

    private static string WriteTempCredentials()
    {
        var path = Path.Combine(Path.GetTempPath(), $"zcode-credentials-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, "{\"zcodejwttoken\":\"test-token\"}");
        return path;
    }

    private sealed class StubHttpHandler : HttpMessageHandler
    {
        private readonly Func<HttpResponseMessage> respond;

        public StubHttpHandler(Func<HttpResponseMessage> respond) => this.respond = respond;

        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(respond());
        }
    }

    private sealed class ThrowingHttpHandler : HttpMessageHandler
    {
        private readonly Exception exception;

        public ThrowingHttpHandler(Exception exception) => this.exception = exception;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromException<HttpResponseMessage>(exception);
        }
    }

    private static string ToBase64Url(byte[] value)
    {
        return Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
