namespace Viamus.Doxie.Orchestrator.Agents.Doxie;

/// <summary>
/// Orchestrates a regen pass: reads the canonical registry from <c>.doxie/</c>
/// (skills, agents, libraries) and dispatches the result through every
/// registered <see cref="IShimEmitter"/>. Single-flight via lock — concurrent
/// calls coalesce to one pass.
/// </summary>
public sealed class DoxieRegenerator
{
    private readonly string _workspaceRoot;
    private readonly DoxieRegistryReader _reader;
    private readonly IReadOnlyList<IShimEmitter> _emitters;
    private readonly object _gate = new();

    public DoxieRegenerator(string workspaceRoot, IReadOnlyList<IShimEmitter> emitters)
        : this(workspaceRoot, emitters, new DoxieRegistryReader(Path.Combine(workspaceRoot, ".doxie"))) { }

    public DoxieRegenerator(string workspaceRoot, IReadOnlyList<IShimEmitter> emitters, DoxieRegistryReader reader)
    {
        _workspaceRoot = workspaceRoot ?? throw new ArgumentNullException(nameof(workspaceRoot));
        _emitters = emitters ?? throw new ArgumentNullException(nameof(emitters));
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
    }

    public RegenResult Regenerate()
    {
        lock (_gate)
        {
            var registry = _reader.LoadAll();
            foreach (var emitter in _emitters)
            {
                emitter.Emit(registry, _workspaceRoot);
            }
            return new RegenResult(
                SkillCount: registry.Skills.Count,
                AgentCount: registry.Agents.Count,
                LibraryCount: registry.Libraries.Count,
                WorkflowCount: registry.Workflows.Count,
                EmitterCount: _emitters.Count);
        }
    }
}

public readonly record struct RegenResult(int SkillCount, int AgentCount, int LibraryCount, int WorkflowCount, int EmitterCount);
