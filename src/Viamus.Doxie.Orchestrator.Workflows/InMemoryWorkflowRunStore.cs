using System.Collections.Concurrent;

namespace Viamus.Doxie.Orchestrator.Workflows;

/// <summary>
/// Prototype-grade run store: keeps every <see cref="WorkflowRun"/>
/// in process memory, indexed by run id and by workflow id. A real
/// implementation would mirror the SQLite persistence used for
/// agent runs so history survives restarts.
/// </summary>
public sealed class InMemoryWorkflowRunStore : IWorkflowRunStore
{
    private readonly ConcurrentDictionary<string, WorkflowRun> _byId = new();

    public void Add(WorkflowRun run) => _byId[run.Id] = run;

    public void Save(WorkflowRun run)
    {
        // No-op: the dictionary already holds the live reference,
        // which the runner mutates in place. Implemented just to
        // satisfy IWorkflowRunStore — the SQLite store uses this
        // hook to write a snapshot.
    }

    public WorkflowRun? Get(string runId) =>
        _byId.TryGetValue(runId, out var run) ? run : null;

    public IReadOnlyList<WorkflowRun> ListByWorkflow(string workflowId) =>
        _byId.Values
            .Where(r => string.Equals(r.WorkflowId, workflowId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(r => r.StartedAt)
            .ToList();

    public IReadOnlyList<WorkflowRun> ListAll() =>
        _byId.Values
            .OrderByDescending(r => r.StartedAt)
            .ToList();

    public int ClearAll()
    {
        var count = _byId.Count;
        _byId.Clear();
        return count;
    }
}
