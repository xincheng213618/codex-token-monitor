using Xunit;

namespace CodexTokenMonitor.Tests;

public sealed class CodexModelContextTests
{
    [Fact]
    public void TurnContext_SetsModelDirectly()
    {
        var context = new CodexModelContext();

        context.Observe("""{"type":"turn_context","payload":{"model":"gpt-5.6-sol"}}""");

        Assert.Equal("gpt-5.6-sol", context.ModelId);
    }

    [Fact]
    public void TurnContext_ReadsNestedInfoModel()
    {
        var context = new CodexModelContext();

        context.Observe("""{"type":"turn_context","payload":{"info":{"model":"gpt-5.6-luna"}}}""");

        Assert.Equal("gpt-5.6-luna", context.ModelId);
    }

    [Fact]
    public void ThreadSettingsApplied_OverridesModel()
    {
        var context = new CodexModelContext();
        context.Observe("""{"type":"turn_context","payload":{"model":"gpt-5.6-sol"}}""");

        context.Observe(
            """{"type":"event_msg","payload":{"type":"thread_settings_applied","thread_settings":{"model":"gpt-5.6-luna"}}}""");

        Assert.Equal("gpt-5.6-luna", context.ModelId);
    }

    [Fact]
    public void MissingModel_DoesNotClearPreviousModel()
    {
        var context = new CodexModelContext();
        context.Observe("""{"type":"turn_context","payload":{"model":"gpt-5.6-sol"}}""");

        context.Observe("""{"type":"turn_context","payload":{"cwd":"C:\\work"}}""");

        Assert.Equal("gpt-5.6-sol", context.ModelId);
    }

    [Fact]
    public void ServiceTier_IsNormalizedToLowercase()
    {
        var context = new CodexModelContext();

        context.Observe(
            """{"type":"turn_context","payload":{"model":"gpt-5.6-sol","service_tier":" Priority "}}""");

        Assert.Equal("priority", context.ServiceTier);
    }

    [Fact]
    public void TierFallback_ReadsFromNestedInfoWhenTopLevelMissing()
    {
        var context = new CodexModelContext();

        context.Observe(
            """{"type":"turn_context","payload":{"info":{"model":"m","service_tier":"fast"}}}""");

        Assert.Equal("fast", context.ServiceTier);
    }

    [Fact]
    public void MalformedLine_IsIgnoredWithoutThrowing()
    {
        var context = new CodexModelContext();

        context.Observe("""{"type":"turn_context","payload":{"model":"gpt-5""");

        Assert.Null(context.ModelId);
    }

    [Fact]
    public void IrrelevantLine_IsIgnoredByFastPath()
    {
        var context = new CodexModelContext();

        // No "model", "service_tier" or "session_meta" marker: skipped before parsing.
        context.Observe("""{"type":"event_msg","payload":{"type":"token_count","total":5}}""");
        context.Observe("not json at all");

        Assert.Null(context.ModelId);
        Assert.Null(context.ServiceTier);
    }

    [Fact]
    public void NonObjectRoot_IsIgnored()
    {
        var context = new CodexModelContext();

        context.Observe("""["not","an","object"]""");

        Assert.Null(context.ModelId);
    }

    [Fact]
    public void SessionIdChange_ResetsTierAlongWithModel()
    {
        var context = new CodexModelContext();
        context.Observe("""{"type":"session_meta","payload":{"id":"session-a","model":"gpt-5.6-sol","service_tier":"priority"}}""");
        Assert.Equal("gpt-5.6-sol", context.ModelId);
        Assert.Equal("priority", context.ServiceTier);

        context.Observe("""{"type":"session_meta","payload":{"id":"session-b","model":"gpt-5.6-luna"}}""");

        // The new session declares its own model, so it survives the reset.
        Assert.Equal("gpt-5.6-luna", context.ModelId);
        Assert.Null(context.ServiceTier);
    }
}
