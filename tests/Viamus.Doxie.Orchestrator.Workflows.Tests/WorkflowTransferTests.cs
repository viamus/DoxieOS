using System.IO.Compression;
using FluentAssertions;
using Viamus.Doxie.Orchestrator.Workflows;

namespace Viamus.Doxie.Orchestrator.Workflows.Tests;

public sealed class WorkflowTransferTests : IDisposable
{
    private readonly string _root;
    private readonly string _sourceWorkflowsRoot;
    private readonly string _sourceSkillsRoot;
    private readonly string _sourceAgentsRoot;
    private readonly string _destWorkflowsRoot;
    private readonly string _destSkillsRoot;
    private readonly string _destAgentsRoot;

    public WorkflowTransferTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"workflow-transfer-{Guid.NewGuid():N}");
        _sourceWorkflowsRoot = Path.Combine(_root, "source-catalog", "workflows");
        _sourceSkillsRoot = Path.Combine(_root, "source-catalog", "skills");
        _sourceAgentsRoot = Path.Combine(_root, "source-catalog", "agents");
        _destWorkflowsRoot = Path.Combine(_root, "dest-catalog", "workflows");
        _destSkillsRoot = Path.Combine(_root, "dest-catalog", "skills");
        _destAgentsRoot = Path.Combine(_root, "dest-catalog", "agents");
        Directory.CreateDirectory(_sourceWorkflowsRoot);
        Directory.CreateDirectory(_sourceSkillsRoot);
        Directory.CreateDirectory(_sourceAgentsRoot);
        Directory.CreateDirectory(_destWorkflowsRoot);
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
    public void Export_writes_workflow_folder_and_nested_agent_zip()
    {
        var workflowDir = WriteWorkflow(_sourceWorkflowsRoot, "daily-review", "source", "reviewer");
        WriteAgent(_sourceSkillsRoot, _sourceAgentsRoot, "reviewer", "review body");

        using var ms = new MemoryStream();
        WorkflowTransfer.ExportToZip(
            workflowDir,
            "daily-review",
            new[]
            {
                new WorkflowTransfer.AgentBundleSource("reviewer", _sourceSkillsRoot, _sourceAgentsRoot),
            },
            ms);

        ms.Position = 0;
        using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
        zip.GetEntry(WorkflowTransfer.BundleManifestName).Should().NotBeNull();
        zip.GetEntry("workflow/daily-review/workflow.json").Should().NotBeNull();
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
    public void Import_creates_workflow_and_missing_agents()
    {
        var workflowDir = WriteWorkflow(_sourceWorkflowsRoot, "daily-review", "source", "reviewer");
        WriteAgent(_sourceSkillsRoot, _sourceAgentsRoot, "reviewer", "review body");
        using var bundle = ExportBundle(workflowDir, "daily-review", "reviewer");

        var result = WorkflowTransfer.ImportFromZip(
            bundle,
            DestinationCatalog(),
            new[] { DestinationCatalog() },
            overwrite: false);

        result.Ok.Should().BeTrue();
        result.WorkflowId.Should().Be("daily-review");
        result.WorkflowAction.Should().Be(WorkflowTransfer.WorkflowImportAction.Imported);
        result.Agents.Should().ContainSingle(a =>
            a.AgentId == "reviewer" && a.Action == WorkflowTransfer.AgentImportAction.Imported);
        File.Exists(Path.Combine(_destWorkflowsRoot, "daily-review", "workflow.json")).Should().BeTrue();
        File.ReadAllText(Path.Combine(_destWorkflowsRoot, "daily-review", "workflow.json"))
            .Should().Contain("\"catalogId\": \"default\"");
        File.Exists(Path.Combine(_destSkillsRoot, "reviewer", "manifest.json")).Should().BeTrue();
        File.Exists(Path.Combine(_destAgentsRoot, "reviewer.md")).Should().BeTrue();
    }

    [Fact]
    public void Import_skips_identical_existing_agent()
    {
        var workflowDir = WriteWorkflow(_sourceWorkflowsRoot, "daily-review", "source", "reviewer");
        WriteAgent(_sourceSkillsRoot, _sourceAgentsRoot, "reviewer", "same body");
        WriteAgent(_destSkillsRoot, _destAgentsRoot, "reviewer", "same body");
        using var bundle = ExportBundle(workflowDir, "daily-review", "reviewer");

        var result = WorkflowTransfer.ImportFromZip(
            bundle,
            DestinationCatalog(),
            new[] { DestinationCatalog() },
            overwrite: false);

        result.Ok.Should().BeTrue();
        result.Agents.Should().ContainSingle(a =>
            a.AgentId == "reviewer"
            && a.Action == WorkflowTransfer.AgentImportAction.SkippedIdentical
            && a.ExistingCatalogId == "default");
    }

    [Fact]
    public void Import_refuses_different_existing_agent_without_overwrite()
    {
        var workflowDir = WriteWorkflow(_sourceWorkflowsRoot, "daily-review", "source", "reviewer");
        WriteAgent(_sourceSkillsRoot, _sourceAgentsRoot, "reviewer", "new body");
        WriteAgent(_destSkillsRoot, _destAgentsRoot, "reviewer", "old body");
        using var bundle = ExportBundle(workflowDir, "daily-review", "reviewer");

        var result = WorkflowTransfer.ImportFromZip(
            bundle,
            DestinationCatalog(),
            new[] { DestinationCatalog() },
            overwrite: false);

        result.Ok.Should().BeFalse();
        result.Error.Should().Be(WorkflowTransfer.ImportError.AgentCollision);
        File.ReadAllText(Path.Combine(_destSkillsRoot, "reviewer", "body.md")).Should().Be("old body");
        Directory.Exists(Path.Combine(_destWorkflowsRoot, "daily-review")).Should().BeFalse();
    }

    [Fact]
    public void Import_replaces_different_existing_agent_when_overwrite_is_true()
    {
        var workflowDir = WriteWorkflow(_sourceWorkflowsRoot, "daily-review", "source", "reviewer");
        WriteAgent(_sourceSkillsRoot, _sourceAgentsRoot, "reviewer", "new body");
        WriteAgent(_destSkillsRoot, _destAgentsRoot, "reviewer", "old body");
        using var bundle = ExportBundle(workflowDir, "daily-review", "reviewer");

        var result = WorkflowTransfer.ImportFromZip(
            bundle,
            DestinationCatalog(),
            new[] { DestinationCatalog() },
            overwrite: true);

        result.Ok.Should().BeTrue();
        result.Agents.Should().ContainSingle(a =>
            a.AgentId == "reviewer" && a.Action == WorkflowTransfer.AgentImportAction.Replaced);
        File.ReadAllText(Path.Combine(_destSkillsRoot, "reviewer", "body.md")).Should().Be("new body");
    }

    [Fact]
    public void Import_refuses_different_existing_workflow_without_overwrite()
    {
        var workflowDir = WriteWorkflow(_sourceWorkflowsRoot, "daily-review", "source", "reviewer", "new workflow");
        WriteWorkflow(_destWorkflowsRoot, "daily-review", "default", "reviewer", "old workflow");
        WriteAgent(_sourceSkillsRoot, _sourceAgentsRoot, "reviewer", "review body");
        using var bundle = ExportBundle(workflowDir, "daily-review", "reviewer");

        var result = WorkflowTransfer.ImportFromZip(
            bundle,
            DestinationCatalog(),
            new[] { DestinationCatalog() },
            overwrite: false);

        result.Ok.Should().BeFalse();
        result.Error.Should().Be(WorkflowTransfer.ImportError.Collision);
        File.ReadAllText(Path.Combine(_destWorkflowsRoot, "daily-review", "workflow.json"))
            .Should().Contain("old workflow");
    }

    [Fact]
    public void Import_replaces_different_existing_workflow_when_overwrite_is_true()
    {
        var workflowDir = WriteWorkflow(_sourceWorkflowsRoot, "daily-review", "source", "reviewer", "new workflow");
        WriteWorkflow(_destWorkflowsRoot, "daily-review", "default", "reviewer", "old workflow");
        WriteAgent(_sourceSkillsRoot, _sourceAgentsRoot, "reviewer", "review body");
        using var bundle = ExportBundle(workflowDir, "daily-review", "reviewer");

        var result = WorkflowTransfer.ImportFromZip(
            bundle,
            DestinationCatalog(),
            new[] { DestinationCatalog() },
            overwrite: true);

        result.Ok.Should().BeTrue();
        result.WorkflowAction.Should().Be(WorkflowTransfer.WorkflowImportAction.Replaced);
        File.ReadAllText(Path.Combine(_destWorkflowsRoot, "daily-review", "workflow.json"))
            .Should().Contain("new workflow");
    }

    private MemoryStream ExportBundle(string workflowDir, string workflowId, params string[] agentIds)
    {
        var ms = new MemoryStream();
        WorkflowTransfer.ExportToZip(
            workflowDir,
            workflowId,
            agentIds.Select(id => new WorkflowTransfer.AgentBundleSource(id, _sourceSkillsRoot, _sourceAgentsRoot)),
            ms);
        ms.Position = 0;
        return ms;
    }

    private static string WriteWorkflow(
        string workflowsRoot,
        string id,
        string catalogId,
        string agentId,
        string description = "fixture")
    {
        var dir = Path.Combine(workflowsRoot, id);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "workflow.json"),
            $$"""
            {
              "id": "{{id}}",
              "name": "Daily Review",
              "description": "{{description}}",
              "trigger": {
                "kind": "manual"
              },
              "nodes": [
                {
                  "id": "trigger",
                  "kind": "trigger",
                  "label": "Manual",
                  "x": 0,
                  "y": 0
                },
                {
                  "id": "review",
                  "kind": "agent",
                  "label": "Review",
                  "x": 160,
                  "y": 0,
                  "agentId": "{{agentId}}",
                  "agentMode": "default"
                }
              ],
              "edges": [
                {
                  "fromNodeId": "trigger",
                  "toNodeId": "review"
                }
              ],
              "createdAt": "2026-05-27T00:00:00Z",
              "updatedAt": "2026-05-27T00:00:00Z",
              "enabled": true,
              "catalogId": "{{catalogId}}"
            }
            """);
        File.WriteAllText(Path.Combine(dir, "README.md"), $"# {id}");
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

    private WorkflowTransfer.ImportCatalog DestinationCatalog() =>
        new("default", _destSkillsRoot, _destAgentsRoot, _destWorkflowsRoot);

    private static string subagentBody(string body) => $"Subagent sees: {body}";
}
