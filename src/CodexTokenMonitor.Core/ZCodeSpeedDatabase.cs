using Microsoft.Data.Sqlite;

namespace CodexTokenMonitor;

/// <summary>Reads only ZCode's authoritative model_usage table; no schema writes or billing/network requests.</summary>
internal sealed class ZCodeSpeedDatabase(string path)
{
    private (long DbLength, DateTime DbWrite, long WalLength, DateTime WalWrite)? signature;
    public int Read(AgentSpeedEngine engine, DateTimeOffset start, DateTimeOffset end, CancellationToken token)
    {
        if (!File.Exists(path)) return 0;
        try
        {
            var db = new FileInfo(path); var wal = new FileInfo(path + "-wal");
            var current = (db.Length, db.LastWriteTimeUtc, wal.Exists ? wal.Length : 0, wal.Exists ? wal.LastWriteTimeUtc : default);
            if (current == signature) return 0;
            using var database = new AgentSqliteReadOnly(path);
            var connection = database.Connection;
            using var schema = connection.CreateCommand(); schema.CommandText = "PRAGMA table_info(model_usage)";
            var columns = new HashSet<string>(StringComparer.Ordinal);
            using (var reader = schema.ExecuteReader()) while (reader.Read()) columns.Add(reader.GetString(1));
            if (!new[] { "model_id", "started_at", "completed_at", "input_tokens", "output_tokens" }.All(columns.Contains) ||
                !(columns.Contains("id") || columns.Contains("logical_request_id"))) return 1;
            HashSet<string> Columns(string table)
            {
                using var inspect = connection.CreateCommand(); inspect.CommandText = "PRAGMA table_info(" + table + ")";
                using var fields = inspect.ExecuteReader(); var names = new HashSet<string>(StringComparer.Ordinal);
                while (fields.Read()) names.Add(fields.GetString(1)); return names;
            }
            var parents = new Dictionary<string, string>(StringComparer.Ordinal);
            if (new[] { "id", "parent_id" }.All(Columns("session").Contains))
            {
                using var family = connection.CreateCommand(); family.CommandText = "SELECT id,parent_id FROM session WHERE parent_id IS NOT NULL";
                using var familyRows = family.ExecuteReader();
                while (familyRows.Read())
                {
                    var child = familyRows.GetString(0); var parent = familyRows.GetString(1); parents[child] = parent;
                    engine.Update(new(path + "#" + parent, parent, "未知模型", false, AgentSpeedPhase.Idle, null, null, UsageSource.ZCode));
                }
            }
            var keyColumn = columns.Contains("id") ? "id" : "logical_request_id";
            var sessionColumn = columns.Contains("session_id") ? "session_id" : "NULL";
            var modeColumn = columns.Contains("service_tier") ? "service_tier" : "NULL";
            var providerColumn = columns.Contains("provider_id") ? "provider_id" : "NULL";
            var hasMessageModes = columns.Contains("parent_user_message_id") && columns.Contains("session_id") && new[] { "id", "session_id", "data" }.All(Columns("message").Contains);
            var selected = hasMessageModes ? "CASE WHEN length(CAST(m.data AS BLOB))<=1048576 THEN m.data END" : "NULL";
            var join = hasMessageModes ? " LEFT JOIN message m ON m.id=u.parent_user_message_id AND m.session_id=u.session_id" : "";
            using var command = connection.CreateCommand();
            string UsageColumn(string column) => column == "NULL" ? column : "u." + column;
            command.CommandText = $"SELECT u.{keyColumn}, {UsageColumn(sessionColumn)}, u.model_id, u.started_at, u.completed_at, u.input_tokens, u.output_tokens, {UsageColumn(modeColumn)}, {UsageColumn(providerColumn)}, {selected} FROM model_usage u{join} WHERE (u.completed_at >= $start AND u.completed_at <= $end) OR (u.completed_at IS NULL AND u.started_at >= $pending) ORDER BY COALESCE(u.completed_at, u.started_at)";
            command.Parameters.AddWithValue("$start", start.ToUnixTimeMilliseconds()); command.Parameters.AddWithValue("$end", end.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$pending", end.AddMinutes(-15).ToUnixTimeMilliseconds());
            using var rows = command.ExecuteReader();
            while (rows.Read())
            {
                token.ThrowIfCancellationRequested();
                if (rows.IsDBNull(0)) continue;
                var key = Convert.ToString(rows.GetValue(0), CultureInfo.InvariantCulture)!;
                var session = rows.IsDBNull(1) ? "ledger" : Convert.ToString(rows.GetValue(1), CultureInfo.InvariantCulture)!;
                var model = rows.IsDBNull(2) ? "未知模型" : rows.GetString(2);
                var began = rows.IsDBNull(3) ? (DateTimeOffset?)null : DateTimeOffset.FromUnixTimeMilliseconds(rows.GetInt64(3));
                var completed = rows.IsDBNull(4) ? (DateTimeOffset?)null : DateTimeOffset.FromUnixTimeMilliseconds(rows.GetInt64(4));
                var input = rows.IsDBNull(5) ? 0 : Math.Max(0, rows.GetInt64(5)); var output = rows.IsDBNull(6) ? 0 : Math.Max(0, rows.GetInt64(6));
                var mode = AgentSpeedJson.ModeWithEvidence(rows.IsDBNull(7) ? null : rows.GetString(7), "记录"); var agentId = path + "#" + session;
                if (!rows.IsDBNull(9)) mode = Accelerator(rows.GetString(9), rows.IsDBNull(8) ? null : rows.GetString(8)) ?? mode;
                if (completed is { } ended) engine.Add(new("zcode:" + key, agentId, model, began, ended, output, input, UsageSource.ZCode, ExecutionMode: mode));
                engine.Update(new(agentId, session, model, parents.ContainsKey(session), completed is null ? AgentSpeedPhase.Generating : AgentSpeedPhase.Idle,
                    completed ?? began, completed is null ? began : null, UsageSource.ZCode, mode, parents.GetValueOrDefault(session)));
            }
            signature = current;
            return 0;
        }
        catch (SqliteException) { return 1; }
        catch (IOException) { return 1; }
        catch (UnauthorizedAccessException) { return 1; }
        catch (ArgumentOutOfRangeException) { return 1; }
    }

    private static string? Accelerator(string json, string? provider)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var card = AgentSpeedJson.Object(AgentSpeedJson.Object(document.RootElement, "metadata"), "highspeed");
            if (AgentSpeedJson.Number(card, "schemaVersion") != 1 || string.IsNullOrWhiteSpace(AgentSpeedJson.Text(card, "provider")) || string.IsNullOrWhiteSpace(AgentSpeedJson.Text(card, "model"))) return null;
            if (AgentSpeedJson.Text(card, "cardId")?.StartsWith("hsc_mock_", StringComparison.Ordinal) == true) return "Highspeed（模拟卡设置）";
            return provider switch
            {
                "account:zai-highspeed-card" or "account:bigmodel-highspeed-card" => "Highspeed（实际路由）",
                null or "" => "Highspeed（加速卡设置）",
                _ => "Standard（实际路由；已选加速卡）"
            };
        }
        catch (JsonException) { return null; }
    }
}
