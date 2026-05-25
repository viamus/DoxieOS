namespace Viamus.Doxie.Orchestrator.Agents.Doxie;

/// <summary>
/// One memory entry attached to a skill — a short markdown block
/// authored by hand under <c>.doxie/skills/&lt;id&gt;/memories/*.md</c>
/// that the runner injects into the prompt at dispatch time when its
/// <see cref="Condition"/> evaluates true for the current invocation.
///
/// Memories are the persistence layer for "lessons" — gotchas, tips,
/// or auto-captured feedback that should bias the agent's next run
/// without re-editing the skill body. Provider-agnostic: the runner
/// hands them to <see cref="IAgentProvider.FormatPrompt"/> which
/// inlines them as a clearly-delimited context block.
/// </summary>
public sealed record DoxieSkillMemory(
    /// <summary>
    /// Source filename (without folder) — stable identifier the UI uses
    /// to reference this memory. Drives sort tiebreaker after priority.
    /// </summary>
    string FileName,

    /// <summary>
    /// Display name pulled from the <c>name</c> frontmatter field;
    /// falls back to <see cref="FileName"/> sans extension.
    /// </summary>
    string Name,

    /// <summary>
    /// Sort priority. Higher-priority memories appear first in the
    /// injected context block so the model sees the most-important
    /// guidance ahead of merely useful tips.
    /// </summary>
    DoxieSkillMemoryPriority Priority,

    /// <summary>
    /// Tiny condition DSL expression (or null = always). Evaluated at
    /// dispatch time against the active <c>mode</c>, <c>provider</c>,
    /// and <c>workspace_id</c>. Memories whose condition fails are
    /// silently skipped for that invocation. See
    /// <c>MemoryConditionEvaluator</c> for the supported grammar.
    /// </summary>
    string? Condition,

    /// <summary>
    /// Markdown body (frontmatter stripped). Inlined verbatim into the
    /// prompt under a "## Auto-loaded context" delimiter.
    /// </summary>
    string Body);

/// <summary>
/// Priority levels used to order injected memories. Default value
/// (<see cref="Medium"/>) is what files without an explicit
/// <c>priority</c> frontmatter field land on — tuned so that "regular
/// tip" doesn't need any annotation.
/// </summary>
public enum DoxieSkillMemoryPriority
{
    Low,
    Medium,
    High,
}
