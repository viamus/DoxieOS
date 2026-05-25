using System.Collections.Concurrent;
using Dapper;
using Microsoft.Data.Sqlite;

namespace Viamus.Doxie.Orchestrator.Workflows;

/// <summary>
/// Workflow run store backed by SQLite (same <c>state.db</c> file as
/// <c>SqliteAgentRunStore</c>; just different tables). Holds a live
/// in-memory cache so <see cref="ListAll"/> stays fast on every
/// dashboard tick, AND persists to SQLite whenever the runner calls
/// <see cref="Save"/> on a terminal status — so historical runs
/// survive an orchestrator restart instead of disappearing the way
/// the in-memory store loses them.
///
/// <para>Hydration on startup: every workflow_runs row + its
/// node_runs are read into the in-memory cache, so the dashboard's
/// "Recent runs" panel shows historical workflow runs alongside the
/// agent runs the SqliteAgentRunStore already persists.</para>
///
/// <para>Save semantics: full snapshot upsert (DELETE + re-INSERT
/// the run + its nodes inside a transaction). Cheap for the prototype
/// scale (handful of nodes per run, small log buffers); a future
/// version could append-only the new log lines instead.</para>
/// </summary>
public sealed class SqliteWorkflowRunStore : IWorkflowRunStore
{
    private const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS workflow_runs (
            id             TEXT PRIMARY KEY NOT NULL,
            workflow_id    TEXT NOT NULL,
            triggered_by   TEXT NOT NULL,
            started_at     TEXT NOT NULL,
            finished_at    TEXT NULL,
            status         TEXT NOT NULL,
            trigger_inputs TEXT NULL
        );
        CREATE TABLE IF NOT EXISTS workflow_node_runs (
            run_id          TEXT NOT NULL REFERENCES workflow_runs(id) ON DELETE CASCADE,
            node_id         TEXT NOT NULL,
            status          TEXT NOT NULL,
            started_at      TEXT NULL,
            finished_at     TEXT NULL,
            output_summary  TEXT NULL,
            logs            TEXT NOT NULL DEFAULT '',
            PRIMARY KEY (run_id, node_id)
        );
        CREATE INDEX IF NOT EXISTS idx_wf_runs_workflow ON workflow_runs(workflow_id);
        CREATE INDEX IF NOT EXISTS idx_wf_runs_started  ON workflow_runs(started_at DESC);
        """;

    // Newline character used to join log lines in the `logs` text
    // column. ASCII Record Separator (0x1E) so a stray newline in a
    // log line itself doesn't fool the round-trip split.
    private const char LogSeparator = (char)0x1E;

    private readonly string _connectionString;
    private readonly ConcurrentDictionary<string, WorkflowRun> _byId = new();

    public SqliteWorkflowRunStore(string connectionString)
    {
        _connectionString = connectionString;
        InitializeSchema();
        HydrateFromDisk();
    }

    public void Add(WorkflowRun run)
    {
        // In-memory only at start time; SQLite write happens on Save()
        // when the run reaches a terminal status. This avoids an
        // INSERT + many UPDATEs per run for a row whose only
        // reader is the same in-memory dictionary.
        _byId[run.Id] = run;
    }

    public void Save(WorkflowRun run)
    {
        // Live reference is already in the dict; this call writes the
        // current snapshot to SQLite. Called by the runner when the
        // run reaches a terminal status — at that point the row is
        // immutable and the next process boot can read it back.
        _byId[run.Id] = run;
        UpsertToDisk(run);
    }

    public WorkflowRun? Get(string runId) =>
        _byId.TryGetValue(runId, out var run) ? run : null;

    public IReadOnlyList<WorkflowRun> ListByWorkflow(string workflowId) =>
        _byId.Values
            .Where(r => string.Equals(r.WorkflowId, workflowId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(r => r.StartedAt)
            .ToList();

    public IReadOnlyList<WorkflowRun> ListAll() =>
        _byId.Values
            .OrderByDescending(r => r.StartedAt)
            .ToList();

    public int ClearAll()
    {
        var count = _byId.Count;
        _byId.Clear();

        using var conn = Open();
        using var tx = conn.BeginTransaction();
        conn.Execute("DELETE FROM workflow_node_runs;", transaction: tx);
        conn.Execute("DELETE FROM workflow_runs;", transaction: tx);
        tx.Commit();

        return count;
    }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        return c;
    }

    private void InitializeSchema()
    {
        using var conn = Open();
        conn.Execute(SchemaSql);

        // Idempotent migration for existing DBs created before
        // trigger_inputs landed. CREATE TABLE IF NOT EXISTS doesn't
        // touch an existing table, so add the column manually and
        // swallow the "duplicate column" error on second runs.
        try
        {
            conn.Execute("ALTER TABLE workflow_runs ADD COLUMN trigger_inputs TEXT NULL");
        }
        catch (SqliteException)
        {
            // Column already exists — nothing to do.
        }
    }

    private void HydrateFromDisk()
    {
        using var conn = Open();
        var runRows = conn.Query<RunRow>("""
            SELECT id, workflow_id AS WorkflowId, triggered_by AS TriggeredBy,
                   started_at AS StartedAt, finished_at AS FinishedAt, status,
                   trigger_inputs AS TriggerInputs
            FROM workflow_runs
            """).ToList();
        if (runRows.Count == 0) return;

        var nodeRows = conn.Query<NodeRow>("""
            SELECT run_id AS RunId, node_id AS NodeId, status,
                   started_at AS StartedAt, finished_at AS FinishedAt,
                   output_summary AS OutputSummary, logs
            FROM workflow_node_runs
            """).ToList();
        var nodesByRun = nodeRows.GroupBy(r => r.RunId).ToDictionary(g => g.Key, g => g.ToList());

        foreach (var row in runRows)
        {
            var nodes = (nodesByRun.TryGetValue(row.Id, out var list) ? list : new List<NodeRow>())
                .ToDictionary(
                    nr => nr.NodeId,
                    nr => WorkflowNodeRun.Hydrate(
                        nodeId: nr.NodeId,
                        status: ParseNodeStatus(nr.Status),
                        startedAt: ParseOffsetOrNull(nr.StartedAt),
                        finishedAt: ParseOffsetOrNull(nr.FinishedAt),
                        outputSummary: nr.OutputSummary,
                        logs: SplitLogs(nr.Logs)),
                    StringComparer.OrdinalIgnoreCase);

            var run = WorkflowRun.Hydrate(
                id: row.Id,
                workflowId: row.WorkflowId,
                triggeredBy: row.TriggeredBy,
                startedAt: ParseOffset(row.StartedAt),
                finishedAt: ParseOffsetOrNull(row.FinishedAt),
                status: ParseRunStatus(row.Status),
                nodeRuns: nodes,
                triggerInputs: ParseTriggerInputs(row.TriggerInputs));

            _byId[run.Id] = run;
        }
    }

    private void UpsertToDisk(WorkflowRun run)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();

        conn.Execute("DELETE FROM workflow_node_runs WHERE run_id = @id", new { id = run.Id }, tx);
        conn.Execute("DELETE FROM workflow_runs WHERE id = @id", new { id = run.Id }, tx);

        conn.Execute("""
            INSERT INTO workflow_runs (id, workflow_id, triggered_by, started_at, finished_at, status, trigger_inputs)
            VALUES (@Id, @WorkflowId, @TriggeredBy, @StartedAt, @FinishedAt, @Status, @TriggerInputs)
            """,
            new
            {
                run.Id,
                run.WorkflowId,
                run.TriggeredBy,
                StartedAt = FormatOffset(run.StartedAt),
                FinishedAt = run.FinishedAt.HasValue ? FormatOffset(run.FinishedAt.Value) : null,
                Status = run.Status.ToString(),
                TriggerInputs = SerializeTriggerInputs(run.TriggerInputs),
            }, tx);

        foreach (var nr in run.NodeRuns.Values)
        {
            conn.Execute("""
                INSERT INTO workflow_node_runs (run_id, node_id, status, started_at, finished_at, output_summary, logs)
                VALUES (@RunId, @NodeId, @Status, @StartedAt, @FinishedAt, @OutputSummary, @Logs)
                """,
                new
                {
                    RunId = run.Id,
                    nr.NodeId,
                    Status = nr.Status.ToString(),
                    StartedAt = nr.StartedAt.HasValue ? FormatOffset(nr.StartedAt.Value) : null,
                    FinishedAt = nr.FinishedAt.HasValue ? FormatOffset(nr.FinishedAt.Value) : null,
                    nr.OutputSummary,
                    Logs = string.Join(LogSeparator, nr.Logs),
                }, tx);
        }

        tx.Commit();
    }

    private static IEnumerable<string> SplitLogs(string raw) =>
        string.IsNullOrEmpty(raw)
            ? Array.Empty<string>()
            : raw.Split(LogSeparator);

    private static string FormatOffset(DateTimeOffset ts) => ts.ToString("O");

    private static DateTimeOffset ParseOffset(string raw) => DateTimeOffset.Parse(raw, System.Globalization.CultureInfo.InvariantCulture);

    private static DateTimeOffset? ParseOffsetOrNull(string? raw) =>
        string.IsNullOrEmpty(raw) ? null : ParseOffset(raw);

    private static WorkflowRunStatus ParseRunStatus(string raw) =>
        Enum.TryParse<WorkflowRunStatus>(raw, ignoreCase: true, out var v) ? v : WorkflowRunStatus.Failed;

    private static WorkflowNodeRunStatus ParseNodeStatus(string raw) =>
        Enum.TryParse<WorkflowNodeRunStatus>(raw, ignoreCase: true, out var v) ? v : WorkflowNodeRunStatus.Skipped;

    private static IReadOnlyDictionary<string, string>? ParseTriggerInputs(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        try
        {
            // Trigger inputs serialise as a flat string-to-string JSON
            // object — the form on the dashboard only collects strings.
            // A typed schema would graduate to a richer payload.
            var dict = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(raw);
            if (dict is null || dict.Count == 0) return null;
            return new Dictionary<string, string>(dict, StringComparer.OrdinalIgnoreCase);
        }
        catch (System.Text.Json.JsonException)
        {
            // Mismatched schema from a previous build — drop the value
            // rather than crashing hydration.
            return null;
        }
    }

    private static string? SerializeTriggerInputs(IReadOnlyDictionary<string, string> inputs)
    {
        if (inputs is null || inputs.Count == 0) return null;
        return System.Text.Json.JsonSerializer.Serialize(inputs);
    }

    private sealed class RunRow
    {
        public string Id { get; set; } = "";
        public string WorkflowId { get; set; } = "";
        public string TriggeredBy { get; set; } = "";
        public string StartedAt { get; set; } = "";
        public string? FinishedAt { get; set; }
        public string Status { get; set; } = "";
        public string? TriggerInputs { get; set; }
    }

    private sealed class NodeRow
    {
        public string RunId { get; set; } = "";
        public string NodeId { get; set; } = "";
        public string Status { get; set; } = "";
        public string? StartedAt { get; set; }
        public string? FinishedAt { get; set; }
        public string? OutputSummary { get; set; }
        public string Logs { get; set; } = "";
    }
}
