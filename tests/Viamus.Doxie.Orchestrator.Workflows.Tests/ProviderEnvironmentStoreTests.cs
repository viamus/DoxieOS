using FluentAssertions;
using Viamus.Doxie.Orchestrator.Agents;
using Viamus.Doxie.Orchestrator.Services;

namespace Viamus.Doxie.Orchestrator.Workflows.Tests;

public sealed class ProviderEnvironmentStoreTests
{
    [Fact]
    public void SaveMcpServersMergesDuplicateIds()
    {
        var settings = new MemorySettingsStore();
        using var sandbox = TestProviderSandbox();
        var store = sandbox.CreateStore(settings);

        var saved = store.SaveMcpServers(new[]
        {
            new McpServerDefinition("example", "Example", "http", null, null, "http://mcp-example:3000", null, new[] { "codex" }, true),
            new McpServerDefinition("example", "Example Tracker", "http", null, null, "http://mcp-example:3000", null, new[] { "claude-code" }, true),
        });

        saved.Should().ContainSingle();
        saved[0].Providers.Should().BeEquivalentTo("codex", "claude-code");
    }

    [Fact]
    public void SaveMcpServersMergesDuplicateProviderEndpoints()
    {
        var settings = new MemorySettingsStore();
        using var sandbox = TestProviderSandbox();
        var store = sandbox.CreateStore(settings);

        var saved = store.SaveMcpServers(new[]
        {
            new McpServerDefinition("mcp-example", "Example Tracker", "stdio", "npx", "-y @example/mcp", null, null, new[] { "codex" }, true),
            new McpServerDefinition("example-tracker", "Example", "stdio", "npx", "-y @example/mcp", null, null, new[] { "claude-code" }, true),
        });

        saved.Should().ContainSingle();
        saved[0].Providers.Should().BeEquivalentTo("codex", "claude-code");
    }

    [Fact]
    public void SaveMcpServersPreservesHeadersWhenMergingDuplicates()
    {
        var settings = new MemorySettingsStore();
        using var sandbox = TestProviderSandbox();
        var store = sandbox.CreateStore(settings);

        var saved = store.SaveMcpServers(new[]
        {
            new McpServerDefinition("example", "Example", "http", null, null, "http://mcp-example:3000", null, new[] { "codex" }, true, """{ "headers": { "Authorization": "Bearer x" } }"""),
            new McpServerDefinition("example", "Example", "http", null, null, "http://mcp-example:3000", null, new[] { "claude-code" }, true),
        });

        saved.Should().ContainSingle();
        saved[0].ExtraJson.Should().Contain("Authorization");
    }

    [Fact]
    public void SaveMcpServersDoesNotDuplicateExistingGlobalCodexServer()
    {
        var settings = new MemorySettingsStore();
        using var sandbox = TestProviderSandbox();
        var codexDir = Path.Combine(sandbox.HomePath, ".codex");
        Directory.CreateDirectory(codexDir);
        var codexConfig = Path.Combine(codexDir, "config.toml");
        File.WriteAllText(codexConfig, """
            [mcp_servers.mcp-example]
            command = "npx"
            args = ["-y", "@example/mcp"]
            """);
        var store = sandbox.CreateStore(settings, writeProviderConfigs: true);

        store.SaveMcpServers(new[]
        {
            new McpServerDefinition("mcp-example", "Example Tracker", "stdio", "npx", "-y @example/mcp", null, null, new[] { "codex" }, true),
        });

        var config = File.ReadAllText(codexConfig);
        config.Should().Contain("[mcp_servers.mcp-example]");
        config.Should().NotContain("[mcp_servers.mcp_example]");
    }

    [Fact]
    public void SaveMcpServersDoesNotDuplicateExistingGlobalClaudeServer()
    {
        var settings = new MemorySettingsStore();
        using var sandbox = TestProviderSandbox();
        Directory.CreateDirectory(sandbox.HomePath);
        File.WriteAllText(Path.Combine(sandbox.HomePath, ".claude.json"), """
            {
              "mcpServers": {
                "mcp-example": {
                  "command": "npx",
                  "args": ["-y", "@example/mcp"]
                }
              }
            }
            """);
        Directory.CreateDirectory(sandbox.WorkspaceRoot);
        var store = sandbox.CreateStore(settings, writeProviderConfigs: true);

        store.SaveMcpServers(new[]
        {
            new McpServerDefinition("mcp-example", "Example Tracker", "stdio", "npx", "-y @example/mcp", null, null, new[] { "claude-code" }, true),
        });

        var projectMcp = File.ReadAllText(Path.Combine(sandbox.WorkspaceRoot, ".mcp.json"));
        projectMcp.Should().NotContain("mcp-example");
    }

    [Fact]
    public void GetAuthStatusesTreatsClaudeApiKeyAsAuthenticatedWithoutExposingSecret()
    {
        var settings = new MemorySettingsStore();
        using var sandbox = TestProviderSandbox();
        var store = sandbox.CreateStore(
            settings,
            environmentProvider: key => key == "ANTHROPIC_API_KEY" ? "sk-ant-secret" : null);

        var status = store.GetAuthStatuses().Single(s => s.ProviderId == "claude-code");

        status.IsAuthenticated.Should().BeTrue();
        status.Detail.Should().Contain("ANTHROPIC_API_KEY");
        status.Detail.Should().NotContain("sk-ant-secret");
    }

    [Fact]
    public void GetAuthStatusesTreatsCodexApiKeyAsAuthenticatedWithoutExposingSecret()
    {
        var settings = new MemorySettingsStore();
        using var sandbox = TestProviderSandbox();
        var store = sandbox.CreateStore(
            settings,
            environmentProvider: key => key == "OPENAI_API_KEY" ? "sk-env-secret" : null);

        var status = store.GetAuthStatuses().Single(s => s.ProviderId == "codex");

        status.IsAuthenticated.Should().BeTrue();
        status.Detail.Should().Contain("OPENAI_API_KEY");
        status.Detail.Should().NotContain("sk-env-secret");
    }

    private sealed class MemorySettingsStore : IKeyValueSettingsStore
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

        public string? Get(string key) => _values.GetValueOrDefault(key);

        public void Set(string key, string value) => _values[key] = value;
    }

    private static ProviderSandbox TestProviderSandbox() => new();

    private sealed class ProviderSandbox : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "doxie-provider-tests", Guid.NewGuid().ToString("N"));

        public string HomePath => Path.Combine(_root, "home");
        public string WorkspaceRoot => Path.Combine(_root, "workspace");

        public ProviderEnvironmentStore CreateStore(
            IKeyValueSettingsStore settings,
            bool writeProviderConfigs = false,
            Func<string, string?>? environmentProvider = null) =>
            new(
                settings,
                homePath: HomePath,
                workspaceRoot: WorkspaceRoot,
                writeProviderConfigs: writeProviderConfigs,
                environmentProvider: environmentProvider ?? (_ => null));

        public void Dispose()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
    }
}
