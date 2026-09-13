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
    private const string CreditsEndpoint = "https://chatgpt.com/backend-api/wham/rate-limit-reset-credits";
    private static readonly MonitorSettingsTable<ResetOpportunityRecord> Table = new(
        "reset_opportunities",
        """
        CREATE TABLE IF NOT EXISTS reset_opportunities (
            id TEXT PRIMARY KEY,
            granted_local TEXT NOT NULL,
            expires_local TEXT NOT NULL,
            is_used INTEGER NOT NULL DEFAULT 0,
            note TEXT NOT NULL DEFAULT ''
        );
        CREATE INDEX IF NOT EXISTS idx_reset_opportunities_expires ON reset_opportunities(expires_local);
        """,
        ReadRecords,
        WriteRecords,
        Defaults,
        CloneRecords,
        NormalizeRecords);

    public static IReadOnlyList<ResetOpportunityRecord> Defaults()
    {
        return new[]
        {
            Record("default-reset-2026-06-16", Local(2026, 6, 16, 0, 0), "手动记录：第 1 次"),
            Record("default-reset-2026-06-24", Local(2026, 6, 24, 14, 10), "本地状态记录：availableCount=2"),
            Record("default-reset-2026-06-27", Local(2026, 6, 27, 15, 23), "截图记录：第 3 次")
        };
    }

    public static IReadOnlyList<ResetOpportunityRecord> Load(bool forceReload = false) => Table.Load(forceReload);

    public static void Save(IReadOnlyList<ResetOpportunityRecord> records) => Table.Save(records);

    private static IReadOnlyList<ResetOpportunityRecord> NormalizeRecords(IReadOnlyList<ResetOpportunityRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        foreach (var record in records) ValidateRecord(record);
        return records
            .OrderBy(item => item.GrantedLocal)
            .Select(item => new ResetOpportunityRecord
            {
                Id = string.IsNullOrWhiteSpace(item.Id) ? Guid.NewGuid().ToString("N") : item.Id,
                GrantedLocal = item.GrantedLocal,
                ExpiresLocal = item.ExpiresLocal,
                IsUsed = item.IsUsed,
                Note = item.Note?.Trim() ?? ""
            })
            .ToList();
    }

    private static void ValidateRecord(ResetOpportunityRecord record)
    {
        if (record.ExpiresLocal <= record.GrantedLocal)
            throw new InvalidDataException("重置卡过期时间必须晚于获得时间。");
    }

    private static void WriteRecords(SqliteConnection connection, SqliteTransaction transaction, IReadOnlyList<ResetOpportunityRecord> records)
    {
        MonitorSettingsDatabase.ExecuteNonQuery(connection, transaction, "DELETE FROM reset_opportunities");
        foreach (var record in records)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO reset_opportunities (id, granted_local, expires_local, is_used, note)
                VALUES ($id, $granted_local, $expires_local, $is_used, $note)
                """;
            command.Parameters.AddWithValue("$id", record.Id);
            command.Parameters.AddWithValue("$granted_local", FormatDateTimeOffset(record.GrantedLocal));
            command.Parameters.AddWithValue("$expires_local", FormatDateTimeOffset(record.ExpiresLocal));
            command.Parameters.AddWithValue("$is_used", record.IsUsed ? 1 : 0);
            command.Parameters.AddWithValue("$note", record.Note);
            command.ExecuteNonQuery();
        }
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
            cancellationToken.ThrowIfCancellationRequested();
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
            cancellationToken.ThrowIfCancellationRequested();
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

    internal static IReadOnlyList<ResetOpportunityRecord> ReadApiRecords(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("credits", out var credits) ||
            credits.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("重置卡响应缺少有效的 credits 数组，未更改本地记录。");
        }

        var records = new List<ResetOpportunityRecord>();
        foreach (var credit in credits.EnumerateArray())
        {
            if (credit.ValueKind != JsonValueKind.Object)
                throw new JsonException("重置卡记录必须是对象，未更改本地记录。");
            var id = GetString(credit, "id");
            var grantedUtc = ParseUtc(GetString(credit, "granted_at"));
            var expiresUtc = ParseUtc(GetString(credit, "expires_at"));
            if (grantedUtc is null || expiresUtc is null || expiresUtc <= grantedUtc)
            {
                throw new JsonException("重置卡记录的获得或过期时间无效，未更改本地记录。");
            }

            var status = GetString(credit, "status");
            if (string.IsNullOrWhiteSpace(status))
                throw new JsonException("重置卡记录缺少有效状态，未更改本地记录。");
            var title = GetString(credit, "title") ?? "Codex 重置卡";
            var redeemedAt = GetString(credit, "redeemed_at");
            if (!string.IsNullOrWhiteSpace(redeemedAt) && ParseUtc(redeemedAt) is null)
                throw new JsonException("重置卡记录的兑换时间无效，未更改本地记录。");
            records.Add(new ResetOpportunityRecord
            {
                Id = $"codex-api-{HashId(id ?? $"{grantedUtc:O}|{expiresUtc:O}")}",
                GrantedLocal = grantedUtc.Value.ToOffset(CodexUsageReader.BeijingOffset),
                ExpiresLocal = expiresUtc.Value.ToOffset(CodexUsageReader.BeijingOffset),
                IsUsed = !string.Equals(status, "available", StringComparison.OrdinalIgnoreCase) ||
                         !string.IsNullOrWhiteSpace(redeemedAt),
                Note = $"Codex 接口同步：{title}"
            });
        }
        return records;
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
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind == JsonValueKind.Null)
            return null;
        if (property.ValueKind != JsonValueKind.String)
            throw new JsonException($"重置卡字段 {propertyName} 必须是字符串，未更改本地记录。");
        return property.GetString();
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

    private static IReadOnlyList<ResetOpportunityRecord> ReadRecords(SqliteConnection connection, SqliteTransaction? transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id, granted_local, expires_local, is_used, note
            FROM reset_opportunities
            ORDER BY granted_local
            """;
        var result = new List<ResetOpportunityRecord>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (reader.GetValue(3) is not long used || used is not (0 or 1))
                throw new FormatException("重置卡使用状态必须为 0 或 1。");
            var record = new ResetOpportunityRecord
            {
                Id = reader.GetString(0),
                GrantedLocal = ParseDateTimeOffset(reader.GetString(1)),
                ExpiresLocal = ParseDateTimeOffset(reader.GetString(2)),
                IsUsed = used != 0,
                Note = reader.GetString(4)
            };
            ValidateRecord(record);
            result.Add(record);
        }
        return result;
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

    private static IReadOnlyList<ResetOpportunityRecord> CloneRecords(IEnumerable<ResetOpportunityRecord> records)
    {
        return records
            .Select(item => new ResetOpportunityRecord
            {
                Id = item.Id,
                GrantedLocal = item.GrantedLocal,
                ExpiresLocal = item.ExpiresLocal,
                IsUsed = item.IsUsed,
                Note = item.Note
            })
            .ToList();
    }
}
