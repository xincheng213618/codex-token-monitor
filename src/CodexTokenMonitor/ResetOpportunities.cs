using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace CodexTokenMonitor;

internal sealed class ResetOpportunityRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset GrantedLocal { get; set; }
    public DateTimeOffset ExpiresLocal { get; set; }
    public bool IsUsed { get; set; }
    public string Note { get; set; } = "";
}

internal sealed record ResetOpportunitySummary(
    int AvailableCount,
    DateTimeOffset? EarliestExpiresLocal,
    IReadOnlyList<ResetOpportunityRecord> AvailableRecords);

internal sealed record ResetOpportunitySyncResult(
    bool Success,
    int Count,
    string Message,
    IReadOnlyList<ResetOpportunityRecord> Records);

internal static class ResetOpportunityFormatter
{
    public static string FormatCompactSummary(ResetOpportunitySummary summary)
    {
        return summary.AvailableRecords.Count == 0
            ? "无可用"
            : string.Join(" / ", summary.AvailableRecords
                .Take(4)
                .Select(item => item.ExpiresLocal.ToString("MM-dd", CultureInfo.InvariantCulture)));
    }

    public static string FormatPanelTitle(ResetOpportunitySummary summary)
    {
        return $"重置卡（{summary.AvailableCount:N0}）";
    }

    public static string FormatRecordLine(ResetOpportunityRecord record, DateTimeOffset nowLocal)
    {
        return
            $"获得 {record.GrantedLocal:yyyy-MM-dd HH:mm} / " +
            $"过期 {record.ExpiresLocal:yyyy-MM-dd HH:mm} / " +
            $"剩 {FormatRemaining(record.ExpiresLocal, nowLocal)}";
    }

    public static string FormatRemaining(DateTimeOffset expiresLocal, DateTimeOffset nowLocal)
    {
        var remaining = expiresLocal - nowLocal;
        if (remaining <= TimeSpan.Zero)
        {
            return "已过期";
        }

        if (remaining.TotalDays >= 1)
        {
            return $"{(int)remaining.TotalDays}天{remaining.Hours}h";
        }

        if (remaining.TotalHours >= 1)
        {
            return $"{(int)remaining.TotalHours}h{remaining.Minutes}m";
        }

        return $"{Math.Max(0, remaining.Minutes)}m";
    }
}

internal static class ResetOpportunityStore
{
    private const string CacheFolder = "CodexTokenMonitor";
    private const string CreditsEndpoint = "https://chatgpt.com/backend-api/wham/rate-limit-reset-credits";
    private static readonly object SyncRoot = new();
    private static bool initialized;

    public static IReadOnlyList<ResetOpportunityRecord> Defaults()
    {
        return new[]
        {
            Record("default-reset-2026-06-16", Local(2026, 6, 16, 0, 0), "手动记录：第 1 次"),
            Record("default-reset-2026-06-24", Local(2026, 6, 24, 14, 10), "本地状态记录：availableCount=2"),
            Record("default-reset-2026-06-27", Local(2026, 6, 27, 15, 23), "截图记录：第 3 次")
        };
    }

    public static IReadOnlyList<ResetOpportunityRecord> Load()
    {
        EnsureInitialized();
        return ReadRecords();
    }

