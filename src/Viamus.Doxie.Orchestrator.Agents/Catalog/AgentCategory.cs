namespace Viamus.Doxie.Orchestrator.Agents;

public enum AgentCategory
{
    /// <summary>
    /// DoxieOS first-party authoring kit — agents that exist to author
    /// MORE agents / workflows / libraries / sandboxes (not to do user-
    /// facing work themselves). The four members are <c>agent-builder</c>,
    /// <c>workflow-builder</c>, <c>library</c>, and <c>sandbox</c>. Sealed:
    /// users cannot move them out via the per-agent category editor —
    /// they're part of the framework, not the user's catalog. Surfaced
    /// at the top of the Agents page so newcomers find the entry points
    /// to authoring quickly. Spelt to match the brand name (<c>DoxieOS</c>
    /// / <c>.doxie/</c>) — never <c>Doxy</c>.
    /// </summary>
    Doxie,

    /// <summary>
    /// Drives forward dev workflow on a real codebase / backlog —
    /// implements work items, refines stories, scaffolds features.
    /// Output is concrete movement on a feature, not a fix to broken
    /// state. Examples: <c>implement-work-item</c> (turns a work
    /// item into a change), <c>story-refine</c> (annotates a story
    /// with refined business + technical scope).
    /// </summary>
    Developer,

    /// <summary>
    /// Makes code / merges / commits — operational mutators on a target
    /// repo. Reserved for future agents like <c>change-fix</c> if/when they
    /// surface in the catalog.
    /// </summary>
    Fixer,

    /// <summary>
    /// Produces a reusable artifact (memory pack, library, generated
    /// code). Output is something the user keeps and re-uses.
    /// Examples: <c>library</c>, <c>deep-research</c>.
    /// </summary>
    Builder,

    /// <summary>
    /// Analyzes existing state and produces insight — reports,
    /// transition detection, triage decisions. May trigger downstream
    /// agents based on what it observes (e.g. <c>sample-watch</c>
    /// dispatching <c>change-fix</c> on a build failure). Subsumed the
    /// older <c>Watcher</c> category — passive observation alone
    /// wasn't a useful distinction; what matters is that the output
    /// is an analytic verdict rather than a built artifact.
    /// </summary>
    Inspector,

    /// <summary>
    /// Workflow-internal helpers — agents that exist to glue workflow
    /// steps together rather than to perform user-facing work. Examples:
    /// <c>aggregate</c> (joins parallel branches), <c>write-to-workspace</c>
    /// (terminal delivery node). Filtered out by default on the Agents
    /// page so they don't clutter the catalog; always available inside
    /// the workflow builder.
    /// </summary>
    Connector,

    Other,
}

/// <summary>
/// Helpers for handling category strings that may name a built-in
/// <see cref="AgentCategory"/> or a user-invented custom category.
/// Centralises the parse-or-customise logic so the manifest promoter,
/// the category-edit endpoint, and any future ingestion path agree on
/// what a "valid" category string looks like.
/// </summary>
public static class AgentCategoryPalette
{
    /// <summary>
    /// Pattern a custom category label must match: 1â€“32 characters,
    /// alphanumeric plus hyphens / spaces, must start with a letter.
    /// Stricter than necessary on purpose — keeps display rendering
    /// predictable and prevents users from accidentally writing JSON
    /// fragments or paths into the field.
    /// </summary>
    public static readonly System.Text.RegularExpressions.Regex CustomLabelPattern =
        new(@"^[A-Za-z][A-Za-z0-9 \-]{0,31}$", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Parses a raw category string into the (built-in enum, custom
    /// label) pair the descriptor expects. Names matching a built-in
    /// case-insensitively normalise to the canonical PascalCase enum
    /// value with no custom label. Anything else becomes
    /// <see cref="AgentCategory.Other"/> + the trimmed string as the
    /// custom label.
    /// </summary>
    /// <returns>True if the input was a valid category (built-in or a
    /// well-formed custom label); false if the string was empty or did
    /// not match <see cref="CustomLabelPattern"/>.</returns>
    public static bool TryParse(string? raw, out AgentCategory category, out string? customLabel)
    {
        category = AgentCategory.Other;
        customLabel = null;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        var trimmed = raw.Trim();
        if (Enum.TryParse<AgentCategory>(trimmed, ignoreCase: true, out var parsed))
        {
            category = parsed;
            return true;
        }
        if (!CustomLabelPattern.IsMatch(trimmed)) return false;
        customLabel = trimmed;
        return true;
    }
}
