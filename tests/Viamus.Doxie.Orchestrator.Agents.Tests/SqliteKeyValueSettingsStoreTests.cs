using FluentAssertions;
using Microsoft.Data.Sqlite;

namespace Viamus.Doxie.Orchestrator.Agents.Tests;

public sealed class SqliteKeyValueSettingsStoreTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteKeyValueSettingsStore _store;

    public SqliteKeyValueSettingsStoreTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"orchestrator-settings-{Guid.NewGuid():N}.db");
        _store = new SqliteKeyValueSettingsStore($"Data Source={_dbPath}");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath))
        {
            try { File.Delete(_dbPath); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void Get_returns_null_for_unknown_key()
    {
        _store.Get("nope").Should().BeNull();
    }

    [Fact]
    public void Set_then_Get_round_trips_a_value()
    {
        _store.Set("default_agent_provider", "claude-code");

        _store.Get("default_agent_provider").Should().Be("claude-code");
    }

    [Fact]
    public void Set_overwrites_an_existing_value()
    {
        _store.Set("k", "first");
        _store.Set("k", "second");

        _store.Get("k").Should().Be("second");
    }

    [Fact]
    public void Set_persists_across_store_instances_on_the_same_file()
    {
        _store.Set("k", "v");

        var second = new SqliteKeyValueSettingsStore($"Data Source={_dbPath}");
        second.Get("k").Should().Be("v");
    }

    [Fact]
    public void Coexists_with_agent_runs_table_in_the_same_database_file()
    {
        // Real-world: state.db hosts both agent_runs (from
        // SqliteAgentRunStore) and key_value_settings (from us). Each
        // store's CREATE TABLE IF NOT EXISTS must be friendly so they
        // can share the file without stepping on each other.
        var runs = new SqliteAgentRunStore($"Data Source={_dbPath}");
        _store.Set("k", "v");

        _store.Get("k").Should().Be("v");
        runs.Should().NotBeNull();
    }
}
