using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace CodexTokenMonitor.Tests;

public sealed class GitHubReleaseDownloaderTests
{
    private static readonly Version Version = typeof(GitHubReleaseDownloader).Assembly.GetName().Version!;
    private static readonly string Tag = $"v{Version.Major}.{Version.Minor}.{Version.Build}";
    private static readonly string Name = $"CodexTokenMonitor-{Tag}-win-x64.zip";
    private static readonly string DownloadUrl = $"https://github.com/{GitHubReleaseUpdateChecker.Repository}/releases/download/{Tag}/{Name}";
    private static readonly GitHubReleaseUpdateResult Release = new(new Version(0, 0, 0, 0), Version, Tag,
        new Uri($"https://github.com/{GitHubReleaseUpdateChecker.Repository}/releases/tag/{Tag}"));

    [Fact]
    public async Task DownloadAsync_VerifiesExtractsAndCleansOwnedTemporaryFiles()
    {
        var package = CreatePackage(("CodexTokenMonitor.exe", File.ReadAllBytes(typeof(GitHubReleaseDownloader).Assembly.Location)),
            ("Notices/license.txt", Encoding.UTF8.GetBytes("license")));
        using var handler = new ReleaseHandler(package);
        using var client = new HttpClient(handler);
        ReleaseDownloadProgress? reported = null;
        var update = await new GitHubReleaseDownloader(client).DownloadAsync(Release,
            new CallbackProgress(value => reported = value));
        var directory = update.DirectoryPath;
        using (update)
        {
            Assert.True(File.Exists(update.ExecutablePath));
            Assert.Equal("license", File.ReadAllText(Path.Combine(update.PayloadPath, "Notices", "license.txt")));
            Assert.False(File.Exists(Path.Combine(directory, "package.zip")));
            Assert.Equal(100, reported!.Percent);
            Assert.Equal(2, handler.RequestCount);
        }
        Assert.False(Directory.Exists(directory));
    }

