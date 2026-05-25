namespace Viamus.Doxie.Orchestrator.Agents.Doxie;

/// <summary>
/// In-memory representation of one skill on disk under <c>.doxie/skills/&lt;id&gt;/</c>.
/// Wraps the parsed <see cref="DoxieSkillManifest"/> plus the body markdown
/// and any companion files (subcommand <c>*.md</c>, scripts/, references).
///
/// Companion files are carried verbatim through the regen pipeline so
/// emitters can copy them into provider shims without parsing — preserves
/// fidelity for skills like <c>sample-watch</c> that ship internal cross-refs
/// (<c>state-schema.md</c>, <c>sonar.md</c>, etc.).
/// </summary>
public sealed record DoxieSkillCanon(
    DoxieSkillManifest Manifest,
    string BodyMarkdown,
    IReadOnlyDictionary<string, byte[]> CompanionFiles,
    bool IsPrivate = false,
    string CatalogId = DoxieCatalogStore.DefaultCatalogId,
    string CatalogName = "Default catalog",
    string CatalogRoot = "");
