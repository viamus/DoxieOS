namespace Viamus.Doxie.Orchestrator.Agents;

/// <summary>
/// Optional provider capability for one-shot runs whose prompt should be
/// streamed through stdin instead of argv. Used when the final formatted
/// prompt is too large for Windows command-line limits.
/// </summary>
public interface IHeadlessStdinAgentProvider
{
    IEnumerable<string> BuildHeadlessStdinArgs(AgentDescriptor agent);

    void WriteHeadlessPrompt(System.IO.TextWriter stdin, AgentDescriptor agent, string formattedPrompt);
}
