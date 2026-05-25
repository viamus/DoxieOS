namespace Viamus.Doxie.Orchestrator.Agents;

public sealed record AgentModeField(
    string Id,
    string Label,
    string Placeholder = "",
    bool Required = true);
