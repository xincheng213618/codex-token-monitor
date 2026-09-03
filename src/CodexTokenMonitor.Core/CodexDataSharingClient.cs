using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace CodexTokenMonitor;

internal sealed class CodexDataSharingClient : IDisposable
{
    private readonly HttpClient client;

    public CodexDataSharingClient(string address, string accessKey)
    {
        CodexDataSharingProtocol.ValidateAccessKey(accessKey);
        client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false })
        {
            BaseAddress = CodexDataSharingProtocol.ParseServerAddress(address),
            Timeout = CodexDataSharingProtocol.TransferTimeout
        };
        client.DefaultRequestHeaders.Add(CodexDataSharingProtocol.KeyHeader, accessKey);
    }

    public async Task<CodexDataSharingPeer> TestConnectionAsync(CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync("api/health", cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        var peer = await response.Content.ReadFromJsonAsync<CodexDataSharingPeer>(cancellationToken).ConfigureAwait(false);
        if (peer is null || peer.Format != CodexDataSharingProtocol.Format || peer.Version != 1)
        {
            throw new InvalidDataException("对方不是兼容的 Codex 数据共享服务器。");
        }

        return peer;
    }

    public async Task<CodexDataImportResult> UploadAsync(string filePath, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(filePath);
        CodexDataSharingProtocol.CheckPackageSize(stream.Length);
        using var content = new StreamContent(stream);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var response = await client.PostAsync("api/week", content, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<CodexDataImportResult>(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("服务器没有返回合并结果。");
    }

    public async Task DownloadAsync(string filePath, CancellationToken cancellationToken)
    {
        // ResponseHeadersRead does not time out body streaming; own that timeout
        // through EOF as well, so an interrupted peer cannot hang the window.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(CodexDataSharingProtocol.TransferTimeout);
        using var response = await client.GetAsync("api/week", HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        await EnsureSuccessAsync(response, timeout.Token).ConfigureAwait(false);
        CodexDataSharingProtocol.CheckPackageSize(response.Content.Headers.ContentLength ?? 0);
        await using var source = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        await using var destination = new FileStream(filePath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await CodexDataSharingProtocol.CopyPackageAsync(source, destination, timeout.Token).ConfigureAwait(false);
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var message = (int)response.StatusCode switch
        {
            401 => "访问密钥不正确，请填写服务器显示的密钥。",
            409 => "服务器正在处理另一个数据包，请稍后重试。",
            413 => "数据包超过 256 MB，请改用文件导出和导入。",
            _ => $"服务器返回错误（HTTP {(int)response.StatusCode}）。"
        };
        if (response.Content.Headers.ContentLength is > 0 and < 4096)
        {
            try
            {
                var error = await response.Content.ReadFromJsonAsync<SharingError>(cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(error?.Error))
                {
                    message = error.Error;
                }
            }
            catch (JsonException) { }
        }

        throw new HttpRequestException(message, null, response.StatusCode);
    }

    public void Dispose() => client.Dispose();
    private sealed record SharingError(string Error);
}
