using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace CodexTokenMonitor.Tests;

public sealed class ClaudeQuotaReaderTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 30, 2, 0, 0, TimeSpan.FromHours(8));
    private readonly string root = Path.Combine(Path.GetTempPath(), "ClaudeQuotaReaderTests-" + Guid.NewGuid().ToString("N"));
    private readonly IDisposable scope;
    public ClaudeQuotaReaderTests() => scope = MonitorCachePaths.PushLocalAppDataRoot(root);
    public void Dispose()
    {
        scope.Dispose();
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    private static string Response(double five = 39) => JsonSerializer.Serialize(new
    {
        five_hour = new { utilization = five, resets_at = Now.AddHours(3).ToString("O") },
        seven_day = new { utilization = 4, resets_at = Now.AddDays(4).ToString("O") }
    });

    [Fact]
    public async Task ReadsOfficialWindowsAndPersistsOnlyQuotaWithSuccessCache()
    {
        var requests = 0;
        var reader = new ClaudeQuotaReader(() => new ClaudeOAuthCredential("test-bearer"), (request, _) =>
        {
            requests++;
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("https://api.anthropic.com/api/oauth/usage", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("test-bearer", request.Headers.Authorization.Parameter);
            Assert.Contains("oauth-2025-04-20", request.Headers.GetValues("anthropic-beta"));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Response()) });
        }, () => Now);
        var result = await reader.ReadAsync();
        Assert.Null(result.Failure);
        Assert.Equal(39, result.Snapshot!.FiveHour!.UsedPercent);
        Assert.Equal(4, result.Snapshot.SevenDay!.UsedPercent);
        Assert.Equal(Now, result.Snapshot.FiveHour.ObservedLocal);
        Assert.Same(result, await reader.ReadAsync());
        Assert.Equal(1, requests);
        var persisted = File.ReadAllText(ClaudeRateLimitLog.CurrentFilePath);
        Assert.DoesNotContain("test-bearer", persisted);
        Assert.Equal(39, ClaudeRateLimitLog.ReadLatest(Now)!.FiveHour!.UsedPercent);
    }

    [Fact]
    public async Task NoLoginDoesNotSendAnHttpRequest()
    {
        var reader = new ClaudeQuotaReader(() => null, (_, _) => throw new Exception("Must not send"), () => Now);
        var result = await reader.ReadAsync();
        Assert.Null(result.Snapshot);
        Assert.Contains("登录", result.Failure);
        Assert.Null(ClaudeOAuthCredentials.ReadCurrent()); // isolated probe never reads real credentials
    }

    [Fact]
    public async Task RateLimitHonorsRetryAfterAndKeepsTheTimestampOfTheLastSuccess()
    {
        var now = Now;
        var requests = 0;
        var reader = new ClaudeQuotaReader(() => new ClaudeOAuthCredential("secret"), (_, _) =>
        {
            requests++;
            var response = new HttpResponseMessage(requests == 1 ? HttpStatusCode.OK : HttpStatusCode.TooManyRequests);
            response.Content = new StringContent(Response());
            if (requests > 1) response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(3));
            return Task.FromResult(response);
        }, () => now);
        await reader.ReadAsync();
        now = Now.AddMinutes(1);
        var failure = await reader.ReadAsync();
        Assert.Contains("限流", failure.Failure);
        Assert.Equal(Now, failure.Snapshot!.RecordedLocal);
        now = Now.AddMinutes(2);
        await reader.ReadAsync();
        Assert.Equal(2, requests);
        now = Now.AddMinutes(4);
        await reader.ReadAsync();
        Assert.Equal(3, requests);
    }

    [Fact]
    public async Task DoesNotReadRedirectedResponsesOrExposeServerErrorBodies()
    {
        var reader = new ClaudeQuotaReader(() => new ClaudeOAuthCredential("secret"), (_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Redirect) { Content = new StringContent("secret server body") };
            response.Headers.Location = new Uri("https://untrusted.invalid/collect");
            return Task.FromResult(response);
        }, () => Now);
        var result = await reader.ReadAsync();
        Assert.Null(result.Snapshot);
        Assert.DoesNotContain("secret", result.Failure);
        Assert.DoesNotContain("secret", new ClaudeOAuthCredential("secret").ToString());
    }

    [Fact]
    public async Task CancellationPropagatesInsteadOfBeingConvertedToAQuotaFailure()
    {
        using var cancellation = new CancellationTokenSource();
        var reader = new ClaudeQuotaReader(() => new ClaudeOAuthCredential("test"), async (_, token) =>
        {
            cancellation.Cancel();
            await Task.Delay(1000, token);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }, () => Now);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.ReadAsync(cancellation.Token));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{\"five_hour\":{\"utilization\":null,\"resets_at\":null}}")]
    [InlineData("{\"five_hour\":{\"utilization\":1000,\"resets_at\":\"2026-11-01T00:00:00Z\"}}")]
    public void InvalidOrAbsentWindowsDoNotBecomeZeroQuota(string json) => Assert.Null(ClaudeQuotaReader.Parse(json, Now));

    [Fact]
    public void DesktopCredentialSelectionRequiresTheActiveAccountAndFirstPartyInferenceScope()
    {
        object Entry(string token) => new { token, expiresAt = Now.AddDays(1).ToUnixTimeMilliseconds() };
        var json = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["acct:old|client:org:https://api.anthropic.com:user:inference"] = Entry("old-account"),
            ["acct:current|client:org:https://untrusted.invalid:user:inference"] = Entry("wrong-host"),
            ["acct:current|client:org:https://api.anthropic.com:user:profile"] = Entry("profile-only"),
            ["acct:current|client:org:https://api.anthropic.com:user:inference user:sessions:claude_code"] = Entry("right-account")
        });
        Assert.Equal("right-account", ClaudeOAuthCredentials.ParseDesktop(json, "current", Now)!.Token);
        Assert.Null(ClaudeOAuthCredentials.ParseDesktop(json, null, Now));
        Assert.Null(ClaudeOAuthCredentials.ParseDesktop(json, "current", Now.AddDays(2)));
    }

    [Fact]
    public void ElectronDecryptionAuthenticatesTheCiphertext()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var plain = Encoding.UTF8.GetBytes("encrypted credential fixture");
        var encrypted = new byte[31 + plain.Length];
        "v10"u8.CopyTo(encrypted);
        RandomNumberGenerator.Fill(encrypted.AsSpan(3, 12));
        using (var aes = new AesGcm(key, 16))
            aes.Encrypt(encrypted.AsSpan(3, 12), plain, encrypted.AsSpan(15, plain.Length), encrypted.AsSpan(15 + plain.Length, 16));
        Assert.Equal(plain, ClaudeOAuthCredentials.DecryptElectron(encrypted, key));
        encrypted[^1] ^= 1;
        Assert.ThrowsAny<CryptographicException>(() => ClaudeOAuthCredentials.DecryptElectron(encrypted, key));
    }
}
