namespace Viamus.Doxie.Orchestrator.Workflows;

/// <summary>
/// One execution of a <see cref="WorkflowDefinition"/>. Holds the
/// per-node run records, lifecycle timestamps, and what fired it
/// (manual vs. cron vs. event). Live runs live in
/// <see cref="IWorkflowRunStore"/> in-memory; terminal runs are
/// persisted to SQLite by <see cref="SqliteWorkflowRunStore"/> and
/// rehydrated via <see cref="Hydrate"/> on orchestrator restart.
/// </summary>
public sealed class WorkflowRun
{
    private readonly Dictionary<string, WorkflowNodeRun> _nodeRuns;
    private IReadOnlyDictionary<string, string> _triggerInputs;

    public WorkflowRun(
        string id,
        string workflowId,
        string triggeredBy,
        DateTimeOffset startedAt,
        IEnumerable<string> nodeIds,
        IReadOnlyDictionary<string, string>? triggerInputs = null)
    {
        Id = id;
        WorkflowId = workflowId;
        TriggeredBy = triggeredBy;
        StartedAt = startedAt;
        Status = WorkflowRunStatus.Queued;
        _nodeRuns = nodeIds.ToDictionary(n => n, n => new WorkflowNodeRun(n));
        _triggerInputs = triggerInputs ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    public string Id { get; }

    public string WorkflowId { get; }

    /// <summary>
    /// Human label for what fired the run: "manual", "cron",
    /// "event:run-completed", "webhook:/x", etc.
    /// </summary>
    public string TriggeredBy { get; }

    public DateTimeOffset StartedAt { get; }

    public DateTimeOffset? FinishedAt { get; internal set; }

    public WorkflowRunStatus Status { get; internal set; }

    /// <summary>
    /// User-supplied values for the workflow's
    /// <see cref="WorkflowTrigger.Inputs"/> at Run time. Substituted
    /// into <c>node.Inputs</c> via <c>{{trigger.&lt;id&gt;}}</c>
    /// placeholders before agent dispatch. Empty when the workflow
    /// declared no trigger inputs (or the run came from cron / event /
    /// webhook).
    /// </summary>
    public IReadOnlyDictionary<string, string> TriggerInputs => _triggerInputs;

    public IReadOnlyDictionary<string, WorkflowNodeRun> NodeRuns => _nodeRuns;

    public WorkflowNodeRun NodeRun(string nodeId) => _nodeRuns[nodeId];

    /// <summary>
    /// Registers a NodeRun for an id that wasn't pre-declared at run
    /// construction time. Used by the Loop primitive to add per-iteration
    /// body-node records (keyed <c>{originalBodyId}#iter-{i}</c>) so the
    /// UI can drill into each iteration's logs without conflating runs.
    /// Idempotent: returns the existing record if the id is already
    /// registered. Thread-safe — concurrent iterations may race here.
    /// </summary>
    public WorkflowNodeRun AddNodeRun(string nodeId)
    {
        lock (_nodeRuns)
        {
            if (_nodeRuns.TryGetValue(nodeId, out var existing)) return existing;
            var nr = new WorkflowNodeRun(nodeId);
            _nodeRuns[nodeId] = nr;
            return nr;
        }
    }

    /// <summary>
    /// Reconstructs a <see cref="WorkflowRun"/> from persisted state.
    /// Used by the SQLite store when hydrating historical runs at
    /// startup. Each <paramref name="nodeRuns"/> entry already
    /// carries its full state (status, output summary, log lines)
    /// from <see cref="WorkflowNodeRun.Hydrate"/>.
    /// </summary>
    public static WorkflowRun Hydrate(
        string id,
        string workflowId,
        string triggeredBy,
        DateTimeOffset startedAt,
        DateTimeOffset? finishedAt,
        WorkflowRunStatus status,
        IReadOnlyDictionary<string, WorkflowNodeRun> nodeRuns,
        IReadOnlyDictionary<string, string>? triggerInputs = null)
    {
        var run = new WorkflowRun(id, workflowId, triggeredBy, startedAt, nodeRuns.Keys, triggerInputs)
        {
            Status = status,
            FinishedAt = finishedAt,
        };
        // The constructor created fresh empty WorkflowNodeRun records;
        // swap them out for the hydrated ones (with logs + summaries).
        foreach (var (nodeId, hydrated) in nodeRuns)
        {
            run._nodeRuns[nodeId] = hydrated;
        }
        return run;
    }
}
