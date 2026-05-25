using System.Text.Json.Serialization;

namespace Viamus.Doxie.Orchestrator.Agents.Doxie;

/// <summary>
/// Canonical, provider-neutral manifest of a single skill. Lives at
/// <c>.doxie/skills/&lt;id&gt;/manifest.json</c> and is the source of truth.
/// Provider-specific shims (<c>.claude/skills/&lt;id&gt;/SKILL.md</c>,
/// <c>.codex/AGENTS.md</c>) are derived from this — never edited directly.
///
/// Fuses what today lives in two separate Claude-shaped files:
/// <list type="bullet">
/// <item>SKILL.md frontmatter — <c>id</c>, <c>description</c></item>
/// <item><c>orchestrator.json</c> sidecar — <c>displayName</c>, <c>category</c>,
///       <c>requirements</c>, <c>modes</c></item>
/// </list>
/// </summary>
public sealed record DoxieSkillManifest
{
    /// <summary>
    /// Stable identifier. Matches the folder name under <c>.doxie/skills/</c>
    /// and the slash-command surface in Claude (<c>/&lt;id&gt;</c>). Lowercase
    /// kebab-case by convention.
    /// </summary>
    public string Id { get; init; } = "";

    /// <summary>
    /// Single-line summary. Surfaces verbatim in the Claude shim's frontmatter
    /// <c>description:</c> field (Claude reads this to decide skill relevance)
    /// and in the Codex AGENTS.md catalog.
    /// </summary>
    public string Description { get; init; } = "";

    /// <summary>
    /// Display label for the orchestrator UI. Falls back to a title-cased
    /// version of <see cref="Id"/> when null.
    /// </summary>
    public string? DisplayName { get; init; }

    /// <summary>
    /// Optional Material icon key used by the DoxieOS UI. The value is a
    /// small allowlisted name such as <c>"Code"</c> or
    /// <c>"Psychology"</c>; provider shims ignore it.
    /// </summary>
    public string? Icon { get; init; }

    /// <summary>
    /// Bucket the orchestrator UI groups the skill under. Free-form
    /// string — values matching a built-in <see cref="AgentCategory"/>
    /// case-insensitively are normalised to PascalCase
    /// (<c>"builder"</c> â†’ <c>"Builder"</c>); anything else is kept
    /// verbatim as a custom label (e.g. <c>"Marketing"</c>,
    /// <c>"DataPipeline"</c>). <c>null</c> means "Other". Stored as
    /// string instead of enum so user-defined categories round-trip
    /// through the canonical .doxie/ manifest without information loss.
    /// </summary>
    public string? Category { get; init; }

    /// <summary>
    /// External prerequisites (MCP servers, env vars, CLIs) the orchestrator
    /// surfaces on the agent detail page. <c>null</c> = unspecified;
    /// explicit empty list = "self-contained, deliberately nothing".
    /// </summary>
    public List<DoxieRequirement>? Requirements { get; init; }

    /// <summary>
    /// Subcommand modes keyed by id. Each entry maps to a
    /// <c>&lt;subcommand&gt;.md</c> sibling file (or is purely synthetic when
    /// <c>argumentsTemplate</c> is set without a backing file).
    /// </summary>
    public Dictionary<string, DoxieMode>? Modes { get; init; }
}

public sealed record DoxieRequirement
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public AgentRequirementKind Kind { get; init; }

    public string Name { get; init; } = "";

    public bool Required { get; init; } = true;

    public string? Purpose { get; init; }

    /// <summary>
    /// For env-var requirements: dotenv files to fall back to when the
    /// process environment doesn't already define <see cref="Name"/>.
    /// </summary>
    public List<string>? SearchPaths { get; init; }
}

public sealed record DoxieMode
{
    public List<DoxieModeField>? Fields { get; init; }

    public string? ArgumentsTemplate { get; init; }

    public bool KeepSessionAlive { get; init; }

    public bool Hidden { get; init; }

    public string? Description { get; init; }

    public bool RequiresWorkspace { get; init; }
}

public sealed record DoxieModeField
{
    public string Id { get; init; } = "";
    public string? Label { get; init; }
    public string? Placeholder { get; init; }
    public bool Required { get; init; }
}
