namespace Viamus.Doxie.Orchestrator.Agents;

/// <summary>
/// Probes the local environment to decide whether an agent's prerequisite
/// is currently satisfied (env var set, MCP server configured, tool on PATH).
/// Used by the agent detail page to show ✓ / ✗ next to each requirement.
/// </summary>
public interface IAgentRequirementChecker
{
    /// <summary>
    /// Default check — looks at process env + skill-local <c>.env</c> files
    /// for Env requirements; PATH for Tool; <c>.mcp.json</c> /
    /// <c>~/.claude.json</c> for Mcp.
    /// </summary>
    AgentRequirementStatus Check(AgentRequirement requirement);

    /// <summary>
    /// Same check, but treats keys present in <paramref name="suppliedEnv"/>
    /// (with non-empty values) as already satisfying an Env requirement.
    /// Used by the agent detail page so the per-run env editor can flip a
    /// missing-token chip green as soon as the user types the value in,
    /// and by the workflow form so a workflow's <c>Env</c> block satisfies
    /// the prereq for any agent step that needs that var. Tool / Mcp
    /// requirements ignore the dict — they're not env-vars.
    /// </summary>
    AgentRequirementStatus Check(AgentRequirement requirement, IReadOnlyDictionary<string, string>? suppliedEnv);
}
