using System.Globalization;
using Microsoft.Data.Sqlite;

namespace CodexTokenMonitor;

internal sealed record SubscriptionPlanImportResult(
    IReadOnlyList<SubscriptionPlanRecord> Records,
    string Message);

internal static class SubscriptionPlanImporter
{
    private static readonly string[] RelevantTableWords =
    {
        "subscription", "billing", "account", "profile", "plan", "purchase", "invoice"
    };

    public static SubscriptionPlanImportResult TryImportFromCodex()
    {
        var records = new List<SubscriptionPlanRecord>();
        var filesChecked = 0;
        foreach (var path in GetCandidateDatabasePaths())
        {
            filesChecked++;
            records.AddRange(ReadPlansFromDatabase(path));
        }

        var distinct = records
            .GroupBy(item => $"{item.StartLocal:O}|{item.EndLocal:O}|{item.PlanName}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(item => item.AmountCny).First())
            .OrderBy(item => item.StartLocal)
            .ToList();
        var message = distinct.Count == 0
            ? $"未在 Codex 本地数据库识别到套餐记录（已检查 {filesChecked} 个库）"
            : $"从 Codex 本地数据库识别到 {distinct.Count} 条套餐记录";
        return new SubscriptionPlanImportResult(distinct, message);
    }

    private static IEnumerable<string> GetCandidateDatabasePaths()
    {
        var result = new List<string>();
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roots = new[]
        {
            Path.Combine(home, ".codex"),
            Path.Combine(home, ".codex", "sqlite"),
            Path.Combine(localAppData, "OpenAI", "Codex")
        };

        foreach (var root in roots)
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            foreach (var pattern in new[] { "*.sqlite", "*.sqlite3", "*.db" })
            {
                try
                {
                    result.AddRange(Directory.EnumerateFiles(root, pattern, SearchOption.TopDirectoryOnly));
                }
                catch
                {
                    // Some app-managed directories can be transient.
                }
            }
        }

        return result.Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<SubscriptionPlanRecord> ReadPlansFromDatabase(string path)
    {
        var result = new List<SubscriptionPlanRecord>();
        try
        {
            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false
            };
            using var connection = new SqliteConnection(builder.ToString());
            connection.Open();
            foreach (var table in ReadTables(connection))
            {
                result.AddRange(ReadPlansFromTable(connection, table));
            }
        }
        catch
        {
            // Best effort only. Codex internals can change or be locked.
        }

        return result;
    }

    private static IReadOnlyList<string> ReadTables(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table'";
        var tables = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var table = reader.GetString(0);
            if (RelevantTableWords.Any(word => table.Contains(word, StringComparison.OrdinalIgnoreCase)))
            {
                tables.Add(table);
            }
        }

