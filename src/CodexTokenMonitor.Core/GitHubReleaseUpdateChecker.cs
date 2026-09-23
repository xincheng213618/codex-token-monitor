using System.Net.Http.Headers;

namespace CodexTokenMonitor;

public sealed record GitHubReleaseUpdateResult(
    Version CurrentVersion,
    Version LatestVersion,
    string ReleaseTag,
    Uri ReleaseUrl)
{
    public bool IsUpdateAvailable => LatestVersion > CurrentVersion;
}

public sealed class GitHubReleaseUpdateChecker
{
    private const string Repository = "xincheng213618/codex-token-monitor";
    private static readonly Uri LatestReleasePage = new(
        $"https://github.com/{Repository}/releases/latest");
    private static readonly HttpClient SharedClient = new(
        new HttpClientHandler { AllowAutoRedirect = false });
    private readonly HttpClient client;

    public GitHubReleaseUpdateChecker(HttpClient? client = null)
    {
        this.client = client ?? SharedClient;
    }

    public async Task<GitHubReleaseUpdateResult> CheckAsync(
        Version currentVersion,
        CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var requestToken = timeout.Token;
        // GitHub redirects this public page to the latest stable release tag.
        // The REST API's anonymous rate limit can be exhausted on shared networks.
        using var request = new HttpRequestMessage(HttpMethod.Head, LatestReleasePage);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("CodexTokenMonitor", "1.0"));
        using var response = await client.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, requestToken).ConfigureAwait(false);
        if ((int)response.StatusCode is < 300 or >= 400)
        {
            response.EnsureSuccessStatusCode();
            throw new InvalidDataException("GitHub 最新发布页没有返回版本跳转。");
        }

        var location = response.Headers.Location;
        if (location is null)
            throw new InvalidDataException("GitHub 最新发布页没有返回发布地址。");

        var releaseUrl = new Uri(LatestReleasePage, location);
        var releasePathPrefix = $"/{Repository}/releases/tag/";
        if (!releaseUrl.AbsolutePath.StartsWith(releasePathPrefix, StringComparison.OrdinalIgnoreCase) ||
            releaseUrl.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(releaseUrl.Host, "github.com", StringComparison.OrdinalIgnoreCase) ||
            releaseUrl.Port != 443 ||
            releaseUrl.UserInfo.Length != 0)
        {
            throw new InvalidDataException("GitHub Release 返回了无效的发布地址。");
        }

        var tag = Uri.UnescapeDataString(releaseUrl.AbsolutePath[releasePathPrefix.Length..]);
        if (!TryParseReleaseVersion(tag, out var latestVersion))
        {
            throw new InvalidDataException("GitHub Release 返回了无法识别的版本号。");
        }

        return new GitHubReleaseUpdateResult(
            NormalizeVersion(currentVersion), latestVersion, tag, releaseUrl);
    }

    public static bool TryParseReleaseVersion(string? tag, out Version version)
    {
        version = new Version(0, 0, 0, 0);
        if (string.IsNullOrWhiteSpace(tag)) return false;
        var value = tag.Trim().TrimStart('v', 'V');
        var parts = value.Split('.');
        if (parts.Length is < 3 or > 4) return false;
        var numbers = new int[4];
        for (var index = 0; index < parts.Length; index++)
        {
            if (parts[index].Length == 0 ||
                !parts[index].All(char.IsAsciiDigit) ||
                !int.TryParse(parts[index], out numbers[index]))
                return false;
        }

        version = new Version(numbers[0], numbers[1], numbers[2], numbers[3]);
        return true;
    }

    private static Version NormalizeVersion(Version version) => new(
        version.Major,
        version.Minor,
        Math.Max(version.Build, 0),
        Math.Max(version.Revision, 0));
}
