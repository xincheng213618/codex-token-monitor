using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using CodexTokenMonitor;

namespace CodexTokenMonitor.Wpf.Probes;

internal static class ProbeReport
{
    internal static void Write(string output, string suite, bool passed, string? error, IReadOnlyList<object> checks)
    {
        var report = JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            suite,
            processId = Environment.ProcessId,
            coreSha256 = HashAssembly(typeof(UsageSource).Assembly.Location),
            wpfSha256 = HashAssembly(typeof(MainWindow).Assembly.Location),
            passed,
            error,
            checks
        }, new JsonSerializerOptions { WriteIndented = true });
        var path = Path.Combine(output, "ui-probe-results.json");
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, report);
        File.Move(temporary, path, overwrite: true);
        Console.WriteLine(report);
    }

    private static string HashAssembly(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
