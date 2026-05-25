namespace Viamus.Doxie.Orchestrator.Workflows;

/// <summary>
/// How a workflow gets fired. Only one kind active at a time — the
/// fields not relevant to the chosen kind are left null. The simulator
/// honours <see cref="Kind"/>=Manual; the other kinds are persisted
/// faithfully but the prototype does not actually wire a Cron daemon
/// or webhook listener.
/// </summary>
public sealed record WorkflowTrigger(
    WorkflowTriggerKind Kind,
    string? CronExpression = null,
    string? EventName = null,
    string? WebhookPath = null,
    /// <summary>
    /// Typed inputs the user must supply at Run-time. Only meaningful
    /// for <c>Manual</c> triggers — cron/event/webhook ignore this
    /// list (cron has nowhere to ask, event/webhook payloads are a
    /// future story). Values land on
    /// <see cref="WorkflowRun.TriggerInputs"/> and substitute into
    /// <c>node.Inputs</c> via the <c>{{trigger.&lt;id&gt;}}</c>
    /// placeholder pattern.
    /// </summary>
    IReadOnlyList<WorkflowTriggerInputField>? Inputs = null);
