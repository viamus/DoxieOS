namespace Viamus.Doxie.Orchestrator.Workflows;

public enum WorkflowNodeRunStatus
{
    Pending,
    Running,
    Succeeded,
    Failed,
    Skipped,

    /// <summary>
    /// The node is an <c>approval-gate</c> connector currently parked
    /// waiting for a human decision. Set when the runner enters the
    /// gate; transitions to <see cref="Succeeded"/> on Approve or
    /// <see cref="Failed"/> on Reject (with the rejection reason in
    /// the node's logs).
    /// </summary>
    AwaitingApproval,
}
