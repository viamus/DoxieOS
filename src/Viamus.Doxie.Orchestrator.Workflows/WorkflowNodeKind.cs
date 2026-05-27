namespace Viamus.Doxie.Orchestrator.Workflows;

/// <summary>
/// Role a node plays in a workflow graph. Trigger and Output are
/// structural anchors; Agent and Aggregate carry actual work.
/// </summary>
public enum WorkflowNodeKind
{
    /// <summary>Entry point — bound to the workflow's Trigger config.</summary>
    Trigger,

    /// <summary>Invokes one agent (catalog id + mode + field values).</summary>
    Agent,

    /// <summary>Joins outputs from N upstream nodes into one payload.</summary>
    Aggregate,

    /// <summary>Terminal — writes the accumulated result somewhere (e.g. workspace).</summary>
    Output,

    /// <summary>
    /// For-each / repetition primitive. Consumes an array-shaped upstream
    /// payload and runs its body sub-graph once per element, with
    /// configurable concurrency. Body nodes are tagged via
    /// <see cref="WorkflowNode.LoopId"/>; the loop aggregates each
    /// iteration's exit-node output into a JSON array on its own folder
    /// for downstream consumers. Recognized via the well-known agent id
    /// <c>"loop"</c>; same connector pattern as aggregate / approval-gate.
    /// </summary>
    Loop,

    /// <summary>
    /// If/else decision primitive. Evaluates a condition against upstream
    /// JSON and activates only outgoing edges whose condition matches the
    /// selected branch (<c>true</c> or <c>false</c>).
    /// </summary>
    Decision,
}
