namespace Viamus.Doxie.Orchestrator.Agents;

/// <summary>
/// Optional provider capability for CLIs that do not always emit usage
/// telemetry in headless mode. The runner calls this only when no
/// authoritative usage event was parsed from stdout.
/// </summary>
public interface IAgentUsageEstimator
{
    AgentRunUsage? EstimateUsage(string formattedPrompt, IReadOnlyList<AgentRunOutputLine> output);
}
