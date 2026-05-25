using FluentAssertions;
using Microsoft.Data.Sqlite;

namespace Viamus.Doxie.Orchestrator.Agents.Tests;

public sealed class SqliteAgentRunStoreTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteAgentRunStore _store;

    public SqliteAgentRunStoreTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"orchestrator-test-{Guid.NewGuid():N}.db");
        _store = new SqliteAgentRunStore($"Data Source={_dbPath}");
    }

    public void Dispose()
    {
        // SQLite pools connections; clear them so the file handle is released
        // before we delete the temp DB.
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath))
        {
            try { File.Delete(_dbPath); } catch { /* best effort during test teardown */ }
        }
    }

    [Fact]
    public void Add_then_Get_round_trips_run_and_outputs()
    {
        var startedAt = DateTimeOffset.UtcNow;
        var lines = new[]
        {
            new AgentRunOutputLine(startedAt.AddMilliseconds(10), AgentRunOutputSource.Stdout, "first"),
            new AgentRunOutputLine(startedAt.AddMilliseconds(20), AgentRunOutputSource.Stderr, "second"),
        };
        var run = AgentRun.Hydrate("r1", "sample-watch", "check", startedAt,
            AgentRunStatus.Queued, finishedAt: null, exitCode: null, output: lines);

        _store.Add(run);

        var loaded = _store.Get("r1");
        loaded.Should().NotBeNull();
        loaded!.Id.Should().Be("r1");
        loaded.AgentId.Should().Be("sample-watch");
        loaded.Arguments.Should().Be("check");
        loaded.Output.Should().HaveCount(2);
        loaded.Output[0].Text.Should().Be("first");
        loaded.Output[0].Source.Should().Be(AgentRunOutputSource.Stdout);
        loaded.Output[1].Text.Should().Be("second");
        loaded.Output[1].Source.Should().Be(AgentRunOutputSource.Stderr);
    }

    [Fact]
    public void Get_returns_null_for_unknown_id()
    {
        _store.Get("does-not-exist").Should().BeNull();
    }

    [Fact]
    public void ListByAgent_returns_runs_descending_by_started_at()
    {
        var now = DateTimeOffset.UtcNow;
        _store.Add(new AgentRun("a", "sample-watch", "1", now.AddMinutes(-10)));
        _store.Add(new AgentRun("b", "sample-watch", "2", now.AddMinutes(-5)));
        _store.Add(new AgentRun("c", "sample-watch", "3", now));

        var ids = _store.ListByAgent("sample-watch").Select(r => r.Id).ToList();

        ids.Should().Equal("c", "b", "a");
    }

    [Fact]
    public void ListByAgent_filters_by_agent_id()
    {
        _store.Add(new AgentRun("x", "sample-watch", "", DateTimeOffset.UtcNow));
        _store.Add(new AgentRun("y", "review", "", DateTimeOffset.UtcNow));

        _store.ListByAgent("sample-watch").Should().ContainSingle().Which.Id.Should().Be("x");
        _store.ListByAgent("review").Should().ContainSingle().Which.Id.Should().Be("y");
    }

    [Fact]
    public void ListByAgent_returns_metadata_without_hydrating_outputs()
    {
        var startedAt = DateTimeOffset.UtcNow;
        var run = AgentRun.Hydrate("r", "sample-watch", "", startedAt,
            AgentRunStatus.Queued, finishedAt: null, exitCode: null, output: new[]
            {
                new AgentRunOutputLine(startedAt.AddMilliseconds(1), AgentRunOutputSource.Stdout, "heavy"),
            });
        _store.Add(run);

        var listed = _store.ListByAgent("sample-watch").Single();

        listed.Output.Should().BeEmpty();
    }

    [Fact]
    public void Get_with_output_tail_count_hydrates_only_latest_lines_and_total_count()
    {
        var startedAt = DateTimeOffset.UtcNow;
        var run = AgentRun.Hydrate("r", "sample-watch", "", startedAt,
            AgentRunStatus.Queued, finishedAt: null, exitCode: null, output: Enumerable.Range(1, 5)
                .Select(i => new AgentRunOutputLine(
                    startedAt.AddMilliseconds(i),
                    AgentRunOutputSource.Stdout,
                    $"line-{i}")));
        _store.Add(run);

        var loaded = _store.Get("r", outputTailCount: 2);

        loaded.Should().NotBeNull();
        loaded!.Output.Select(o => o.Text).Should().Equal("line-4", "line-5");
        loaded.OutputLineCount.Should().Be(5);
    }

    [Fact]
    public void AppendOutput_persists_lines_in_insertion_order()
    {
        _store.Add(new AgentRun("r", "sample-watch", "", DateTimeOffset.UtcNow));

        var ts = DateTimeOffset.UtcNow;
        _store.AppendOutput("r", new AgentRunOutputLine(ts, AgentRunOutputSource.Stdout, "one"));
        _store.AppendOutput("r", new AgentRunOutputLine(ts.AddMilliseconds(1), AgentRunOutputSource.Stdout, "two"));
        _store.AppendOutput("r", new AgentRunOutputLine(ts.AddMilliseconds(2), AgentRunOutputSource.Stderr, "three"));

        var loaded = _store.Get("r")!;
        loaded.Output.Select(o => o.Text).Should().Equal("one", "two", "three");
        loaded.Output[2].Source.Should().Be(AgentRunOutputSource.Stderr);
    }

    [Fact]
    public void UpdateStatus_persists_status_finishedAt_and_exitCode()
    {
        _store.Add(new AgentRun("r", "sample-watch", "", DateTimeOffset.UtcNow));

        var finished = DateTimeOffset.UtcNow;
        _store.UpdateStatus("r", AgentRunStatus.Completed, finished, exitCode: 0);

        var loaded = _store.Get("r")!;
        loaded.Status.Should().Be(AgentRunStatus.Completed);
        loaded.FinishedAt.Should().NotBeNull();
        loaded.FinishedAt!.Value.Should().BeCloseTo(finished, TimeSpan.FromMilliseconds(50));
        loaded.ExitCode.Should().Be(0);
    }

    [Fact]
    public void MarkOrphanedAsInterrupted_flips_only_queued_and_running()
    {
        AddRunWithStatus("queued", AgentRunStatus.Queued);
        AddRunWithStatus("running", AgentRunStatus.Running);
        AddRunWithStatus("completed", AgentRunStatus.Completed);
        AddRunWithStatus("failed", AgentRunStatus.Failed);
        AddRunWithStatus("cancelled", AgentRunStatus.Cancelled);
        AddRunWithStatus("interrupted", AgentRunStatus.Interrupted);

        var affected = _store.MarkOrphanedAsInterrupted();

        affected.Should().Be(2);
        _store.Get("queued")!.Status.Should().Be(AgentRunStatus.Interrupted);
        _store.Get("running")!.Status.Should().Be(AgentRunStatus.Interrupted);
        _store.Get("completed")!.Status.Should().Be(AgentRunStatus.Completed);
        _store.Get("failed")!.Status.Should().Be(AgentRunStatus.Failed);
        _store.Get("cancelled")!.Status.Should().Be(AgentRunStatus.Cancelled);
        _store.Get("interrupted")!.Status.Should().Be(AgentRunStatus.Interrupted);
    }

    [Fact]
    public void MarkOrphanedAsInterrupted_stamps_finishedAt_for_flipped_rows()
    {
        AddRunWithStatus("running", AgentRunStatus.Running);

        var before = DateTimeOffset.UtcNow.AddSeconds(-1);
        _store.MarkOrphanedAsInterrupted();
        var after = DateTimeOffset.UtcNow.AddSeconds(1);

        var run = _store.Get("running")!;
        run.FinishedAt.Should().NotBeNull();
        run.FinishedAt!.Value.Should().BeOnOrAfter(before).And.BeOnOrBefore(after);
    }

    [Fact]
    public void MarkOrphanedAsInterrupted_returns_zero_when_nothing_to_flip()
    {
        AddRunWithStatus("done", AgentRunStatus.Completed);

        _store.MarkOrphanedAsInterrupted().Should().Be(0);
    }

    [Fact]
    public void UpdateUsage_persists_token_breakdown_and_cost()
    {
        _store.Add(new AgentRun("r", "sample-watch", "", DateTimeOffset.UtcNow));

        var usage = new AgentRunUsage(
            InputTokens: 1234,
            OutputTokens: 567,
            CacheReadTokens: 8901,
            CacheCreationTokens: 234,
            TotalCostUsd: 0.0123m);

        _store.UpdateUsage("r", usage);

        var loaded = _store.Get("r")!;
        loaded.Usage.Should().NotBeNull();
        loaded.Usage!.InputTokens.Should().Be(1234);
        loaded.Usage.OutputTokens.Should().Be(567);
        loaded.Usage.CacheReadTokens.Should().Be(8901);
        loaded.Usage.CacheCreationTokens.Should().Be(234);
        loaded.Usage.TotalCostUsd.Should().NotBeNull();
        loaded.Usage.TotalCostUsd!.Value.Should().BeApproximately(0.0123m, 0.0001m);
    }

    [Fact]
    public void UpdateUsage_supports_null_cost()
    {
        _store.Add(new AgentRun("r", "sample-watch", "", DateTimeOffset.UtcNow));

        var usage = new AgentRunUsage(100, 200, 0, 0, TotalCostUsd: null);
        _store.UpdateUsage("r", usage);

        var loaded = _store.Get("r")!;
        loaded.Usage.Should().NotBeNull();
        loaded.Usage!.TotalCostUsd.Should().BeNull();
        loaded.Usage.InputTokens.Should().Be(100);
        loaded.Usage.OutputTokens.Should().Be(200);
    }

    [Fact]
    public void Get_returns_null_Usage_when_no_usage_was_recorded()
    {
        _store.Add(new AgentRun("r", "sample-watch", "", DateTimeOffset.UtcNow));

        var loaded = _store.Get("r")!;
        loaded.Usage.Should().BeNull();
    }

    [Fact]
    public void ProviderId_round_trips_through_Add_and_Get()
    {
        var run = AgentRun.Hydrate(
            id: "r",
            agentId: "sample-watch",
            arguments: "",
            startedAt: DateTimeOffset.UtcNow,
            status: AgentRunStatus.Queued,
            finishedAt: null,
            exitCode: null,
            output: Array.Empty<AgentRunOutputLine>(),
            providerId: "claude-code");

        _store.Add(run);

        var loaded = _store.Get("r");
        loaded.Should().NotBeNull();
        loaded!.ProviderId.Should().Be("claude-code");
    }

    [Fact]
    public void ProviderId_can_be_null_for_runs_without_a_provider()
    {
        // Defensive: legacy rows or test fixtures may not stamp the field.
        _store.Add(new AgentRun("r", "sample-watch", "", DateTimeOffset.UtcNow));

        _store.Get("r")!.ProviderId.Should().BeNull();
    }

    [Fact]
    public void Schema_migration_adds_usage_columns_to_pre_existing_db()
    {
        // Create a DB with the *old* schema (no usage columns), then
        // re-open via SqliteAgentRunStore which should idempotently
        // ALTER TABLE. The migration is what protects users upgrading
        // across versions from a "no such column" runtime crash.
        SqliteConnection.ClearAllPools();
        using (var conn = new SqliteConnection($"Data Source={_dbPath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                DROP TABLE IF EXISTS agent_runs;
                CREATE TABLE agent_runs (
                    id          TEXT PRIMARY KEY NOT NULL,
                    agent_id    TEXT NOT NULL,
                    arguments   TEXT NOT NULL,
                    started_at  TEXT NOT NULL,
                    finished_at TEXT NULL,
                    status      TEXT NOT NULL,
                    exit_code   INTEGER NULL
                );
                INSERT INTO agent_runs (id, agent_id, arguments, started_at, status)
                VALUES ('legacy', 'sample-watch', '', '2026-05-01T00:00:00.0000000+00:00', 'Completed');
                """;
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        var migrated = new SqliteAgentRunStore($"Data Source={_dbPath}");

        // Existing row still readable.
        var loaded = migrated.Get("legacy");
        loaded.Should().NotBeNull();
        loaded!.Usage.Should().BeNull();

        // New usage columns now writable.
        migrated.UpdateUsage("legacy", new AgentRunUsage(10, 20, 0, 0, 0.001m));
        var reread = migrated.Get("legacy")!;
        reread.Usage.Should().NotBeNull();
        reread.Usage!.InputTokens.Should().Be(10);
        reread.Usage.OutputTokens.Should().Be(20);
    }

    [Fact]
    public void State_persists_across_store_reopens()
    {
        var startedAt = DateTimeOffset.UtcNow;
        var run = AgentRun.Hydrate("r", "sample-watch", "check", startedAt,
            AgentRunStatus.Queued, finishedAt: null, exitCode: null,
            output: new[]
            {
                new AgentRunOutputLine(startedAt.AddMilliseconds(1), AgentRunOutputSource.Stdout, "hello"),
            });
        _store.Add(run);
        _store.UpdateStatus("r", AgentRunStatus.Completed, DateTimeOffset.UtcNow, 0);

        SqliteConnection.ClearAllPools();
        var reopened = new SqliteAgentRunStore($"Data Source={_dbPath}");

        var loaded = reopened.Get("r");
        loaded.Should().NotBeNull();
        loaded!.Status.Should().Be(AgentRunStatus.Completed);
        loaded.Output.Should().ContainSingle().Which.Text.Should().Be("hello");
    }

    private void AddRunWithStatus(string id, AgentRunStatus status)
    {
        _store.Add(new AgentRun(id, "sample-watch", "", DateTimeOffset.UtcNow));
        var finishedAt = IsTerminal(status) ? DateTimeOffset.UtcNow : (DateTimeOffset?)null;
        _store.UpdateStatus(id, status, finishedAt, null);
    }

    private static bool IsTerminal(AgentRunStatus status) =>
        status is AgentRunStatus.Completed
            or AgentRunStatus.Failed
            or AgentRunStatus.Cancelled
            or AgentRunStatus.Interrupted;
}
