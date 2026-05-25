using Dapper;
using Microsoft.Data.Sqlite;

namespace Viamus.Doxie.Orchestrator.Agents;

public sealed class SqliteAgentRunStore : IAgentRunStore
{
    private const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS agent_runs (
            id                       TEXT PRIMARY KEY NOT NULL,
            agent_id                 TEXT NOT NULL,
            arguments                TEXT NOT NULL,
            started_at               TEXT NOT NULL,
            finished_at              TEXT NULL,
            status                   TEXT NOT NULL,
            exit_code                INTEGER NULL,
            output_dir               TEXT NULL,
            input_tokens             INTEGER NULL,
            output_tokens            INTEGER NULL,
            cache_read_tokens        INTEGER NULL,
            cache_creation_tokens    INTEGER NULL,
            total_cost_usd           REAL NULL,
            provider_id              TEXT NULL
        );
        CREATE TABLE IF NOT EXISTS agent_run_outputs (
            id        INTEGER PRIMARY KEY AUTOINCREMENT,
            run_id    TEXT NOT NULL REFERENCES agent_runs(id) ON DELETE CASCADE,
            timestamp TEXT NOT NULL,
            source    TEXT NOT NULL,
            text      TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS idx_runs_agent ON agent_runs(agent_id);
        CREATE INDEX IF NOT EXISTS idx_outputs_run ON agent_run_outputs(run_id, id);
        """;

    private readonly string _connectionString;

    public SqliteAgentRunStore(string connectionString)
    {
        _connectionString = connectionString;
        InitializeSchema();
    }

    public static SqliteAgentRunStore CreateDefault()
    {
        var path = GetDefaultDatabasePath();
        return new SqliteAgentRunStore($"Data Source={path}");
    }

    public static string GetDefaultDatabasePath()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var dir = Path.Combine(localAppData, "Viamus.Doxie.Orchestrator");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "state.db");
    }

    public void Add(AgentRun run)
    {
        using var conn = Open();
        conn.Execute("""
            INSERT INTO agent_runs (
                id, agent_id, arguments, started_at, status,
                finished_at, exit_code, output_dir,
                input_tokens, output_tokens, cache_read_tokens,
                cache_creation_tokens, total_cost_usd,
                provider_id)
            VALUES (
                @Id, @AgentId, @Arguments, @StartedAt, @Status,
                @FinishedAt, @ExitCode, @OutputDir,
                @InputTokens, @OutputTokens, @CacheReadTokens,
                @CacheCreationTokens, @TotalCostUsd,
                @ProviderId);
            """,
            new
            {
                run.Id,
                run.AgentId,
                run.Arguments,
                StartedAt = FormatTimestamp(run.StartedAt),
                Status = run.Status.ToString(),
                FinishedAt = run.FinishedAt.HasValue ? FormatTimestamp(run.FinishedAt.Value) : null,
                run.ExitCode,
                run.OutputDir,
                InputTokens = run.Usage?.InputTokens,
                OutputTokens = run.Usage?.OutputTokens,
                CacheReadTokens = run.Usage?.CacheReadTokens,
                CacheCreationTokens = run.Usage?.CacheCreationTokens,
                TotalCostUsd = run.Usage?.TotalCostUsd.HasValue == true
                    ? (double?)decimal.ToDouble(run.Usage.TotalCostUsd!.Value)
                    : null,
                run.ProviderId,
            });

        foreach (var line in run.Output)
        {
            AppendOutputCore(conn, run.Id, line);
        }
    }

    public AgentRun? Get(string runId, int? outputTailCount = null)
    {
        using var conn = Open();
        return GetWithConnection(runId, conn, outputTailCount);
    }

    public IReadOnlyList<AgentRun> ListByAgent(string agentId)
    {
        using var conn = Open();
        var rows = conn.Query<RunRow>("""
            SELECT id                    AS Id,
                   agent_id              AS AgentId,
                   arguments             AS Arguments,
                   started_at            AS StartedAt,
                   finished_at           AS FinishedAt,
                   status                AS Status,
                   exit_code             AS ExitCode,
                   output_dir            AS OutputDir,
                   input_tokens          AS InputTokens,
                   output_tokens         AS OutputTokens,
                   cache_read_tokens     AS CacheReadTokens,
                   cache_creation_tokens AS CacheCreationTokens,
                   total_cost_usd        AS TotalCostUsd,
                   provider_id           AS ProviderId
            FROM agent_runs
            WHERE agent_id = @agentId
            ORDER BY started_at DESC, id DESC;
            """, new { agentId }).ToList();
        return rows.Select(row => Hydrate(row, Array.Empty<OutputRow>(), outputLineCount: 0)).ToList();
    }

    public IReadOnlyList<AgentRun> ListAll()
    {
        using var conn = Open();
        var rows = conn.Query<RunRow>("""
            SELECT id                    AS Id,
                   agent_id              AS AgentId,
                   arguments             AS Arguments,
                   started_at            AS StartedAt,
                   finished_at           AS FinishedAt,
                   status                AS Status,
                   exit_code             AS ExitCode,
                   output_dir            AS OutputDir,
                   input_tokens          AS InputTokens,
                   output_tokens         AS OutputTokens,
                   cache_read_tokens     AS CacheReadTokens,
                   cache_creation_tokens AS CacheCreationTokens,
                   total_cost_usd        AS TotalCostUsd,
                   provider_id           AS ProviderId
            FROM agent_runs
            ORDER BY started_at DESC, id DESC;
            """).ToList();
        return rows.Select(row => Hydrate(row, Array.Empty<OutputRow>(), outputLineCount: 0)).ToList();
    }

    public void UpdateStatus(string runId, AgentRunStatus status, DateTimeOffset? finishedAt, int? exitCode)
    {
        using var conn = Open();
        conn.Execute("""
            UPDATE agent_runs
            SET status = @status, finished_at = @finishedAt, exit_code = @exitCode
            WHERE id = @runId;
            """,
            new
            {
                runId,
                status = status.ToString(),
                finishedAt = finishedAt.HasValue ? FormatTimestamp(finishedAt.Value) : null,
                exitCode,
            });
    }

    public void AppendOutput(string runId, AgentRunOutputLine line)
    {
        using var conn = Open();
        AppendOutputCore(conn, runId, line);
    }

    public void UpdateOutputDir(string runId, string outputDir)
    {
        using var conn = Open();
        conn.Execute(
            "UPDATE agent_runs SET output_dir = @outputDir WHERE id = @runId;",
            new { runId, outputDir });
    }

    public void UpdateUsage(string runId, AgentRunUsage usage)
    {
        using var conn = Open();
        conn.Execute("""
            UPDATE agent_runs
            SET input_tokens          = @InputTokens,
                output_tokens         = @OutputTokens,
                cache_read_tokens     = @CacheReadTokens,
                cache_creation_tokens = @CacheCreationTokens,
                total_cost_usd        = @TotalCostUsd
            WHERE id = @runId;
            """,
            new
            {
                runId,
                usage.InputTokens,
                usage.OutputTokens,
                usage.CacheReadTokens,
                usage.CacheCreationTokens,
                TotalCostUsd = usage.TotalCostUsd.HasValue
                    ? (double?)decimal.ToDouble(usage.TotalCostUsd.Value)
                    : null,
            });
    }

    public int MarkOrphanedAsInterrupted()
    {
        using var conn = Open();
        return conn.Execute("""
            UPDATE agent_runs
            SET status = @newStatus, finished_at = @finishedAt
            WHERE status IN (@queued, @running);
            """,
            new
            {
                newStatus = AgentRunStatus.Interrupted.ToString(),
                finishedAt = FormatTimestamp(DateTimeOffset.UtcNow),
                queued = AgentRunStatus.Queued.ToString(),
                running = AgentRunStatus.Running.ToString(),
            });
    }

    public int ClearAll()
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        conn.Execute("DELETE FROM agent_run_outputs;", transaction: tx);
        var count = conn.Execute("DELETE FROM agent_runs;", transaction: tx);
        tx.Commit();
        return count;
    }

    private void InitializeSchema()
    {
        using var conn = Open();
        conn.Execute(SchemaSql);

        // Migration: pre-existing databases won't have columns added in
        // later releases — CREATE TABLE IF NOT EXISTS only fires for a
        // fresh table. PRAGMA-driven detect-and-add keeps each upgrade
        // idempotent.
        var columnNames = conn.Query<TableInfoRow>("PRAGMA table_info(agent_runs);")
            .Select(r => r.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        EnsureColumn(conn, columnNames, "output_dir", "TEXT NULL");
        EnsureColumn(conn, columnNames, "input_tokens", "INTEGER NULL");
        EnsureColumn(conn, columnNames, "output_tokens", "INTEGER NULL");
        EnsureColumn(conn, columnNames, "cache_read_tokens", "INTEGER NULL");
        EnsureColumn(conn, columnNames, "cache_creation_tokens", "INTEGER NULL");
        EnsureColumn(conn, columnNames, "total_cost_usd", "REAL NULL");
        EnsureColumn(conn, columnNames, "provider_id", "TEXT NULL");
    }

    private static void EnsureColumn(
        SqliteConnection conn,
        HashSet<string> existing,
        string name,
        string typeAndConstraints)
    {
        if (existing.Contains(name)) return;
        conn.Execute($"ALTER TABLE agent_runs ADD COLUMN {name} {typeAndConstraints};");
    }

    private sealed class TableInfoRow
    {
        public int Cid { get; set; }
        public string Name { get; set; } = "";
        public string Type { get; set; } = "";
        public int Notnull { get; set; }
        public string? Dflt_Value { get; set; }
        public int Pk { get; set; }
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }

    private static void AppendOutputCore(SqliteConnection conn, string runId, AgentRunOutputLine line)
    {
        conn.Execute("""
            INSERT INTO agent_run_outputs (run_id, timestamp, source, text)
            VALUES (@runId, @timestamp, @source, @text);
            """,
            new
            {
                runId,
                timestamp = FormatTimestamp(line.Timestamp),
                source = line.Source.ToString(),
                text = line.Text,
            });
    }

    private static AgentRun? GetWithConnection(string runId, SqliteConnection conn, int? outputTailCount)
    {
        var row = conn.QuerySingleOrDefault<RunRow>("""
            SELECT id                    AS Id,
                   agent_id              AS AgentId,
                   arguments             AS Arguments,
                   started_at            AS StartedAt,
                   finished_at           AS FinishedAt,
                   status                AS Status,
                   exit_code             AS ExitCode,
                   output_dir            AS OutputDir,
                   input_tokens          AS InputTokens,
                   output_tokens         AS OutputTokens,
                   cache_read_tokens     AS CacheReadTokens,
                   cache_creation_tokens AS CacheCreationTokens,
                   total_cost_usd        AS TotalCostUsd,
                   provider_id           AS ProviderId
            FROM agent_runs WHERE id = @runId;
            """, new { runId });
        if (row is null) return null;

        var outputs = LoadOutputRows(conn, runId, outputTailCount);
        return Hydrate(row, outputs.Rows, outputs.TotalCount);
    }

    private static (IReadOnlyList<OutputRow> Rows, int TotalCount) LoadOutputRows(
        SqliteConnection conn,
        string runId,
        int? outputTailCount)
    {
        if (outputTailCount is null)
        {
            var allRows = conn.Query<OutputRow>("""
                SELECT timestamp AS Timestamp,
                       source    AS Source,
                       text      AS Text
                FROM agent_run_outputs WHERE run_id = @runId ORDER BY id;
                """, new { runId }).ToList();
            return (allRows, allRows.Count);
        }

        var totalCount = conn.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM agent_run_outputs WHERE run_id = @runId;",
            new { runId });
        if (outputTailCount <= 0 || totalCount == 0)
        {
            return (Array.Empty<OutputRow>(), totalCount);
        }

        var tailRows = conn.Query<OutputRow>("""
            SELECT timestamp AS Timestamp,
                   source    AS Source,
                   text      AS Text
            FROM (
                SELECT id, timestamp, source, text
                FROM agent_run_outputs
                WHERE run_id = @runId
                ORDER BY id DESC
                LIMIT @limit
            )
            ORDER BY id;
            """, new { runId, limit = outputTailCount.Value }).ToList();

        return (tailRows, totalCount);
    }

    private static AgentRun Hydrate(
        RunRow row,
        IReadOnlyList<OutputRow> outputs,
        int outputLineCount)
    {
        AgentRunUsage? usage = null;
        if (row.InputTokens.HasValue
            || row.OutputTokens.HasValue
            || row.CacheReadTokens.HasValue
            || row.CacheCreationTokens.HasValue
            || row.TotalCostUsd.HasValue)
        {
            usage = new AgentRunUsage(
                InputTokens: row.InputTokens ?? 0,
                OutputTokens: row.OutputTokens ?? 0,
                CacheReadTokens: row.CacheReadTokens ?? 0,
                CacheCreationTokens: row.CacheCreationTokens ?? 0,
                TotalCostUsd: row.TotalCostUsd.HasValue
                    ? (decimal?)decimal.CreateSaturating(row.TotalCostUsd.Value)
                    : null);
        }

        return AgentRun.Hydrate(
            id: row.Id,
            agentId: row.AgentId,
            arguments: row.Arguments,
            startedAt: ParseTimestamp(row.StartedAt),
            status: Enum.Parse<AgentRunStatus>(row.Status),
            finishedAt: row.FinishedAt is null ? null : ParseTimestamp(row.FinishedAt),
            exitCode: row.ExitCode is null ? null : checked((int)row.ExitCode.Value),
            output: outputs.Select(o => new AgentRunOutputLine(
                ParseTimestamp(o.Timestamp),
                Enum.Parse<AgentRunOutputSource>(o.Source),
                o.Text)).ToList(),
            outputDir: row.OutputDir,
            usage: usage,
            providerId: row.ProviderId,
            outputLineCount: outputLineCount);
    }

    private static string FormatTimestamp(DateTimeOffset value) =>
        value.ToString("O", System.Globalization.CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.Parse(value, System.Globalization.CultureInfo.InvariantCulture);

    // Plain classes (not records) so Dapper materializes via property setters,
    // which is more tolerant to SQLite's nullability and Int64 default for INTEGER.
    private sealed class RunRow
    {
        public string Id { get; set; } = "";
        public string AgentId { get; set; } = "";
        public string Arguments { get; set; } = "";
        public string StartedAt { get; set; } = "";
        public string? FinishedAt { get; set; }
        public string Status { get; set; } = "";
        public long? ExitCode { get; set; }
        public string? OutputDir { get; set; }
        public long? InputTokens { get; set; }
        public long? OutputTokens { get; set; }
        public long? CacheReadTokens { get; set; }
        public long? CacheCreationTokens { get; set; }
        public double? TotalCostUsd { get; set; }
        public string? ProviderId { get; set; }
    }

    private sealed class OutputRow
    {
        public string Timestamp { get; set; } = "";
        public string Source { get; set; } = "";
        public string Text { get; set; } = "";
    }
}
