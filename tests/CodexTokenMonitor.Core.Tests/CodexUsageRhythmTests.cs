using Microsoft.Data.Sqlite;
using Xunit;

namespace CodexTokenMonitor.Core.Tests;

public sealed class CodexUsageRhythmTests
{
    private static readonly TimeSpan Offset = TimeSpan.FromHours(8);

    [Fact]
    public void SplitsMidnightInteractionAndKeepsTheFullSilenceOnItsStartDay()
    {
        DateTimeOffset[] messages = [At(1, 23, 50), At(2, 0, 20), At(2, 23, 50), At(3, 0, 10), At(4, 6)];
        var history = new CodexMessageHistory(messages, [], At(4, 9), "test", 0);
        var rhythm = CodexUsageRhythm.Calculate(history, Offset);

        Assert.Equal(2, history.CountInRange(At(2, 0), At(3, 0)));
        Assert.Collection(rhythm.Days,
            day =>
            {
                Assert.Equal(2, day.Messages);
                Assert.Equal(2, day.ClockHours);
                Assert.Equal(0.5, day.InteractionHours);
                Assert.Equal(23.5, day.LongestSilence?.Hours);
            },
            day =>
            {
                Assert.Equal(1, day.Messages);
                Assert.Equal(1d / 6, day.InteractionHours);
                Assert.Equal(At(4, 6), day.LongestSilence?.End);
            });
        Assert.Equal(At(3, 0, 10), Assert.Single(rhythm.LongSilences).Start);
        var week = Assert.Single(rhythm.Weeks);
        Assert.Equal(2, week.WindowCount);
        Assert.Equal(TimeSpan.FromMinutes(10), week.WindowStart);
        Assert.Equal(week.WindowStart.Add(TimeSpan.FromMinutes(10)), week.WindowEnd);
    }

    [Fact]
    public void SkipsGapsCrossingUnobservedDaysAndKeepsConfirmedZeroDays()
    {
        var history = new CodexMessageHistory(
            [At(1, 10), At(2, 10), At(4, 10), At(4, 14), At(6, 10)],
            [At(5, 10)], At(6, 15), "test", 0);
        var rhythm = CodexUsageRhythm.Calculate(history, Offset);

        Assert.Null(rhythm.Days[0].LongestSilence);
        Assert.Null(rhythm.Days[1].Messages);
        Assert.Equal(0, rhythm.Days[3].Messages);
        Assert.Equal(0, rhythm.Days[3].InteractionHours);
        Assert.Equal(3, Assert.Single(rhythm.Months).Days);
        Assert.Equal(44, Assert.Single(rhythm.LongSilences).Hours);
    }

    [Fact]
    public void WeeklyWindowWrapsAroundMidnight()
    {
        var history = new CodexMessageHistory(
            [At(1, 12), At(2, 23, 50), At(3, 0, 10), At(4, 12)], [], At(4, 15), "test", 0);
        var week = Assert.Single(CodexUsageRhythm.Calculate(history, Offset).Weeks);
        Assert.Equal(2, week.WindowCount);
        Assert.Equal(TimeSpan.FromHours(23) + TimeSpan.FromMinutes(50), week.WindowStart);
        Assert.Equal(TimeSpan.FromMinutes(10), week.WindowEnd);
    }

    [Fact]
    public void DatabaseRefreshReadsChangedThreadsAndKeepsSavedHistory()
    {
        var root = Directory.CreateTempSubdirectory("CodexRhythmTests-").FullName;
        try
        {
            using var cache = MonitorCachePaths.PushLocalAppDataRoot(root);
            using var logs = UsageLogPaths.PushRoot(Path.Combine(root, "logs"));
            var home = UsageLogPaths.GetCodexHome();
            Directory.CreateDirectory(home);
            var state = Path.Combine(home, "state_5.sqlite");
            var history = Path.Combine(home, "thread_history_1.sqlite");
            var first = At(1, 8).ToUnixTimeMilliseconds();
            var second = At(2, 8).ToUnixTimeMilliseconds();
            var third = At(2, 9).ToUnixTimeMilliseconds();
            Execute(state, $"""
                CREATE TABLE threads(id TEXT, thread_source TEXT, source TEXT, created_at_ms INTEGER, created_at INTEGER);
                INSERT INTO threads VALUES ('first', 'user', NULL, {first}, NULL), ('second', 'user', NULL, {second}, NULL);
                """);
            Execute(history, $$"""
                CREATE TABLE thread_history_projection_state(thread_id TEXT, next_rollout_byte_offset INTEGER, next_rollout_ordinal INTEGER);
                CREATE TABLE thread_items(thread_id TEXT, item_id TEXT, item_type TEXT, rollout_ordinal INTEGER, created_at_ms INTEGER, item_json TEXT);
                CREATE TABLE thread_turns(thread_id TEXT, started_at INTEGER);
                INSERT INTO thread_history_projection_state VALUES ('first', 100, 2), ('second', 100, 2);
                INSERT INTO thread_items VALUES
                    ('first', 'item-0', 'userMessage', 1, {{first}}, '{"id":"item-0","clientId":"original","content":[{"type":"text","text":"first"}]}'),
                    ('second', 'item-0', 'userMessage', 1, {{second}}, '{"id":"item-0","content":[{"type":"text","text":"second"}]}');
                INSERT INTO thread_turns VALUES ('first', {{first}});
                """);

            Assert.Equal(new[] { At(1, 8), At(2, 8) }, CodexRhythmReader.Read(At(3, 8), default).Messages);
            Execute(history, $$"""
                UPDATE thread_items SET item_json = 'invalid' WHERE thread_id = 'first';
                INSERT INTO thread_items VALUES
                    ('second', 'item-1', 'userMessage', 2, {{third}}, '{"id":"item-1","clientId":"original","content":[{"type":"text","text":"replay"}]}'),
                    ('second', 'item-2', 'userMessage', 3, {{third}}, '{"id":"item-2","content":[{"type":"text","text":"third"}]}');
                UPDATE thread_history_projection_state SET next_rollout_ordinal = 4 WHERE thread_id = 'second';
                """);
            var refreshed = CodexRhythmReader.Read(At(3, 9), default);
            Assert.Equal(new[] { At(1, 8), At(2, 8), At(2, 9) }, refreshed.Messages);
            Assert.Equal(0, refreshed.Skipped);
            Assert.Equal(refreshed.Messages, CodexRhythmReader.ReadSaved()!.Messages);

            Execute(state, "DELETE FROM threads WHERE id = 'first'");
            Execute(history, """
                DELETE FROM thread_history_projection_state WHERE thread_id = 'first';
                DELETE FROM thread_items WHERE thread_id = 'first';
                DELETE FROM thread_turns WHERE thread_id = 'first';
                """);
            var afterPruning = CodexRhythmReader.Read(At(3, 10), default);
            Assert.Equal(refreshed.Messages, afterPruning.Messages);
            Assert.Equal(At(1, 8), Assert.Single(afterPruning.TurnStarts));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static void Execute(string database, string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = database,
            Pooling = false
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static DateTimeOffset At(int day, int hour, int minute = 0) => new(2026, 11, day, hour, minute, 0, Offset);
}
