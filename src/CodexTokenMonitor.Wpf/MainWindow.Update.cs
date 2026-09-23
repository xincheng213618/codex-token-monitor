using System.Diagnostics;
using System.Net.Http;
using System.Windows;

namespace CodexTokenMonitor;

public partial class MainWindow
{
    private readonly GitHubReleaseUpdateChecker releaseUpdateChecker = new();

    private async void CheckForUpdatesButton_Click(object sender, RoutedEventArgs e)
    {
        CheckForUpdatesButton.IsEnabled = false;
        SetStatus("正在检查 GitHub Release...");
        try
        {
            var currentVersion = typeof(MainWindow).Assembly.GetName().Version
                ?? throw new InvalidOperationException("无法读取当前软件版本。");
            var result = await releaseUpdateChecker.CheckAsync(currentVersion, runtime.LifetimeToken);
            if (isClosed) return;

            if (!result.IsUpdateAvailable)
            {
                SetStatus($"已是最新版本：{result.CurrentVersion}");
                MessageBox.Show(this,
                    $"当前版本：{result.CurrentVersion}\n最新发布：{result.ReleaseTag}\n\n已经是最新版本。",
                    "检查更新", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            SetStatus($"发现新版本：{result.ReleaseTag}");
            var openRelease = MessageBox.Show(this,
                $"当前版本：{result.CurrentVersion}\n最新发布：{result.ReleaseTag}\n\n打开 GitHub 发布页下载吗？",
                "发现新版本", MessageBoxButton.YesNo, MessageBoxImage.Information);
            if (openRelease == MessageBoxResult.Yes)
            {
                Process.Start(new ProcessStartInfo(result.ReleaseUrl.AbsoluteUri) { UseShellExecute = true });
            }
        }
        catch (OperationCanceledException) when (isClosed || runtime.IsStopping)
        {
            // Closing the window cancels the request.
        }
        catch (Exception ex)
        {
            if (isClosed) return;
            var message = ex is OperationCanceledException
                ? "连接 GitHub 超时，请稍后重试。"
                : ex is HttpRequestException
                    ? $"连接 GitHub 失败：{ex.Message}"
                    : $"检查更新失败：{ex.Message}";
            SetStatus(message);
            MessageBox.Show(this, message, "检查更新", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            if (!isClosed) CheckForUpdatesButton.IsEnabled = true;
        }
    }
}
