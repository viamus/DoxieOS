namespace Viamus.Doxie.Orchestrator.Workflows;

/// <summary>
/// Convention for where a workflow run's per-node scratch folders live.
/// Each run gets its own folder under <c>WorkflowRunsDirectory</c>; each
/// node inside that run gets a sub-folder. Agents drop produced files
/// into their node folder; downstream consumers (aggregate, output)
/// walk the upstream folders to collect them.
///
/// <para>The folder names are deliberately filesystem-safe (run id is
/// already a Guid hex prefix; node ids are validated kebab-case at the
/// API edge) — no escaping needed. Pure path math, no I/O — callers
/// are responsible for actually creating directories.</para>
/// </summary>
public static class WorkflowRunPaths
{
    /// <summary>Env var pointing the agent at its node-scoped output folder.</summary>
    public const string OutputDirEnvVar = "DOXIE_WORKFLOW_OUTPUT_DIR";

    /// <summary>Env var carrying the (semicolon-separated) list of upstream node folders.</summary>
    public const string InputDirsEnvVar = "DOXIE_WORKFLOW_INPUT_DIRS";

    /// <summary>Env var carrying the workflow-level workspace id when attached.</summary>
    public const string WorkspaceIdEnvVar = "DOXIE_WORKFLOW_WORKSPACE_ID";

    /// <summary>Env var carrying the workflow-level workspace path when attached.</summary>
    public const string WorkspacePathEnvVar = "DOXIE_WORKFLOW_WORKSPACE_PATH";

    /// <summary>Env var carrying the workflow id for agent nodes running inside a workflow.</summary>
    public const string WorkflowIdEnvVar = "DOXIE_WORKFLOW_ID";

    /// <summary>Env var carrying the workflow definition root folder.</summary>
    public const string WorkflowRootEnvVar = "DOXIE_WORKFLOW_ROOT";

    /// <summary>The run's root folder: <c>{runsRoot}/{runId}/</c>.</summary>
    public static string RunDir(string runsRoot, string runId) =>
        Path.Combine(runsRoot, runId);

    /// <summary>One node's folder inside a run: <c>{runsRoot}/{runId}/{nodeId}/</c>.</summary>
    public static string NodeDir(string runsRoot, string runId, string nodeId) =>
        Path.Combine(runsRoot, runId, nodeId);

    /// <summary>
    /// One iteration's root folder under a Loop node:
    /// <c>{runsRoot}/{runId}/{loopNodeId}/iter-{index}/</c>. Each body
    /// node's per-iteration output dir lives under this as
    /// <c>{loopNodeId}/iter-{i}/{bodyNodeId}/</c> via
    /// <see cref="IterationNodeDir"/>. The folder also holds
    /// <c>_loop-input/item.json</c> — the JSON payload of the current
    /// element, exposed to the body's entry node via INPUT_DIRS.
    /// </summary>
    public static string IterationDir(string runsRoot, string runId, string loopNodeId, int iterationIndex) =>
        Path.Combine(runsRoot, runId, loopNodeId, $"iter-{iterationIndex}");

    /// <summary>
    /// One body node's output folder for one iteration:
    /// <c>{runsRoot}/{runId}/{loopNodeId}/iter-{i}/{bodyNodeId}/</c>.
    /// </summary>
    public static string IterationNodeDir(string runsRoot, string runId, string loopNodeId, int iterationIndex, string bodyNodeId) =>
        Path.Combine(IterationDir(runsRoot, runId, loopNodeId, iterationIndex), bodyNodeId);

    /// <summary>
    /// Folder inside an iteration that holds the loop's per-element
    /// inputs (today: <c>item.json</c>). Surfaced to the body's entry
    /// node via the INPUT_DIRS env var alongside any other upstream
    /// (only relevant if a body node has upstreams outside the body,
    /// which the runner currently disallows).
    /// </summary>
    public static string IterationInputDir(string runsRoot, string runId, string loopNodeId, int iterationIndex) =>
        Path.Combine(IterationDir(runsRoot, runId, loopNodeId, iterationIndex), "_loop-input");

    /// <summary>Env var carrying the JSON of the current iteration's element.</summary>
    public const string LoopItemEnvVar = "LOOP_ITEM";

    /// <summary>Env var carrying the zero-based iteration index.</summary>
    public const string LoopIndexEnvVar = "LOOP_INDEX";

    /// <summary>Env var carrying the total number of iterations (length of the input array).</summary>
    public const string LoopTotalEnvVar = "LOOP_TOTAL";
}
