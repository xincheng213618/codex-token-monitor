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
    private static readonly MonitorSettingsTable<SubscriptionPlanRecord> Table = new(
        "subscription_plans",
        """
        CREATE TABLE IF NOT EXISTS subscription_plans (
            id TEXT PRIMARY KEY,
            start_local TEXT NOT NULL,
            end_local TEXT NOT NULL,
            plan_name TEXT NOT NULL,
            amount_cny TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS idx_subscription_plans_range ON subscription_plans(start_local, end_local);
        """,
        ReadRecords,
        WriteRecords,
        () => Defaults(),
        CloneRecords,
        NormalizeRecords);

    public static SubscriptionPlanRecord CreateMonthly(DateTimeOffset start, string planName = "Pro 20x", decimal amountCny = 1380m)
    {
        if (amountCny < 0) throw new ArgumentOutOfRangeException(nameof(amountCny), "月费不能为负数。");
        return new SubscriptionPlanRecord
        {
            StartLocal = start, EndLocal = start.AddMonths(1), PlanName = planName.Trim(), AmountCny = amountCny
        };
    }

    public static IReadOnlyList<SubscriptionPlanRecord> Defaults(DateTimeOffset? asOfLocal = null)
    {
        var records = new List<SubscriptionPlanRecord>
        {
            new SubscriptionPlanRecord
            {
                Id = "default-2026-05-plus",
                StartLocal = Local(2026, 5, 1, 0, 0),
                EndLocal = Local(2026, 6, 1, 0, 0),
                PlanName = "Plus",
                AmountCny = 128m
            }
        };
        var now = (asOfLocal ?? BeijingClock.Now).ToOffset(CodexUsageReader.BeijingOffset);
        for (var start = Local(2026, 6, 2, 0, 0); start <= now; start = start.AddMonths(1))
        {
            records.Add(new SubscriptionPlanRecord
            {
                Id = $"default-{start:yyyy-MM}-pro20x", StartLocal = start, EndLocal = start.AddMonths(1),
                PlanName = "Pro 20x", AmountCny = 1380m
            });
        }
        return records;
    }

    public static IReadOnlyList<SubscriptionPlanRecord> Load(bool forceReload = false) => Table.Load(forceReload);

    private static IReadOnlyList<SubscriptionPlanRecord> ReadRecords(SqliteConnection connection, SqliteTransaction? transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
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
            ValidateRecord(record);
            result.Add(record);
        }
        return result;
    }

    public static void Save(IReadOnlyList<SubscriptionPlanRecord> records) => Table.Save(records);

    private static IReadOnlyList<SubscriptionPlanRecord> NormalizeRecords(IReadOnlyList<SubscriptionPlanRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        foreach (var record in records) ValidateRecord(record);
        return records
            .OrderBy(item => item.StartLocal)
            .Select(item => new SubscriptionPlanRecord
            {
                Id = string.IsNullOrWhiteSpace(item.Id) ? Guid.NewGuid().ToString("N") : item.Id,
                StartLocal = item.StartLocal,
                EndLocal = item.EndLocal,
                PlanName = string.IsNullOrWhiteSpace(item.PlanName) ? "未命名套餐" : item.PlanName.Trim(),
                AmountCny = item.AmountCny
            })
            .ToList();
    }

    private static void ValidateRecord(SubscriptionPlanRecord record)
    {
        if (record.EndLocal <= record.StartLocal)
            throw new InvalidDataException("套餐结束时间必须晚于开始时间。");
        if (record.AmountCny < 0)
            throw new InvalidDataException("套餐金额不能为负数。");
    }

    private static void WriteRecords(SqliteConnection connection, SqliteTransaction transaction, IReadOnlyList<SubscriptionPlanRecord> records)
    {
        MonitorSettingsDatabase.ExecuteNonQuery(connection, transaction, "DELETE FROM subscription_plans");
        foreach (var record in records)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO subscription_plans (id, start_local, end_local, plan_name, amount_cny)
                VALUES ($id, $start_local, $end_local, $plan_name, $amount_cny)
                """;
            command.Parameters.AddWithValue("$id", record.Id);
            command.Parameters.AddWithValue("$start_local", FormatDateTimeOffset(record.StartLocal));
            command.Parameters.AddWithValue("$end_local", FormatDateTimeOffset(record.EndLocal));
            command.Parameters.AddWithValue("$plan_name", record.PlanName);
            command.Parameters.AddWithValue("$amount_cny", record.AmountCny.ToString(CultureInfo.InvariantCulture));
            command.ExecuteNonQuery();
        }
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

    private static IReadOnlyList<SubscriptionPlanRecord> CloneRecords(IEnumerable<SubscriptionPlanRecord> records)
    {
        return records
            .Select(item => new SubscriptionPlanRecord
            {
                Id = item.Id,
                StartLocal = item.StartLocal,
                EndLocal = item.EndLocal,
                PlanName = item.PlanName,
                AmountCny = item.AmountCny
            })
            .ToList();
    }
}
