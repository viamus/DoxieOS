namespace Viamus.Doxie.Orchestrator.Workflows;

/// <summary>
/// Directed dependency between two workflow nodes. The downstream node
/// cannot start until the upstream one finishes successfully. Multiple
/// edges into the same node = it has multiple upstream deps (forming
/// the join pattern an Aggregate node expects).
///
/// <para><see cref="Condition"/> is optional. When set on an edge
/// leaving a Decision node, the downstream node only starts if the
/// decision selected that branch. Supported values are normalized by the
/// runner; the authoring UI writes <c>true</c> and <c>false</c>.</para>
/// </summary>
public sealed record WorkflowEdge(string FromNodeId, string ToNodeId, string? Condition = null);
