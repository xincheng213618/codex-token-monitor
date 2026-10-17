using System.Net.Http;
using System.Windows;

namespace CodexTokenMonitor;

public partial class MainWindow
{
    private readonly GitHubReleaseUpdateChecker releaseUpdateChecker;
    private GitHubReleaseUpdateResult? availableRelease;
    private bool startupUpdateCheckStarted;
    private bool updateCheckInProgress;
    private bool updateDownloadInProgress;

    private async void CheckForUpdatesButton_Click(object sender, RoutedEventArgs e)
    {
        if (isClosed || runtime.IsStopping || updateDownloadInProgress) return;
        if (availableRelease is { } release)
        {
            await DownloadAndInstallUpdateAsync(release);
            return;
        }
        await CheckForUpdatesAsync(isAutomatic: false);
    }

    private Task CheckForUpdatesOnStartupAsync()
    {
        if (startupUpdateCheckStarted || isClosed || runtime.IsStopping) return Task.CompletedTask;
        startupUpdateCheckStarted = true;
        return CheckForUpdatesAsync(isAutomatic: true);
    }

    private async Task CheckForUpdatesAsync(bool isAutomatic)
    {
        if (updateCheckInProgress || isClosed || runtime.IsStopping) return;
        updateCheckInProgress = true;
        CheckForUpdatesButton.IsEnabled = false;
        if (!isAutomatic) SetStatus("正在检查 GitHub Release...");
        try
        {
            var currentVersion = typeof(MainWindow).Assembly.GetName().Version
                ?? throw new InvalidOperationException("无法读取当前软件版本。");
            var result = await runtime.Run<GitHubReleaseUpdateResult>("GitHub 更新检查",
                token => releaseUpdateChecker.CheckAsync(currentVersion, token));
            if (isClosed || runtime.IsStopping) return;

            if (!result.IsUpdateAvailable)
            {
                CheckForUpdatesButton.ToolTip = $"当前版本：{result.CurrentVersion}\n最新正式版：{result.ReleaseTag}";
                if (isAutomatic) return;
                SetStatus($"已是最新版本：{result.CurrentVersion}");
                MessageBox.Show(this,
                    $"当前版本：{result.CurrentVersion}\n最新发布：{result.ReleaseTag}\n\n已经是最新版本。",
                    "检查更新", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            availableRelease = result;
            ShowAvailableUpdate(result);
            if (isAutomatic) return;
            SetStatus($"发现新版本：{result.ReleaseTag}");
            var installRelease = MessageBox.Show(this,
                $"当前版本：{result.CurrentVersion}\n最新发布：{result.ReleaseTag}\n\n立即下载并更新吗？下载完成后将自动替换并重启，保留配置和用量数据。",
                "发现新版本", MessageBoxButton.YesNo, MessageBoxImage.Information);
            if (installRelease == MessageBoxResult.Yes)
            {
                await DownloadAndInstallUpdateAsync(result);
            }
        }
        catch (OperationCanceledException) when (isClosed || runtime.IsStopping)
        {
            // Closing the window cancels the request.
        }
        catch (Exception ex)
        {
            if (isClosed || runtime.IsStopping) return;
            var message = ex is OperationCanceledException
                ? "连接 GitHub 超时，请稍后重试。"
                : ex is HttpRequestException
                    ? $"连接 GitHub 失败：{ex.Message}"
                    : $"检查更新失败：{ex.Message}";
            if (isAutomatic)
            {
                CheckForUpdatesButton.ToolTip = $"启动时自动检查未完成：{message}\n点击手动重试。";
                return;
            }
            SetStatus(message);
            MessageBox.Show(this, message, "检查更新", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            updateCheckInProgress = false;
            if (!isClosed) CheckForUpdatesButton.IsEnabled = !updateDownloadInProgress;
        }
    }

    private void ShowAvailableUpdate(GitHubReleaseUpdateResult release)
    {
        CheckForUpdatesButton.Content = $"更新 {release.ReleaseTag}";
        CheckForUpdatesButton.ToolTip = $"当前版本：{release.CurrentVersion}\n最新正式版：{release.ReleaseTag}\n点击下载并更新，完成后自动替换并重启。配置和用量数据将保留。";
    }

    private async Task DownloadAndInstallUpdateAsync(GitHubReleaseUpdateResult release)
    {
        if (updateDownloadInProgress || isClosed || runtime.IsStopping) return;
        updateDownloadInProgress = true;
        CheckForUpdatesButton.IsEnabled = false;
        CheckForUpdatesButton.Content = "准备下载…";
        SetStatus($"正在获取 {release.ReleaseTag} 更新包…");
        try
        {
            var progress = new Progress<ReleaseDownloadProgress>(value =>
            {
                if (isClosed || runtime.IsStopping || !updateDownloadInProgress) return;
                CheckForUpdatesButton.Content = $"下载更新 {value.Percent}%";
                CheckForUpdatesButton.ToolTip = $"{value.BytesReceived / 1048576d:N1} / {value.TotalBytes / 1048576d:N1} MB\n下载并校验完成后自动替换并重启。";
            });
            using var prepared = await runtime.Run("下载更新", token =>
                new GitHubReleaseDownloader().DownloadAsync(release, progress, token));
            if (isClosed || runtime.IsStopping) return;
            CheckForUpdatesButton.Content = "正在安装…";
            SetStatus("更新包已校验，正在准备替换并重启…");
            await runtime.Run("准备更新安装", token => Task.Run(() => SelfUpdateInstaller.StartAsync(prepared, token), token));
            // Close through the normal shutdown path so pending settings and cache writes drain.
            if (!isClosed && !runtime.IsStopping) Close();
        }
        catch (OperationCanceledException) when (isClosed || runtime.IsStopping) { }
        catch (Exception ex)
        {
            if (isClosed || runtime.IsStopping) return;
            var message = ex is OperationCanceledException ? "下载更新超时，请稍后重试。" : $"更新失败：{ex.Message}";
            SetStatus(message);
            MessageBox.Show(this, message + "\n原程序未替换，可点击更新重试。", "下载更新",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            updateDownloadInProgress = false;
            if (!isClosed)
            {
                ShowAvailableUpdate(release);
                CheckForUpdatesButton.IsEnabled = !updateCheckInProgress;
            }
        }
    }
}
