namespace Viamus.Doxie.Orchestrator.Workflows;

public interface IWorkflowRunStore
{
    void Add(WorkflowRun run);

    /// <summary>
    /// Snapshot the current state of <paramref name="run"/> to whatever
    /// backing store this is. The in-memory implementation is a no-op
    /// (it already holds the live reference); SQLite uses this as the
    /// "persist on terminal status" hook so historical runs survive an
    /// orchestrator restart. Safe to call multiple times for the same
    /// run id — the row is upserted.
    /// </summary>
    void Save(WorkflowRun run);

    WorkflowRun? Get(string runId);

    IReadOnlyList<WorkflowRun> ListByWorkflow(string workflowId);

    IReadOnlyList<WorkflowRun> ListAll();

    int ClearAll();
}
