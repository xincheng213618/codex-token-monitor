using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Xunit;

namespace CodexTokenMonitor.Tests;

public sealed class ReleasePackageDownloaderTests
{
    private static readonly Uri AssetUrl = new("https://github.com/example/project/releases/download/v1/package.zip");
    private static readonly byte[] Package = Enumerable.Range(0, 180000).Select(index => (byte)(index % 251)).ToArray();
    private static readonly string Digest = Convert.ToHexString(SHA256.HashData(Package));
    private const int InterruptedAt = 71000;

    [Theory]
    [InlineData("reset")]
    [InlineData("eof")]
    [InlineData("stall")]
    public async Task DownloadAsync_ResumesAfterInterruptedBody(string fault)
    {
        using var handler = new ScriptedHandler((request, attempt) =>
        {
            Assert.Equal(AssetUrl, request.RequestUri);
            Assert.Contains(request.Headers.AcceptEncoding, value => value.Value == "identity");
            if (attempt == 1)
            {
                Assert.Null(request.Headers.Range);
                return InterruptedResponse(fault);
            }
            Assert.Equal($"bytes={InterruptedAt}-", request.Headers.Range!.ToString());
            return PartialResponse(InterruptedAt);
        });
        var reports = new List<ReleaseDownloadProgress>();
        await WithDownloadAsync(handler, async (downloader, path) =>
        {
            await downloader.DownloadAsync(AssetUrl, path, Package.Length, Digest, new CallbackProgress(reports.Add), default);
            Assert.Equal(Package, await File.ReadAllBytesAsync(path));
        });
        Assert.Equal(2, handler.RequestCount);
        Assert.Contains(reports, value => value.IsRetrying && value.Attempt == 2 && value.BytesReceived == InterruptedAt);
        Assert.Equal(100, reports[^1].Percent);
    }

    [Fact]
    public async Task DownloadAsync_RestartsWhenServerIgnoresRange()
    {
        using var handler = new ScriptedHandler((request, attempt) =>
        {
            if (attempt == 1) return InterruptedResponse("reset");
            Assert.NotNull(request.Headers.Range);
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(Package) };
        });
        await WithDownloadAsync(handler, async (downloader, path) =>
        {
            await downloader.DownloadAsync(AssetUrl, path, Package.Length, Digest, null, default);
            Assert.Equal(Package, await File.ReadAllBytesAsync(path));
        });
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task DownloadAsync_UsesResponseSizeWhenPublicPageDoesNotSupplySize()
    {
        using var handler = new ScriptedHandler((_, attempt) => attempt == 1 ? InterruptedResponse("eof") : PartialResponse(InterruptedAt));
        await WithDownloadAsync(handler, async (downloader, path) =>
        {
            await downloader.DownloadAsync(AssetUrl, path, 0, Digest, null, default);
            Assert.Equal(Package, await File.ReadAllBytesAsync(path));
        });
        Assert.Equal(2, handler.RequestCount);
    }

