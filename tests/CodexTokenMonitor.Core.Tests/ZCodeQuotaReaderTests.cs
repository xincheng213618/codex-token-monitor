using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
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

    /// <summary>Shaped after a live response: two concurrently active plans, the
    /// higher-priority one first, each metering its own buckets. The account of
    /// 2026-09-29 held both; Trust Build's Flash bucket was exhausted while the
    /// Start Plan's GLM-5.3 daily bucket still carried units.</summary>
    private const string MultiPlanEnvelope = """
        {"code":0,"msg":"","data":{"server_time":1790616367,"plans":[
            {"user_plan_id":"upl_trust","plan_id":"zcode-v3-start-plan-trust-0929","name":"ZCode Trust Build","description":"ZCode Global Build","priority":110,"status":"active","starts_at":1790611478,"ends_at":1790697600},
            {"user_plan_id":"upl_start","plan_id":"zcode-v3-start-plan-0817","name":"ZCode Start Plan","description":"免费 GLM 旗舰模型体验","priority":90,"status":"active","starts_at":1790611029,"ends_at":1790956799}],
        "balances":[
            {"bucket_id":"bucket_trust","user_plan_id":"upl_trust","plan_id":"zcode-v3-start-plan-trust-0929","entitlement_id":"zcode-v3-start-plan-trust-0929-1","show_name":"GLM-5.3-Flash","capabilities":["model:glm-5.3-flash"],"total_units":100000000,"used_units":100000000,"remaining_units":0,"available_units":0,"period_start":1790611478,"period_end":1790697600,"expires_at":1790697600},
            {"bucket_id":"bucket_start_53","user_plan_id":"upl_start","plan_id":"zcode-v3-start-plan-0817","entitlement_id":"ent_2_0817_glm_5p3","show_name":"GLM-5.3","capabilities":["model:glm-5.3"],"total_units":3000000,"used_units":963819,"remaining_units":2036181,"available_units":2036181,"period_start":1790611200,"period_end":1790697599,"expires_at":1790697599},
            {"bucket_id":"bucket_start_flash","user_plan_id":"upl_start","plan_id":"zcode-v3-start-plan-0817","entitlement_id":"ent_2_0817_glm_5p3f","show_name":"GLM-5.3-Flash","capabilities":["model:glm-5.3-flash"],"total_units":5000000,"used_units":5000000,"remaining_units":0,"available_units":0,"period_start":1790611200,"period_end":1790697599,"expires_at":1790697599}]}}
        """;

    private static readonly DateTimeOffset TrustEnd = DateTimeOffset.FromUnixTimeSeconds(1790697600).ToOffset(Beijing);
    private static readonly DateTimeOffset StartEnd = DateTimeOffset.FromUnixTimeSeconds(1790956799).ToOffset(Beijing);

    [Fact]
    public void Parse_ReadsActivePlanAndBalanceFields()
    {
        var snapshot = ZCodeQuotaParser.Parse(BalanceEnvelope, SnapshotTime);

        Assert.NotNull(snapshot);
        var plan = Assert.Single(snapshot.Plans);
        Assert.Equal("zcode-v3-start-plan-wk-0918", plan.PlanId);
        Assert.Equal("upl_1", plan.UserPlanId);
        Assert.Equal("ZCode Weekend Build", plan.Name);
        Assert.Equal("ZCode 周末活动", plan.Description);
        Assert.Equal("active", plan.Status);
        Assert.Equal(SnapshotTime, snapshot.SnapshotLocal);
        Assert.Equal(
            DateTimeOffset.FromUnixTimeSeconds(1789806133).ToOffset(Beijing),
            snapshot.ServerTimeLocal);

        var balance = Assert.Single(plan.Balances);
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
        Assert.Same(balance, plan.PrimaryBalance);
        Assert.Same(plan, snapshot.DefaultPlan);
    }

    [Fact]
    public void Parse_KeepsEveryConcurrentPlanAndItsOwnBuckets()
    {
        var snapshot = ZCodeQuotaParser.Parse(MultiPlanEnvelope, SnapshotTime);

        Assert.NotNull(snapshot);
        Assert.Equal(2, snapshot.Plans.Count);

        var trust = snapshot.Plans[0];
        Assert.Equal("ZCode Trust Build", trust.Name);
        Assert.Equal("upl_trust", trust.UserPlanId);
        Assert.Equal(TrustEnd, trust.EndsAtLocal);
        var trustBalance = Assert.Single(trust.Balances);
        Assert.Equal("GLM-5.3-Flash", trustBalance.ModelName);
        Assert.Equal(0, trustBalance.RemainingUnits);

        var start = snapshot.Plans[1];
        Assert.Equal("ZCode Start Plan", start.Name);
        Assert.Equal(StartEnd, start.EndsAtLocal);
        Assert.Equal(2, start.Balances.Count);
        // Buckets stay ordered by remaining units inside the plan.
        Assert.Equal("GLM-5.3", start.Balances[0].ModelName);
        Assert.Equal(2_036_181, start.Balances[0].RemainingUnits);
        Assert.Equal("GLM-5.3-Flash", start.Balances[1].ModelName);
        Assert.Equal(5_000_000, start.Balances[1].TotalUnits);
        Assert.Same(start.Balances[0], start.PrimaryBalance);

        // Default view lands on the active plan that still carries units, not
        // on the higher-priority exhausted one listed first.
        Assert.Same(start, snapshot.DefaultPlan);
    }

    [Fact]
    public void FindPlan_ResolvesPersistedSelectionsWithPlanIdFallback()
    {
        var snapshot = ZCodeQuotaParser.Parse(MultiPlanEnvelope, SnapshotTime)!;

        Assert.Same(snapshot.Plans[1], snapshot.FindPlan("upl_start", null));
        Assert.Same(snapshot.Plans[0], snapshot.FindPlan("upl_trust", "zcode-v3-start-plan-0817"));
        // A repurchased plan mints a new user_plan_id; the stored plan_id still resolves.
        Assert.Same(snapshot.Plans[1], snapshot.FindPlan("upl_gone", "zcode-v3-start-plan-0817"));
        Assert.Null(snapshot.FindPlan("upl_missing", "plan_missing"));
        Assert.Null(snapshot.FindPlan(null, null));
    }

    [Fact]
    public void Parse_GroupsOrphanBalancesUnderASyntheticPlan()
    {
        var response = """
            {"code":0,"data":{"plans":[
                {"plan_id":"p_known","name":"Known","status":"active","user_plan_id":"upl_known"}],
            "balances":[
                {"show_name":"A","total_units":100,"used_units":40,"remaining_units":60,"plan_id":"p_orphan","user_plan_id":"upl_orphan"},
                {"show_name":"B","total_units":200,"used_units":10,"remaining_units":190,"plan_id":"p_known","user_plan_id":"upl_known"}]}}
            """;

        var snapshot = ZCodeQuotaParser.Parse(response, SnapshotTime);

        Assert.NotNull(snapshot);
        Assert.Equal(2, snapshot.Plans.Count);
        Assert.Equal("Known", snapshot.Plans[0].Name);
        Assert.Single(snapshot.Plans[0].Balances);
        var orphan = snapshot.Plans[1];
        Assert.Equal("p_orphan", orphan.PlanId);
        Assert.Equal("upl_orphan", orphan.UserPlanId);
        Assert.Single(orphan.Balances);
    }

    [Fact]
    public void Parse_SelectsRemainingBalanceAsPrimaryAndOrdersByRemaining()
    {
        var response = """
            {"code":0,"data":{"plans":[{"plan_id":"p","name":"Plan","status":"active","user_plan_id":"upl_p"}],"balances":[
                {"show_name":"A","total_units":100,"used_units":100,"remaining_units":0,"plan_id":"p","user_plan_id":"upl_p"},
                {"show_name":"B","total_units":200,"used_units":150,"remaining_units":50,"plan_id":"p","user_plan_id":"upl_p"}
            ]}}
            """;

        var snapshot = ZCodeQuotaParser.Parse(response, SnapshotTime);

        Assert.NotNull(snapshot);
        var plan = Assert.Single(snapshot.Plans);
        Assert.Equal(2, plan.Balances.Count);
        Assert.Equal("B", plan.PrimaryBalance!.ModelName);
        Assert.True(plan.Balances[0].RemainingUnits >= plan.Balances[1].RemainingUnits);
    }

    [Fact]
    public void Parse_RejectsFailedEnvelopeAndMissingPlans()
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
            {"code":0,"data":{"plans":[{"plan_id":"p2","name":"Upcoming","status":"pending","user_plan_id":"upl_p2"}],"balances":[
                {"show_name":"M","total_units":10,"used_units":1,"remaining_units":9,"plan_id":"p2","user_plan_id":"upl_p2"}
            ]}}
            """;

        var snapshot = ZCodeQuotaParser.Parse(response, SnapshotTime);

        Assert.NotNull(snapshot);
        Assert.Equal("p2", snapshot.Plans[0].PlanId);
        Assert.False(snapshot.Plans[0].IsActive);
        Assert.Single(snapshot.Plans[0].Balances);
        // Nothing active with units left: the default falls back to the first entry.
        Assert.Same(snapshot.Plans[0], snapshot.DefaultPlan);
    }

    private const string CodingPlanEnvelope = """
        {"code":200,"msg":"操作成功","data":{"limits":[
            {"type":"CREDIT_LIMIT","unit":3,"number":5,"usage":2000,"currentValue":36,"remaining":1963,"percentage":1,"nextResetTime":1790629049818},
            {"type":"CREDIT_LIMIT","unit":6,"number":1,"usage":10000,"currentValue":36,"remaining":9963,"percentage":1,"nextResetTime":1791215748984}],
        "level":"lite"},"success":true}
        """;

    private static readonly DateTimeOffset FiveHourReset =
        DateTimeOffset.FromUnixTimeMilliseconds(1790629049818).ToOffset(Beijing);
    private static readonly DateTimeOffset WeeklyReset =
        DateTimeOffset.FromUnixTimeMilliseconds(1791215748984).ToOffset(Beijing);

    [Fact]
    public void CodingPlanParse_BuildsPercentWindowBucketsShortestFirst()
    {
        var plan = ZCodeCodingPlanParser.Parse(CodingPlanEnvelope, "acc_1", SnapshotTime);

        Assert.NotNull(plan);
        Assert.Equal("bigmodel-individual-coding-plan", plan.PlanId);
        Assert.Equal("acc_1", plan.UserPlanId);
        Assert.Equal("GLM Coding Lite", plan.Name);
        Assert.True(plan.IsActive);

        Assert.Equal(2, plan.Balances.Count);
        var fiveHour = plan.Balances[0];
        Assert.Equal("5 小时窗口", fiveHour.ModelName);
        Assert.Equal(2_000, fiveHour.TotalUnits);
        Assert.Equal(37, fiveHour.UsedUnits);
        Assert.Equal(1_963, fiveHour.RemainingUnits);
        Assert.Equal(1.8m, fiveHour.UsedPercent);
        Assert.Equal(FiveHourReset, fiveHour.ExpiresAtLocal);
        Assert.True(fiveHour.IsWindowScaled);
        Assert.Same(fiveHour, plan.PrimaryBalance);

        var weekly = plan.Balances[1];
        Assert.Equal("每周窗口", weekly.ModelName);
        Assert.Equal(10_000, weekly.TotalUnits);
        Assert.Equal(9_963, weekly.RemainingUnits);
        Assert.Equal(0.4m, weekly.UsedPercent);
        Assert.Equal(WeeklyReset, weekly.ExpiresAtLocal);
    }

    [Fact]
    public void CodingPlanParse_ReturnsNullWithoutLimitsOrPlan()
    {
        Assert.Null(ZCodeCodingPlanParser.Parse(CodingPlanEnvelope.Replace("\"limits\"", "\"windows\""), "acc_1", SnapshotTime));
        Assert.Null(ZCodeCodingPlanParser.Parse(
            "{\"code\":500,\"msg\":\"当前用户不存在coding plan\",\"success\":false}", "acc_1", SnapshotTime));
        Assert.Null(ZCodeCodingPlanParser.Parse("{}", "acc_1", SnapshotTime));
        Assert.Null(ZCodeCodingPlanParser.Parse("invalid", "acc_1", SnapshotTime));
    }

    [Fact]
    public void CodingPlanParse_ToleratesSecondPrecisionResetTimes()
    {
        const string response = """
            {"code":200,"data":{"level":"pro","limits":[
                {"type":"CREDIT_LIMIT","unit":3,"number":5,"usage":1000,"remaining":500,"nextResetTime":1790629049}]}}
            """;

        var plan = ZCodeCodingPlanParser.Parse(response, "acc_1", SnapshotTime);

        Assert.NotNull(plan);
        Assert.Equal("GLM Coding Pro", plan.Name);
        var bucket = Assert.Single(plan.Balances);
        Assert.Equal(
            DateTimeOffset.FromUnixTimeMilliseconds(1790629049000).ToOffset(Beijing),
            bucket.ExpiresAtLocal);
    }

    [Fact]
    public void LocateCodingPlanAccount_FindsBigModelKeyAndPassesPlaintextThrough()
    {
        var credentialsPath = Path.Combine(Path.GetTempPath(), $"zcode-credentials-{Guid.NewGuid():N}.json");
        File.WriteAllText(credentialsPath, """
            {"zcodejwttoken":"jwt",
             "account-provider:coding-plan:account:bigmodel-individual-coding-plan:account:29851736656137483:api-key":"plain-key",
             "account-provider:coding-plan:account:zai-team-coding-plan:account:abc:api-key":"team-key"}
            """);
        try
        {
            var account = ZCodeCodingPlanReader.LocateCodingPlanAccount(credentialsPath);

            Assert.NotNull(account);
            Assert.Equal("29851736656137483", account.AccountId);
            Assert.Equal("plain-key", account.ApiKey);
        }
        finally
        {
            File.Delete(credentialsPath);
        }
    }

    [Fact]
    public void LocateCodingPlanAccount_ReturnsNullWithoutBigModelKey()
    {
        var credentialsPath = Path.Combine(Path.GetTempPath(), $"zcode-credentials-{Guid.NewGuid():N}.json");
        File.WriteAllText(credentialsPath, "{\"zcodejwttoken\":\"jwt\"}");
        try
        {
            Assert.Null(ZCodeCodingPlanReader.LocateCodingPlanAccount(credentialsPath));
            Assert.Null(ZCodeCodingPlanReader.LocateCodingPlanAccount(null));
        }
        finally
        {
            File.Delete(credentialsPath);
        }
    }

    [Fact]
    public void CodingPlanShared_CarriesTheDefaultRequestTimeout()
    {
        // Regression: the Shared singleton is constructed before the type's
        // other static fields initialize, which used to capture a zero timeout
        // and cancel every live read instantly.
        Assert.Equal(TimeSpan.FromSeconds(15), ZCodeCodingPlanReader.Shared.RequestTimeout);
    }

    [Fact]
    public void ReadCurrentResult_AppendsCodingPlanAfterServerPlans()
    {
        var credentialsPath = WriteTempCredentials();
        try
        {
            using var handler = new StubHttpHandler(() => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(BalanceEnvelope)
            });
            var reader = new ZCodeQuotaReader(
                locateCredentialsFile: () => credentialsPath,
                locateDeviceMidFile: () => null,
                locateAppVersion: () => "3.12.3",
                createHttpSender: () => new HttpClient(handler),
                readCodingPlan: _ => new ZCodeQuotaPlan(
                    "bigmodel-individual-coding-plan", "acc_1", "GLM Coding Lite", null, "active",
                    null, null, Array.Empty<ZCodeQuotaBalance>()));

            var snapshot = reader.ReadCurrent();
            Assert.NotNull(snapshot);
            Assert.Equal(2, snapshot!.Plans.Count);
            Assert.Equal("ZCode Weekend Build", snapshot.Plans[0].Name);
            Assert.Equal("GLM Coding Lite", snapshot.Plans[1].Name);
        }
        finally
        {
            File.Delete(credentialsPath);
        }
    }

    [Fact]
    public void ReadCurrentResult_KeepsBalanceSnapshotWhenCodingPlanIsAbsent()
    {
        var credentialsPath = WriteTempCredentials();
        try
        {
            using var handler = new StubHttpHandler(() => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(BalanceEnvelope)
            });
            var reader = new ZCodeQuotaReader(
                locateCredentialsFile: () => credentialsPath,
                locateDeviceMidFile: () => null,
                locateAppVersion: () => "3.12.3",
                createHttpSender: () => new HttpClient(handler),
                readCodingPlan: _ => null);

            var snapshot = reader.ReadCurrent();
            Assert.NotNull(snapshot);
            Assert.Single(snapshot!.Plans);
        }
        finally
        {
            File.Delete(credentialsPath);
        }
    }

    [Fact]
    public void ReadCurrentResult_CodingPlanAloneSurvivesBalanceParseError()
    {
        var credentialsPath = WriteTempCredentials();
        try
        {
            using var handler = new StubHttpHandler(() => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}")
            });
            var reader = new ZCodeQuotaReader(
                locateCredentialsFile: () => credentialsPath,
                locateDeviceMidFile: () => null,
                locateAppVersion: () => "3.12.3",
                createHttpSender: () => new HttpClient(handler),
                readCodingPlan: _ => new ZCodeQuotaPlan(
                    "bigmodel-individual-coding-plan", "acc_1", "GLM Coding Lite", null, "active",
                    null, null, Array.Empty<ZCodeQuotaBalance>()));

            var result = reader.ReadCurrentResult();

            Assert.Null(result.Failure);
            Assert.NotNull(result.Snapshot);
            Assert.Single(result.Snapshot.Plans);
        }
        finally
        {
            File.Delete(credentialsPath);
        }
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
            var reader = new ZCodeQuotaReader(
                locateCredentialsFile: () => credentialsPath,
                locateDeviceMidFile: () => "device-mid",
                locateAppVersion: () => "3.12.3",
                createHttpSender: () => new HttpClient(handler),
                readCodingPlan: _ => null);

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
    public void Shared_CarriesTheDefaultRequestTimeout()
    {
        // Regression: the Shared singleton is constructed before the type's
        // other static fields initialize, which used to capture a zero timeout.
        Assert.Equal(TimeSpan.FromSeconds(15), ZCodeQuotaReader.Shared.RequestTimeout);
    }

    [Fact]
    public void ReadCurrentResult_ClassifiesRequestTimeoutAsNetworkError()
    {
        var credentialsPath = WriteTempCredentials();
        try
        {
            using var handler = new DelayingHttpHandler(TimeSpan.FromMilliseconds(500));
            var reader = new ZCodeQuotaReader(
                locateCredentialsFile: () => credentialsPath,
                locateDeviceMidFile: () => "device-mid",
                locateAppVersion: () => "3.12.3",
                createHttpSender: () => new HttpClient(handler),
                requestTimeout: TimeSpan.FromMilliseconds(20));

            var result = reader.ReadCurrentResult();

            Assert.Null(result.Snapshot);
            Assert.Equal(ZCodeQuotaFailureKind.NetworkError, result.Failure!.Kind);
            Assert.Contains("超时", result.Failure.Message);
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

    [Theory]
    [InlineData(45, 45)]
    [InlineData(5, 30)]
    [InlineData(3600, 600)]
    public void CacheDuration_RespectsServerRetryAfterWithFloorAndCap(int retryAfterSeconds, int expectedSeconds)
    {
        var credentialsPath = WriteTempCredentials();
        try
        {
            using var handler = new StubHttpHandler(() =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(retryAfterSeconds));
                return response;
            });
            var reader = CreateReaderWithCredentials(handler, credentialsPath);

            var result = reader.ReadCurrentResult();

            Assert.Equal(ZCodeQuotaFailureKind.HttpError, result.Failure!.Kind);
            Assert.Equal(429, result.Failure.StatusCode);
            Assert.Equal(TimeSpan.FromSeconds(retryAfterSeconds), result.Failure.RetryAfter);
            Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), reader.CacheDuration());
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

    private sealed class DelayingHttpHandler : HttpMessageHandler
    {
        private readonly TimeSpan delay;

        public DelayingHttpHandler(TimeSpan delay) => this.delay = delay;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(delay, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        }
    }

    private static string ToBase64Url(byte[] value)
    {
        return Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
