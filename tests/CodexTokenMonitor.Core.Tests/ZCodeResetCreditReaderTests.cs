using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using Xunit;

namespace CodexTokenMonitor.Tests;

public sealed class ZCodeResetCreditReaderTests
{
    private static readonly TimeSpan Beijing = TimeSpan.FromHours(8);
    private static readonly DateTimeOffset SnapshotTime = new(2026, 10, 22, 12, 0, 0, Beijing);

    /// <summary>Shaped after a live response with cards granted: the expiry
    /// list arrives unsorted and must be normalized earliest-first.</summary>
    private const string GrantedEnvelope = """
        {"code":0,"msg":"success","data":{"available_five_hour_resets":[{"expire_at":1798953600000},{"expire_at":1796016000000}],"available_week_resets":[{"expire_at":1798867200000}],"latest_five_hour_reset_history":{"used_at":1791830848000},"latest_week_reset_history":null,"has_unread_history":true},"logid":"test"}
        """;

    /// <summary>The account state observed live on 2026-10-22: every card
    /// used or expired, only the 5-hour usage history remains.</summary>
    private const string EmptyEnvelope = """
        {"code":0,"msg":"success","data":{"available_five_hour_resets":[],"available_week_resets":[],"latest_five_hour_reset_history":{"used_at":1791830848000},"latest_week_reset_history":null,"has_unread_history":false},"logid":"test"}
        """;

    [Fact]
    public void Parse_ReadsCountsExpiriesAndHistory()
    {
        var status = ZCodeResetCreditParser.Parse(GrantedEnvelope, SnapshotTime);

        Assert.NotNull(status);
        Assert.Equal(2, status!.FiveHourCount);
        Assert.Equal(1, status.WeekCount);
        Assert.Equal(3, status.TotalCount);
        Assert.Equal(
            DateTimeOffset.FromUnixTimeMilliseconds(1_796_016_000_000).ToOffset(Beijing),
            status.FiveHourExpiresLocal[0]);
        Assert.Equal(
            DateTimeOffset.FromUnixTimeMilliseconds(1_798_953_600_000).ToOffset(Beijing),
            status.FiveHourExpiresLocal[1]);
        Assert.Single(status.WeekExpiresLocal);
        Assert.Equal(status.FiveHourExpiresLocal[0], status.EarliestExpiresLocal);
        Assert.Equal(
            DateTimeOffset.FromUnixTimeMilliseconds(1_791_830_848_000).ToOffset(Beijing),
            status.LatestFiveHourUsedLocal);
        Assert.Null(status.LatestWeekUsedLocal);
        Assert.True(status.HasUnreadHistory);
        Assert.Equal(SnapshotTime, status.SnapshotLocal);
    }

    [Fact]
    public void Parse_EmptyAccount_KeepsHistoryWithZeroCards()
    {
        var status = ZCodeResetCreditParser.Parse(EmptyEnvelope, SnapshotTime);

        Assert.NotNull(status);
        Assert.Equal(0, status!.TotalCount);
        Assert.Empty(status.FiveHourExpiresLocal);
        Assert.Empty(status.WeekExpiresLocal);
        Assert.Null(status.EarliestExpiresLocal);
        Assert.NotNull(status.LatestFiveHourUsedLocal);
    }

    [Fact]
    public void Parse_RejectsBusinessFailureAndMalformedPayloads()
    {
        Assert.Null(ZCodeResetCreditParser.Parse(
            GrantedEnvelope.Replace("\"code\":0", "\"code\":500"), SnapshotTime));
        Assert.Null(ZCodeResetCreditParser.Parse("{}", SnapshotTime));
        Assert.Null(ZCodeResetCreditParser.Parse("invalid", SnapshotTime));
        // Missing card arrays parse as zero cards rather than failing.
        Assert.NotNull(ZCodeResetCreditParser.Parse("""{"code":0,"data":{}}""", SnapshotTime));
    }