    [Theory]
    [InlineData("offset")]
    [InlineData("total")]
    [InlineData("length")]
    [InlineData("missing")]
    [InlineData("unit")]
    public async Task DownloadAsync_RejectsInvalidResumeResponse(string fault)
    {
        using var handler = new ScriptedHandler((_, attempt) =>
        {
            if (attempt == 1) return InterruptedResponse("reset");
            var response = PartialResponse(InterruptedAt);
            var range = response.Content.Headers.ContentRange!;
            switch (fault)
            {
                case "offset": response.Content.Headers.ContentRange = new(InterruptedAt - 1, Package.Length - 1, Package.Length); break;
                case "total": response.Content.Headers.ContentRange = new(InterruptedAt, Package.Length, Package.Length + 1); break;
                case "length": response.Content.Headers.ContentLength = 1; break;
                case "missing": response.Content.Headers.ContentRange = null; break;
                case "unit": range.Unit = "items"; break;
            }
            return response;
        });
        await WithDownloadAsync(handler, (downloader, path) => Assert.ThrowsAsync<InvalidDataException>(() =>
            downloader.DownloadAsync(AssetUrl, path, Package.Length, Digest, null, default)));
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task DownloadAsync_StillVerifiesEntireResumedPackage()
    {
        using var handler = new ScriptedHandler((_, attempt) => attempt == 1 ? InterruptedResponse("reset") : PartialResponse(InterruptedAt));
        await WithDownloadAsync(handler, (downloader, path) => Assert.ThrowsAsync<InvalidDataException>(() =>
            downloader.DownloadAsync(AssetUrl, path, Package.Length, new string('0', 64), null, default)));
        Assert.Equal(2, handler.RequestCount);
    }

    [Theory]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task DownloadAsync_RetriesTemporaryHttpErrors(HttpStatusCode status)
    {
        using var handler = new ScriptedHandler((_, attempt) => attempt == 1 ? new(status)
            : new(HttpStatusCode.OK) { Content = new ByteArrayContent(Package) });
        await WithDownloadAsync(handler, (downloader, path) =>
            downloader.DownloadAsync(AssetUrl, path, Package.Length, Digest, null, default));
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task DownloadAsync_RetriesConnectionFailureBeforeHeaders()
    {
        using var handler = new ScriptedHandler((_, attempt) => attempt == 1 ? throw new HttpRequestException("connection reset")
            : new(HttpStatusCode.OK) { Content = new ByteArrayContent(Package) });
        await WithDownloadAsync(handler, (downloader, path) =>
            downloader.DownloadAsync(AssetUrl, path, Package.Length, Digest, null, default));
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task DownloadAsync_LimitsRetriesAndUsesBackoff()
    {
        using var handler = new ScriptedHandler((_, _) => new(HttpStatusCode.BadGateway));
        var delays = new List<TimeSpan>();
        await WithDownloadAsync(handler, async (downloader, path) =>
        {
            var error = await Assert.ThrowsAsync<HttpRequestException>(() =>
                downloader.DownloadAsync(AssetUrl, path, Package.Length, Digest, null, default));
            Assert.Contains("已自动重试 4 次", error.Message);
        }, (wait, _) => { delays.Add(wait); return Task.CompletedTask; });
        Assert.Equal(5, handler.RequestCount);
        Assert.Equal(new[] { 1d, 2d, 4d, 8d }, delays.Select(value => value.TotalSeconds));
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task DownloadAsync_DoesNotRetryPermanentHttpErrors(HttpStatusCode status)
    {
        using var handler = new ScriptedHandler((_, _) => new(status));
        await WithDownloadAsync(handler, (downloader, path) => Assert.ThrowsAsync<HttpRequestException>(() =>
            downloader.DownloadAsync(AssetUrl, path, Package.Length, Digest, null, default)));
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task DownloadAsync_VerifiesBytesWhenOnlyFinalReadFails()
    {
        using var handler = new ScriptedHandler((_, _) => new(HttpStatusCode.OK)
        {
            Content = new StreamContent(new InterruptedStream(Package, "reset")) { Headers = { ContentLength = Package.Length } }
        });
        await WithDownloadAsync(handler, (downloader, path) =>
            downloader.DownloadAsync(AssetUrl, path, Package.Length, Digest, null, default));
        Assert.Equal(1, handler.RequestCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DownloadAsync_CancelsDuringReadOrBackoffWithoutRetry(bool duringBackoff)
    {
        using var cancellation = new CancellationTokenSource();
        using var handler = new ScriptedHandler((_, _) => duringBackoff ? new(HttpStatusCode.BadGateway) : new(HttpStatusCode.OK)
        {
            Content = new StreamContent(new InterruptedStream(Package[..InterruptedAt], "cancel", cancellation.Cancel))
            { Headers = { ContentLength = Package.Length } }
        });
        await WithDownloadAsync(handler, async (downloader, path) =>
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                downloader.DownloadAsync(AssetUrl, path, Package.Length, Digest, null, cancellation.Token));
        }, (_, token) => { cancellation.Cancel(); return Task.Delay(Timeout.Infinite, token); });
        Assert.Equal(1, handler.RequestCount);
    }

    private static HttpResponseMessage InterruptedResponse(string fault) => new(HttpStatusCode.OK)
    {
        Content = new StreamContent(new InterruptedStream(Package[..InterruptedAt], fault))
        { Headers = { ContentLength = Package.Length } }
    };

    private static HttpResponseMessage PartialResponse(int offset) => new(HttpStatusCode.PartialContent)
    {
        Content = new ByteArrayContent(Package[offset..])
        { Headers = { ContentRange = new ContentRangeHeaderValue(offset, Package.Length - 1, Package.Length) } }
    };

    private static async Task WithDownloadAsync(HttpMessageHandler handler,
        Func<ReleasePackageDownloader, string, Task> action, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        var directory = Path.Combine(Path.GetTempPath(), "CodexTokenMonitor-download-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var client = new HttpClient(handler, disposeHandler: false);
            var downloader = new ReleasePackageDownloader(client, TimeSpan.FromMilliseconds(100), delay ?? ((_, _) => Task.CompletedTask));
            await action(downloader, Path.Combine(directory, "package.zip"));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private sealed class CallbackProgress(Action<ReleaseDownloadProgress> callback) : IProgress<ReleaseDownloadProgress>
    {
        public void Report(ReleaseDownloadProgress value) => callback(value);
    }

    private sealed class ScriptedHandler(Func<HttpRequestMessage, int, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request, ++RequestCount));
    }

    private sealed class InterruptedStream(byte[] prefix, string fault, Action? onInterrupted = null) : MemoryStream(prefix)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position < Length) return await base.ReadAsync(buffer, cancellationToken);
            onInterrupted?.Invoke();
            if (fault == "reset") throw new IOException("Unable to read data from the transport connection: connection reset.");
            if (fault is "stall" or "cancel") await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
    }
}
