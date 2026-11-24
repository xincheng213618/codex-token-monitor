using System.Text.Json;
using Xunit;

namespace CodexTokenMonitor.Core.Tests;

public sealed class CodexRhythmTodaySourceTests
{
    [Fact]
    public void RefreshesAnEmptyCacheFromTheSessionLogAndUsesAnExclusiveCutoff()
    {
        var root = Path.Combine(Path.GetTempPath(), "CodexRhythmTodaySourceTests-" + Guid.NewGuid().ToString("N"));
        using var cacheScope = MonitorCachePaths.PushLocalAppDataRoot(Path.Combine(root, "cache"));
        using var logScope = UsageLogPaths.PushRoot(Path.Combine(root, "logs"));
        var directory = Path.Combine(UsageLogPaths.GetCodexHome(), "sessions");
        Directory.CreateDirectory(directory);
        var log = Path.Combine(directory, "rollout-rhythm-today.jsonl");
        var cutoff = DateTimeOffset.UtcNow.AddMinutes(-1);
        // Keep the fixture inside a reporting day even when the test runs near midnight.
        var offset = cutoff.Hour is >= 6 and < 18 ? TimeSpan.Zero : TimeSpan.FromHours(12);
        var start = new DateTimeOffset(cutoff.ToOffset(offset).Date, offset);

        try
        {
            File.WriteAllLines(log,
            [
                TokenEvent(start.AddTicks(-1), 100),
                TokenEvent(start, 17),
                TokenEvent(cutoff.AddTicks(-1), 23),
                TokenEvent(cutoff, 41),
                TokenEvent(cutoff.AddSeconds(1), 59)
            ]);

            Assert.False(File.Exists(UsageCacheStore.GetCachePath("CodexTokenMonitor")));
            var reader = new CodexUsageReader();

            Assert.Equal(40L, reader.ReadRhythmTodayTotal(cutoff, offset, CancellationToken.None));
            Assert.Equal(140L, reader.ReadRhythmTodayTotal(cutoff.AddSeconds(2), offset, CancellationToken.None));
        }
        finally
        {
            UsageCacheStore.Delete("CodexTokenMonitor");
            File.Delete(log);
        }
    }

    private static string TokenEvent(DateTimeOffset timestamp, long tokens) => JsonSerializer.Serialize(new
    {
        timestamp,
        type = "event_msg",
        payload = new
        {
            type = "token_count",
            info = new { last_token_usage = new { input_tokens = tokens, total_tokens = tokens } }
        }
    });
}
