namespace Viamus.Doxie.Orchestrator.Workflows;

/// <summary>
/// Per-node execution record inside a <see cref="WorkflowRun"/>.
/// Mutated in place by the runner; the UI subscribes to
/// <see cref="IWorkflowRunner.RunUpdated"/> to refresh.
/// </summary>
public sealed class WorkflowNodeRun
{
    private readonly List<string> _logs = new();

    public WorkflowNodeRun(string nodeId)
    {
        NodeId = nodeId;
        Status = WorkflowNodeRunStatus.Pending;
    }

    public string NodeId { get; }

    public WorkflowNodeRunStatus Status { get; internal set; }

    public DateTimeOffset? StartedAt { get; internal set; }

    public DateTimeOffset? FinishedAt { get; internal set; }

    /// <summary>
    /// Simulated agent stdout / status lines, in order. The simulator
    /// streams 3-6 of these per node so the UI looks alive.
    /// </summary>
    public IReadOnlyList<string> Logs => _logs;

    /// <summary>
    /// Free-form payload representing what this node "produced" — the
    /// downstream node's inputs reference it via the
    /// <c>{{node.id.output}}</c> placeholder syntax. The simulator just
    /// stores a one-line summary; a real runner would carry an actual
    /// artifact path / structured result.
    /// </summary>
    public string? OutputSummary { get; internal set; }

    internal void AppendLog(string line) => _logs.Add(line);

    /// <summary>
    /// Reconstructs a node-run from persisted state. Used by the
    /// SQLite store at startup. <paramref name="logs"/> is appended
    /// verbatim — order is the only thing the run UI cares about.
    /// </summary>
    public static WorkflowNodeRun Hydrate(
        string nodeId,
        WorkflowNodeRunStatus status,
        DateTimeOffset? startedAt,
        DateTimeOffset? finishedAt,
        string? outputSummary,
        IEnumerable<string> logs)
    {
        var nr = new WorkflowNodeRun(nodeId)
        {
            Status = status,
            StartedAt = startedAt,
            FinishedAt = finishedAt,
            OutputSummary = outputSummary,
        };
        foreach (var line in logs) nr._logs.Add(line);
        return nr;
    }
}