    public static void Save(IReadOnlyList<ResetOpportunityRecord> records)
    {
        EnsureInitialized();
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        using (var deleteCommand = connection.CreateCommand())
        {
            deleteCommand.Transaction = transaction;
            deleteCommand.CommandText = "DELETE FROM reset_opportunities";
            deleteCommand.ExecuteNonQuery();
        }

        foreach (var record in records
                     .Where(item => item.ExpiresLocal > item.GrantedLocal)
                     .OrderBy(item => item.GrantedLocal))
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO reset_opportunities (id, granted_local, expires_local, is_used, note)
                VALUES ($id, $granted_local, $expires_local, $is_used, $note)
                """;
            command.Parameters.AddWithValue("$id", string.IsNullOrWhiteSpace(record.Id) ? Guid.NewGuid().ToString("N") : record.Id);
            command.Parameters.AddWithValue("$granted_local", FormatDateTimeOffset(record.GrantedLocal));
            command.Parameters.AddWithValue("$expires_local", FormatDateTimeOffset(record.ExpiresLocal));
            command.Parameters.AddWithValue("$is_used", record.IsUsed ? 1 : 0);
            command.Parameters.AddWithValue("$note", record.Note.Trim());
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public static ResetOpportunitySummary Summarize(DateTimeOffset nowLocal)
    {
        var available = Load()
            .Where(item => !item.IsUsed && item.ExpiresLocal > nowLocal)
            .OrderBy(item => item.ExpiresLocal)
            .ToList();
        return new ResetOpportunitySummary(
            available.Count,
            available.FirstOrDefault()?.ExpiresLocal,
            available);
    }

    public static async Task<ResetOpportunitySyncResult> SyncFromCodexAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var accessToken = ReadCodexAccessToken();
            if (string.IsNullOrWhiteSpace(accessToken))
            {
                return new ResetOpportunitySyncResult(false, 0, "未找到本机 Codex access_token", Array.Empty<ResetOpportunityRecord>());
            }

            using var client = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(30)
            };
            using var request = new HttpRequestMessage(HttpMethod.Get, CreditsEndpoint);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
            request.Headers.Accept.ParseAdd("application/json");

            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                return new ResetOpportunitySyncResult(false, 0, "HTTP 401：凭证失效，或 Authorization header 没有正确带上", Array.Empty<ResetOpportunityRecord>());
            }

            if (!response.IsSuccessStatusCode)
            {
                return new ResetOpportunitySyncResult(false, 0, $"HTTP {(int)response.StatusCode}", Array.Empty<ResetOpportunityRecord>());
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            var records = ReadApiRecords(doc.RootElement)
                .OrderBy(item => item.GrantedLocal)
                .ToList();
            Save(records);

            return new ResetOpportunitySyncResult(true, records.Count, $"已同步 {records.Count:N0} 张重置卡", records);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new ResetOpportunitySyncResult(false, 0, $"同步失败：{ex.Message}", Array.Empty<ResetOpportunityRecord>());
        }
    }

    private static string? ReadCodexAccessToken()
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".codex",
            "auth.json");
        if (!File.Exists(path))
        {
            return null;
        }

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return doc.RootElement.TryGetProperty("tokens", out var tokens) &&
               tokens.TryGetProperty("access_token", out var accessToken)
            ? accessToken.GetString()
            : null;
    }

    private static IEnumerable<ResetOpportunityRecord> ReadApiRecords(JsonElement root)
    {
        if (!root.TryGetProperty("credits", out var credits) ||
            credits.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var credit in credits.EnumerateArray())
        {
            var id = GetString(credit, "id");
            var grantedUtc = ParseUtc(GetString(credit, "granted_at"));
            var expiresUtc = ParseUtc(GetString(credit, "expires_at"));
            if (grantedUtc is null || expiresUtc is null || expiresUtc <= grantedUtc)
            {
                continue;
            }

            var status = GetString(credit, "status") ?? "";
            var title = GetString(credit, "title") ?? "Codex 重置卡";
            yield return new ResetOpportunityRecord
            {
                Id = $"codex-api-{HashId(id ?? $"{grantedUtc:O}|{expiresUtc:O}")}",
                GrantedLocal = grantedUtc.Value.ToOffset(CodexUsageReader.BeijingOffset),
                ExpiresLocal = expiresUtc.Value.ToOffset(CodexUsageReader.BeijingOffset),
                IsUsed = !string.Equals(status, "available", StringComparison.OrdinalIgnoreCase) ||
                         !string.IsNullOrWhiteSpace(GetString(credit, "redeemed_at")),
                Note = $"Codex 接口同步：{title}"
            };
        }
    }

    private static string HashId(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash).ToLowerInvariant()[..16];
    }

    private static DateTimeOffset? ParseUtc(string? value)
    {
        return DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed
            : null;
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) &&
               property.ValueKind != JsonValueKind.Null
            ? property.GetString()
            : null;
    }

    private static ResetOpportunityRecord Record(string id, DateTimeOffset grantedLocal, string note)
    {
        return new ResetOpportunityRecord
        {
            Id = id,
            GrantedLocal = grantedLocal,
            ExpiresLocal = grantedLocal.AddDays(30),
            Note = note
        };
    }

    private static IReadOnlyList<ResetOpportunityRecord> ReadRecords()
    {
        try
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT id, granted_local, expires_local, is_used, note
                FROM reset_opportunities
                ORDER BY granted_local
                """;
            var result = new List<ResetOpportunityRecord>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var record = new ResetOpportunityRecord
                {
                    Id = reader.GetString(0),
                    GrantedLocal = ParseDateTimeOffset(reader.GetString(1)),
                    ExpiresLocal = ParseDateTimeOffset(reader.GetString(2)),
                    IsUsed = reader.GetInt32(3) != 0,
                    Note = reader.GetString(4)
                };
                if (record.ExpiresLocal > record.GrantedLocal)
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
                CREATE TABLE IF NOT EXISTS reset_opportunities (
                    id TEXT PRIMARY KEY,
                    granted_local TEXT NOT NULL,
                    expires_local TEXT NOT NULL,
                    is_used INTEGER NOT NULL DEFAULT 0,
                    note TEXT NOT NULL DEFAULT ''
                )
                """);
            ExecuteNonQuery(connection, "CREATE INDEX IF NOT EXISTS idx_reset_opportunities_expires ON reset_opportunities(expires_local)");

            using var countCommand = connection.CreateCommand();
            countCommand.CommandText = "SELECT COUNT(*) FROM reset_opportunities";
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

    private static string FormatDateTimeOffset(DateTimeOffset value)
    {
        return value.ToString("O", CultureInfo.InvariantCulture);
    }

    private static DateTimeOffset ParseDateTimeOffset(string value)
    {
        return DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    }
}
