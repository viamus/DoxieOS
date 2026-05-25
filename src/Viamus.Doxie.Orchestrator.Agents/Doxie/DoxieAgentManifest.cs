using System.Text.Json.Serialization;

namespace Viamus.Doxie.Orchestrator.Agents.Doxie;

/// <summary>
/// Canonical manifest for a Claude subagent (Task-tool dispatch target).
/// Lives in <c>.doxie/agents/&lt;id&gt;.md</c> as YAML frontmatter; the body
/// of that file is the agent's system prompt.
///
/// Mirrors the fields Claude Code reads from <c>.claude/agents/&lt;id&gt;.md</c>:
/// <c>name</c>, <c>description</c>, <c>tools</c>, <c>model</c>. Codex doesn't
/// have first-class subagents but the Codex shim (<c>AGENTS.md</c>) lists
/// them in a "Subagents" section so the model knows they exist.
/// </summary>
public sealed record DoxieAgentManifest
{
    public string Id { get; init; } = "";

    public string Description { get; init; } = "";

    /// <summary>Comma-separated tool list as Claude Code expects in the frontmatter.</summary>
    public string? Tools { get; init; }

    /// <summary>Model id (e.g. <c>claude-sonnet-4-5</c>) or <c>inherit</c>.</summary>
    public string? Model { get; init; }

    /// <summary>Optional preferred isolation hint for the orchestrator UI.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public DoxieAgentIsolation? Isolation { get; init; }
}

public enum DoxieAgentIsolation
{
    None,
    Worktree,
    Sandbox,
}

public sealed record DoxieAgentCanon(
    DoxieAgentManifest Manifest,
    string SystemPrompt);
