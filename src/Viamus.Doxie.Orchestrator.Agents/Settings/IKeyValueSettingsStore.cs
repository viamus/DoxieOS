namespace Viamus.Doxie.Orchestrator.Agents;

/// <summary>
/// Tiny key-value persistence layer for orchestrator settings that
/// outlive a single run but are too small to deserve their own table.
/// Used today for the active <c>IAgentProvider</c> id; further
/// settings (UI preferences, feature flags, ...) plug in here.
/// </summary>
public interface IKeyValueSettingsStore
{
    /// <summary>Returns the persisted value or <c>null</c> if absent.</summary>
    string? Get(string key);

    /// <summary>Inserts or updates the given key.</summary>
    void Set(string key, string value);
}
