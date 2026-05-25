namespace Viamus.Doxie.Orchestrator.Workflows;

/// <summary>
/// One box on the workflow canvas. <see cref="Kind"/>=Agent means this
/// step actually invokes an agent from the catalog; the other kinds are
/// structural (Trigger anchors the entry point, Aggregate joins
/// sibling outputs, Output writes a final artifact).
///
/// <para><see cref="Inputs"/> carry per-mode field values for Agent
/// nodes. They support <c>{{previous-node-id.output}}</c> placeholders
/// — the simulator does not actually evaluate them but the canvas
/// shows them so the user can prototype the data flow.</para>
/// </summary>
public sealed record WorkflowNode(
    string Id,
    WorkflowNodeKind Kind,
    string Label,
    int X,
    int Y,
    string? AgentId = null,
    string? AgentMode = null,
    IReadOnlyDictionary<string, string>? Inputs = null,
    string? OutputWorkspaceId = null,
    string? OutputFileName = null,
    /// <summary>
    /// User-owned Workspace this step runs *inside*. When set, the
    /// runner dispatches the agent with the workspace folder as cwd
    /// (so its mounted libraries / CLAUDE.md / memory are reachable)
    /// and the workspace's <c>.env</c> is layered into the subprocess
    /// env. Required for agents whose mode declares
    /// <c>RequiresWorkspace</c>; optional otherwise.
    /// </summary>
    string? WorkspaceId = null,
    /// <summary>
    /// Body marker for a for-each Loop. When set, identifies which
    /// Loop owns this node — the runner skips body nodes from the
    /// main scheduling loop and runs them N times (once per iteration)
    /// inside <see cref="Kind"/>=Loop's task. The Loop node itself
    /// has <c>LoopId == null</c>; only body nodes carry the marker.
    /// Default <c>null</c> for backward-compat with pre-Loop
    /// <c>workflow.json</c> files.
    /// </summary>
    string? LoopId = null);
