using FluentAssertions;

namespace Viamus.Doxie.Orchestrator.Context.Tests;

public sealed class FilesystemWorkspaceStoreTests : IDisposable
{
    private readonly string _root;
    private readonly string _librariesRoot;

    public FilesystemWorkspaceStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"doxie-ws-tests-{Guid.NewGuid():N}");
        _librariesRoot = Path.Combine(_root, "libs");
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_librariesRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void Create_persists_workspace_with_scaffold()
    {
        var store = new FilesystemWorkspaceStore(directory: _root, librariesDirectory: _librariesRoot);

        var ws = store.Create(
            id: "demo-ws",
            displayName: "Demo Workspace",
            description: "smoke test target",
            mountedLibraryIds: null);

        ws.Id.Should().Be("demo-ws");
        ws.Name.Should().Be("Demo Workspace");
        ws.Description.Should().Be("smoke test target");
        Directory.Exists(ws.Path).Should().BeTrue();
        File.Exists(Path.Combine(ws.Path, "workspace.json")).Should().BeTrue();
        File.Exists(Path.Combine(ws.Path, "WORKSPACE.md")).Should().BeTrue();
        File.Exists(Path.Combine(ws.Path, "memory", "MEMORY.md")).Should().BeTrue();
    }

    [Fact]
    public void GetById_round_trips_a_created_workspace()
    {
        var store = new FilesystemWorkspaceStore(directory: _root, librariesDirectory: _librariesRoot);
        var created = store.Create("alpha", displayName: null, description: null);

        var fetched = store.GetById("alpha");

        fetched.Should().NotBeNull();
        fetched!.Id.Should().Be(created.Id);
        fetched.Name.Should().Be("Alpha"); // derived from id
        fetched.MountedLibraryIds.Should().BeEmpty();
    }

    [Fact]
    public void SetMountedLibraries_replaces_set_idempotently()
    {
        var store = new FilesystemWorkspaceStore(directory: _root, librariesDirectory: _librariesRoot);
        store.Create("beta", displayName: null, description: null);

        var afterFirst = store.SetMountedLibraries("beta", new[] { "lib-a", "lib-b" });
        var afterSame = store.SetMountedLibraries("beta", new[] { "lib-a", "lib-b" });
        var afterEmpty = store.SetMountedLibraries("beta", Array.Empty<string>());

        afterFirst.MountedLibraryIds.Should().Equal("lib-a", "lib-b");
        afterSame.MountedLibraryIds.Should().Equal("lib-a", "lib-b");
        afterEmpty.MountedLibraryIds.Should().BeEmpty();
    }

    [Fact]
    public void SetMountedAgents_replaces_set_idempotently()
    {
        var store = new FilesystemWorkspaceStore(directory: _root, librariesDirectory: _librariesRoot);
        store.Create("agent-room", displayName: null, description: null, mountedAgentIds: new[] { "reviewer" });

        var afterFirst = store.SetMountedAgents("agent-room", new[] { "reviewer", "builder", "reviewer" });
        var fetched = store.GetById("agent-room");
        var afterEmpty = store.SetMountedAgents("agent-room", Array.Empty<string>());

        afterFirst.MountedAgentIds.Should().Equal("reviewer", "builder");
        fetched!.MountedAgentIds.Should().Equal("reviewer", "builder");
        afterEmpty.MountedAgentIds.Should().BeEmpty();
    }

    [Fact]
    public void SetMountedLibraries_writes_claude_imports_and_codex_inline_context()
    {
        WriteMemory("lib-a", "feedback_naming.md", "Naming", "feedback", body: "Use clear names.");
        WriteMemory("lib-a", Path.Combine("memory", "reference_paths.md"), "Paths", "reference", body: "Repo lives at C:/repo.");

        var store = new FilesystemWorkspaceStore(directory: _root, librariesDirectory: _librariesRoot);
        store.Create("gamma", displayName: "Gamma", description: null);

        store.SetMountedLibraries("gamma", new[] { "lib-a" });

        var workspaceDir = Path.Combine(_root, "gamma");
        var claudeMd = File.ReadAllText(Path.Combine(workspaceDir, "CLAUDE.md"));
        var agentsMd = File.ReadAllText(Path.Combine(workspaceDir, "AGENTS.md"));

        claudeMd.Should().Contain("@../libs/lib-a/feedback_naming.md");
        claudeMd.Should().Contain("@../libs/lib-a/memory/reference_paths.md");
        agentsMd.Should().Contain("# Gamma - workspace context");
        agentsMd.Should().Contain("Use clear names.");
        agentsMd.Should().Contain("Repo lives at C:/repo.");
    }

    [Fact]
    public void SetMountedLibraries_preserves_hand_authored_agents_md()
    {
        WriteMemory("lib-a", "feedback_naming.md", "Naming", "feedback");
        var store = new FilesystemWorkspaceStore(directory: _root, librariesDirectory: _librariesRoot);
        store.Create("delta", displayName: null, description: null);
        var agentsPath = Path.Combine(_root, "delta", "AGENTS.md");
        File.WriteAllText(agentsPath, "# hand authored");

        store.SetMountedLibraries("delta", new[] { "lib-a" });

        File.ReadAllText(agentsPath).Should().Be("# hand authored");
    }

    private void WriteMemory(string libraryId, string relativePath, string name, string type, string body = "body content")
    {
        var path = Path.Combine(_librariesRoot, libraryId, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var content = $"---\nname: {name}\ntype: {type}\n---\n{body}";
        File.WriteAllText(path, content);
    }
}
