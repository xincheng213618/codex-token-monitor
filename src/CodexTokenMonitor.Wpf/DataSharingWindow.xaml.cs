using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Windows;

namespace CodexTokenMonitor;

internal partial class DataSharingWindow : Window
{
    private readonly CodexDataSharingServer server;
    private readonly Func<string, CancellationToken, Task<CodexDataExportResult>> exportWeek;
    private readonly Func<string, CancellationToken, Task<CodexDataImportResult>> importWeek;
    private readonly Func<string, CancellationToken, Task<CodexDataExportResult>> exportToday;
    private readonly Func<string, CancellationToken, Task<CodexDataImportResult>> importToday;
    private readonly CodexHistorySharingStore historyStore;
    private readonly SemaphoreSlim clientTransferGate;
    private readonly Action automaticUploadSettingsChanged;
    private readonly CodexDataSharingSettings settings = CodexDataSharingSettings.Load();
    private readonly CancellationTokenSource lifetime;
    private readonly MonitorRuntime? runtime;
    private readonly Queue<string> activity = new();
    private CancellationTokenSource? operation;
    private bool closed;
    private bool changingServer;

    public DataSharingWindow(CodexDataSharingServer server,
        Func<string, CancellationToken, Task<CodexDataExportResult>> exportWeek,
        Func<string, CancellationToken, Task<CodexDataImportResult>> importWeek,
        Func<string, CancellationToken, Task<CodexDataExportResult>> exportToday,
        Func<string, CancellationToken, Task<CodexDataImportResult>> importToday,
        CodexHistorySharingStore historyStore,
        SemaphoreSlim clientTransferGate,
        Action automaticUploadSettingsChanged,
        CancellationToken cancellationToken,
        MonitorRuntime? runtime = null)
    {
        InitializeComponent();
        this.server = server;
        this.exportWeek = exportWeek;
        this.importWeek = importWeek;
        this.exportToday = exportToday;
        this.importToday = importToday;
        this.historyStore = historyStore;
        this.clientTransferGate = clientTransferGate;
        this.automaticUploadSettingsChanged = automaticUploadSettingsChanged;
        this.runtime = runtime;
        lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        PortBox.Text = settings.Port.ToString(CultureInfo.InvariantCulture);
        LocalKeyBox.Text = settings.AccessKey;
        RemoteAddressBox.Text = settings.ServerAddress;
        RemoteKeyBox.Text = settings.ServerAccessKey;
        AutoUploadTodayBox.IsChecked = settings.AutoUploadToday;
        AutoUploadIntervalBox.Text = settings.AutoUploadIntervalHours.ToString(CultureInfo.InvariantCulture);
        AutoStartBox.IsChecked = settings.AutoStart;
        AutoStartBox.Checked += SaveAutoStart;
        AutoStartBox.Unchecked += SaveAutoStart;
        server.Activity += OnServerActivity;
        UpdateServerState();
        Closed += (_, _) =>
        {
            closed = true;
            server.Activity -= OnServerActivity;
            lifetime.Cancel();
            lifetime.Dispose();
        };
    }

    private void SaveAutoStart(object sender, RoutedEventArgs e)
    {
        settings.AutoStart = AutoStartBox.IsChecked == true;
        try { settings.Save(); }
        catch (Exception ex) { AddActivity($"保存自动开启设置失败：{ex.Message}"); }
    }

    private async void ServerToggleButton_Click(object sender, RoutedEventArgs e)
    {
        await RunWindowOperationAsync("共享服务启停", ToggleServerAsync);
    }

