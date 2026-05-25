using FluentAssertions;

namespace Viamus.Doxie.Orchestrator.Agents.Tests;

public sealed class AgentProviderResolverTests
{
    private readonly InMemorySettingsStore _settings = new();
    private readonly IAgentProvider _claude = new ClaudeCodeProvider(new ClaudeRunnerOptions());
    private readonly IAgentProvider _codex = new CodexProvider(new CodexProviderOptions());

    private AgentProviderResolver NewResolver() =>
        new(new[] { _claude, _codex }, _settings);

    [Fact]
    public void Construction_throws_when_no_providers_registered()
    {
        var act = () => new AgentProviderResolver(Array.Empty<IAgentProvider>(), _settings);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*at least one*");
    }

    [Fact]
    public void Resolve_falls_back_to_first_provider_when_no_setting_persisted()
    {
        NewResolver().Resolve().Should().BeSameAs(_claude);
    }

    [Fact]
    public void Resolve_returns_persisted_default_when_setting_matches_a_registered_id()
    {
        _settings.Set("default_agent_provider", "codex");

        NewResolver().Resolve().Should().BeSameAs(_codex);
    }

    [Fact]
    public void Resolve_falls_back_to_first_when_persisted_id_is_unknown()
    {
        _settings.Set("default_agent_provider", "ghost-provider");

        NewResolver().Resolve().Should().BeSameAs(_claude);
    }

    [Fact]
    public void Resolve_is_case_insensitive_against_persisted_id()
    {
        _settings.Set("default_agent_provider", "CODEX");

        NewResolver().Resolve().Should().BeSameAs(_codex);
    }

    [Fact]
    public void AllProviders_lists_every_registered_provider_in_registration_order()
    {
        NewResolver().AllProviders.Should().Equal(new[] { _claude, _codex });
    }

    [Fact]
    public void ActiveProviderId_reflects_current_default()
    {
        var resolver = NewResolver();
        resolver.ActiveProviderId.Should().Be("claude-code");

        resolver.SetDefault("codex");

        resolver.ActiveProviderId.Should().Be("codex");
    }

    [Fact]
    public void SetDefault_persists_the_setting()
    {
        NewResolver().SetDefault("codex");

        _settings.Get("default_agent_provider").Should().Be("codex");
    }

    [Fact]
    public void SetDefault_throws_when_provider_id_is_unknown()
    {
        var act = () => NewResolver().SetDefault("ghost-provider");

        act.Should().Throw<ArgumentException>()
            .WithMessage("*Unknown provider Id*");
    }

    [Fact]
    public void ForSingle_resolver_always_returns_the_same_provider()
    {
        var resolver = AgentProviderResolver.ForSingle(_claude);

        resolver.Resolve().Should().BeSameAs(_claude);
        resolver.AllProviders.Should().Equal(new[] { _claude });
        resolver.ActiveProviderId.Should().Be("claude-code");
    }

    [Fact]
    public void ForSingle_resolver_SetDefault_to_other_provider_throws()
    {
        var resolver = AgentProviderResolver.ForSingle(_claude);

        var act = () => resolver.SetDefault("codex");

        act.Should().Throw<ArgumentException>();
    }

    private sealed class InMemorySettingsStore : IKeyValueSettingsStore
    {
        private readonly Dictionary<string, string> _store = new();
        public string? Get(string key) => _store.TryGetValue(key, out var v) ? v : null;
        public void Set(string key, string value) => _store[key] = value;
    }
}
