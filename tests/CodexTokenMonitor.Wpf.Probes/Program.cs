using System.IO;
using System.Text;

namespace CodexTokenMonitor.Wpf.Probes;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        Console.InputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        if (!TryReadArguments(args, out var suite, out var output, out var error))
        {
            Console.Error.WriteLine(error);
            Console.Error.WriteLine("Usage: CodexTokenMonitor.Wpf.Probes --suite main|settings|analysis --output <new-or-empty-directory>");
            return 2;
        }

        // Validate before loading any window or initializing a configuration
        // store. Existing output is evidence and must never be overwritten.
        try
        {
            output = Path.GetFullPath(output);
            if (File.Exists(output) ||
                (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any()))
            {
                Console.Error.WriteLine("Output must be a new or empty directory: " + output);
                return 2;
            }
            Directory.CreateDirectory(output);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            Console.Error.WriteLine("Invalid output directory: " + ex.Message);
            return 2;
        }

        try
        {
            switch (suite)
            {
                case "main": MainWindowProbe.Run(output); break;
                case "settings": SettingsProbe.Run(output); break;
                case "analysis": AnalysisProbe.Run(output); break;
            }
            return Environment.ExitCode;
        }
        catch (Exception ex)
        {
            // Include bootstrap failures (for example a missing WPF resource)
            // which occur before the suite's dispatcher callback can report.
            try { ProbeReport.Write(output, suite, false, ex.ToString(), Array.Empty<object>()); }
            catch (Exception reportError) { Console.Error.WriteLine("Could not write probe report: " + reportError); }
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static bool TryReadArguments(string[] args, out string suite, out string output, out string error)
    {
        suite = output = "";
        error = "Expected exactly one --suite and one --output argument.";
        if (args.Length != 4) return false;
        for (var index = 0; index < args.Length; index += 2)
        {
            var value = args[index + 1];
            if (string.IsNullOrWhiteSpace(value) || value.StartsWith("--", StringComparison.Ordinal)) return false;
            switch (args[index])
            {
                case "--suite" when suite.Length == 0: suite = value; break;
                case "--output" when output.Length == 0: output = value; break;
                default: return false;
            }
        }
        if (suite is not ("main" or "settings" or "analysis"))
        {
            error = "Unknown suite. Supported suites: main, settings, analysis.";
            return false;
        }
        return output.Length > 0;
    }
}
