using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Viamus.Doxie.Orchestrator.Agents;
using Viamus.Doxie.Orchestrator.Agents.Doxie;
using Viamus.Doxie.Orchestrator.Components.Shared;

namespace Viamus.Doxie.Orchestrator.Builder;

/// <summary>
/// Promotes a validated <see cref="AgentManifest"/> from a builder
/// sandbox into the canonical agents catalog on disk
/// (<c>&lt;skills-dir&gt;/&lt;id&gt;/SKILL.md</c> +
/// <c>&lt;skills-dir&gt;/&lt;id&gt;/orchestrator.json</c>). After a
/// successful write the agent catalog is refreshed so the new agent
/// shows up without an app restart.
/// </summary>
public sealed class AgentManifestPromoter
{
    private static readonly Regex KebabCase = new("^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.Compiled);
    private static readonly Regex TemplateField = new(@"\{([a-z0-9][a-z0-9_-]*)\}", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly JsonSerializerOptions WriteOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// Lenient parser used by the live-preview pipeline. Tolerates
    /// partial drafts and unknown properties so the right pane renders
    /// progressive refinement instead of red errors mid-conversation.
    /// </summary>
    public static readonly JsonSerializerOptions ParseOptionsLenient = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>
    /// Strict parser used at Save time. Refuses unknown properties so
    /// typos like <c>"categry"</c> surface as a clear deserialization
    /// error instead of silently dropping the field and producing a
    /// vague "category is required" downstream.
    /// </summary>
    public static readonly JsonSerializerOptions ParseOptionsStrict = new()
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private readonly StorageOptions _storage;
    private readonly IAgentCatalog _catalog;
    private readonly DoxieRegenerator _regenerator;
    private readonly IDoxieCatalogStore _catalogStore;

    public AgentManifestPromoter(StorageOptions storage, IAgentCatalog catalog, DoxieRegenerator regenerator, IDoxieCatalogStore catalogStore)
    {
        _storage = storage;
        _catalog = catalog;
        _regenerator = regenerator;
        _catalogStore = catalogStore;
    }

    /// <summary>
    /// Validates a manifest and writes it to disk. Returns
    /// <c>(true, agentId, [])</c> on success or
    /// <c>(false, null, errors)</c> with one or more user-facing error
    /// messages on failure. Never throws on validation problems —
    /// they're returned as ordinary results so the API endpoint stays
    /// simple. The error list is the authoritative source for what
    /// the UI renders; <see cref="PromotionResult.Error"/> is just the
    /// joined first-line summary kept for backwards compatibility.
    /// </summary>
    public PromotionResult Promote(AgentManifest manifest, bool overwrite = false, string? catalogId = null)
    {
        var errors = Validate(manifest);
        if (errors.Count > 0)
        {
            return new PromotionResult(false, null, JoinErrors(errors), errors);
        }

        // Canonical layout: .doxie/skills/<id>/{manifest.json, body.md}.
        // Provider shims (.claude/skills/<id>/SKILL.md + orchestrator.json,
        // .codex/AGENTS.md) are emitted by DoxieRegenerator at the end.
        var catalog = _catalogStore.FindCatalog(catalogId);
        if (catalog is null)
        {
            var message = $"Catalog '{catalogId}' was not found. Check Settings > Doxie catalogs.";
            return new PromotionResult(false, null, message, new[] { message });
        }

        var skillsRoot = catalog.SkillsDirectory;
        Directory.CreateDirectory(skillsRoot);

        var agentDir = Path.Combine(skillsRoot, manifest.Id!);
        var collisions = _catalogStore.ListCatalogs()
            .Select(c => Path.Combine(c.SkillsDirectory, manifest.Id!))
            .Where(Directory.Exists)
            .ToList();
        if (collisions.Count > 0 && !overwrite)
        {
            var collisionPath = collisions[0];
            var collision = $"An agent named '{manifest.Id}' already exists at {collisionPath}. Choose a different id or pass overwrite=true.";
            return new PromotionResult(false, null, collision, new[] { collision });
        }
        Directory.CreateDirectory(agentDir);

        // manifest.json — the canonical metadata Doxie reads from.
        var doxieManifest = BuildDoxieManifest(manifest);
        File.WriteAllText(
            Path.Combine(agentDir, "manifest.json"),
            JsonSerializer.Serialize(doxieManifest, WriteOpts) + "\n",
            Encoding.UTF8);

        // body.md — natural-language instructions (no frontmatter; the
        // shim emitter reconstructs SKILL.md frontmatter from manifest.json).
        File.WriteAllText(
            Path.Combine(agentDir, "body.md"),
            BuildBodyMarkdown(manifest),
            Encoding.UTF8);

        foreach (var oldAgentDir in collisions)
        {
            if (!string.Equals(Path.GetFullPath(oldAgentDir), Path.GetFullPath(agentDir), StringComparison.OrdinalIgnoreCase)
                && Directory.Exists(oldAgentDir))
            {
                Directory.Delete(oldAgentDir, recursive: true);
            }
        }

        // Regen the .claude/ + .codex/ shims so Claude Code and Codex see
        // the new agent immediately. Catalog refresh follows so the UI
        // reflects the new agent without a navigation round-trip.
        _regenerator.Regenerate();
        _catalog.Refresh();

        return new PromotionResult(true, manifest.Id, null, Array.Empty<string>());
    }

    /// <summary>
    /// Runs the full validation suite over a manifest. Public so the
    /// live-preview API endpoint can surface the same checks the Save
    /// endpoint will run, letting the user fix problems as they appear
    /// instead of only at Save time. Returns an empty list on success.
    /// </summary>
    public static IReadOnlyList<string> Validate(AgentManifest? m)
    {
        var errors = new List<string>();

        if (m is null)
        {
            errors.Add("manifest is empty");
            return errors;
        }

        // Identity
        if (string.IsNullOrWhiteSpace(m.Id)) errors.Add("id is required");
        else if (!KebabCase.IsMatch(m.Id!)) errors.Add($"id '{m.Id}' must be kebab-case (^[a-z0-9]+(-[a-z0-9]+)*$)");

        if (string.IsNullOrWhiteSpace(m.Name)) errors.Add("name is required");
        if (string.IsNullOrWhiteSpace(m.Description)) errors.Add("description is required");
        else if (m.Description!.Length > 280) errors.Add($"description must be <= 280 chars (currently {m.Description.Length})");

        if (string.IsNullOrWhiteSpace(m.Category)) errors.Add("category is required");
        else if (!AgentCategoryPalette.TryParse(m.Category, out _, out _))
            errors.Add(
                $"category '{m.Category}' is invalid — pick a built-in (Builder, Inspector, " +
                $"Fixer, Connector, Other) or a custom label (1-32 chars: letters, digits, " +
                $"spaces or hyphens; must start with a letter).");

        // Modes — at least one, all valid.
        if (!string.IsNullOrWhiteSpace(m.Icon) && !AgentIconPalette.Contains(m.Icon))
        {
            errors.Add($"icon '{m.Icon}' is invalid Ã¢â‚¬â€ pick a supported Material icon key from the Doxie icon picker.");
        }

        if (m.Modes is null || m.Modes.Count == 0)
        {
            errors.Add("at least one mode is required");
        }
        else
        {
            var seenModeIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < m.Modes.Count; i++)
            {
                var mode = m.Modes[i];
                var label = string.IsNullOrWhiteSpace(mode.Id) ? $"modes[{i}]" : $"mode '{mode.Id}'";

                if (string.IsNullOrWhiteSpace(mode.Id))
                    errors.Add($"{label}: id is required");
                else if (!KebabCase.IsMatch(mode.Id!))
                    errors.Add($"{label}: id must be kebab-case");
                else if (!seenModeIds.Add(mode.Id!))
                    errors.Add($"{label}: duplicate mode id");

                if (string.IsNullOrWhiteSpace(mode.Description))
                    errors.Add($"{label}: description is required");

                // Per-field checks
                if (mode.Fields is { Count: > 0 })
                {
                    var seenFieldIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    for (var j = 0; j < mode.Fields.Count; j++)
                    {
                        var f = mode.Fields[j];
                        var fieldLabel = string.IsNullOrWhiteSpace(f.Id) ? $"{label}.fields[{j}]" : $"{label}.fields['{f.Id}']";
                        if (string.IsNullOrWhiteSpace(f.Id))
                            errors.Add($"{fieldLabel}: id is required");
                        else if (!KebabCase.IsMatch(f.Id!))
                            errors.Add($"{fieldLabel}: id must be kebab-case");
                        else if (!seenFieldIds.Add(f.Id!))
                            errors.Add($"{fieldLabel}: duplicate field id");

                        if (string.IsNullOrWhiteSpace(f.Label))
                            errors.Add($"{fieldLabel}: label is required");
                    }

                    // argumentsTemplate is required when fields are non-empty,
                    // and every {token} it references must exist among them.
                    if (string.IsNullOrWhiteSpace(mode.ArgumentsTemplate))
                    {
                        errors.Add($"{label}: argumentsTemplate is required when fields are present");
                    }
                    else
                    {
                        foreach (Match match in TemplateField.Matches(mode.ArgumentsTemplate!))
                        {
                            var token = match.Groups[1].Value;
                            if (!seenFieldIds.Contains(token))
                                errors.Add($"{label}: argumentsTemplate references unknown field '{{{token}}}'");
                        }
                    }
                }
                else if (!string.IsNullOrWhiteSpace(mode.ArgumentsTemplate)
                         && TemplateField.IsMatch(mode.ArgumentsTemplate!))
                {
                    errors.Add($"{label}: argumentsTemplate references field tokens but the mode has no fields");
                }
            }
        }

        // Tools — empty list is allowed (some agents are pure-thought),
        // but every entry must be a non-empty string.
        if (m.Tools is { Count: > 0 })
        {
            for (var i = 0; i < m.Tools.Count; i++)
            {
                if (string.IsNullOrWhiteSpace(m.Tools[i]))
                    errors.Add($"tools[{i}]: empty tool name");
            }
        }

        // Requirements — kind must parse, name must be non-empty,
        // searchPaths is Env-only.
        if (m.Requirements is { Count: > 0 })
        {
            for (var i = 0; i < m.Requirements.Count; i++)
            {
                var r = m.Requirements[i];
                var label = string.IsNullOrWhiteSpace(r.Name) ? $"requirements[{i}]" : $"requirement '{r.Name}'";

                if (string.IsNullOrWhiteSpace(r.Name))
                    errors.Add($"{label}: name is required");

                AgentRequirementKind? parsedKind = null;
                if (string.IsNullOrWhiteSpace(r.Kind))
                {
                    errors.Add($"{label}: kind is required");
                }
                else if (Enum.TryParse<AgentRequirementKind>(r.Kind, ignoreCase: true, out var k))
                {
                    parsedKind = k;
                }
                else
                {
                    errors.Add($"{label}: kind '{r.Kind}' must be one of: Env, Mcp, Tool");
                }

                if (string.IsNullOrWhiteSpace(r.Purpose))
                    errors.Add($"{label}: purpose is required");

                if (r.SearchPaths is { Count: > 0 } && parsedKind is not AgentRequirementKind.Env)
                    errors.Add($"{label}: searchPaths is only valid on Env-kind requirements");
            }
        }

        return errors;
    }

    private static string JoinErrors(IReadOnlyList<string> errors) =>
        errors.Count == 1 ? errors[0] : string.Join("; ", errors);

    private static DoxieSkillManifest BuildDoxieManifest(AgentManifest m)
    {
        // Normalise the raw input: built-in names round-trip as the
        // canonical PascalCase enum name; custom labels are written
        // verbatim (with surrounding whitespace trimmed). Falsy input
        // becomes "Other" — the manifest validator already rejects
        // empty Category before this runs, but defending here keeps
        // the legacy export endpoint (BuildSidecar) safe too.
        var category = NormaliseCategoryString(m.Category);

        Dictionary<string, DoxieMode>? modes = null;
        if (m.Modes is { Count: > 0 })
        {
            modes = new Dictionary<string, DoxieMode>(StringComparer.Ordinal);
            foreach (var mm in m.Modes)
            {
                if (string.IsNullOrWhiteSpace(mm.Id)) continue;
                modes[mm.Id!] = new DoxieMode
                {
                    Description = mm.Description,
                    ArgumentsTemplate = string.IsNullOrWhiteSpace(mm.ArgumentsTemplate) ? null : mm.ArgumentsTemplate,
                    Hidden = mm.Hidden,
                    RequiresWorkspace = mm.RequiresWorkspace,
                    Fields = mm.Fields is { Count: > 0 }
                        ? mm.Fields.Select(f => new DoxieModeField
                        {
                            Id = f.Id ?? string.Empty,
                            Label = f.Label,
                            Placeholder = f.Placeholder,
                            Required = f.Required,
                        }).ToList()
                        : null,
                };
            }
        }

        var requirements = m.Requirements?
            .Where(r => !string.IsNullOrWhiteSpace(r.Name))
            .Select(r => new DoxieRequirement
            {
                Kind = ParseRequirementKind(r.Kind),
                Name = r.Name!,
                Required = r.Required,
                Purpose = r.Purpose,
                SearchPaths = r.SearchPaths is { Count: > 0 } ? r.SearchPaths : null,
            })
            .ToList();

        return new DoxieSkillManifest
        {
            Id = m.Id!,
            Description = (m.Description ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim(),
            DisplayName = m.Name,
            Icon = string.IsNullOrWhiteSpace(m.Icon) ? null : m.Icon.Trim(),
            Category = category,
            Modes = modes,
            Requirements = requirements,
        };
    }

    private static string BuildBodyMarkdown(AgentManifest m)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {m.Name}");
        sb.AppendLine();
        if (!string.IsNullOrWhiteSpace(m.Description))
        {
            sb.AppendLine(m.Description!.Trim());
            sb.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(m.SystemPrompt))
        {
            sb.AppendLine("## System prompt");
            sb.AppendLine();
            sb.AppendLine(m.SystemPrompt!.Trim());
            sb.AppendLine();
        }

        if (m.Modes is { Count: > 0 })
        {
            // One H1 per mode in the FilesystemAgentCatalog discovery
            // contract: "# `/<skill> <subcommand>` — <description>".
            // That's how each subcommand becomes a discoverable mode.
            foreach (var mode in m.Modes)
            {
                sb.AppendLine($"# `/{m.Id} {mode.Id}` — {(mode.Description ?? string.Empty).Trim()}");
                sb.AppendLine();
                if (!string.IsNullOrWhiteSpace(mode.Description))
                {
                    sb.AppendLine(mode.Description!.Trim());
                    sb.AppendLine();
                }
                if (mode.Fields is { Count: > 0 })
                {
                    sb.AppendLine("**Inputs:**");
                    foreach (var f in mode.Fields)
                    {
                        var req = f.Required ? " (required)" : string.Empty;
                        sb.AppendLine($"- `{f.Id}` — {f.Label}{req}");
                    }
                    sb.AppendLine();
                }
                if (!string.IsNullOrWhiteSpace(mode.ArgumentsTemplate))
                {
                    sb.AppendLine($"**Invocation:** `{mode.ArgumentsTemplate}`");
                    sb.AppendLine();
                }
            }
        }

        if (m.Tools is { Count: > 0 })
        {
            sb.AppendLine("## Tools");
            sb.AppendLine();
            foreach (var t in m.Tools)
            {
                sb.AppendLine($"- `{t}`");
            }
            sb.AppendLine();
        }

        if (m.Requirements is { Count: > 0 })
        {
            sb.AppendLine("## Requirements");
            sb.AppendLine();
            foreach (var r in m.Requirements)
            {
                sb.AppendLine($"- **{r.Name}** ({r.Kind}{(r.Required ? ", required" : "")}): {r.Purpose}");
                if (r.SearchPaths is { Count: > 0 })
                {
                    sb.AppendLine($"  - searchPaths: {string.Join(", ", r.SearchPaths)}");
                }
            }
        }

        return sb.ToString();
    }

    [Obsolete("Kept for the legacy export endpoint that still produces a SKILL.md+orchestrator.json zip for backwards-compat consumers. New writes go through BuildDoxieManifest + BuildBodyMarkdown.")]
    private static SidecarShape BuildSidecar(AgentManifest m)
    {
        var category = NormaliseCategoryString(m.Category);

        var modes = m.Modes?.ToDictionary(
            mm => mm.Id!,
            mm => new SidecarModeShape
            {
                Description = mm.Description,
                RequiresWorkspace = mm.RequiresWorkspace,
                Hidden = mm.Hidden ? true : null,
                Fields = mm.Fields is { Count: > 0 }
                    ? mm.Fields.Select(f => new SidecarFieldShape
                    {
                        Id = f.Id,
                        Label = f.Label,
                        Placeholder = f.Placeholder,
                        Required = f.Required,
                    }).ToList()
                    : null,
                ArgumentsTemplate = string.IsNullOrWhiteSpace(mm.ArgumentsTemplate) ? null : mm.ArgumentsTemplate,
            });

        var requirements = m.Requirements?
            .Where(r => !string.IsNullOrWhiteSpace(r.Name))
            .Select(r => new SidecarRequirementShape
            {
                Kind = ParseRequirementKind(r.Kind),
                Name = r.Name,
                Required = r.Required,
                Purpose = r.Purpose,
                SearchPaths = r.SearchPaths is { Count: > 0 } ? r.SearchPaths : null,
            })
            .ToList();

        return new SidecarShape
        {
            DisplayName = m.Name,
            Icon = string.IsNullOrWhiteSpace(m.Icon) ? null : m.Icon.Trim(),
            Category = category,
            Modes = modes,
            Requirements = requirements,
        };
    }

    private static AgentRequirementKind ParseRequirementKind(string? raw) =>
        Enum.TryParse<AgentRequirementKind>(raw, ignoreCase: true, out var k) ? k : AgentRequirementKind.Tool;

    /// <summary>
    /// Normalises a raw category string for persistence in
    /// <c>manifest.json</c> / orchestrator sidecar. Built-in names
    /// (matching <see cref="AgentCategory"/> case-insensitively) become
    /// canonical PascalCase (<c>"builder"</c> â†’ <c>"Builder"</c>).
    /// Unknown but well-formed strings (per
    /// <see cref="AgentCategoryPalette.CustomLabelPattern"/>) are kept
    /// verbatim, just trimmed. Anything else collapses to
    /// <c>"Other"</c>. The manifest validator rejects malformed
    /// categories upstream of this method, so the fallback only
    /// fires when this is called from non-validated paths.
    /// </summary>
    private static string NormaliseCategoryString(string? raw)
    {
        if (!AgentCategoryPalette.TryParse(raw, out var category, out var customLabel))
        {
            return AgentCategory.Other.ToString();
        }
        return customLabel ?? category.ToString();
    }

    private static string EscapeYamlScalar(string value)
    {
        // Quote if the value contains anything YAML treats as special.
        // The agent description is plain prose so this almost always
        // ends up wrapped in double quotes.
        if (value.Length == 0) return "\"\"";
        if (value.IndexOfAny(new[] { ':', '#', '"', '\'', '\\' }) >= 0
            || value.StartsWith(' ') || value.EndsWith(' '))
        {
            return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }
        return value;
    }

    /// <summary>
    /// Shape that mirrors <c>FilesystemAgentCatalog.OrchestratorSidecar</c>
    /// for serialisation. Kept local so the Builder doesn't take a
    /// reference to a private nested class on the catalog.
    /// </summary>
    private sealed class SidecarShape
    {
        public string? DisplayName { get; set; }
        public string? Icon { get; set; }

        /// <summary>
        /// Category as a free-form string. Built-ins are written as
        /// PascalCase enum names (<c>"Builder"</c>), customs stay as
        /// the user-typed label (<c>"Marketing"</c>). Stored as string
        /// so unknown labels round-trip through legacy exports.
        /// </summary>
        public string Category { get; set; } = AgentCategory.Other.ToString();
        public Dictionary<string, SidecarModeShape>? Modes { get; set; }
        public List<SidecarRequirementShape>? Requirements { get; set; }
    }

    private sealed class SidecarModeShape
    {
        public string? Description { get; set; }
        public bool RequiresWorkspace { get; set; }
        public bool? Hidden { get; set; }
        public List<SidecarFieldShape>? Fields { get; set; }
        public string? ArgumentsTemplate { get; set; }
    }

    private sealed class SidecarFieldShape
    {
        public string? Id { get; set; }
        public string? Label { get; set; }
        public string? Placeholder { get; set; }
        public bool Required { get; set; }
    }

    private sealed class SidecarRequirementShape
    {
        public AgentRequirementKind Kind { get; set; }
        public string? Name { get; set; }
        public bool Required { get; set; } = true;
        public string? Purpose { get; set; }
        public List<string>? SearchPaths { get; set; }
    }
}

public sealed record PromotionResult(bool Ok, string? AgentId, string? Error, IReadOnlyList<string> Errors);
