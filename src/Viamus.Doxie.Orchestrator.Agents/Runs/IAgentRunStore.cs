namespace Viamus.Doxie.Orchestrator.Agents;

public interface IAgentRunStore
{
    void Add(AgentRun run);

    /// <summary>
    /// Loads one run. By default the full output log is hydrated; pass
    /// <paramref name="outputTailCount"/> to load only the most recent lines
    /// while preserving <see cref="AgentRun.OutputLineCount"/>.
    /// </summary>
    AgentRun? Get(string runId, int? outputTailCount = null);

    /// <summary>
    /// Lists run metadata for an agent without hydrating output logs.
    /// Use <see cref="Get(string, int?)"/> when a selected run's output is needed.
    /// </summary>
    IReadOnlyList<AgentRun> ListByAgent(string agentId);

    /// <summary>
    /// Lists run metadata without hydrating output logs.
    /// Use <see cref="Get(string, int?)"/> when a selected run's output is needed.
    /// </summary>
    IReadOnlyList<AgentRun> ListAll();

    void UpdateStatus(string runId, AgentRunStatus status, DateTimeOffset? finishedAt, int? exitCode);

    /// <summary>
    /// Persists the output folder produced by a run — typically extracted
    /// from the agent's final JSON manifest at process exit. Called once
    /// per run; downstream UI uses this to render a file explorer rooted
    /// at the path.
    /// </summary>
    void UpdateOutputDir(string runId, string outputDir);

    /// <summary>
    /// Persists token + cost accounting captured from the provider's
    /// terminal "result" event. Called at most once per run, near
    /// process exit. <paramref name="usage"/> mirrors what is set on
    /// <see cref="AgentRun.Usage"/>; the store should update the row
    /// in place so reloads see the same numbers as the live run.
    /// </summary>
    void UpdateUsage(string runId, AgentRunUsage usage);

    void AppendOutput(string runId, AgentRunOutputLine line);

    /// <summary>
    /// Flips every run currently persisted as <see cref="AgentRunStatus.Queued"/> or
    /// <see cref="AgentRunStatus.Running"/> to <see cref="AgentRunStatus.Interrupted"/>,
    /// stamping <c>FinishedAt</c> with the current time. Returns the number of rows
    /// affected. Called once at orchestrator startup to reconcile runs whose subprocess
    /// died with the previous orchestrator instance (Job Object kill-on-close).
    /// </summary>
    int MarkOrphanedAsInterrupted();

    int ClearAll();
}
