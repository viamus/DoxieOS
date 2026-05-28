namespace Viamus.Doxie.Orchestrator.Agents.Doxie;

/// <summary>
/// Minimal projection of a workflow definition into the canonical registry.
/// We only carry the fields the shim emitters actually render — enough for
/// Codex to know what workflows exist and how they're triggered, without
/// pulling in <c>Viamus.Doxie.Orchestrator.Workflows</c> as a dependency
/// (which would invert the layering: Agents is a lower layer than Workflows).
///
/// The canonical source is still <c>.doxie/workflows/&lt;id&gt;/workflow.json</c>
/// — this record is just the slice the regen pipeline needs.
/// </summary>
public sealed record DoxieWorkflowCanon(
    string Id,
    string Name,
    string Description,
    string TriggerKind,
    string? CronExpression,
    bool Enabled,
    bool IsPrivate = false,
    string CatalogId = DoxieCatalogStore.DefaultCatalogId,
    string CatalogName = "Default catalog",
    string CatalogRoot = "",
    string Category = "Other")
{
    public string DisplayCategory =>
        string.IsNullOrWhiteSpace(Category) ? "Other" : Category.Trim();
}
