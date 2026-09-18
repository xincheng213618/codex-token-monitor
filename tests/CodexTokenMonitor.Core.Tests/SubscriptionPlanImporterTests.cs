using Microsoft.Data.Sqlite;
using Xunit;

namespace CodexTokenMonitor.Tests;

public sealed class SubscriptionPlanImporterTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"PlanImporterTests-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch
        {
            // Best-effort cleanup of the temporary tree.
        }
    }

    private string FixtureDatabase(string fileName, string createSql, params (string InsertSql, object[] Values)[] rows)
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, fileName);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString());
        connection.Open();
        using (var create = connection.CreateCommand())
        {
            create.CommandText = createSql;
            create.ExecuteNonQuery();
        }

        foreach (var (insertSql, values) in rows)
        {
            using var insert = connection.CreateCommand();
            insert.CommandText = insertSql;
            for (var index = 0; index < values.Length; index++)
            {
                insert.Parameters.AddWithValue($"$p{index}", values[index]);
            }

            insert.ExecuteNonQuery();
        }

        return path;
    }

    [Fact]
    public void ImportFromRoot_PlanTable_ParsesRecordWithNormalizedName()
    {
        FixtureDatabase(
            "plan.sqlite",
            """CREATE TABLE plan_purchase (plan_name TEXT, start_time TEXT, end_time TEXT, amount_total REAL)""",
            ("""INSERT INTO plan_purchase VALUES ($p0, $p1, $p2, $p3)""",
             new object[] { "ChatGPT Pro 20x", "2026-05-01 00:00:00", "2026-06-02 00:00:00", 138d }));

        var result = SubscriptionPlanImporter.TryImportFromCodexRoot(root);

        var record = Assert.Single(result.Records);
        Assert.Equal("Pro 20x", record.PlanName);
        Assert.Equal(138m, record.AmountCny);
        Assert.Equal(new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.FromHours(8)), record.StartLocal);
        Assert.Equal(new DateTimeOffset(2026, 6, 2, 0, 0, 0, TimeSpan.FromHours(8)), record.EndLocal);
        Assert.Contains("1 条套餐记录", result.Message);
    }

    [Fact]
    public void ImportFromRoot_IrrelevantTables_AreIgnored()
    {
        FixtureDatabase(
            "app.db",
            """CREATE TABLE settings (key TEXT, value TEXT)""",
            ("""INSERT INTO settings VALUES ($p0, $p1)""", new object[] { "theme", "Pro 20x" }));

        var result = SubscriptionPlanImporter.TryImportFromCodexRoot(root);

        Assert.Empty(result.Records);
        Assert.Contains("已检查 1 个库", result.Message);
    }

    [Fact]
    public void ImportFromRoot_PlansWithoutRelevantColumns_AreIgnored()
    {
        FixtureDatabase(
            "billing.sqlite",
            """CREATE TABLE billing_history (note TEXT, created_at TEXT)""",
            ("""INSERT INTO billing_history VALUES ($p0, $p1)""",
             new object[] { "payment received", "2026-05-01 00:00:00" }));

        var result = SubscriptionPlanImporter.TryImportFromCodexRoot(root);

        Assert.Empty(result.Records);
    }

    [Fact]
    public void ImportFromRoot_DuplicateRows_KeepHighestAmountOnce()
    {
        FixtureDatabase(
            "plan.sqlite",
            """CREATE TABLE plan_purchase (plan_name TEXT, start_time TEXT, end_time TEXT, amount_total REAL)""",
            ("""INSERT INTO plan_purchase VALUES ($p0, $p1, $p2, $p3)""",
             new object[] { "Pro 20x", "2026-05-01 00:00:00", "2026-06-02 00:00:00", 100d }),
            ("""INSERT INTO plan_purchase VALUES ($p0, $p1, $p2, $p3)""",
             new object[] { "Pro 20x", "2026-05-01 00:00:00", "2026-06-02 00:00:00", 138d }));

        var result = SubscriptionPlanImporter.TryImportFromCodexRoot(root);

        var record = Assert.Single(result.Records);
        Assert.Equal(138m, record.AmountCny);
    }

    [Fact]
    public void ImportFromRoot_CorruptDatabase_IsSkippedWithoutFailing()
    {
        Directory.CreateDirectory(root);
        File.WriteAllBytes(Path.Combine(root, "broken.sqlite"), new byte[] { 0x00, 0x01, 0x02, 0x03 });

        var result = SubscriptionPlanImporter.TryImportFromCodexRoot(root);

        Assert.Empty(result.Records);
        Assert.Contains("已检查 1 个库", result.Message);
    }

    [Fact]
    public void ImportFromRoot_EmptyRoot_ExplainsNoRecords()
    {
        Directory.CreateDirectory(root);

        var result = SubscriptionPlanImporter.TryImportFromCodexRoot(root);

        Assert.Empty(result.Records);
        Assert.Contains("已检查 0 个库", result.Message);
    }
}
