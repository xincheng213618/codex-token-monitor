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
        if (peer is null || peer.Format != CodexDataSharingProtocol.Format || peer.Version != CodexDataSharingProtocol.Version)
        {
            throw new InvalidDataException("两台电脑都需要使用同一新版共享协议，请更新程序后重试。");
        }

        return peer;
    }

    public async Task<CodexDataImportResult> UploadAsync(string filePath, CancellationToken cancellationToken)
        => await UploadToAsync("api/week", filePath, cancellationToken).ConfigureAwait(false);

    public async Task<CodexDataImportResult> UploadTodayAsync(string filePath, CancellationToken cancellationToken)
        => await UploadToAsync("api/today", filePath, cancellationToken).ConfigureAwait(false);

    private async Task<CodexDataImportResult> UploadToAsync(string endpoint, string filePath, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(filePath);
        CodexDataSharingProtocol.CheckPackageSize(stream.Length);
        using var content = new StreamContent(stream);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var response = await client.PostAsync(endpoint, content, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<CodexDataImportResult>(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("服务器没有返回合并结果。");
    }

    public async Task DownloadAsync(string filePath, CancellationToken cancellationToken)
        => await DownloadFromAsync("api/week", filePath, cancellationToken).ConfigureAwait(false);

    public async Task DownloadTodayAsync(string filePath, CancellationToken cancellationToken)
        => await DownloadFromAsync("api/today", filePath, cancellationToken).ConfigureAwait(false);

    private async Task DownloadFromAsync(string endpoint, string filePath, CancellationToken cancellationToken)
    {
        // ResponseHeadersRead does not time out body streaming; own that timeout
        // through EOF as well, so an interrupted peer cannot hang the window.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(CodexDataSharingProtocol.TransferTimeout);
        using var response = await client.GetAsync(endpoint, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        await EnsureSuccessAsync(response, timeout.Token).ConfigureAwait(false);
        CodexDataSharingProtocol.CheckPackageSize(response.Content.Headers.ContentLength ?? 0);
        await using var source = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        await using var destination = new FileStream(filePath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await CodexDataSharingProtocol.CopyPackageAsync(source, destination, timeout.Token).ConfigureAwait(false);
    }

    public async Task<CodexHistorySyncResult> SyncHistoryAsync(CodexHistorySharingStore localStore,
        Action<string>? progress, CancellationToken cancellationToken)
    {
        var peer = await TestConnectionAsync(cancellationToken).ConfigureAwait(false);
        if (!peer.SupportsHistory) throw new InvalidDataException("服务器不支持全部历史同步，请更新另一台电脑的程序。");
        progress?.Invoke("正在读取两台电脑已缓存的历史日期…");
        using var response = await client.GetAsync("api/history/dates", cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        var remoteDates = await response.Content.ReadFromJsonAsync<DateOnly[]>(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("服务器没有返回历史日期。");
        var localDates = await localStore.GetDatesAsync(cancellationToken).ConfigureAwait(false);
        var batches = CodexHistorySharingStore.BatchDates(localDates.Concat(remoteDates));
        var uploaded = new CodexDataImportResult(0, 0, 0, 0, 0, 0);
        var downloaded = uploaded;
        for (var i = 0; i < batches.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var range = batches[i];
            var endpoint = FormattableString.Invariant($"api/history?start={range.Start:yyyy-MM-dd}&end={range.End:yyyy-MM-dd}");
            progress?.Invoke($"全部历史 {i + 1}/{batches.Count} 批 · {range.Start:yyyy-MM-dd} 至 {range.End.AddDays(-1):yyyy-MM-dd}");
            using var upload = new CodexSharingTemporaryFile();
            await localStore.ExportAsync(upload.FilePath, range, cancellationToken).ConfigureAwait(false);
            uploaded = Add(uploaded, await UploadToAsync(endpoint, upload.FilePath, cancellationToken).ConfigureAwait(false));
            using var download = new CodexSharingTemporaryFile();
            await DownloadFromAsync(endpoint, download.FilePath, cancellationToken).ConfigureAwait(false);
            downloaded = Add(downloaded, await localStore.ImportAsync(download.FilePath, range, cancellationToken).ConfigureAwait(false));
        }
        return new(batches.Count, uploaded, downloaded);
    }

    private static CodexDataImportResult Add(CodexDataImportResult left, CodexDataImportResult right) => new(
        left.FileCount + right.FileCount, Math.Max(left.DeviceCount, right.DeviceCount),
        left.AddedUsageEventCount + right.AddedUsageEventCount, left.ExistingUsageEventCount + right.ExistingUsageEventCount,
        left.AddedQuotaSnapshotCount + right.AddedQuotaSnapshotCount, left.ExistingQuotaSnapshotCount + right.ExistingQuotaSnapshotCount);

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