    private async Task ToggleServerAsync()
    {
        changingServer = true;
        UpdateServerState();
        try
        {
            if (server.IsRunning)
            {
                await server.StopAsync();
                AddActivity("本机共享服务已停止。");
            }
            else
            {
                if (!int.TryParse(PortBox.Text, out var port) || port is < 1 or > 65535)
                {
                    throw new ArgumentException("端口需为 1–65535。默认端口为 36666。");
                }

                settings.Port = port;
                settings.Save();
                await server.StartAsync(port, settings.AccessKey, lifetime.Token);
                AddActivity($"本机共享服务已开启，端口 {server.Port}，等待其他电脑操作。");
            }
        }
        catch (OperationCanceledException) when (closed) { }
        catch (Exception ex)
        {
            AddActivity($"服务启停失败：{ex.Message} 若端口被占用，请更换端口再试。");
        }
        finally
        {
            changingServer = false;
            if (!closed)
            {
                UpdateServerState();
            }
        }
    }

    private void UpdateServerState()
    {
        ServerToggleButton.IsEnabled = !changingServer;
        ServerToggleButton.Content = server.IsRunning ? "停止服务" : "开启服务";
        PortBox.IsEnabled = !server.IsRunning && !changingServer;
        ServerStateText.Text = server.IsRunning ? $"已开启 · 端口 {server.Port}" : "未开启";
        if (server.IsRunning)
        {
            try
            {
                var addresses = NetworkInterface.GetAllNetworkInterfaces()
                    .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up)
                    .SelectMany(adapter => adapter.GetIPProperties().UnicastAddresses)
                    .Select(item => item.Address)
                    .Where(ip => ip.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip))
                    .Select(ip => $"http://{ip}:{server.Port}")
                    .Distinct().ToArray();
                LocalAddressesBox.Text = addresses.Length > 0
                    ? string.Join(Environment.NewLine, addresses)
                    : $"http://{Environment.MachineName}:{server.Port}";
            }
            catch (NetworkInformationException)
            {
                LocalAddressesBox.Text = $"http://{Environment.MachineName}:{server.Port}";
            }
        }
        else
        {
            LocalAddressesBox.Text = "开启服务后显示可供其他电脑连接的地址";
        }
    }

    private void CopyKeyButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Windows.Clipboard.SetText(settings.AccessKey);
            OperationText.Text = "访问密钥已复制。请在另一台电脑的共享窗口粘贴。";
        }
        catch (Exception ex)
        {
            OperationText.Text = $"复制失败，请手动选中密钥复制：{ex.Message}";
        }
    }

    private async void TestButton_Click(object sender, RoutedEventArgs e) => await RunTransferAsync("测试连接", async (client, token) =>
    {
        var peer = await client.TestConnectionAsync(token);
        return peer.SupportsToday
            ? $"已连接：{peer.DeviceName}。可以同步今天、最近 8 天或全部历史数据。"
            : $"已连接：{peer.DeviceName}。另一台电脑需更新后才能使用今天快速同步。";
    });

    private void SaveAutoUploadButton_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(AutoUploadIntervalBox.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var hours) ||
            hours is < 1 or > CodexDataSharingSettings.MaxAutoUploadIntervalHours)
        {
            OperationText.Text = $"自动上传间隔需为 1–{CodexDataSharingSettings.MaxAutoUploadIntervalHours} 小时。";
            AutoUploadIntervalBox.Focus();
            AutoUploadIntervalBox.SelectAll();
            return;
        }

        var enabled = AutoUploadTodayBox.IsChecked == true;
        var address = RemoteAddressBox.Text.Trim();
        var accessKey = RemoteKeyBox.Text.Trim();
        try
        {
            if (enabled)
            {
                _ = CodexDataSharingProtocol.ParseServerAddress(address);
                CodexDataSharingProtocol.ValidateAccessKey(accessKey);
            }

            settings.ServerAddress = address;
            settings.ServerAccessKey = accessKey;
            settings.AutoUploadToday = enabled;
            settings.AutoUploadIntervalHours = hours;
            settings.Save();
            automaticUploadSettingsChanged();
            AddActivity(enabled
                ? $"自动上传已开启：程序运行期间每 {hours} 小时上传今天数据。"
                : "自动上传已关闭。");
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            OperationText.Text = $"保存自动上传设置失败：{ex.Message}";
        }
    }

    private async void TodaySyncButton_Click(object sender, RoutedEventArgs e) => await RunTransferAsync("双向同步今天（1 天）", async (client, token) =>
    {
        var peer = await client.TestConnectionAsync(token);
        if (!peer.SupportsToday)
        {
            throw new InvalidDataException("服务器不支持今天快速同步，请更新另一台电脑的程序后重试。");
        }
        using var upload = new CodexSharingTemporaryFile();
        await exportToday(upload.FilePath, token);
        var uploaded = await client.UploadTodayAsync(upload.FilePath, token);
        using var download = new CodexSharingTemporaryFile();
        await client.DownloadTodayAsync(download.FilePath, token);
        var imported = await importToday(download.FilePath, token);
        return FormatResult("服务器已合并", uploaded) + Environment.NewLine + FormatResult("本机已合并", imported);
    });

    private async void SyncButton_Click(object sender, RoutedEventArgs e) => await RunTransferAsync("双向同步最近 8 天", async (client, token) =>
    {
        await client.TestConnectionAsync(token);
        using var upload = new CodexSharingTemporaryFile();
        await exportWeek(upload.FilePath, token);
        var uploaded = await client.UploadAsync(upload.FilePath, token);
        using var download = new CodexSharingTemporaryFile();
        await client.DownloadAsync(download.FilePath, token);
        var imported = await importWeek(download.FilePath, token);
        return FormatResult("服务器已合并", uploaded) + Environment.NewLine + FormatResult("本机已合并", imported);
    });

    private async void UploadButton_Click(object sender, RoutedEventArgs e) => await RunTransferAsync("上传最近 8 天数据", async (client, token) =>
    {
        await client.TestConnectionAsync(token);
        using var temporary = new CodexSharingTemporaryFile();
        await exportWeek(temporary.FilePath, token);
        var result = await client.UploadAsync(temporary.FilePath, token);
        return FormatResult("服务器已合并", result);
    });

    private async void HistorySyncButton_Click(object sender, RoutedEventArgs e) => await RunTransferAsync("双向同步全部历史", async (client, token) =>
    {
        var result = await client.SyncHistoryAsync(historyStore, message =>
        {
            if (!Dispatcher.HasShutdownStarted)
                _ = Dispatcher.BeginInvoke(new Action(() => { if (!closed) OperationText.Text = message; }));
        }, token);
        // Finish with the normal recent path so today's actively appended logs
        // are refreshed on both computers after the cached history batches.
        using var upload = new CodexSharingTemporaryFile();
        await exportWeek(upload.FilePath, token);
        var recentUploaded = await client.UploadAsync(upload.FilePath, token);
        using var download = new CodexSharingTemporaryFile();
        await client.DownloadAsync(download.FilePath, token);
        var recentImported = await importWeek(download.FilePath, token);
        return $"全部历史已同步（{result.BatchCount:N0} 批），最近 8 天已刷新。" + Environment.NewLine +
            FormatResult("历史：服务器已合并", result.Uploaded) + Environment.NewLine +
            FormatResult("历史：本机已合并", result.Downloaded) + Environment.NewLine +
            FormatResult("最近：服务器已合并", recentUploaded) + Environment.NewLine +
            FormatResult("最近：本机已合并", recentImported);
    });

    private async void DownloadButton_Click(object sender, RoutedEventArgs e) => await RunTransferAsync("下载并合并最近 8 天数据", async (client, token) =>
    {
        await client.TestConnectionAsync(token);
        using var temporary = new CodexSharingTemporaryFile();
        await client.DownloadAsync(temporary.FilePath, token);
        var result = await importWeek(temporary.FilePath, token);
        return FormatResult("本机已合并", result);
    });

    private Task RunTransferAsync(string label, Func<CodexDataSharingClient, CancellationToken, Task<string>> action)
        => RunWindowOperationAsync(label, () => RunTransferCoreAsync(label, action));

    private async Task RunWindowOperationAsync(string label, Func<Task> action)
    {
        if (closed || runtime?.IsStopping == true) return;
        try
        {
            if (runtime is null) await action();
            else await runtime.Run(label, _ => action());
        }
        catch (OperationCanceledException) when (closed || runtime?.IsStopping == true) { }
    }

    private async Task RunTransferCoreAsync(string label, Func<CodexDataSharingClient, CancellationToken, Task<string>> action)
    {
        if (operation is not null)
        {
            return;
        }

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        operation = cancellation;
        SetTransferBusy(true);
        OperationText.Text = $"正在{label}…";
        var enteredTransferGate = false;
        try
        {
            await clientTransferGate.WaitAsync(cancellation.Token);
            enteredTransferGate = true;
            settings.ServerAddress = RemoteAddressBox.Text.Trim();
            settings.ServerAccessKey = RemoteKeyBox.Text.Trim();
            using var client = new CodexDataSharingClient(settings.ServerAddress, settings.ServerAccessKey);
            settings.Save();
            automaticUploadSettingsChanged();
            AddActivity(await action(client, cancellation.Token));
        }
        catch (OperationCanceledException)
        {
            AddActivity(cancellation.IsCancellationRequested
                ? "操作已取消。若服务器已经完成合并，可以重新操作确认结果，重复数据不会重复计数。"
                : "连接或传输超时，请检查网络及服务器状态后重试。");
        }
        catch (HttpRequestException ex)
        {
            AddActivity($"{label}失败：{ex.Message}" + (ex.StatusCode is null
                ? " 请检查服务器地址、端口、防火墙，以及服务是否已开启。" : ""));
        }
        catch (Exception ex)
        {
            AddActivity($"{label}失败：{ex.Message}");
        }
        finally
        {
            if (enteredTransferGate)
            {
                clientTransferGate.Release();
            }
            operation = null;
            if (!closed)
            {
                SetTransferBusy(false);
            }
        }
    }

    private static string FormatResult(string target, CodexDataImportResult result) =>
        $"{target}：新增 {result.AddedUsageEventCount:N0} 条用量、{result.AddedQuotaSnapshotCount:N0} 条额度快照；" +
        $"跳过 {result.ExistingUsageEventCount:N0} 条已有用量、{result.ExistingQuotaSnapshotCount:N0} 条已有快照。";

    private void CancelButton_Click(object sender, RoutedEventArgs e) => operation?.Cancel();

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void SetTransferBusy(bool busy)
    {
        TestButton.IsEnabled = TodaySyncButton.IsEnabled = SyncButton.IsEnabled = HistorySyncButton.IsEnabled = UploadButton.IsEnabled = DownloadButton.IsEnabled = !busy;
        SaveAutoUploadButton.IsEnabled = AutoUploadTodayBox.IsEnabled = AutoUploadIntervalBox.IsEnabled = !busy;
        RemoteAddressBox.IsEnabled = RemoteKeyBox.IsEnabled = !busy;
        CancelButton.IsEnabled = busy;
    }

    private void OnServerActivity(string message)
    {
        if (!Dispatcher.HasShutdownStarted)
        {
            _ = Dispatcher.BeginInvoke(new Action(() => AddActivity(message)));
        }
    }

    private void AddActivity(string message)
    {
        if (closed)
        {
            return;
        }

        activity.Enqueue($"{BeijingClock.DateTimeNow:HH:mm:ss}  {message}");
        while (activity.Count > 30)
        {
            activity.Dequeue();
        }

        ActivityBox.Text = string.Join(Environment.NewLine, activity);
        ActivityBox.ScrollToEnd();
        OperationText.Text = message;
    }

    internal void ReportAutomaticUpload(string message)
    {
        if (closed)
        {
            return;
        }

        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(new Action(() => ReportAutomaticUpload(message)));
            return;
        }

        AddActivity(message);
    }
}