    [Fact]
    public void ReadCurrent_SendsClientHeadersAndParsesStatus()
    {
        var credentialsPath = WriteTempCredentials(
            "{\"zcodejwttoken\":\"jwt-token\",\"oauth:bigmodel:access_token\":\"maas-token\"}");
        try
        {
            var handler = new CapturingHandler(() => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(GrantedEnvelope)
            });
            var reader = new ZCodeResetCreditReader(
                locateCredentialsFile: () => credentialsPath,
                createHttpSender: () => new HttpClient(handler));

            var status = reader.ReadCurrent();

            Assert.NotNull(status);
            Assert.Equal(3, status!.TotalCount);
            Assert.Equal("https://zcode.z.ai/api/v1/coding-plan/reset/status", handler.LastRequestUri?.ToString());
            Assert.Equal("Bearer jwt-token", handler.LastRequestHeaders.Authorization?.ToString());
            Assert.Equal(
                "maas-token",
                handler.LastRequestHeaders.TryGetValues("X-Bigmodel-Authorization", out var maas) ? string.Join(",", maas) : null);
            Assert.Equal(
                "PERSONAL",
                handler.LastRequestHeaders.TryGetValues("Bigmodel-Target-Type", out var target) ? string.Join(",", target) : null);
        }
        finally
        {
            File.Delete(credentialsPath);
        }
    }

    [Fact]
    public void ReadCurrent_PrefersZaiTokenWhenBothFamiliesAreStored()
    {
        var credentialsPath = WriteTempCredentials(
            "{\"zcodejwttoken\":\"jwt-token\",\"oauth:zai:access_token\":\"zai-token\",\"oauth:bigmodel:access_token\":\"bigmodel-token\"}");
        try
        {
            var handler = new CapturingHandler(() => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(EmptyEnvelope)
            });
            var reader = new ZCodeResetCreditReader(
                locateCredentialsFile: () => credentialsPath,
                createHttpSender: () => new HttpClient(handler));

            Assert.NotNull(reader.ReadCurrent());
            Assert.Equal(
                "zai-token",
                handler.LastRequestHeaders.TryGetValues("X-Bigmodel-Authorization", out var maas) ? string.Join(",", maas) : null);
        }
        finally
        {
            File.Delete(credentialsPath);
        }
    }

    [Fact]
    public void ReadCurrent_WithoutAccountTokens_MakesNoHttpRequest()
    {
        var credentialsPath = WriteTempCredentials("{\"zcodejwttoken\":\"jwt-token\"}");
        try
        {
            var handler = new CapturingHandler(() => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(GrantedEnvelope)
            });
            var reader = new ZCodeResetCreditReader(
                locateCredentialsFile: () => credentialsPath,
                createHttpSender: () => new HttpClient(handler));

            Assert.Null(reader.ReadCurrent());
            Assert.Equal(0, handler.CallCount);
        }
        finally
        {
            File.Delete(credentialsPath);
        }
    }

    [Fact]
    public void ReadCurrent_ServerError_ReturnsNullWithoutThrowing()
    {
        var credentialsPath = WriteTempCredentials(
            "{\"zcodejwttoken\":\"jwt-token\",\"oauth:bigmodel:access_token\":\"maas-token\"}");
        try
        {
            var handler = new CapturingHandler(() => new HttpResponseMessage(HttpStatusCode.InternalServerError));
            var reader = new ZCodeResetCreditReader(
                locateCredentialsFile: () => credentialsPath,
                createHttpSender: () => new HttpClient(handler));

            Assert.Null(reader.ReadCurrent());
        }
        finally
        {
            File.Delete(credentialsPath);
        }
    }

    [Fact]
    public void ReadCurrent_ReusesCachedSuccessWithinTheWindow()
    {
        var credentialsPath = WriteTempCredentials(
            "{\"zcodejwttoken\":\"jwt-token\",\"oauth:bigmodel:access_token\":\"maas-token\"}");
        try
        {
            var handler = new CapturingHandler(() => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(EmptyEnvelope)
            });
            var reader = new ZCodeResetCreditReader(
                locateCredentialsFile: () => credentialsPath,
                createHttpSender: () => new HttpClient(handler));

            Assert.NotNull(reader.ReadCurrent());
            Assert.NotNull(reader.ReadCurrent());
            Assert.Equal(1, handler.CallCount);
        }
        finally
        {
            File.Delete(credentialsPath);
        }
    }

    [Fact]
    public void Shared_CarriesTheDefaultRequestTimeout()
    {
        // Mirrors the coding-plan regression: the Shared singleton initializes
        // before the type's static fields, which can capture a zero timeout.
        Assert.Equal(TimeSpan.FromSeconds(15), ZCodeResetCreditReader.Shared.RequestTimeout);
    }

    private static string WriteTempCredentials(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"zcode-reset-credentials-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, content);
        return path;
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly Func<HttpResponseMessage> respond;

        public CapturingHandler(Func<HttpResponseMessage> respond) => this.respond = respond;

        public int CallCount { get; private set; }

        public Uri? LastRequestUri { get; private set; }

        public HttpRequestHeaders LastRequestHeaders { get; private set; } = null!;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            LastRequestUri = request.RequestUri;
            LastRequestHeaders = request.Headers;
            return Task.FromResult(respond());
        }
    }
}
