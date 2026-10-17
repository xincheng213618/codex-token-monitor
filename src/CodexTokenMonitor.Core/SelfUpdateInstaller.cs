using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodexTokenMonitor;

public static class SelfUpdateInstaller
{
    public static async Task StartAsync(PreparedReleaseUpdate update, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        using var current = Process.GetCurrentProcess();
        var target = Environment.ProcessPath ?? throw new InvalidOperationException("无法读取当前程序路径。");
        if (!Path.GetFileName(target).Equals("CodexTokenMonitor.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("请从发布的 CodexTokenMonitor.exe 中执行自动更新。");
        var directory = update.DirectoryPath;
        var planPath = Path.Combine(directory, "plan.json");
        var files = Directory.GetFiles(update.PayloadPath, "*", SearchOption.AllDirectories)
            .Select(path => new { RelativePath = Path.GetRelativePath(update.PayloadPath, path), Sha256 = HashFile(path) }).ToArray();
        await File.WriteAllTextAsync(planPath, JsonSerializer.Serialize(new
        {
            TargetPath = target,
            ProcessId = current.Id,
            ProcessStartTicks = current.StartTime.ToUniversalTime().Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture),
            OriginalSha256 = HashFile(target),
            Files = files
        }), cancellationToken).ConfigureAwait(false);
        var scriptPath = Path.Combine(directory, "install.ps1");
        await using (var resource = typeof(SelfUpdateInstaller).Assembly.GetManifestResourceStream("CodexTokenMonitor.SelfUpdate.ps1")
            ?? throw new InvalidOperationException("缺少更新安装程序。"))
        using (var reader = new StreamReader(resource))
            await File.WriteAllTextAsync(scriptPath, await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), cancellationToken).ConfigureAwait(false);

        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = directory
        };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", scriptPath, "-PlanPath", planPath })
            start.ArgumentList.Add(argument);
        using var helper = Process.Start(start) ?? throw new InvalidOperationException("无法启动更新安装程序。");
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            while (!File.Exists(Path.Combine(directory, "ready")))
            {
                if (helper.HasExited)
                {
                    var errorPath = Path.Combine(directory, "error.txt");
                    throw new IOException(File.Exists(errorPath) ? await File.ReadAllTextAsync(errorPath, timeout.Token).ConfigureAwait(false)
                        : "更新安装程序未能启动。");
                }
                await Task.Delay(100, timeout.Token).ConfigureAwait(false);
            }
            update.HandedOff = true;
        }
        catch
        {
            if (!helper.HasExited) helper.Kill();
            await helper.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
