namespace Viamus.Doxie.Orchestrator.Agents.Doxie;

/// <summary>
/// Combined snapshot of everything authored under <c>.doxie/</c>. Passed to
/// every <see cref="IShimEmitter"/> on each regen pass — emitters pick the
/// slices they care about (Claude skill emitter only reads <see cref="Skills"/>,
/// Codex emitter reads <see cref="Skills"/> + <see cref="Agents"/>, etc.).
/// </summary>
public sealed record DoxieRegistry(
    IReadOnlyList<DoxieSkillCanon> Skills,
    IReadOnlyList<DoxieAgentCanon> Agents,
    IReadOnlyList<DoxieLibraryCanon> Libraries,
    IReadOnlyList<DoxieWorkflowCanon> Workflows)
{
    public static DoxieRegistry Empty { get; } = new(
        Array.Empty<DoxieSkillCanon>(),
        Array.Empty<DoxieAgentCanon>(),
        Array.Empty<DoxieLibraryCanon>(),
        Array.Empty<DoxieWorkflowCanon>());
}
