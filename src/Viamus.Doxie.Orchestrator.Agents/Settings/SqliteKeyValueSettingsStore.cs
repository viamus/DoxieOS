using Dapper;
using Microsoft.Data.Sqlite;

namespace Viamus.Doxie.Orchestrator.Agents;

/// <summary>
/// SQLite-backed implementation. Lives in the same database file as
/// <see cref="SqliteAgentRunStore"/> (configured via <c>Storage:DatabasePath</c>),
/// so the orchestrator's persistent state is one file end-to-end.
/// </summary>
public sealed class SqliteKeyValueSettingsStore : IKeyValueSettingsStore
{
    private const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS key_value_settings (
            key         TEXT PRIMARY KEY NOT NULL,
            value       TEXT NOT NULL,
            updated_at  TEXT NOT NULL
        );
        """;

    private readonly string _connectionString;

    public SqliteKeyValueSettingsStore(string connectionString)
    {
        _connectionString = connectionString;
        using var conn = Open();
        conn.Execute(SchemaSql);
    }

    public string? Get(string key)
    {
        using var conn = Open();
        return conn.QuerySingleOrDefault<string?>(
            "SELECT value FROM key_value_settings WHERE key = @key;",
            new { key });
    }

    public void Set(string key, string value)
    {
        using var conn = Open();
        conn.Execute("""
            INSERT INTO key_value_settings (key, value, updated_at)
            VALUES (@key, @value, @updatedAt)
            ON CONFLICT(key) DO UPDATE SET
                value = excluded.value,
                updated_at = excluded.updated_at;
            """,
            new
            {
                key,
                value,
                updatedAt = DateTimeOffset.UtcNow.ToString("O"),
            });
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }
}
