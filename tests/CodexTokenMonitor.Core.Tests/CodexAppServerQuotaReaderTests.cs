using Xunit;

namespace CodexTokenMonitor.Tests;

public sealed class CodexAppServerQuotaReaderTests
{
    private static readonly TimeSpan Beijing = TimeSpan.FromHours(8);

    [Fact]
    public void ParseRateLimitsResponse_ReadsWeeklyOnlyPrimaryWindow()
    {
        var snapshotTime = new DateTimeOffset(2026, 7, 19, 13, 0, 0, Beijing);
        var response = """
            {"id":2,"result":{"rateLimits":{"limitId":"codex","primary":{"usedPercent":2,"windowDurationMins":10080,"resetsAt":1784958401},"secondary":null},"rateLimitsByLimitId":{"codex":{"limitId":"codex","primary":{"usedPercent":2,"windowDurationMins":10080,"resetsAt":1784958401},"secondary":null}}}}
            """;

        var snapshot = CodexAppServerQuotaReader.ParseRateLimitsResponse(response, snapshotTime);

        Assert.NotNull(snapshot);
        Assert.Null(snapshot!.FiveHourUsedPercent);
        Assert.Equal(2m, snapshot.WeekUsedPercent);
        Assert.NotNull(snapshot.WeekResetAtLocal);
    }

    [Fact]
    public void ParseRateLimitsResponse_ClassifiesWindowsByDurationInsteadOfPosition()
    {
        var snapshotTime = new DateTimeOffset(2026, 7, 19, 13, 0, 0, Beijing);
        var response = """
            {"id":2,"result":{"rateLimits":{"limitId":"codex","primary":{"usedPercent":15,"windowDurationMins":10080,"resetsAt":1784958401},"secondary":{"usedPercent":40,"windowDurationMins":300,"resetsAt":1784520000}}}}
            """;

        var snapshot = CodexAppServerQuotaReader.ParseRateLimitsResponse(response, snapshotTime);

        Assert.NotNull(snapshot);
        Assert.Equal(40m, snapshot!.FiveHourUsedPercent);
        Assert.Equal(15m, snapshot.WeekUsedPercent);
    }

    [Fact]
    public void ParseRateLimitsResponse_PrefersGeneralCodexBucket()
    {
        var snapshotTime = new DateTimeOffset(2026, 7, 19, 13, 0, 0, Beijing);
        var response = """
            {"id":2,"result":{"rateLimits":{"limitId":"codex_bengalfox","primary":{"usedPercent":90,"windowDurationMins":10080}},"rateLimitsByLimitId":{"codex_bengalfox":{"limitId":"codex_bengalfox","primary":{"usedPercent":90,"windowDurationMins":10080}},"codex":{"limitId":"codex","primary":{"usedPercent":3,"windowDurationMins":10080}}}}}
            """;

        var snapshot = CodexAppServerQuotaReader.ParseRateLimitsResponse(response, snapshotTime);

        Assert.NotNull(snapshot);
        Assert.Equal("codex", snapshot!.LimitId);
        Assert.Equal(3m, snapshot.WeekUsedPercent);
    }

    [Fact]
    public void ParseRateLimitsResponse_RejectsMalformedOrUnrelatedPayloads()
    {
        var snapshotTime = new DateTimeOffset(2026, 7, 19, 13, 0, 0, Beijing);

        Assert.Null(CodexAppServerQuotaReader.ParseRateLimitsResponse("not json", snapshotTime));
        Assert.Null(CodexAppServerQuotaReader.ParseRateLimitsResponse(
            "{\"id\":2,\"result\":{\"rateLimits\":{\"limitId\":\"codex_bengalfox\",\"primary\":{\"usedPercent\":3,\"windowDurationMins\":10080}}}}",
            snapshotTime));
    }
}
