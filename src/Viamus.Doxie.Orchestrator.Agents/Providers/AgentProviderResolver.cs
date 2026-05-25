namespace Viamus.Doxie.Orchestrator.Agents;

/// <summary>
/// Default <see cref="IAgentProviderResolver"/>. Reads
/// <c>default_agent_provider</c> from <see cref="IKeyValueSettingsStore"/>
/// on every <see cref="Resolve"/> call; falls back to the first
/// registered provider when nothing is persisted (or the persisted Id
/// no longer exists, e.g. a provider was removed in a later release).
/// </summary>
public sealed class AgentProviderResolver : IAgentProviderResolver
{
    private const string SettingKey = "default_agent_provider";

    private readonly IReadOnlyList<IAgentProvider> _providers;
    private readonly IKeyValueSettingsStore _settings;

    public AgentProviderResolver(IEnumerable<IAgentProvider> providers, IKeyValueSettingsStore settings)
    {
        _providers = providers.ToList();
        if (_providers.Count == 0)
        {
            throw new InvalidOperationException(
                "No IAgentProvider registered — at least one (e.g. ClaudeCodeProvider) must be added to DI.");
        }
        _settings = settings;
    }

    public IReadOnlyList<IAgentProvider> AllProviders => _providers;

    public string ActiveProviderId => Resolve().Id;

    public IAgentProvider Resolve()
    {
        var preferred = _settings.Get(SettingKey);
        if (!string.IsNullOrWhiteSpace(preferred))
        {
            var match = _providers.FirstOrDefault(p =>
                string.Equals(p.Id, preferred, StringComparison.OrdinalIgnoreCase));
            if (match is not null) return match;
        }
        return _providers[0];
    }

    public void SetDefault(string providerId)
    {
        if (!_providers.Any(p => string.Equals(p.Id, providerId, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException(
                $"Unknown provider Id '{providerId}'. Registered: {string.Join(", ", _providers.Select(p => p.Id))}.",
                nameof(providerId));
        }
        _settings.Set(SettingKey, providerId);
    }

    /// <summary>
    /// Builds a no-op resolver that always returns the given single
    /// provider. Useful for tests and any one-shot dispatch where there
    /// is nothing to resolve.
    /// </summary>
    public static IAgentProviderResolver ForSingle(IAgentProvider provider) =>
        new SingleProviderResolver(provider);

    private sealed class SingleProviderResolver : IAgentProviderResolver
    {
        private readonly IAgentProvider _provider;
        public SingleProviderResolver(IAgentProvider provider) => _provider = provider;
        public IAgentProvider Resolve() => _provider;
        public IReadOnlyList<IAgentProvider> AllProviders => new[] { _provider };
        public string ActiveProviderId => _provider.Id;

        public void SetDefault(string providerId)
        {
            if (!string.Equals(providerId, _provider.Id, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    $"SingleProviderResolver only knows '{_provider.Id}'.",
                    nameof(providerId));
            }
            // No-op — the single provider is already the default.
        }
    }
}
