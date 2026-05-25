namespace Viamus.Doxie.Orchestrator.Agents;

public interface IAgentRunner
{
    /// <summary>
    /// Spawns the agent.
    /// When <paramref name="keepSessionAlive"/> is <c>true</c> the runner
    /// uses Claude's stream-json input/output mode and leaves stdin open
    /// after the initial command, so session-scoped crons keep firing
    /// inside the same subprocess until cancel or shutdown.
    /// When <paramref name="workingDirectoryOverride"/> is non-null, the
    /// subprocess is launched with that directory as cwd instead of the
    /// configured default — used to dispatch an agent inside a specific
    /// user-owned Workspace so its mounted libraries and on-disk state
    /// are reachable.
    /// When <paramref name="envOverrides"/> is non-null, each entry is
    /// layered on top of the orchestrator's process environment when
    /// the subprocess is spawned. Used by the workflow runner to pass
    /// <c>DOXIE_WORKFLOW_OUTPUT_DIR</c>, the per-workflow env block,
    /// etc. Keys already present in the process env are overwritten.
    /// <paramref name="modeId"/> and <paramref name="workspaceId"/>
    /// flow into the per-skill memory filter as condition inputs (so
    /// a memory with <c>condition: mode = "from-files"</c> only loads
    /// when the user picks that mode). Both default to empty strings
    /// — callers without this context still dispatch correctly, they
    /// just lose access to the mode/workspace condition keys.
    /// When <paramref name="displayArguments"/> is supplied, it is the
    /// compact label persisted to run history while <paramref name="arguments"/>
    /// remains the real prompt sent to the provider. This keeps internal
    /// orchestration prompts from turning history tables into transcript dumps.
    /// </summary>
    AgentRun Start(
        string agentId,
        string arguments,
        bool keepSessionAlive = false,
        string? workingDirectoryOverride = null,
        IReadOnlyDictionary<string, string>? envOverrides = null,
        string? modeId = null,
        string? workspaceId = null,
        string? displayArguments = null);

    void Cancel(string runId);

    /// <summary>
    /// Adds human guidance to a live run. Providers with an open stdin
    /// session receive the hint immediately; all live runs persist the
    /// hint to their operator-hints file so agents can inspect it while
    /// they work.
    /// </summary>
    bool AddOperatorHint(string runId, string hint) => false;

    AgentRun? Get(string runId);

    event Action<AgentRun>? RunUpdated;
}
