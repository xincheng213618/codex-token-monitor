using System.Net;
using Xunit;

namespace CodexTokenMonitor.Tests;

public sealed class GitHubReleaseUpdateCheckerTests
{
    [Theory]
    [InlineData("v2026.09.20", "2026.9.20.0")]
    [InlineData("2026.9.21.1", "2026.9.21.1")]
    public void TryParseReleaseVersion_AcceptsVersionTags(string tag, string expected)
    {
        Assert.True(GitHubReleaseUpdateChecker.TryParseReleaseVersion(tag, out var version));
        Assert.Equal(Version.Parse(expected), version);
    }

    [Theory]
    [InlineData("v2026.09.20-beta")]
    [InlineData("release-2026.09.20")]
    [InlineData("v2026.09")]
    public void TryParseReleaseVersion_RejectsUnsupportedTags(string tag)
    {
        Assert.False(GitHubReleaseUpdateChecker.TryParseReleaseVersion(tag, out _));
    }

    [Fact]
    public async Task CheckAsync_ComparesPaddedReleaseTagWithAssemblyVersion()
    {
        using var client = CreateClient("https://github.com/xincheng213618/codex-token-monitor/releases/tag/v2026.09.20");
        var result = await new GitHubReleaseUpdateChecker(client).CheckAsync(new Version(2026, 9, 20, 0));
        Assert.False(result.IsUpdateAvailable);
        Assert.Equal("v2026.09.20", result.ReleaseTag);
    }

    [Fact]
    public async Task CheckAsync_DetectsNewerRelease()
    {
        using var client = CreateClient("https://github.com/xincheng213618/codex-token-monitor/releases/tag/v2026.09.21");
        var result = await new GitHubReleaseUpdateChecker(client).CheckAsync(new Version(2026, 9, 20));
        Assert.True(result.IsUpdateAvailable);
    }

    [Fact]
    public async Task CheckAsync_RejectsUnexpectedReleaseHost()
    {
        using var client = CreateClient("https://example.com/release");
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new GitHubReleaseUpdateChecker(client).CheckAsync(new Version(2026, 9, 20)));
    }

    [Fact]
    public async Task CheckAsync_RejectsMissingRedirect()
    {
        using var client = CreateClient(null);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new GitHubReleaseUpdateChecker(client).CheckAsync(new Version(2026, 9, 20)));
    }

    [Fact]
    public async Task CheckAsync_ReportsHttpError()
    {
        using var client = new HttpClient(new StaticResponseHandler(null, HttpStatusCode.Forbidden));
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            new GitHubReleaseUpdateChecker(client).CheckAsync(new Version(2026, 9, 20)));
    }

    private static HttpClient CreateClient(string? location) => new(new StaticResponseHandler(location));

    private sealed class StaticResponseHandler(
        string? location, HttpStatusCode status = HttpStatusCode.Redirect) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(
                "https://github.com/xincheng213618/codex-token-monitor/releases/latest",
                request.RequestUri?.AbsoluteUri);
            Assert.Equal(HttpMethod.Head, request.Method);
            var response = new HttpResponseMessage(status);
            if (location is not null) response.Headers.Location = new Uri(location);
            return Task.FromResult(response);
        }
    }
}
