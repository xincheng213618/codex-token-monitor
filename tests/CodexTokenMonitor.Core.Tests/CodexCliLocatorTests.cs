using Xunit;

namespace CodexTokenMonitor.Tests;

public sealed class CodexCliLocatorTests
{
    [Fact]
    public void FindAll_PrefersDesktopAppServerCopyWhenMultipleInstallationsExist()
    {
        var userProfile = Path.GetFullPath(Path.Combine("test-root", "user"));
        var applicationData = Path.GetFullPath(Path.Combine("test-root", "app-data"));
        var pathDirectory = Path.GetFullPath(Path.Combine("test-root", "path"));
        var desktopCli = Path.Combine(userProfile, ".codex", "plugins", ".plugin-appserver", "codex.exe");
        var npmCli = Path.Combine(applicationData, "npm", "codex.cmd");
        var pathCli = Path.Combine(pathDirectory, "codex.exe");
        var existing = new HashSet<string>(new[] { desktopCli, npmCli, pathCli }, StringComparer.OrdinalIgnoreCase);

        var commands = CodexCliLocator.FindAll(
            applicationData,
            userProfile,
            pathDirectory,
            existing.Contains);

        Assert.Equal(new[] { desktopCli, npmCli, pathCli }, commands.Select(item => item.FilePath));
        Assert.False(commands[0].RequiresCommandShell);
        Assert.True(commands[1].RequiresCommandShell);
        Assert.False(commands[2].RequiresCommandShell);
    }

    [Fact]
    public void FindAll_FallsBackToNpmCliWhenDesktopCopyIsMissing()
    {
        var userProfile = Path.GetFullPath(Path.Combine("test-root", "user"));
        var applicationData = Path.GetFullPath(Path.Combine("test-root", "app-data"));
        var npmCli = Path.Combine(applicationData, "npm", "codex.cmd");

        var command = Assert.Single(CodexCliLocator.FindAll(
            applicationData,
            userProfile,
            null,
            path => string.Equals(path, npmCli, StringComparison.OrdinalIgnoreCase)));

        Assert.Equal(npmCli, command.FilePath);
        Assert.True(command.RequiresCommandShell);
    }

    [Fact]
    public void FindAll_DeduplicatesKnownLocationAndPathEntry()
    {
        var applicationData = Path.GetFullPath(Path.Combine("test-root", "app-data"));
        var npmDirectory = Path.Combine(applicationData, "npm");
        var npmCli = Path.Combine(npmDirectory, "codex.cmd");

        var command = Assert.Single(CodexCliLocator.FindAll(
            applicationData,
            null,
            npmDirectory,
            path => string.Equals(path, npmCli, StringComparison.OrdinalIgnoreCase)));

        Assert.Equal(npmCli, command.FilePath);
    }
}
