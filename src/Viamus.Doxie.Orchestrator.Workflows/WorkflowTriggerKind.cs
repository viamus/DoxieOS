namespace Viamus.Doxie.Orchestrator.Workflows;

/// <summary>
/// What fires a workflow run. Manual is "user clicks Run", Cron is a
/// time schedule, Event is reaction to an in-process orchestrator
/// event (a run completed, a notification fired, etc.), Webhook is a
/// local HTTP POST.
/// </summary>
public enum WorkflowTriggerKind
{
    Manual,
    Cron,
    Event,
    Webhook,
}
