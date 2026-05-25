namespace Viamus.Doxie.Orchestrator.Agents.Doxie;

/// <summary>
/// Emits one slice of provider-specific shim from the canonical Doxie registry.
/// Each provider may register multiple emitters (Claude has separate ones for
/// skills, agents, and libraries; Codex collapses skills+agents into a single
/// AGENTS.md emitter). Emitters MUST be idempotent — running twice over an
/// unchanged registry produces a byte-identical output tree, never touching
/// files outside their declared <see cref="ShimRoot"/> subtree.
/// </summary>
public interface IShimEmitter
{
    /// <summary>
    /// Output root the emitter writes into, relative to the workspace root
    /// (e.g. <c>".claude/skills"</c>, <c>".claude/agents"</c>, <c>".codex"</c>).
    /// Used for logging and to scope orphan-cleanup to a single subtree.
    /// </summary>
    string ShimRoot { get; }

    void Emit(DoxieRegistry registry, string workspaceRoot);
}
