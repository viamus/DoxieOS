namespace Viamus.Doxie.Orchestrator.Workflows;

public enum WorkflowRunStatus
{
    Queued,
    Running,
    Succeeded,
    Failed,
    Cancelled,

    /// <summary>
    /// The run reached an <c>approval-gate</c> connector node and is
    /// parked waiting for a human to click Approve or Reject. The
    /// runner holds the in-process await; the SQLite snapshot is also
    /// updated at pause time so the dashboard shows the parked state.
    /// On approve the run resumes (status flips back to Running until
    /// the next gate or terminal status); on reject the run lands as
    /// Cancelled with the rejection reason captured on the gate node.
    /// </summary>
    AwaitingApproval,
}
