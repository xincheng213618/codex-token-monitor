using System.Globalization;
using Microsoft.Data.Sqlite;

namespace CodexTokenMonitor;

internal sealed class SubscriptionPlanRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset StartLocal { get; set; }
    public DateTimeOffset EndLocal { get; set; }
    public string PlanName { get; set; } = "";
    public decimal AmountCny { get; set; }
}

internal sealed record SubscriptionPlanSummary(
    decimal AmountCny,
    string PlanNames,
    IReadOnlyList<SubscriptionPlanRecord> Records)
{
    public bool HasRecords => Records.Count > 0;
}

internal static class SubscriptionPlanStore
{
    private const string CacheFolder = "CodexTokenMonitor";
    private static readonly object SyncRoot = new();
    private static bool initialized;

    public static IReadOnlyList<SubscriptionPlanRecord> Defaults()
    {
        return new[]
        {
            new SubscriptionPlanRecord
            {
                Id = "default-2026-05-plus",
                StartLocal = Local(2026, 5, 1, 0, 0),
                EndLocal = Local(2026, 6, 1, 0, 0),
                PlanName = "Plus",
                AmountCny = 128m
            },
            new SubscriptionPlanRecord
            {
                Id = "default-2026-06-pro20x",
                StartLocal = Local(2026, 6, 2, 0, 0),
                EndLocal = Local(2026, 7, 2, 0, 0),
                PlanName = "Pro 20x",
                AmountCny = 1380m
            }
        };
    }

    public static IReadOnlyList<SubscriptionPlanRecord> Load()
    {
        EnsureInitialized();
        return ReadRecords();
    }

    private static IReadOnlyList<SubscriptionPlanRecord> ReadRecords()
    {
        try
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT id, start_local, end_local, plan_name, amount_cny
                FROM subscription_plans
                ORDER BY start_local
                """;
            var result = new List<SubscriptionPlanRecord>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var record = new SubscriptionPlanRecord
                {
                    Id = reader.GetString(0),
                    StartLocal = ParseDateTimeOffset(reader.GetString(1)),
                    EndLocal = ParseDateTimeOffset(reader.GetString(2)),
                    PlanName = reader.GetString(3),
                    AmountCny = decimal.Parse(reader.GetString(4), CultureInfo.InvariantCulture)
                };
                if (record.EndLocal > record.StartLocal)
                {
                    result.Add(record);
                }
            }

            return result;
        }
        catch
        {
            return Defaults();
        }
    }

    public static void Save(IReadOnlyList<SubscriptionPlanRecord> records)
    {
        EnsureInitialized();
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        using (var deleteCommand = connection.CreateCommand())
        {
            deleteCommand.Transaction = transaction;
            deleteCommand.CommandText = "DELETE FROM subscription_plans";
            deleteCommand.ExecuteNonQuery();
        }

        foreach (var record in records
                     .Where(item => item.EndLocal > item.StartLocal)
                     .OrderBy(item => item.StartLocal))
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO subscription_plans (id, start_local, end_local, plan_name, amount_cny)
                VALUES ($id, $start_local, $end_local, $plan_name, $amount_cny)
                """;
            command.Parameters.AddWithValue("$id", string.IsNullOrWhiteSpace(record.Id) ? Guid.NewGuid().ToString("N") : record.Id);
            command.Parameters.AddWithValue("$start_local", FormatDateTimeOffset(record.StartLocal));
            command.Parameters.AddWithValue("$end_local", FormatDateTimeOffset(record.EndLocal));
            command.Parameters.AddWithValue("$plan_name", string.IsNullOrWhiteSpace(record.PlanName) ? "未命名套餐" : record.PlanName.Trim());
            command.Parameters.AddWithValue("$amount_cny", record.AmountCny.ToString(CultureInfo.InvariantCulture));
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public static SubscriptionPlanSummary Summarize(DateTimeOffset startLocal, DateTimeOffset endLocal)
    {
        if (endLocal <= startLocal)
        {
            return new SubscriptionPlanSummary(0m, "-", Array.Empty<SubscriptionPlanRecord>());
        }

        var matching = new List<SubscriptionPlanRecord>();
        decimal amount = 0m;
        foreach (var record in Load())
        {
            var overlapStart = Max(startLocal, record.StartLocal);
            var overlapEnd = Min(endLocal, record.EndLocal);
            if (overlapEnd <= overlapStart)
            {
                continue;
            }

            matching.Add(record);
            var planTicks = Math.Max(1m, record.EndLocal.Ticks - record.StartLocal.Ticks);
            var overlapTicks = overlapEnd.Ticks - overlapStart.Ticks;
            amount += record.AmountCny * overlapTicks / planTicks;
        }

        var names = matching.Count == 0
            ? "-"
            : string.Join(" / ", matching.Select(item => item.PlanName).Where(item => !string.IsNullOrWhiteSpace(item)).Distinct());
        return new SubscriptionPlanSummary(amount, names, matching);
    }

    private static void EnsureInitialized()
    {
        lock (SyncRoot)
        {
            if (initialized)
            {
                return;
            }

            using var connection = OpenConnection();
            ExecuteNonQuery(connection, """
                CREATE TABLE IF NOT EXISTS subscription_plans (
                    id TEXT PRIMARY KEY,
                    start_local TEXT NOT NULL,
                    end_local TEXT NOT NULL,
                    plan_name TEXT NOT NULL,
                    amount_cny TEXT NOT NULL
                )
                """);
            ExecuteNonQuery(connection, "CREATE INDEX IF NOT EXISTS idx_subscription_plans_range ON subscription_plans(start_local, end_local)");

            using var countCommand = connection.CreateCommand();
            countCommand.CommandText = "SELECT COUNT(*) FROM subscription_plans";
            var count = Convert.ToInt32(countCommand.ExecuteScalar(), CultureInfo.InvariantCulture);
            initialized = true;
            if (count == 0)
            {
                Save(Defaults());
            }
        }
    }

    private static SqliteConnection OpenConnection()
    {
        var path = UsageCacheStore.GetCachePath(CacheFolder);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true
        };
        var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        ExecuteNonQuery(connection, "PRAGMA busy_timeout=5000;");
        return connection;
    }

    private static void ExecuteNonQuery(SqliteConnection connection, string commandText)
    {
        using var command = connection.CreateCommand();
        command.CommandText = commandText;
        command.ExecuteNonQuery();
    }

    private static DateTimeOffset Local(int year, int month, int day, int hour, int minute)
    {
        return new DateTimeOffset(year, month, day, hour, minute, 0, CodexUsageReader.BeijingOffset);
    }

    private static DateTimeOffset Max(DateTimeOffset first, DateTimeOffset second)
    {
        return first >= second ? first : second;
    }

    private static DateTimeOffset Min(DateTimeOffset first, DateTimeOffset second)
    {
        return first <= second ? first : second;
    }

    private static string FormatDateTimeOffset(DateTimeOffset value)
    {
        return value.ToString("O", CultureInfo.InvariantCulture);
    }

    private static DateTimeOffset ParseDateTimeOffset(string value)
    {
        return DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    }
}