    [Theory]
    [InlineData("digest")]
    [InlineData("size")]
    [InlineData("url")]
    [InlineData("tag")]
    [InlineData("prerelease")]
    [InlineData("missing")]
    [InlineData("checksum")]
    [InlineData("version")]
    public async Task DownloadAsync_RejectsUntrustedOrMismatchedPackages(string fault)
    {
        var package = CreatePackage(("CodexTokenMonitor.exe", File.ReadAllBytes(fault == "version"
            ? typeof(FactAttribute).Assembly.Location : typeof(GitHubReleaseDownloader).Assembly.Location)));
        using var handler = new ReleaseHandler(package, fault);
        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => new GitHubReleaseDownloader(client).DownloadAsync(Release));
    }

    [Fact]
    public async Task DownloadAsync_CancelsWhileDownloading()
    {
        var package = CreatePackage(("CodexTokenMonitor.exe", new byte[200000]));
        using var cancellation = new CancellationTokenSource();
        using var handler = new ReleaseHandler(package);
        using var client = new HttpClient(handler);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new GitHubReleaseDownloader(client).DownloadAsync(Release,
            new CallbackProgress(_ => cancellation.Cancel()), cancellation.Token));
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task DownloadAsync_ExplainsGitHubRateLimits(HttpStatusCode status)
    {
        using var handler = new ReleaseHandler([], status: status);
        using var client = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => new GitHubReleaseDownloader(client).DownloadAsync(Release));
        Assert.Contains("稍后重试", error.Message);
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task DownloadAsync_UsesPublicAssetDigestWhenAnonymousApiIsLimited()
    {
        var package = CreatePackage(("CodexTokenMonitor.exe", File.ReadAllBytes(typeof(GitHubReleaseDownloader).Assembly.Location)));
        using var handler = new ReleaseHandler(package, "page", HttpStatusCode.Forbidden);
        using var client = new HttpClient(handler);
        using var update = await new GitHubReleaseDownloader(client).DownloadAsync(Release);
        Assert.True(File.Exists(update.ExecutablePath));
        Assert.Equal(3, handler.RequestCount);
    }

    [Theory]
    [InlineData("page-missing-digest")]
    [InlineData("page-other-asset")]
    [InlineData("page-conflicting-digest")]
    public async Task DownloadAsync_RejectsPublicPageWithoutUnambiguousAssetDigest(string fault)
    {
        using var handler = new ReleaseHandler([], fault, HttpStatusCode.Forbidden);
        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => new GitHubReleaseDownloader(client).DownloadAsync(Release));
        Assert.Equal(2, handler.RequestCount);
    }

    [Theory]
    [InlineData("../CodexTokenMonitor.exe")]
    [InlineData("Notices/../../outside.txt")]
    [InlineData("Notices\\..\\outside.txt")]
    [InlineData("Notices/file.txt:stream")]
    [InlineData("Notices/file. ")]
    [InlineData("settings.json")]
    [InlineData("CodexTokenMonitor.exe", true)]
    [InlineData("Notices/only.txt")]
    public void ExtractPackage_RejectsUnsafeDuplicateOrIncompleteArchives(string name, bool duplicate = false)
    {
        var directory = Path.Combine(Path.GetTempPath(), "CodexTokenMonitor-package-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var archive = Path.Combine(directory, "package.zip");
            File.WriteAllBytes(archive, duplicate ? CreatePackage((name, [1]), (name, [2])) : CreatePackage((name, [1])));
            Assert.Throws<InvalidDataException>(() => GitHubReleaseDownloader.ExtractPackage(archive,
                Path.Combine(directory, "payload"), CancellationToken.None));
            Assert.False(File.Exists(Path.Combine(directory, "outside.txt")));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static byte[] CreatePackage(params (string Name, byte[] Content)[] files)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var file in files)
            {
                using var entry = zip.CreateEntry(file.Name, CompressionLevel.NoCompression).Open();
                entry.Write(file.Content);
            }
        return stream.ToArray();
    }

    private sealed class CallbackProgress(Action<ReleaseDownloadProgress> callback) : IProgress<ReleaseDownloadProgress>
    {
        public void Report(ReleaseDownloadProgress value) => callback(value);
    }

    private sealed class ReleaseHandler(byte[] package, string? fault = null, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            Assert.NotEmpty(request.Headers.UserAgent);
            if (request.RequestUri!.Host == "api.github.com")
            {
                Assert.Equal($"/repos/{GitHubReleaseUpdateChecker.Repository}/releases/tags/{Tag}", request.RequestUri.AbsolutePath);
                return Task.FromResult(new HttpResponseMessage(status)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new
                    {
                        tag_name = fault == "tag" ? "v2099.1.1" : Tag,
                        draft = false,
                        prerelease = fault == "prerelease",
                        assets = new[] { new
                        {
                            name = fault == "missing" ? "Other.zip" : Name,
                            browser_download_url = fault == "url" ? "https://example.com/Other.zip" : DownloadUrl,
                            size = fault == "size" ? package.Length + 1 : package.Length,
                            digest = fault == "digest" ? null : "sha256:" + (fault == "checksum" ? new string('0', 64) : Convert.ToHexString(SHA256.HashData(package)))
                        }}
                    }))
                });
            }
            if (request.RequestUri.AbsolutePath.Contains("/releases/expanded_assets/"))
            {
                var digest = fault == "page-missing-digest" ? "" : "sha256:" + Convert.ToHexString(SHA256.HashData(package));
                var path = fault == "page-other-asset" ? "/different.zip" : new Uri(DownloadUrl).AbsolutePath;
                var conflict = fault == "page-conflicting-digest" ? "sha256:" + new string('1', 64) : "";
                return Task.FromResult(new HttpResponseMessage(fault?.StartsWith("page") == true ? HttpStatusCode.OK : status)
                {
                    Content = new StringContent($"<li><a href=\"{path}\">{Name}</a><span>{digest}</span>{conflict}</li>")
                });
            }
            Assert.Equal(DownloadUrl, request.RequestUri.AbsoluteUri);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(package) });
        }
    }
}
