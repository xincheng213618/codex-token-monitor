namespace CodexTokenMonitor;

internal sealed record CodexDataSharingPeer(string Format, int Version, string DeviceName, bool SupportsHistory = false);

internal static class CodexDataSharingProtocol
{
    public const int Version = 4;
    public const string Format = "codex-token-monitor-sharing";
    public const string KeyHeader = "X-Codex-Sharing-Key";
    public const long MaxPackageBytes = 256L * 1024 * 1024;
    public static readonly TimeSpan TransferTimeout = TimeSpan.FromMinutes(3);

    public static Uri ParseServerAddress(string address)
    {
        address = address.Trim();
        var hasScheme = address.Contains("://", StringComparison.Ordinal);
        if (!hasScheme)
        {
            address = "http://" + address;
        }

        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || string.IsNullOrWhiteSpace(uri.Host) ||
            uri.UserInfo.Length != 0 || uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0)
        {
            throw new ArgumentException("请输入服务器地址，例如 http://192.168.1.10:36666，不要附加路径或密钥。");
        }

        // A bare IP or hostname uses the sharing port. Explicit http(s) URLs
        // preserve their port, including the scheme's default port.
        var authority = address[(address.IndexOf("://", StringComparison.Ordinal) + 3)..].TrimEnd('/');
        var hasPort = authority.StartsWith('[')
            ? authority.Contains("]:", StringComparison.Ordinal)
            : authority.Contains(':');
        if (!hasScheme && !hasPort)
        {
            return new UriBuilder(uri) { Port = CodexDataSharingSettings.DefaultPort }.Uri;
        }

        return uri;
    }

    public static void ValidateAccessKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length is < 8 or > 128 ||
            key.Any(character => character is < '!' or > '~'))
        {
            throw new ArgumentException("访问密钥需为 8–128 位可见英文字符或数字，不能包含空格。");
        }
    }

    public static async Task CopyPackageAsync(Stream source, Stream destination, CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        long total = 0;
        int count;
        while ((count = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            total += count;
            CheckPackageSize(total);
            await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
        }

        if (total == 0)
        {
            throw new InvalidDataException("数据包为空。");
        }
    }

    public static void CheckPackageSize(long length)
    {
        if (length > MaxPackageBytes)
        {
            throw new InvalidDataException("本周数据包超过 256 MB，请改用文件导出和导入。");
        }
    }
}

internal sealed class CodexSharingTemporaryFile : IDisposable
{
    public string FilePath { get; } = Path.Combine(Path.GetTempPath(), $"codex-sharing-{Guid.NewGuid():N}.codex.json");

    public void Dispose()
    {
        try
        {
            File.Delete(FilePath);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
