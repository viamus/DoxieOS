namespace Viamus.Doxie.Orchestrator.Agents;

/// <summary>
/// Picks the <see cref="IAgentProvider"/> that the runner should use
/// for the next agent dispatch. Reads the persisted "default provider"
/// setting on every <see cref="Resolve"/> so a Settings-page change
/// takes effect for subsequent runs without an orchestrator restart.
/// </summary>
public interface IAgentProviderResolver
{
    /// <summary>Active provider for the next dispatch.</summary>
    IAgentProvider Resolve();

    /// <summary>All providers registered in DI, in registration order.</summary>
    IReadOnlyList<IAgentProvider> AllProviders { get; }

    /// <summary>Id of the active default — i.e. what <see cref="Resolve"/>
    /// returns. Useful for the Settings UI without re-resolving.</summary>
    string ActiveProviderId { get; }

    /// <summary>Persists a new default by provider Id. The Id must
    /// match one of <see cref="AllProviders"/>; throws otherwise.</summary>
    void SetDefault(string providerId);
}
