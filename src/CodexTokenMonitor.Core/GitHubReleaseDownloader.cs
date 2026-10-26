using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodexTokenMonitor;

public sealed record ReleaseDownloadProgress(long BytesReceived, long TotalBytes)
{
    public int Percent => TotalBytes <= 0 ? 0 : (int)Math.Min(100, BytesReceived * 100 / TotalBytes);
    public int Attempt { get; init; } = 1;
    public bool IsRetrying { get; init; }
}

public sealed class PreparedReleaseUpdate : IDisposable
{
    internal PreparedReleaseUpdate(string directory) => DirectoryPath = directory;
    public string DirectoryPath { get; }
    public string PayloadPath => Path.Combine(DirectoryPath, "payload");
    public string ExecutablePath => Path.Combine(PayloadPath, "CodexTokenMonitor.exe");
    internal bool HandedOff { get; set; }

    public void Dispose()
    {
        if (HandedOff) return;
        try { Directory.Delete(DirectoryPath, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

public sealed class GitHubReleaseDownloader(HttpClient? client = null)
{
    private const long MaximumPackageBytes = ReleasePackageDownloader.MaximumPackageBytes;
    private static readonly HttpClient SharedClient = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly HttpClient client = client ?? SharedClient;

    public async Task<PreparedReleaseUpdate> DownloadAsync(GitHubReleaseUpdateResult release,
        IProgress<ReleaseDownloadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var asset = await GetAssetAsync(release, cancellationToken).ConfigureAwait(false);
        var directory = Path.Combine(Path.GetTempPath(), "CodexTokenMonitor-update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var prepared = new PreparedReleaseUpdate(directory);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(15));
            var token = timeout.Token;
            var archivePath = Path.Combine(directory, "package.zip");
            await new ReleasePackageDownloader(client).DownloadAsync(asset.Url, archivePath,
                asset.Size, asset.Sha256, progress, token).ConfigureAwait(false);

            await Task.Run(() =>
            {
                ExtractPackage(archivePath, prepared.PayloadPath, token);
                var info = FileVersionInfo.GetVersionInfo(prepared.ExecutablePath);
                var version = new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart, info.FilePrivatePart);
                if (version != release.LatestVersion)
                    throw new InvalidDataException("更新包中的程序版本与发布版本不一致。");
            }, token).ConfigureAwait(false);
            File.Delete(archivePath);
            return prepared;
        }
        catch
        {
            prepared.Dispose();
            throw;
        }
    }

    private async Task<ReleaseAsset> GetAssetAsync(GitHubReleaseUpdateResult release, CancellationToken cancellationToken)
    {
        if (!GitHubReleaseUpdateChecker.TryParseReleaseVersion(release.ReleaseTag, out var version) || version != release.LatestVersion)
            throw new InvalidDataException("更新版本号无效。");
        var repository = GitHubReleaseUpdateChecker.Repository;
        var tag = Uri.EscapeDataString(release.ReleaseTag);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var request = CreateRequest(new Uri($"https://api.github.com/repos/{repository}/releases/tags/{tag}"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var response = await client.SendAsync(request, timeout.Token).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            return await GetAssetFromReleasePageAsync(release.ReleaseTag, timeout.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false));
        var root = document.RootElement;
        if (root.GetProperty("tag_name").GetString() != release.ReleaseTag ||
            root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean())
            throw new InvalidDataException("GitHub 返回的正式发布信息不匹配。");
        var name = $"CodexTokenMonitor-{release.ReleaseTag}-win-x64.zip";
        foreach (var item in root.GetProperty("assets").EnumerateArray())
        {
            if (item.GetProperty("name").GetString() != name) continue;
            var expectedUrl = $"https://github.com/{repository}/releases/download/{tag}/{Uri.EscapeDataString(name)}";
            var size = item.GetProperty("size").GetInt64();
            var digest = item.TryGetProperty("digest", out var value) ? value.GetString() : null;
            if (item.GetProperty("browser_download_url").GetString() != expectedUrl || size <= 0 || size > MaximumPackageBytes ||
                digest is null || !digest.StartsWith("sha256:", StringComparison.Ordinal) ||
                digest.Length != 71 || !digest[7..].All(char.IsAsciiHexDigit))
                throw new InvalidDataException("GitHub 更新包缺少有效的下载地址、大小或 SHA-256 校验信息。");
            return new(new Uri(expectedUrl), size, digest[7..]);
        }
        throw new InvalidDataException("此版本尚未提供 Windows x64 更新包，请稍后重试。");
    }

    private async Task<ReleaseAsset> GetAssetFromReleasePageAsync(string releaseTag, CancellationToken token)
    {
        // GitHub also displays each asset's immutable digest on the public release page.
        // Use that same digest when the anonymous REST quota is exhausted on a shared network.
        var repository = GitHubReleaseUpdateChecker.Repository;
        var tag = Uri.EscapeDataString(releaseTag);
        var name = $"CodexTokenMonitor-{releaseTag}-win-x64.zip";
        var href = $"/{repository}/releases/download/{tag}/{Uri.EscapeDataString(name)}";
        using var request = CreateRequest(new Uri($"https://github.com/{repository}/releases/expanded_assets/{tag}"));
        using var response = await client.SendAsync(request, token).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            throw new HttpRequestException("GitHub 暂时限制下载信息请求，请稍后重试。", null, response.StatusCode);
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
        var matchingRows = new List<string>();
        foreach (Match row in Regex.Matches(html, @"<li\b[^>]*>(.*?)</li>", RegexOptions.Singleline | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2)))
        {
            foreach (Match link in Regex.Matches(row.Groups[1].Value, "<a\\b[^>]*\\bhref\\s*=\\s*[\"']([^\"']*)[\"']", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2)))
                if (WebUtility.HtmlDecode(link.Groups[1].Value) == href)
                {
                    matchingRows.Add(row.Groups[1].Value);
                    break;
                }
        }
        if (matchingRows.Count != 1) throw new InvalidDataException("无法从 GitHub 发布页识别唯一的 Windows x64 更新包。");
        var digests = Regex.Matches(matchingRows[0], @"sha256:([a-fA-F0-9]{64})(?![a-fA-F0-9])", RegexOptions.None, TimeSpan.FromSeconds(2))
            .Select(match => match.Groups[1].Value).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (digests.Length != 1) throw new InvalidDataException("GitHub 更新包缺少唯一有效的 SHA-256 校验信息。");
        return new(new Uri("https://github.com" + href), 0, digests[0]);
    }

    internal static void ExtractPackage(string archivePath, string payloadPath, CancellationToken token)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        if (archive.Entries.Count > 1024) throw new InvalidDataException("更新包文件数量异常。");
        Directory.CreateDirectory(payloadPath);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long totalSize = 0;
        foreach (var entry in archive.Entries)
        {
            token.ThrowIfCancellationRequested();
            var name = entry.FullName.Replace('\\', '/');
            var parts = name.TrimEnd('/').Split('/');
            if (parts.Any(part => part.Length == 0 || part is "." or ".." ||
                part.EndsWith('.') || part.EndsWith(' ') || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) ||
                !(name == "CodexTokenMonitor.exe" || name.StartsWith("Notices/", StringComparison.Ordinal)) ||
                !names.Add(name.TrimEnd('/')))
                throw new InvalidDataException("更新包包含无效或重复的文件路径。");
            totalSize += entry.Length;
            if (totalSize > MaximumPackageBytes * 2) throw new InvalidDataException("更新包解压后过大。");
            var target = Path.GetFullPath(Path.Combine(payloadPath, Path.Combine(parts)));
            if (!target.StartsWith(Path.GetFullPath(payloadPath) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("更新包路径超出临时目录。");
            if (name.EndsWith('/')) { Directory.CreateDirectory(target); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target);
        }
        if (!File.Exists(Path.Combine(payloadPath, "CodexTokenMonitor.exe")))
            throw new InvalidDataException("更新包中没有 CodexTokenMonitor.exe。");
    }

    private static HttpRequestMessage CreateRequest(Uri url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("CodexTokenMonitor", "1.0"));
        return request;
    }

    private sealed record ReleaseAsset(Uri Url, long Size, string Sha256);
}
