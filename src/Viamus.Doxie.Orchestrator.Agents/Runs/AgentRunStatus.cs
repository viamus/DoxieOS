namespace Viamus.Doxie.Orchestrator.Agents;

public enum AgentRunStatus
{
    Queued,
    Running,
    Completed,
    Failed,
    Cancelled,
    Interrupted,
}
