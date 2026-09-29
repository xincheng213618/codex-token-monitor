using System.Diagnostics;
using System.Net.Http;
using System.Windows;

namespace CodexTokenMonitor;

public partial class MainWindow
{
    private readonly GitHubReleaseUpdateChecker releaseUpdateChecker;
    private GitHubReleaseUpdateResult? availableRelease;
    private bool startupUpdateCheckStarted;
    private bool updateCheckInProgress;

    private async void CheckForUpdatesButton_Click(object sender, RoutedEventArgs e)
    {
        if (isClosed || runtime.IsStopping) return;
        if (availableRelease is { } release)
        {
            try
            {
                OpenReleasePage(release);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"无法打开发布页：{ex.Message}", "检查更新",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
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
            CheckForUpdatesButton.Content = $"新版 {result.ReleaseTag} →";
            CheckForUpdatesButton.ToolTip = $"当前版本：{result.CurrentVersion}\n最新正式版：{result.ReleaseTag}\n点击打开 GitHub 发布页下载。";
            if (isAutomatic) return;
            SetStatus($"发现新版本：{result.ReleaseTag}");
            var openRelease = MessageBox.Show(this,
                $"当前版本：{result.CurrentVersion}\n最新发布：{result.ReleaseTag}\n\n打开 GitHub 发布页下载吗？",
                "发现新版本", MessageBoxButton.YesNo, MessageBoxImage.Information);
            if (openRelease == MessageBoxResult.Yes)
            {
                OpenReleasePage(result);
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
            if (!isClosed) CheckForUpdatesButton.IsEnabled = true;
        }
    }

    private static void OpenReleasePage(GitHubReleaseUpdateResult release) =>
        Process.Start(new ProcessStartInfo(release.ReleaseUrl.AbsoluteUri) { UseShellExecute = true });
}
