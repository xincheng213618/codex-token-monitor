using System.Text;
using Xunit;

namespace CodexTokenMonitor.Tests;

public sealed class LiveFileTailReaderTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"CodexTokenMonitorTests-{Guid.NewGuid():N}");

    [Fact]
    public void ReadNewLines_ReturnsOnlyAppendedCompleteLines()
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "session.jsonl");
        File.WriteAllText(path, "one\ntwo\n", Encoding.UTF8);
        var reader = new LiveFileTailReader();

        var first = Read(reader, path);
        var unchanged = Read(reader, path);
        File.AppendAllText(path, "three\n", Encoding.UTF8);
        var appended = Read(reader, path);

        Assert.Equal(new[] { "one", "two" }, first);
        Assert.Empty(unchanged);
        Assert.Equal(new[] { "three" }, appended);
    }

    [Fact]
    public void ReadNewLines_DoesNotAdvancePastPartialLine()
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "session.jsonl");
        File.WriteAllText(path, "complete\npartial", Encoding.UTF8);
        var reader = new LiveFileTailReader();

        _ = Read(reader, path);
        File.AppendAllText(path, "-done\n", Encoding.UTF8);
        var second = Read(reader, path);

        Assert.Contains("partial-done", second);
    }

    [Fact]
    public void Reset_ReplaysFileFromBeginning()
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "session.jsonl");
        File.WriteAllText(path, "one\n", Encoding.UTF8);
        var reader = new LiveFileTailReader();

        _ = Read(reader, path);
        reader.Reset();

        Assert.Equal(new[] { "one" }, Read(reader, path));
    }

    [Fact]
    public void EarlierCoverage_ReplaysFileFromBeginning()
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "session.jsonl");
        File.WriteAllText(path, "one\ntwo\n", Encoding.UTF8);
        var reader = new LiveFileTailReader();
        var late = new DateTimeOffset(2026, 7, 13, 12, 0, 0, TimeSpan.FromHours(8));

        _ = Read(reader, path, late);

        Assert.Equal(new[] { "one", "two" }, Read(reader, path, late.AddHours(-4)));
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static List<string> Read(
        LiveFileTailReader reader,
        string path,
        DateTimeOffset? coverageStart = null)
    {
        var lines = new List<string>();
        reader.ReadNewLines(path, coverageStart ?? DateTimeOffset.UtcNow, lines.Add);
        return lines;
    }
}
