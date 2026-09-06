using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CodexTokenMonitor;

/// <summary>A passive HTTP endpoint whose lifetime is owned by the desktop app.</summary>
internal sealed class CodexDataSharingServer(
    Func<string, CancellationToken, Task<CodexDataExportResult>> exportWeek,
    Func<string, CancellationToken, Task<CodexDataImportResult>> importWeek,
    CodexHistorySharingStore? historyStore = null) : IAsyncDisposable
{
    private readonly SemaphoreSlim lifecycleGate = new(1, 1);
    private readonly SemaphoreSlim transferGate = new(1, 1);
    private WebApplication? application;
    private CancellationTokenSource? stopping;

    public bool IsRunning => application is not null;
    public int Port { get; private set; }
    public event Action<string>? Activity;

    public async Task StartAsync(int port, string accessKey, CancellationToken cancellationToken = default,
        IPAddress? listenAddress = null)
    {
        if (port is < 0 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port), "端口需为 1–65535。");
        }

        CodexDataSharingProtocol.ValidateAccessKey(accessKey);
        await lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            if (application is not null)
            {
                throw new InvalidOperationException("共享服务已经开启，请先停止。");
            }

            var expectedKey = SHA256.HashData(Encoding.UTF8.GetBytes(accessKey));
            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
            {
                Args = Array.Empty<string>(),
                ApplicationName = typeof(CodexDataSharingServer).Assembly.FullName,
                ContentRootPath = AppContext.BaseDirectory,
                EnvironmentName = Environments.Production
            });
            // The desktop app owns all endpoints and shutdown; do not inherit
            // ASPNETCORE_URLS, appsettings endpoints or console lifetime hooks.
            builder.Configuration.Sources.Clear();
            builder.Logging.ClearProviders();
            builder.Services.AddSingleton<IHostLifetime, DesktopHostLifetime>();
            builder.WebHost.ConfigureKestrel(options =>
            {
                options.AddServerHeader = false;
                options.Listen(listenAddress ?? IPAddress.Any, port);
                options.Limits.MaxRequestBodySize = CodexDataSharingProtocol.MaxPackageBytes;
                options.Limits.MaxConcurrentConnections = 12;
                options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(15);
            });
            var app = builder.Build();
            var stopSource = new CancellationTokenSource();
            app.Run(context => HandleAsync(context, expectedKey, stopSource.Token));
            try
            {
                await app.StartAsync(cancellationToken).ConfigureAwait(false);
                Port = new Uri(app.Urls.Single()).Port;
                stopping = stopSource;
                application = app;
            }
            catch
            {
                stopSource.Dispose();
                await app.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    public async Task StopAsync()
    {
        await lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (application is not { } app)
            {
                return;
            }

            stopping!.Cancel();
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await app.StopAsync(timeout.Token).ConfigureAwait(false);
            }
            finally
            {
                await app.DisposeAsync().ConfigureAwait(false);
                stopping.Dispose();
                stopping = null;
                application = null;
                Port = 0;
            }
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    private async Task HandleAsync(HttpContext context, byte[] expectedKey, CancellationToken stopToken)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        var receivedKey = context.Request.Headers[CodexDataSharingProtocol.KeyHeader].ToString();
        if (receivedKey.Length > 128 ||
            !CryptographicOperations.FixedTimeEquals(expectedKey, SHA256.HashData(Encoding.UTF8.GetBytes(receivedKey))))
        {
            await WriteErrorAsync(context, 401, "访问密钥不正确。");
            return;
        }

        if (context.Request.Path == "/api/health" && HttpMethods.IsGet(context.Request.Method))
        {
            await context.Response.WriteAsJsonAsync(new CodexDataSharingPeer(
                CodexDataSharingProtocol.Format, CodexDataSharingProtocol.Version, Environment.MachineName,
                historyStore is not null), context.RequestAborted);
            return;
        }

        var historyDates = context.Request.Path == "/api/history/dates" && historyStore is not null;
        var history = context.Request.Path == "/api/history" && historyStore is not null;
        if (context.Request.Path != "/api/week" && !history && !historyDates)
        {
            await WriteErrorAsync(context, 404, "未找到共享接口。");
            return;
        }

        var upload = HttpMethods.IsPost(context.Request.Method);
        if ((!upload && !HttpMethods.IsGet(context.Request.Method)) || (historyDates && upload))
        {
            context.Response.Headers.Allow = "GET, POST";
            await WriteErrorAsync(context, 405, "不支持此请求方法。");
            return;
        }

        if (upload && !context.Request.HasJsonContentType())
        {
            await WriteErrorAsync(context, 415, "请上传 JSON 格式的 Codex 数据包。");
            return;
        }

        if (!await transferGate.WaitAsync(0, context.RequestAborted))
        {
            await WriteErrorAsync(context, 409, "服务器正在处理另一个数据包，请稍后重试。");
            return;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, stopToken);
        timeout.CancelAfter(CodexDataSharingProtocol.TransferTimeout);
        using var temporary = new CodexSharingTemporaryFile();
        try
        {
            if (historyDates)
            {
                await context.Response.WriteAsJsonAsync(await historyStore!.GetDatesAsync(timeout.Token), timeout.Token);
                return;
            }
            CodexHistoryRange? historyRange = null;
            if (history)
            {
                if (!DateOnly.TryParseExact(context.Request.Query["start"], "yyyy-MM-dd", CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var start) ||
                    !DateOnly.TryParseExact(context.Request.Query["end"], "yyyy-MM-dd", CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var end))
                    throw new InvalidDataException("请提供有效的历史同步日期范围。");
                historyRange = new(start, end);
                historyRange.Validate();
            }
            if (upload)
            {
                CodexDataSharingProtocol.CheckPackageSize(context.Request.ContentLength ?? 0);
                await using (var stream = File.Create(temporary.FilePath))
                {
                    await CodexDataSharingProtocol.CopyPackageAsync(context.Request.Body, stream, timeout.Token);
                }

                var result = historyRange is null
                    ? await importWeek(temporary.FilePath, timeout.Token)
                    : await historyStore!.ImportAsync(temporary.FilePath, historyRange, timeout.Token);
                NotifyActivity($"收到上传：新增 {result.AddedUsageEventCount:N0} 条用量、{result.AddedQuotaSnapshotCount:N0} 条额度快照");
                await context.Response.WriteAsJsonAsync(result, timeout.Token);
            }
            else
            {
                var result = historyRange is null
                    ? await exportWeek(temporary.FilePath, timeout.Token)
                    : await historyStore!.ExportAsync(temporary.FilePath, historyRange, timeout.Token);
                await using var stream = File.OpenRead(temporary.FilePath);
                CodexDataSharingProtocol.CheckPackageSize(stream.Length);
                context.Response.ContentType = "application/json; charset=utf-8";
                context.Response.ContentLength = stream.Length;
                context.Response.Headers.ContentDisposition = "attachment; filename=codex-usage.codex.json";
                await stream.CopyToAsync(context.Response.Body, timeout.Token);
                NotifyActivity($"完成下载：{result.UsageEventCount:N0} 条用量、{result.QuotaSnapshotCount:N0} 条额度快照");
            }
        }
        catch (OperationCanceledException)
        {
            NotifyActivity("传输已取消或超时；如未收到结果，可以重新操作，重复合并不会重复计数。");
            context.Abort();
        }
        catch (BadHttpRequestException ex)
        {
            await WriteErrorAsync(context, ex.StatusCode, "上传未完成或数据包超过 256 MB。");
        }
        catch (InvalidDataException ex)
        {
            NotifyActivity("拒绝数据包：" + ex.Message);
            await WriteErrorAsync(context, 400, ex.Message);
        }
        catch (Exception)
        {
            NotifyActivity("共享失败：请检查缓存是否可读写，以及磁盘空间是否充足。");
            await WriteErrorAsync(context, 500, "服务器处理数据失败，请检查服务器上的共享状态。");
        }
        finally
        {
            transferGate.Release();
        }
    }

    private static Task WriteErrorAsync(HttpContext context, int status, string message)
    {
        if (context.Response.HasStarted || context.RequestAborted.IsCancellationRequested)
        {
            context.Abort();
            return Task.CompletedTask;
        }

        context.Response.StatusCode = status;
        return context.Response.WriteAsJsonAsync(new { error = message }, context.RequestAborted);
    }

    private void NotifyActivity(string message)
    {
        // A closed UI subscriber must never change an already committed upload
        // into a failed HTTP response.
        try { Activity?.Invoke(message); }
        catch (Exception) { }
    }

    public ValueTask DisposeAsync() => new(StopAsync());

    private sealed class DesktopHostLifetime : IHostLifetime
    {
        public Task WaitForStartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
