using System.Text.Json.Serialization;
using Viamus.Doxie.Orchestrator.Agents;

namespace Viamus.Doxie.Orchestrator.Builder;

/// <summary>
/// Wire-shape of <c>./.draft/manifest.json</c> for an agent-builder
/// session. This is what Claude (driven by the <c>/agent-builder</c>
/// skill) writes during the conversation, and what the right-pane
/// preview deserialises every poll cycle. Loose / forgiving — Claude is
/// expected to keep this valid at all times, but the API endpoint
/// tolerates partial drafts (any null/empty field becomes a "—" in the
/// UI) so the user sees progressive refinement instead of red errors.
/// </summary>
public sealed class AgentManifest
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("category")]
    public string? Category { get; set; }

    [JsonPropertyName("icon")]
    public string? Icon { get; set; }

    [JsonPropertyName("skillName")]
    public string? SkillName { get; set; }

    [JsonPropertyName("systemPrompt")]
    public string? SystemPrompt { get; set; }

    [JsonPropertyName("tools")]
    public List<string>? Tools { get; set; }

    [JsonPropertyName("modes")]
    public List<AgentManifestMode>? Modes { get; set; }

    [JsonPropertyName("requirements")]
    public List<AgentManifestRequirement>? Requirements { get; set; }
}

public sealed class AgentManifestMode
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("requiresWorkspace")]
    public bool RequiresWorkspace { get; set; }

    /// <summary>
    /// Hidden modes don't show up in the catalog UI — used for
    /// internal/experimental subcommands (e.g., <c>library/interactive</c>).
    /// </summary>
    [JsonPropertyName("hidden")]
    public bool Hidden { get; set; }

    /// <summary>
    /// Form inputs the user fills in to invoke this mode. Empty / null
    /// means the mode takes no inputs.
    /// </summary>
    [JsonPropertyName("fields")]
    public List<AgentManifestModeField>? Fields { get; set; }

    /// <summary>
    /// String template the orchestrator expands with field values to
    /// build the CLI arg string. Reference fields with <c>{field-id}</c>.
    /// Required when <see cref="Fields"/> is non-empty.
    /// </summary>
    [JsonPropertyName("argumentsTemplate")]
    public string? ArgumentsTemplate { get; set; }
}

public sealed class AgentManifestModeField
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("label")]
    public string? Label { get; set; }

    [JsonPropertyName("placeholder")]
    public string? Placeholder { get; set; }

    [JsonPropertyName("required")]
    public bool Required { get; set; }
}

public sealed class AgentManifestRequirement
{
    [JsonPropertyName("kind")]
    public string? Kind { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("required")]
    public bool Required { get; set; } = true;

    [JsonPropertyName("purpose")]
    public string? Purpose { get; set; }

    /// <summary>
    /// Env-kind only. Extra workspace-relative paths the orchestrator
    /// should look at for a <c>.env</c> file before falling back to the
    /// process environment. Ignored on other kinds.
    /// </summary>
    [JsonPropertyName("searchPaths")]
    public List<string>? SearchPaths { get; set; }
}
