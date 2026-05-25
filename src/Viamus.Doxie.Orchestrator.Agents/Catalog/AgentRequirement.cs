namespace Viamus.Doxie.Orchestrator.Agents;

/// <summary>
/// A single prerequisite for running an agent — surfaced on the agent detail
/// page so the user knows what to configure before clicking Run.
/// </summary>
/// <param name="Kind">Category (MCP server, env var, external tool, permission).</param>
/// <param name="Name">Identifier shown in the chip (e.g. <c>mcp-example</c>, <c>QUALITY_TOKEN</c>, <c>git</c>).</param>
/// <param name="Required">
/// True when the agent cannot run without it. False marks "nice-to-have"
/// requirements that disable a feature when missing (e.g. Quality Tool polling
/// is skipped if <c>QUALITY_TOKEN</c> is unset, but sample-watch still runs).
/// </param>
/// <param name="Purpose">One short sentence describing why the agent needs it.</param>
/// <param name="SearchPaths">
/// Optional fallback locations the checker probes when the runtime
/// environment doesn't satisfy the requirement directly. For
/// <see cref="AgentRequirementKind.Env"/>, each path is parsed as a
/// dotenv file (<c>KEY=VALUE</c> per line) and the requirement counts as
/// satisfied if <see cref="Name"/> is present and non-empty. Mirrors what
/// helper scripts already do (e.g. sample-watch's Sonar scripts read
/// <c>.sample-watch/.env</c> at the project root) so the UI doesn't lie about a
/// "missing" token that the agent would actually find at runtime.
/// </param>
public sealed record AgentRequirement(
    AgentRequirementKind Kind,
    string Name,
    bool Required,
    string Purpose,
    IReadOnlyList<string>? SearchPaths = null);
