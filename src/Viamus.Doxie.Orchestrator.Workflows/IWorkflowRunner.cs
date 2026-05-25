namespace Viamus.Doxie.Orchestrator.Workflows;

public interface IWorkflowRunner
{
    /// <summary>
    /// Starts a new run of the given workflow. Returns immediately with
    /// a <see cref="WorkflowRun"/> in <see cref="WorkflowRunStatus.Queued"/>
    /// or <see cref="WorkflowRunStatus.Running"/>; subsequent state
    /// changes fire <see cref="RunUpdated"/>. <paramref name="triggeredBy"/>
    /// labels what fired the run ("manual", "cron", "event:foo", etc.)
    /// purely for display/audit. <paramref name="triggerInputs"/>
    /// supplies values for the workflow's
    /// <see cref="WorkflowTrigger.Inputs"/>; substituted into
    /// <c>node.Inputs</c> via <c>{{trigger.&lt;id&gt;}}</c> placeholders
    /// at dispatch time. Pass null when the workflow declares no
    /// trigger inputs (cron / event / webhook entries always pass null).
    /// </summary>
    WorkflowRun Start(
        WorkflowDefinition definition,
        string triggeredBy,
        IReadOnlyDictionary<string, string>? triggerInputs = null);

    /// <summary>
    /// Starts a new run by replaying a previous run from
    /// <paramref name="nodeId"/> onward. Nodes upstream of
    /// <paramref name="nodeId"/> are reused from <paramref name="sourceRunId"/>
    /// when they succeeded, including their artifact folders, so downstream
    /// nodes keep receiving normal workflow input directories.
    /// </summary>
    WorkflowRun StartFrom(
        WorkflowDefinition definition,
        string sourceRunId,
        string nodeId,
        string? guidance = null);

    /// <summary>
    /// Cooperative cancel — flips the run to Cancelled and skips any
    /// nodes that have not yet started. Nodes already running in the
    /// simulator complete normally (the prototype does not preempt).
    /// </summary>
    void Cancel(string runId);

    /// <summary>
    /// Adds operator guidance to a live workflow run. Active agent nodes
    /// receive the hint through the agent runner when possible; future
    /// nodes receive the accumulated hints in their dispatch prompt.
    /// </summary>
    bool AddOperatorHint(string runId, string hint) => false;

    /// <summary>
    /// Resolve an <c>approval-gate</c> connector node that's currently
    /// parked waiting for a human decision. <paramref name="approve"/>
    /// = true forwards the upstream payload to dependents; false marks
    /// the gate as Failed (with <paramref name="comment"/> captured on
    /// the node's logs) and cancels the rest of the run. Returns a
    /// status the caller can map to HTTP — RunNotFound / NotAwaiting
    /// translate to 404 / 409 respectively.
    /// </summary>
    ApprovalGateResolveResult ResumeApprovalGate(
        string runId,
        string nodeId,
        bool approve,
        string? comment);

    /// <summary>
    /// Fires whenever the run's overall status, any node's status, or
    /// any node's log/output set changes. UI subscribers re-render on
    /// every event.
    /// </summary>
    event Action<WorkflowRun>? RunUpdated;
}

public enum ApprovalGateResolveResult
{
    /// <summary>The gate was resolved and the run is moving on.</summary>
    Resolved,

    /// <summary>No live run with the supplied id (already finished or never existed).</summary>
    RunNotFound,

    /// <summary>The node isn't currently parked at an approval-gate await.</summary>
    NotAwaitingApproval,
}
