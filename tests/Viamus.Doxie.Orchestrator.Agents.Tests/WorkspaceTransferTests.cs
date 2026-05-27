using System.IO.Compression;
using FluentAssertions;

namespace Viamus.Doxie.Orchestrator.Agents.Tests;

public sealed class WorkspaceTransferTests : IDisposable
{
    private readonly string _root;
    private readonly string _sourceWorkspacesRoot;
    private readonly string _sourceSkillsRoot;
    private readonly string _sourceAgentsRoot;
    private readonly string _destWorkspacesRoot;
    private readonly string _destSkillsRoot;
    private readonly string _destAgentsRoot;

    public WorkspaceTransferTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"workspace-transfer-{Guid.NewGuid():N}");
        _sourceWorkspacesRoot = Path.Combine(_root, "source-workspaces");
        _sourceSkillsRoot = Path.Combine(_root, "source-catalog", "skills");
        _sourceAgentsRoot = Path.Combine(_root, "source-catalog", "agents");
        _destWorkspacesRoot = Path.Combine(_root, "dest-workspaces");
        _destSkillsRoot = Path.Combine(_root, "dest-catalog", "skills");
        _destAgentsRoot = Path.Combine(_root, "dest-catalog", "agents");
        Directory.CreateDirectory(_sourceWorkspacesRoot);
        Directory.CreateDirectory(_sourceSkillsRoot);
        Directory.CreateDirectory(_sourceAgentsRoot);
        Directory.CreateDirectory(_destWorkspacesRoot);
        Directory.CreateDirectory(_destSkillsRoot);
        Directory.CreateDirectory(_destAgentsRoot);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch { /* best effort */ }
    }

    [Fact]
    public void Export_writes_workspace_folder_and_nested_agent_zip()
    {
        var workspaceDir = WriteWorkspace("project-alpha", "reviewer");
        WriteAgent(_sourceSkillsRoot, _sourceAgentsRoot, "reviewer", "review body");

        using var ms = new MemoryStream();
        WorkspaceTransfer.ExportToZip(
            workspaceDir,
            "project-alpha",
            new[]
            {
                new WorkspaceTransfer.AgentBundleSource("reviewer", _sourceSkillsRoot, _sourceAgentsRoot),
            },
            ms);

        ms.Position = 0;
        using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
        zip.GetEntry(WorkspaceTransfer.BundleManifestName).Should().NotBeNull();
        zip.GetEntry("workspace/project-alpha/workspace.json").Should().NotBeNull();
        var nested = zip.GetEntry("agents/reviewer.zip");
        nested.Should().NotBeNull();

        using var nestedMs = new MemoryStream();
        using (var nestedStream = nested!.Open())
        {
            nestedStream.CopyTo(nestedMs);
        }
        nestedMs.Position = 0;
        using var agentZip = new ZipArchive(nestedMs, ZipArchiveMode.Read);
        agentZip.GetEntry("reviewer/manifest.json").Should().NotBeNull();
        agentZip.GetEntry("reviewer/body.md").Should().NotBeNull();
        agentZip.GetEntry("reviewer.md").Should().NotBeNull();
    }

    [Fact]
    public void Import_creates_workspace_and_missing_agents()
    {
        var workspaceDir = WriteWorkspace("project-alpha", "reviewer");
        WriteAgent(_sourceSkillsRoot, _sourceAgentsRoot, "reviewer", "review body");
        using var bundle = ExportBundle(workspaceDir, "project-alpha", "reviewer");

        var result = WorkspaceTransfer.ImportFromZip(
            bundle,
            _destWorkspacesRoot,
            DestinationCatalog(),
            new[] { DestinationCatalog() },
            overwrite: false);

        result.Ok.Should().BeTrue();
        result.WorkspaceId.Should().Be("project-alpha");
        result.WorkspaceAction.Should().Be(WorkspaceTransfer.WorkspaceImportAction.Imported);
        result.Agents.Should().ContainSingle(a =>
            a.AgentId == "reviewer" && a.Action == WorkspaceTransfer.AgentImportAction.Imported);
        File.Exists(Path.Combine(_destWorkspacesRoot, "project-alpha", "workspace.json")).Should().BeTrue();
        File.Exists(Path.Combine(_destSkillsRoot, "reviewer", "manifest.json")).Should().BeTrue();
        File.Exists(Path.Combine(_destAgentsRoot, "reviewer.md")).Should().BeTrue();
    }

    [Fact]
    public void Import_skips_identical_existing_agent()
    {
        var workspaceDir = WriteWorkspace("project-alpha", "reviewer");
        WriteAgent(_sourceSkillsRoot, _sourceAgentsRoot, "reviewer", "same body");
        WriteAgent(_destSkillsRoot, _destAgentsRoot, "reviewer", "same body");
        using var bundle = ExportBundle(workspaceDir, "project-alpha", "reviewer");

        var result = WorkspaceTransfer.ImportFromZip(
            bundle,
            _destWorkspacesRoot,
            DestinationCatalog(),
            new[] { DestinationCatalog() },
            overwrite: false);

        result.Ok.Should().BeTrue();
        result.Agents.Should().ContainSingle(a =>
            a.AgentId == "reviewer"
            && a.Action == WorkspaceTransfer.AgentImportAction.SkippedIdentical
            && a.ExistingCatalogId == "default");
    }

    [Fact]
    public void Import_refuses_different_existing_agent_without_overwrite()
    {
        var workspaceDir = WriteWorkspace("project-alpha", "reviewer");
        WriteAgent(_sourceSkillsRoot, _sourceAgentsRoot, "reviewer", "new body");
        WriteAgent(_destSkillsRoot, _destAgentsRoot, "reviewer", "old body");
        using var bundle = ExportBundle(workspaceDir, "project-alpha", "reviewer");

        var result = WorkspaceTransfer.ImportFromZip(
            bundle,
            _destWorkspacesRoot,
            DestinationCatalog(),
            new[] { DestinationCatalog() },
            overwrite: false);

        result.Ok.Should().BeFalse();
        result.Error.Should().Be(WorkspaceTransfer.ImportError.AgentCollision);
        File.ReadAllText(Path.Combine(_destSkillsRoot, "reviewer", "body.md")).Should().Be("old body");
        Directory.Exists(Path.Combine(_destWorkspacesRoot, "project-alpha")).Should().BeFalse();
    }

    [Fact]
    public void Import_replaces_different_existing_agent_when_overwrite_is_true()
    {
        var workspaceDir = WriteWorkspace("project-alpha", "reviewer");
        WriteAgent(_sourceSkillsRoot, _sourceAgentsRoot, "reviewer", "new body");
        WriteAgent(_destSkillsRoot, _destAgentsRoot, "reviewer", "old body");
        using var bundle = ExportBundle(workspaceDir, "project-alpha", "reviewer");

        var result = WorkspaceTransfer.ImportFromZip(
            bundle,
            _destWorkspacesRoot,
            DestinationCatalog(),
            new[] { DestinationCatalog() },
            overwrite: true);

        result.Ok.Should().BeTrue();
        result.Agents.Should().ContainSingle(a =>
            a.AgentId == "reviewer" && a.Action == WorkspaceTransfer.AgentImportAction.Replaced);
        File.ReadAllText(Path.Combine(_destSkillsRoot, "reviewer", "body.md")).Should().Be("new body");
    }

    private MemoryStream ExportBundle(string workspaceDir, string workspaceId, params string[] agentIds)
    {
        var ms = new MemoryStream();
        WorkspaceTransfer.ExportToZip(
            workspaceDir,
            workspaceId,
            agentIds.Select(id => new WorkspaceTransfer.AgentBundleSource(id, _sourceSkillsRoot, _sourceAgentsRoot)),
            ms);
        ms.Position = 0;
        return ms;
    }

    private string WriteWorkspace(string id, params string[] mountedAgents)
    {
        var dir = Path.Combine(_sourceWorkspacesRoot, id);
        Directory.CreateDirectory(Path.Combine(dir, "memory"));
        File.WriteAllText(Path.Combine(dir, "workspace.json"),
            $$"""
            {
              "name": "Project Alpha",
              "description": "fixture",
              "createdAt": "2026-05-27T00:00:00Z",
              "mountedLibraries": [],
              "mountedAgents": [{{string.Join(", ", mountedAgents.Select(a => $"\"{a}\""))}}]
            }
            """);
        File.WriteAllText(Path.Combine(dir, "WORKSPACE.md"), "# Project Alpha");
        File.WriteAllText(Path.Combine(dir, "memory", "MEMORY.md"), "# Memory");
        return dir;
    }

    private static void WriteAgent(string skillsRoot, string agentsRoot, string id, string body)
    {
        var skillDir = Path.Combine(skillsRoot, id);
        Directory.CreateDirectory(skillDir);
        Directory.CreateDirectory(agentsRoot);
        File.WriteAllText(Path.Combine(skillDir, "manifest.json"),
            $$"""{"id":"{{id}}","description":"fixture agent","category":"Builder"}""");
        File.WriteAllText(Path.Combine(skillDir, "body.md"), body);
        File.WriteAllText(Path.Combine(agentsRoot, $"{id}.md"),
            $"---\nname: {id}\ndescription: fixture\ntools: Read\n---\n{subagentBody(body)}");
    }

    private WorkspaceTransfer.AgentImportCatalog DestinationCatalog() =>
        new("default", _destSkillsRoot, _destAgentsRoot);

    private static string subagentBody(string body) => $"Subagent sees: {body}";
}
