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

        var first = new List<string>();
        Assert.False(reader.ReadNewLines(path, DateTimeOffset.UtcNow, first.Add));
        Assert.Equal(new[] { "complete" }, first);
        File.AppendAllText(path, "-done\n", Encoding.UTF8);
        var second = Read(reader, path);

        Assert.Contains("partial-done", second);
    }

    [Fact]
    public void ReadNewLines_ReturnsFalseWhenFileDisappears()
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "missing.jsonl");
        var reader = new LiveFileTailReader();

        Assert.False(reader.ReadNewLines(path, DateTimeOffset.UtcNow, _ => { }));
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
    public void PruneBeforeUtc_RemovesMissingAndOlderFiles()
    {
        Directory.CreateDirectory(directory);
        var oldPath = Path.Combine(directory, "old.jsonl");
        var recentPath = Path.Combine(directory, "recent.jsonl");
        var missingPath = Path.Combine(directory, "missing.jsonl");
        File.WriteAllText(oldPath, "old\n", Encoding.UTF8);
        File.WriteAllText(recentPath, "recent\n", Encoding.UTF8);
        File.WriteAllText(missingPath, "missing\n", Encoding.UTF8);
        var reader = new LiveFileTailReader();
        _ = Read(reader, oldPath);
        _ = Read(reader, recentPath);
        _ = Read(reader, missingPath);

        File.SetLastWriteTimeUtc(oldPath, DateTime.UtcNow.AddDays(-2));
        File.Delete(missingPath);
        var cutoffUtc = DateTime.UtcNow.AddDays(-1);

        Assert.Equal(2, reader.PruneBeforeUtc(cutoffUtc));
        Assert.False(reader.IsTracked(oldPath));
        Assert.True(reader.IsTracked(recentPath));
        Assert.False(reader.IsTracked(missingPath));
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

    [Fact]
    public void ReadNewLinesWhile_RetriesRejectedLineOnNextPass()
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "session.jsonl");
        File.WriteAllText(path, "old\nfuture\nafter\n", Encoding.UTF8);
        var reader = new LiveFileTailReader();
        var first = new List<string>();

        reader.ReadNewLinesWhile(path, DateTimeOffset.UtcNow, line =>
        {
            if (line == "future")
            {
                return false;
            }

            first.Add(line);
            return true;
        });

        Assert.Equal(new[] { "old" }, first);
        Assert.Equal(new[] { "future", "after" }, Read(reader, path));
    }

    [Fact]
    public void Cancellation_DoesNotCommitAAfterPartialRead()
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "session.jsonl");
        File.WriteAllText(path, "one\ntwo\n", Encoding.UTF8);
        var reader = new LiveFileTailReader();
        using var cancellation = new CancellationTokenSource();
        var seen = new List<string>();

        Assert.Throws<OperationCanceledException>(() =>
            reader.ReadNewLinesWhile(path, DateTimeOffset.UtcNow, line =>
            {
                seen.Add(line);
                cancellation.Cancel();
                return true;
            }, cancellation.Token));

        Assert.Equal(new[] { "one" }, seen);
        Assert.Equal(new[] { "one", "two" }, Read(reader, path));
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