        return tables;
    }

    private static IReadOnlyList<SubscriptionPlanRecord> ReadPlansFromTable(SqliteConnection connection, string table)
    {
        var columns = ReadColumns(connection, table);
        if (columns.Count == 0 ||
            !columns.Any(column => RelevantTableWords.Any(word => column.Contains(word, StringComparison.OrdinalIgnoreCase))))
        {
            return Array.Empty<SubscriptionPlanRecord>();
        }

        var result = new List<SubscriptionPlanRecord>();
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT * FROM {QuoteIdentifier(table)} LIMIT 500";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var row = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                for (var index = 0; index < reader.FieldCount; index++)
                {
                    row[reader.GetName(index)] = reader.IsDBNull(index)
                        ? null
                        : Convert.ToString(reader.GetValue(index), CultureInfo.InvariantCulture);
                }

                var record = TryParseRecord(row);
                if (record is not null)
                {
                    result.Add(record);
                }
            }
        }
        catch
        {
            return Array.Empty<SubscriptionPlanRecord>();
        }

        return result;
    }

    private static IReadOnlyList<string> ReadColumns(SqliteConnection connection, string table)
    {
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"PRAGMA table_info({QuoteIdentifier(table)})";
            var columns = new List<string>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                columns.Add(reader.GetString(1));
            }

            return columns;
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static SubscriptionPlanRecord? TryParseRecord(IReadOnlyDictionary<string, string?> row)
    {
        var planName = FindPlanName(row);
        if (string.IsNullOrWhiteSpace(planName))
        {
            return null;
        }

        if (!TryFindPeriod(row, out var start, out var end))
        {
            return null;
        }

        var amount = FindAmountCny(row, planName);
        return new SubscriptionPlanRecord
        {
            Id = $"codex-{start:yyyyMMddHHmm}-{end:yyyyMMddHHmm}-{NormalizeId(planName)}",
            StartLocal = start,
            EndLocal = end,
            PlanName = planName,
            AmountCny = amount
        };
    }

    private static string? FindPlanName(IReadOnlyDictionary<string, string?> row)
    {
        foreach (var pair in row)
        {
            var key = pair.Key;
            var value = pair.Value;
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            var keyLooksRelevant = key.Contains("plan", StringComparison.OrdinalIgnoreCase) ||
                                   key.Contains("tier", StringComparison.OrdinalIgnoreCase) ||
                                   key.Contains("subscription", StringComparison.OrdinalIgnoreCase) ||
                                   key.Contains("product", StringComparison.OrdinalIgnoreCase) ||
                                   key.Contains("sku", StringComparison.OrdinalIgnoreCase);
            var valueLooksRelevant = value.Contains("plus", StringComparison.OrdinalIgnoreCase) ||
                                     value.Contains("pro", StringComparison.OrdinalIgnoreCase);
            if (keyLooksRelevant && valueLooksRelevant)
            {
                return NormalizePlanName(value);
            }
        }

        return null;
    }

    private static bool TryFindPeriod(
        IReadOnlyDictionary<string, string?> row,
        out DateTimeOffset start,
        out DateTimeOffset end)
    {
        start = default;
        end = default;
        var startCandidates = new List<DateTimeOffset>();
        var endCandidates = new List<DateTimeOffset>();
        foreach (var pair in row)
        {
            if (!TryParseDate(pair.Value, out var value))
            {
                continue;
            }

            var key = pair.Key.ToLowerInvariant();
            if (key.Contains("start") || key.Contains("begin") || key.Contains("created") || key.Contains("activated"))
            {
                startCandidates.Add(value);
            }

            if (key.Contains("end") || key.Contains("expire") || key.Contains("renew") || key.Contains("period") || key.Contains("reset"))
            {
                endCandidates.Add(value);
            }
        }

        foreach (var candidateStart in startCandidates.OrderBy(item => item))
        {
            var candidateEnd = endCandidates
                .Where(item => item > candidateStart.AddHours(1) && item <= candidateStart.AddDays(370))
                .OrderBy(item => item)
                .FirstOrDefault();
            if (candidateEnd != default)
            {
                start = candidateStart;
                end = candidateEnd;
                return true;
            }
        }

        return false;
    }

    private static decimal FindAmountCny(IReadOnlyDictionary<string, string?> row, string planName)
    {
        var currency = row
            .Where(pair => pair.Key.Contains("currency", StringComparison.OrdinalIgnoreCase))
            .Select(pair => pair.Value)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        var isCny = string.IsNullOrWhiteSpace(currency) ||
                    currency.Contains("cny", StringComparison.OrdinalIgnoreCase) ||
                    currency.Contains("rmb", StringComparison.OrdinalIgnoreCase) ||
                    currency.Contains("¥", StringComparison.OrdinalIgnoreCase);

        if (isCny)
        {
            foreach (var pair in row)
            {
                var key = pair.Key.ToLowerInvariant();
                if (!(key.Contains("amount") || key.Contains("price") || key.Contains("paid") || key.Contains("total") || key.Contains("cost")))
                {
                    continue;
                }

                if (decimal.TryParse(pair.Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var value) && value > 0)
                {
                    return value > 10_000 ? value / 100m : value;
                }
            }
        }

        if (planName.Contains("20x", StringComparison.OrdinalIgnoreCase))
        {
            return 1380m;
        }

        if (planName.Contains("plus", StringComparison.OrdinalIgnoreCase))
        {
            return 128m;
        }

        return 0m;
    }

    private static bool TryParseDate(string? text, out DateTimeOffset value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numeric))
        {
            try
            {
                if (numeric > 1_000_000_000_000)
                {
                    value = DateTimeOffset.FromUnixTimeMilliseconds(numeric).ToOffset(CodexUsageReader.BeijingOffset);
                    return value.Year is >= 2024 and <= 2035;
                }

                if (numeric > 1_000_000_000)
                {
                    value = DateTimeOffset.FromUnixTimeSeconds(numeric).ToOffset(CodexUsageReader.BeijingOffset);
                    return value.Year is >= 2024 and <= 2035;
                }
            }
            catch
            {
                return false;
            }
        }

        if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsedOffset))
        {
            value = parsedOffset.ToOffset(CodexUsageReader.BeijingOffset);
            return value.Year is >= 2024 and <= 2035;
        }

        if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var parsed))
        {
            value = new DateTimeOffset(parsed.Year, parsed.Month, parsed.Day, parsed.Hour, parsed.Minute, parsed.Second, CodexUsageReader.BeijingOffset);
            return value.Year is >= 2024 and <= 2035;
        }

        return false;
    }

    private static string NormalizePlanName(string value)
    {
        var text = value.Trim();
        if (text.Contains("plus", StringComparison.OrdinalIgnoreCase))
        {
            return "Plus";
        }

        if (text.Contains("20x", StringComparison.OrdinalIgnoreCase))
        {
            return "Pro 20x";
        }

        if (text.Contains("5x", StringComparison.OrdinalIgnoreCase))
        {
            return "Pro 5x";
        }

        return text;
    }

    private static string NormalizeId(string value)
    {
        return new string(value
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray());
    }

    private static string QuoteIdentifier(string identifier)
    {
        return "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }
}
