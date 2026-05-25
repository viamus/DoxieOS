namespace Viamus.Doxie.Orchestrator.Workflows;

/// <summary>
/// A composition of agents that runs on a trigger. Persisted as a
/// single <c>workflow.json</c> file under <c>.doxie/workflows/&lt;id&gt;/</c>.
/// </summary>
public sealed record WorkflowDefinition(
    string Id,
    string Name,
    string Description,
    WorkflowTrigger Trigger,
    IReadOnlyList<WorkflowNode> Nodes,
    IReadOnlyList<WorkflowEdge> Edges,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    string? WorkspaceId = null,
    /// <summary>
    /// Workflow-scoped environment variables, edited via the UI and
    /// persisted in <c>workflow.json</c>. Composed into the subprocess
    /// env at run time on top of the orchestrator's process env, the
    /// skill-local <c>.env</c>, and the workspace's <c>.env</c> (in
    /// that priority order, lowest â†’ highest). Plaintext: do not put
    /// production secrets here.
    /// </summary>
    IReadOnlyDictionary<string, string>? Env = null,
    /// <summary>
    /// Master switch. When false, manual Run requests are rejected
    /// (HTTP 409) and the cron daemon skips this workflow at trigger
    /// time. The constructor default is <c>true</c> so pre-existing
    /// <c>workflow.json</c> files (where the field is absent on disk)
    /// stay enabled on deserialization — that's the only reason the
    /// property default isn't <c>false</c>. New cron-triggered
    /// workflows are forced to <c>false</c> at creation time via
    /// <see cref="WithCreateTimeEnabledPolicy"/>.
    /// </summary>
    bool Enabled = true,
    bool IsPrivate = false,
    string CatalogId = "default")
{
    /// <summary>
    /// Returns a copy with the create-time enabled policy applied:
    /// cron-triggered workflows nascem desabilitados (the workspace's
    /// env vars haven't been filled yet, so an enabled cron would
    /// fire against an unconfigured environment). Manual / Event /
    /// Webhook are passed through — those don't fire on a schedule.
    /// Call this on the API + builder creation paths only, never on
    /// update or deserialization.
    /// </summary>
    public WorkflowDefinition WithCreateTimeEnabledPolicy()
        => Trigger.Kind == WorkflowTriggerKind.Cron && Enabled
            ? this with { Enabled = false }
            : this;
}
