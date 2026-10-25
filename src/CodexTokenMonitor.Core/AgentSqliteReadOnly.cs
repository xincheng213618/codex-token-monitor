using Microsoft.Data.Sqlite;

namespace CodexTokenMonitor;

/// <summary>Read another tool's SQLite database without creating WAL/SHM files beside it.</summary>
internal sealed class AgentSqliteReadOnly : IDisposable
{
    private readonly string? temporary;
    public SqliteConnection Connection { get; }

    public AgentSqliteReadOnly(string path)
    {
        var database = path;
        var immutable = false;
        if (File.Exists(path + "-wal") && !File.Exists(path + "-shm"))
        {
            // A detached WAL needs an SHM rebuild. Do it in a private disposable copy, never at the source.
            temporary = Path.Combine(Path.GetTempPath(), "agent-speed-sqlite-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temporary); database = Path.Combine(temporary, "snapshot.sqlite3");
            try { File.Copy(path, database); File.Copy(path + "-wal", database + "-wal"); }
            catch { Cleanup(); throw; }
        }
        else if (!File.Exists(path + "-wal"))
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            Span<byte> header = stackalloc byte[20];
            immutable = stream.Read(header) == header.Length && header[..16].SequenceEqual("SQLite format 3\0"u8) && header[18] == 2 && header[19] == 2;
        }
        Connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = immutable ? new Uri(Path.GetFullPath(database)).AbsoluteUri + "?immutable=1" : database,
            Mode = SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = 1
        }.ToString());
        try { Connection.Open(); }
        catch { Connection.Dispose(); Cleanup(); throw; }
    }
    public void Dispose() { Connection.Dispose(); Cleanup(); }
    private void Cleanup()
    {
        if (temporary is null || !Directory.Exists(temporary)) return;
        foreach (var file in Directory.EnumerateFiles(temporary)) File.Delete(file);
        Directory.Delete(temporary);
    }
}
