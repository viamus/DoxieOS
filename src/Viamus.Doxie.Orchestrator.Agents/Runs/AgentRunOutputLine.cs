namespace Viamus.Doxie.Orchestrator.Agents;

public sealed record AgentRunOutputLine(
    DateTimeOffset Timestamp,
    AgentRunOutputSource Source,
    string Text);
