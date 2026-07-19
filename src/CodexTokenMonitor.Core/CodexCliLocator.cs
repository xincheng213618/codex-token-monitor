namespace CodexTokenMonitor;

internal sealed record CodexCliCommand(string FilePath, bool RequiresCommandShell);

/// <summary>
/// Locates runnable Codex CLI copies without assuming that the first PATH
/// entry belongs to the desktop app or can be launched by another process.
/// </summary>
internal static class CodexCliLocator
{
    public static IReadOnlyList<CodexCliCommand> FindAll()
    {
        return FindAll(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetEnvironmentVariable("PATH"),
            File.Exists);
    }

    internal static IReadOnlyList<CodexCliCommand> FindAll(
        string? applicationData,
        string? userProfile,
        string? pathEnvironment,
        Func<string, bool> fileExists)
    {
        ArgumentNullException.ThrowIfNull(fileExists);

        var results = new List<CodexCliCommand>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Codex Desktop maintains this launchable copy for local app-server
        // integrations. The package binary under Program Files\WindowsApps
        // may appear on PATH but cannot always be spawned by another desktop
        // process, so prefer this copy when it is available.
        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            AddCandidate(
                Path.Combine(userProfile, ".codex", "plugins", ".plugin-appserver", "codex.exe"),
                fileExists,
                seen,
                results);
        }

        // Keep supporting the standalone CLI installed through npm.
        if (!string.IsNullOrWhiteSpace(applicationData))
        {
            AddCandidate(
                Path.Combine(applicationData, "npm", "codex.cmd"),
                fileExists,
                seen,
                results);
        }

        if (!string.IsNullOrWhiteSpace(pathEnvironment))
        {
            foreach (var rawDirectory in pathEnvironment.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                var directory = Environment.ExpandEnvironmentVariables(rawDirectory.Trim().Trim('"'));
                if (string.IsNullOrWhiteSpace(directory))
                {
                    continue;
                }

                AddCandidate(Path.Combine(directory, "codex.exe"), fileExists, seen, results);
                AddCandidate(Path.Combine(directory, "codex.cmd"), fileExists, seen, results);
                AddCandidate(Path.Combine(directory, "codex.bat"), fileExists, seen, results);
                AddCandidate(Path.Combine(directory, "codex"), fileExists, seen, results);
            }
        }

        return results;
    }

    private static void AddCandidate(
        string candidate,
        Func<string, bool> fileExists,
        ISet<string> seen,
        ICollection<CodexCliCommand> results)
    {
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(candidate);
        }
        catch
        {
            return;
        }

        if (!fileExists(fullPath) || !seen.Add(fullPath))
        {
            return;
        }

        var extension = Path.GetExtension(fullPath);
        var requiresCommandShell =
            extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".bat", StringComparison.OrdinalIgnoreCase);
        results.Add(new CodexCliCommand(fullPath, requiresCommandShell));
    }
}
