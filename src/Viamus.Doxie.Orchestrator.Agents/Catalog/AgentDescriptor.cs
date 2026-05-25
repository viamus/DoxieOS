namespace Viamus.Doxie.Orchestrator.Agents;

public sealed record AgentDescriptor(
    string Id,
    string Name,
    string Description,
    string SkillName,
    AgentCategory Category,
    IReadOnlyList<AgentMode>? Modes = null,
    IReadOnlyList<AgentRequirement>? Requirements = null,
    /// <summary>
    /// User-defined category label that overrides <see cref="Category"/>
    /// for display, grouping and filtering when set. Populated when the
    /// agent's manifest carries a <c>category</c> string that doesn't
    /// match any well-known <see cref="AgentCategory"/> name (e.g.
    /// <c>"Marketing"</c>, <c>"DataPipeline"</c>). Built-ins (matching
    /// the enum case-insensitively on Save) leave this null and rely on
    /// the typed <see cref="Category"/>.
    /// </summary>
    string? CustomCategory = null,
    /// <summary>
    /// Optional Material icon key chosen by the user. Null means the UI
    /// should fall back to the category default.
    /// </summary>
    string? Icon = null,
    bool IsPrivate = false,
    string CatalogId = "default",
    string CatalogName = "Default catalog",
    string CatalogRoot = "")
{
    /// <summary>
    /// The label shown to humans — the custom category if the user named
    /// one, otherwise the well-known <see cref="Category"/> enum name.
    /// Use this everywhere in the UI for grouping / filtering / display
    /// instead of switching on <see cref="Category"/> directly so custom
    /// categories surface as first-class buckets.
    /// </summary>
    public string DisplayCategory =>
        string.IsNullOrWhiteSpace(CustomCategory) ? Category.ToString() : CustomCategory!;

    /// <summary>
    /// True when the user cannot move this agent into a different
    /// category via the UI. Sealed today for two groups:
    /// <list type="bullet">
    /// <item><b>Doxie</b> — DoxieOS first-party authoring kit; part of
    ///       the framework, not a user catalog entry.</item>
    /// <item><b>Connector</b> — workflow-internal helpers; their
    ///       categorisation is a wiring decision the engine makes,
    ///       not a presentation preference.</item>
    /// </list>
    /// Custom categories are never sealed — the user defined them, the
    /// user can rename them. Everything else is user-editable so people
    /// can re-organise their own catalog (move <c>deep-research</c>
    /// from Builder to Inspector, etc.).
    /// </summary>
    public bool CategorySealed =>
        string.IsNullOrWhiteSpace(CustomCategory) &&
        Category is AgentCategory.Doxie or AgentCategory.Connector;
}
