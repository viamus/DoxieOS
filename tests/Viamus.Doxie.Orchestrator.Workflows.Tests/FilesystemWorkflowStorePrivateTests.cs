using FluentAssertions;
using Viamus.Doxie.Orchestrator.Common;
using Viamus.Doxie.Orchestrator.Workflows;

namespace Viamus.Doxie.Orchestrator.Workflows.Tests;

public sealed class FilesystemWorkflowStoreCatalogTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"doxie-wf-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { /* best effort */ }
    }

    [Fact]
    public void Save_writes_workflow_to_selected_custom_catalog()
    {
        var store = CreateStore();
        var workflow = MakeWorkflow("custom-flow") with { CatalogId = "shared-team" };

        store.Save(workflow);

        File.Exists(Path.Combine(_root, "shared-team-catalog", "workflows", "custom-flow", "workflow.json"))
            .Should().BeTrue();
        File.Exists(Path.Combine(_root, ".doxie", "workflows", "custom-flow", "workflow.json"))
            .Should().BeFalse();
        store.GetById("custom-flow")!.CatalogId.Should().Be("shared-team");
        store.GetById("custom-flow")!.IsPrivate.Should().BeFalse();
    }

    [Fact]
    public void Save_moves_workflow_between_default_and_custom_catalogs()
    {
        var store = CreateStore();
        store.Save(MakeWorkflow("movable-flow"));

        store.Save(MakeWorkflow("movable-flow") with { CatalogId = "shared-team" });

        File.Exists(Path.Combine(_root, ".doxie", "workflows", "movable-flow", "workflow.json"))
            .Should().BeFalse();
        File.Exists(Path.Combine(_root, "shared-team-catalog", "workflows", "movable-flow", "workflow.json"))
            .Should().BeTrue();
        store.ListAll().Should().ContainSingle(w => w.Id == "movable-flow" && w.CatalogId == "shared-team" && !w.IsPrivate);
    }

    [Fact]
    public void Save_round_trips_workflow_category()
    {
        var store = CreateStore();

        store.Save(MakeWorkflow("delivery-flow") with { Category = "Delivery" });

        store.GetById("delivery-flow")!.DisplayCategory.Should().Be("Delivery");
    }

    private FilesystemWorkflowStore CreateStore() =>
        new(
            Path.Combine(_root, ".doxie", "workflows"),
            catalogRootsProvider: () => new[]
            {
                new DoxieCatalogRoot(
                    "default",
                    "Default catalog",
                    Path.Combine(_root, ".doxie"),
                    IsDefault: true,
                    WorkflowsPath: Path.Combine(_root, ".doxie", "workflows")),
                new DoxieCatalogRoot(
                    "shared-team",
                    "Shared team",
                    Path.Combine(_root, "shared-team-catalog")),
            });

    private static WorkflowDefinition MakeWorkflow(string id) =>
        new(
            Id: id,
            Name: id,
            Description: "test",
            Trigger: new WorkflowTrigger(WorkflowTriggerKind.Manual),
            Nodes: new[]
            {
                new WorkflowNode(
                    Id: "step-1",
                    Kind: WorkflowNodeKind.Agent,
                    Label: "Step 1",
                    X: 0,
                    Y: 0,
                    AgentId: "developer"),
            },
            Edges: Array.Empty<WorkflowEdge>(),
            CreatedAt: DateTime.UtcNow,
            UpdatedAt: DateTime.UtcNow);
}
