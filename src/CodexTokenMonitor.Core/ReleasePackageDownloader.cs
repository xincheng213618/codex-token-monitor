using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace CodexTokenMonitor;

// Each retry requests the original GitHub URL again, obtaining a fresh signed asset redirect.
// Partial bytes belong to this download only; the full file must still match the release digest.
internal sealed class ReleasePackageDownloader(HttpClient client,
    TimeSpan? inactivityTimeout = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
{
    internal const long MaximumPackageBytes = 512L * 1024 * 1024;
    internal const int MaximumAttempts = 5;
    private readonly TimeSpan inactivityTimeout = inactivityTimeout ?? TimeSpan.FromSeconds(30);
    private readonly Func<TimeSpan, CancellationToken, Task> delay = delay ?? Task.Delay;

    internal async Task DownloadAsync(Uri url, string archivePath, long expectedSize, string sha256,
        IProgress<ReleaseDownloadProgress>? progress, CancellationToken token)
    {
        await using var destination = new FileStream(archivePath, FileMode.CreateNew, FileAccess.ReadWrite,
            FileShare.None, 81920, useAsync: true);
        var buffer = new byte[81920];
        for (var attempt = 1; ; attempt++)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(token);
                idle.CancelAfter(inactivityTimeout);
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.UserAgent.Add(new ProductInfoHeaderValue("CodexTokenMonitor", "1.0"));
                request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("identity"));
                var offset = destination.Length;
                if (offset > 0) request.Headers.Range = new RangeHeaderValue(offset, null);
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                    idle.Token).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                var length = response.Content.Headers.ContentLength;
                if (response.StatusCode == HttpStatusCode.PartialContent)
                {
                    var range = response.Content.Headers.ContentRange;
                    if (offset == 0 || range is null || range.Unit != "bytes" || range.From != offset ||
                        range.To is not { } end || range.Length is not { } total || end < offset || end >= total ||
                        total > MaximumPackageBytes || (expectedSize > 0 && total != expectedSize) ||
                        (length.HasValue && length != end - offset + 1))
                        throw new InvalidDataException("更新包断点续传范围与已下载内容不一致。");
                    expectedSize = total;
                    length ??= end - offset + 1;
                }
                else if (response.StatusCode == HttpStatusCode.OK)
                {
                    // A server/proxy may ignore Range. Replace the partial file instead of appending a full package.
                    if (length > MaximumPackageBytes || (expectedSize > 0 && length.HasValue && length != expectedSize))
                        throw new InvalidDataException("更新包大小与 GitHub 发布信息不一致。");
                    if (expectedSize == 0) expectedSize = length ?? 0;
                    destination.SetLength(0);
                    destination.Position = 0;
                }
                else
                {
                    throw new InvalidDataException("GitHub 返回了无效的更新包下载响应。");
                }

                progress?.Report(new(destination.Length, expectedSize) { Attempt = attempt });
                await using var source = await response.Content.ReadAsStreamAsync(idle.Token).ConfigureAwait(false);
                var lastProgress = Stopwatch.StartNew();
                long responseBytes = 0;
                while (true)
                {
                    idle.CancelAfter(inactivityTimeout);
                    int read;
                    try { read = await source.ReadAsync(buffer, idle.Token).ConfigureAwait(false); }
                    catch (IOException ex) { throw new HttpRequestException("下载连接中断。", ex); }
                    if (read == 0) break;
                    responseBytes += read;
                    var received = destination.Position + read;
                    if (received > MaximumPackageBytes || (expectedSize > 0 && received > expectedSize) ||
                        (length.HasValue && responseBytes > length))
                        throw new InvalidDataException("更新包超过预期大小。");
                    // Disk errors must propagate directly instead of being treated as network failures.
                    await destination.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                    if (lastProgress.ElapsedMilliseconds >= 150 || received == expectedSize)
                    {
                        progress?.Report(new(received, expectedSize) { Attempt = attempt });
                        lastProgress.Restart();
                    }
                }
                if (destination.Length == 0 || (expectedSize > 0 && destination.Length != expectedSize) ||
                    (length.HasValue && responseBytes != length))
                    throw new HttpRequestException("下载连接提前关闭，更新包尚未下载完整。");
                break;
            }
            catch (Exception ex) when (!token.IsCancellationRequested && IsTransient(ex))
            {
                // A read can fail after the final bytes arrive. Verify those bytes without issuing bytes=size-.
                if (expectedSize > 0 && destination.Length == expectedSize) break;
                if (attempt == MaximumAttempts)
                    throw new HttpRequestException($"更新包下载连接多次中断，已自动重试 {MaximumAttempts - 1} 次。请检查网络后重试。", ex);
                progress?.Report(new(destination.Length, expectedSize) { Attempt = attempt + 1, IsRetrying = true });
                await delay(TimeSpan.FromSeconds(Math.Min(1 << (attempt - 1), 8)), token).ConfigureAwait(false);
            }
        }

        await destination.FlushAsync(token).ConfigureAwait(false);
        destination.Position = 0;
        var digest = await SHA256.HashDataAsync(destination, token).ConfigureAwait(false);
        if (!Convert.ToHexString(digest).Equals(sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("更新包 SHA-256 校验失败，请重新下载。");
        progress?.Report(new(destination.Length, destination.Length));
    }

    private static bool IsTransient(Exception error) => error is OperationCanceledException ||
        error is HttpRequestException request && (request.StatusCode is null or HttpStatusCode.RequestTimeout or
            HttpStatusCode.TooManyRequests || (int)request.StatusCode.Value >= 500);
}
