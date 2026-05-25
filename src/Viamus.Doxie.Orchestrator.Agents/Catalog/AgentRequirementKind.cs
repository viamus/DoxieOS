namespace Viamus.Doxie.Orchestrator.Agents;

/// <summary>
/// Categorises a prerequisite the orchestrator needs to satisfy before an
/// agent can run end-to-end. Drives the icon and grouping shown in the UI.
/// </summary>
public enum AgentRequirementKind
{
    /// <summary>An MCP server must be reachable (e.g. <c>mcp-example</c>).</summary>
    Mcp,

    /// <summary>An environment variable must be set (e.g. <c>QUALITY_TOKEN</c>).</summary>
    Env,

    /// <summary>An external CLI/tool must be on PATH (e.g. <c>git</c>, <c>dotnet script</c>).</summary>
    Tool,

    /// <summary>A Claude Code permission/tool grant must be in place (rare; most are inherent to the agent).</summary>
    Permission,
}
