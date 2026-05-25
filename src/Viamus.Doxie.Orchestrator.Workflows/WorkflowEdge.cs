namespace Viamus.Doxie.Orchestrator.Workflows;

/// <summary>
/// Directed dependency between two workflow nodes. The downstream node
/// cannot start until the upstream one finishes successfully. Multiple
/// edges into the same node = it has multiple upstream deps (forming
/// the join pattern an Aggregate node expects).
/// </summary>
public sealed record WorkflowEdge(string FromNodeId, string ToNodeId);
