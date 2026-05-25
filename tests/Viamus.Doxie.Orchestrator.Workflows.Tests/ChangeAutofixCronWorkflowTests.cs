using FluentAssertions;
using Viamus.Doxie.Orchestrator.Workflows;

namespace Viamus.Doxie.Orchestrator.Workflows.Tests;

/// <summary>
/// Round-trip + structure check for the canonical change-autofix-cron
/// workflow shape. Exercises both fixes shipped in this milestone:
/// cron disabled-by-default + the Loop primitive with body sub-graph.
/// The JSON is embedded so the test doesn't depend on the user-local
/// <c>.doxie/workflows/</c> folder (gitignored by policy).
/// </summary>
public sealed class ChangeAutofixCronWorkflowTests : IDisposable
{
    private readonly string _tempDir;

    public ChangeAutofixCronWorkflowTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"doxie-change-autofix-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public void Canonical_workflow_json_loads_via_FilesystemWorkflowStore()
    {
        // Stage the embedded canonical JSON under a temp directory in
        // the layout the FilesystemWorkflowStore expects:
        //   <root>/<workflow-id>/workflow.json
        var stagedDir = Path.Combine(_tempDir, "change-autofix-cron");
        Directory.CreateDirectory(stagedDir);
        File.WriteAllText(Path.Combine(stagedDir, "workflow.json"), CanonicalWorkflowJson);

        var store = new FilesystemWorkflowStore(_tempDir);
        var loaded = store.GetById("change-autofix-cron");

        loaded.Should().NotBeNull("workflow should deserialize without error");
        loaded!.Id.Should().Be("change-autofix-cron");

        // Cron disabled-by-default fix: the on-disk file carries
        // enabled=false to reflect the policy. Without the fix this
        // workflow would have nascent enabled=true and the cron daemon
        // could fire it before env vars are filled.
        loaded.Enabled.Should().BeFalse("cron-triggered workflows must nascer disabled");
        loaded.Trigger.Kind.Should().Be(WorkflowTriggerKind.Cron);

        // Schema: 4 nodes (list-changes, loop-changes, diagnose, fix).
        loaded.Nodes.Should().HaveCount(4);
        var loopNode = loaded.Nodes.Single(n => n.Id == "loop-changes");
        loopNode.AgentId.Should().Be("loop");
        loopNode.Inputs.Should().NotBeNull();
        loopNode.Inputs!["concurrency"].Should().Be("4");
        loopNode.Inputs["on_failure"].Should().Be("continue");
        loopNode.LoopId.Should().BeNull("the Loop node itself isn't in its own body");

        // Body nodes carry the LoopId pointing at the loop owner.
        var diagnose = loaded.Nodes.Single(n => n.Id == "diagnose");
        diagnose.LoopId.Should().Be("loop-changes");
        var fix = loaded.Nodes.Single(n => n.Id == "fix");
        fix.LoopId.Should().Be("loop-changes");

        // Topology that the runner expects:
        //   list-changes -> loop-changes -> diagnose -> fix
        // Body = (diagnose, fix); body-entry = diagnose; body-exit = fix
        loaded.Edges.Should().BeEquivalentTo(new[]
        {
            new { FromNodeId = "list-changes", ToNodeId = "loop-changes" },
            new { FromNodeId = "loop-changes", ToNodeId = "diagnose" },
            new { FromNodeId = "diagnose", ToNodeId = "fix" },
        });

        // The body is a connected sub-DAG with a single entry edge from
        // the loop owner — exactly what V1 SimulateLoopNodeAsync allows.
        var bodyEntries = loaded.Edges
            .Where(e => e.FromNodeId == "loop-changes"
                     && loaded.Nodes.Any(n => n.Id == e.ToNodeId && n.LoopId == "loop-changes"))
            .ToList();
        bodyEntries.Should().HaveCount(1, "loop must have exactly one body-entry");

        // Fix is body-exit (no downstream within the body, no
        // downstream outside either — terminal in the graph).
        var fixDownstream = loaded.Edges.Where(e => e.FromNodeId == "fix").ToList();
        fixDownstream.Should().BeEmpty("fix is the body-exit + workflow terminal");
    }

    // Canonical change-autofix-cron workflow.json — embedded so the test
    // doesn't depend on .doxie/workflows/ (gitignored user-content).
    private const string CanonicalWorkflowJson = """
    {
      "id": "change-autofix-cron",
      "name": "Change Auto-Fix (Cron)",
      "description": "List open changes across configured repos, fan out with loop-changes (concurrency=4), diagnose each, and have developer fix the failing ones. on_failure=continue so a single bad change does not abort the batch.",
      "trigger": {
        "kind": "cron",
        "cronExpression": "*/30 * * * 1-5",
        "eventName": null,
        "webhookPath": null,
        "inputs": null
      },
      "nodes": [
        {
          "id": "list-changes",
          "kind": "agent",
          "label": "Listar changes",
          "x": 60,
          "y": 40,
          "agentId": "list-open-changes",
          "agentMode": "list",
          "inputs": { "repo": "{{env.REPOSITORY_URLS}}" },
          "outputWorkspaceId": null,
          "outputFileName": null,
          "workspaceId": null,
          "loopId": null
        },
        {
          "id": "loop-changes",
          "kind": "agent",
          "label": "Loop por item",
          "x": 300,
          "y": 40,
          "agentId": "loop",
          "agentMode": "iterate",
          "inputs": {
            "array_source": "result.json",
            "concurrency": "4",
            "on_failure": "continue"
          },
          "outputWorkspaceId": null,
          "outputFileName": null,
          "workspaceId": null,
          "loopId": null
        },
        {
          "id": "diagnose",
          "kind": "agent",
          "label": "Diagnosticar item",
          "x": 540,
          "y": 40,
          "agentId": "diagnose-change",
          "agentMode": "diagnose",
          "inputs": { "change-url": "{{env.LOOP_ITEM}}" },
          "outputWorkspaceId": null,
          "outputFileName": null,
          "workspaceId": null,
          "loopId": "loop-changes"
        },
        {
          "id": "fix",
          "kind": "agent",
          "label": "Corrigir item",
          "x": 780,
          "y": 40,
          "agentId": "developer",
          "agentMode": "implement",
          "inputs": { "plan": "{{upstream}}" },
          "outputWorkspaceId": null,
          "outputFileName": null,
          "workspaceId": null,
          "loopId": "loop-changes"
        }
      ],
      "edges": [
        { "fromNodeId": "list-changes", "toNodeId": "loop-changes" },
        { "fromNodeId": "loop-changes", "toNodeId": "diagnose" },
        { "fromNodeId": "diagnose", "toNodeId": "fix" }
      ],
      "createdAt": "2026-05-10T17:18:51.6130582Z",
      "updatedAt": "2026-05-10T18:30:00.0000000Z",
      "workspaceId": null,
      "env": {
        "REPOSITORY_URLS": "(preencher: JSON array de URLs de repos no Example Tracker)"
      },
      "enabled": false
    }
    """;
}
