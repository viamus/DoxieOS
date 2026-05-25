using Viamus.Doxie.Orchestrator.Common;

namespace Viamus.Doxie.Orchestrator.Agents.Doxie;

/// <summary>
/// Reads the entire canonical registry (skills + agents + libraries) from a
/// single <c>.doxie/</c> root in one pass. Composes the three per-type readers
/// so the regenerator only needs one collaborator.
/// </summary>
public sealed class DoxieRegistryReader
{
    private readonly DoxieSkillReader _skillReader;
    private readonly DoxieAgentReader _agentReader;
    private readonly DoxieLibraryReader _libraryReader;
    private readonly DoxieWorkflowReader _workflowReader;

    public DoxieRegistryReader(string doxieRoot, Func<IReadOnlyList<DoxieCatalogRoot>>? catalogRootsProvider = null)
    {
        _skillReader = new DoxieSkillReader(doxieRoot, catalogRootsProvider);
        _agentReader = new DoxieAgentReader(doxieRoot, catalogRootsProvider);
        _libraryReader = new DoxieLibraryReader(doxieRoot, catalogRootsProvider);
        _workflowReader = new DoxieWorkflowReader(doxieRoot, catalogRootsProvider);
    }

    public DoxieRegistry LoadAll() =>
        new(
            Skills: _skillReader.LoadAll(),
            Agents: _agentReader.LoadAll(),
            Libraries: _libraryReader.LoadAll(),
            Workflows: _workflowReader.LoadAll());
}
