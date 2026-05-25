using FluentAssertions;
using Viamus.Doxie.Orchestrator.Agents;
using Viamus.Doxie.Orchestrator.Agents.Doxie;
using Viamus.Doxie.Orchestrator.Common;
using Viamus.Doxie.Orchestrator.Context;
using Viamus.Doxie.Orchestrator.Workflows;

namespace Viamus.Doxie.Orchestrator.Workflows.Tests;

public sealed class OrchestratedWorkflowRunnerTests : IDisposable
{
    private readonly string _runsDir;

    public OrchestratedWorkflowRunnerTests()
    {
        _runsDir = Path.Combine(Path.GetTempPath(), $"doxie-orch-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_runsDir);
    }

    public void Dispose()
    {
        // Some tests (e.g. the approval-gate ones) don't await the run's
        // background task to finish before returning, so the runner can
        // still be writing into <runsDir>/<runId>/<node>/ when teardown
        // runs. On Windows that races with Directory.Delete and surfaces
        // as "file is being used by another process". Retry briefly —
        // handles are usually released within tens of ms.
        if (!Directory.Exists(_runsDir)) return;
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                Directory.Delete(_runsDir, recursive: true);
                return;
            }
            catch (IOException) when (attempt < 9)
            {
                Thread.Sleep(100);
            }
            catch (UnauthorizedAccessException) when (attempt < 9)
            {
                Thread.Sleep(100);
            }
        }
    }

    [Fact]
    public async Task Agent_node_dispatches_via_IAgentRunner_with_expected_args_and_env()
    {
        // Arrange — a single-step workflow that hits one agent node.
        var catalog = new FakeCatalog(new AgentDescriptor(
            Id: "fake-agent",
            Name: "Fake Agent",
            Description: "test",
            SkillName: "fake-agent",
            Category: AgentCategory.Builder,
            Modes: new[]
            {
                new AgentMode(
                    Id: "do-thing",
                    Name: "Do Thing",
                    Description: "",
                    ArgumentsTemplate: "do-thing --target {target}",
                    Fields: new[] { new AgentModeField(Id: "target", Label: "Target", Placeholder: string.Empty, Required: true) }),
            }));

        var runner = new FakeAgentRunner();
        var workflowRuns = new InMemoryWorkflowRunStore();
        var workspaces = new EmptyWorkspaceStore();

        var orchestrator = new OrchestratedWorkflowRunner(
            runStore: workflowRuns,
            workspaceStore: workspaces,
            agentCatalog: catalog,
            agentRunner: runner,
            runsDirectory: _runsDir);

        var wf = new WorkflowDefinition(
            Id: "test-wf",
            Name: "Test",
            Description: "",
            Trigger: new WorkflowTrigger(WorkflowTriggerKind.Manual),
            Nodes: new[]
            {
                new WorkflowNode("trigger", WorkflowNodeKind.Trigger, "Manual", 0, 0),
                new WorkflowNode("step", WorkflowNodeKind.Agent, "Step", 0, 0,
                    AgentId: "fake-agent",
                    AgentMode: "do-thing",
                    Inputs: new Dictionary<string, string> { ["target"] = "production" }),
            },
            Edges: new[] { new WorkflowEdge("trigger", "step") },
            CreatedAt: DateTime.UtcNow,
            UpdatedAt: DateTime.UtcNow,
            Env: new Dictionary<string, string> { ["MY_TOKEN"] = "abc123" });

        // Act
        var run = orchestrator.Start(wf, triggeredBy: "test");

        // Wait for the agent dispatch to register, then auto-complete it.
        await WaitUntil(() => runner.LastDispatch is not null, TimeSpan.FromSeconds(30));
        var outputDir = runner.LastDispatch!.EnvOverrides[WorkflowRunPaths.OutputDirEnvVar];
        Directory.CreateDirectory(outputDir);
        await File.WriteAllTextAsync(Path.Combine(outputDir, "output.md"), "# Useful result\n\nActionable output.");

        runner.CompleteCurrent(AgentRunStatus.Completed, exitCode: 0);
        await WaitUntil(() => run.Status is WorkflowRunStatus.Succeeded or WorkflowRunStatus.Failed, TimeSpan.FromSeconds(30));

        // Assert — the runner saw exactly the args + env we expected.
        run.Status.Should().Be(WorkflowRunStatus.Succeeded);
        runner.LastDispatch.Should().NotBeNull();
        runner.LastDispatch!.AgentId.Should().Be("fake-agent");
        runner.LastDispatch.Arguments.Should().StartWith("do-thing --target production");
        runner.LastDispatch.Arguments.Should().Contain("DoxieOS workflow artifact contract");
        runner.LastDispatch.Arguments.Should().Contain("DOXIE_WORKFLOW_OUTPUT_DIR");
        runner.LastDispatch.EnvOverrides.Should().ContainKey("MY_TOKEN").WhoseValue.Should().Be("abc123");
        runner.LastDispatch.EnvOverrides.Should().ContainKey(WorkflowRunPaths.OutputDirEnvVar);
        runner.LastDispatch.EnvOverrides[WorkflowRunPaths.OutputDirEnvVar]
            .Should().Be(WorkflowRunPaths.NodeDir(_runsDir, run.Id, "step"));

        // The per-node output folder keeps a manifest that later workflow
        // steps and humans can inspect.
        Directory.Exists(WorkflowRunPaths.NodeDir(_runsDir, run.Id, "step")).Should().BeTrue();
        var manifest = await File.ReadAllTextAsync(Path.Combine(outputDir, ".doxie-artifact.json"));
        manifest.Should().Contain("\"schema\": \"doxie.output-artifact.v1\"");
        manifest.Should().Contain("\"producerId\": \"fake-agent\"");
        manifest.Should().Contain("\"kind\": \"workflow-agent-node\"");
        manifest.Should().Contain("\"output.md\"");
    }

    [Fact]
    public async Task Agent_node_materializes_stdout_when_agent_writes_no_files()
    {
        var catalog = new FakeCatalog(new AgentDescriptor(
            Id: "chatty-agent",
            Name: "Chatty Agent",
            Description: "test",
            SkillName: "chatty-agent",
            Category: AgentCategory.Builder,
            Modes: new[] { new AgentMode("answer", "Answer", "", "answer") }));

        var runner = new FakeAgentRunner();
        var orchestrator = new OrchestratedWorkflowRunner(
            runStore: new InMemoryWorkflowRunStore(),
            workspaceStore: new EmptyWorkspaceStore(),
            agentCatalog: catalog,
            agentRunner: runner,
            runsDirectory: _runsDir);

        var wf = new WorkflowDefinition(
            Id: "stdout-wf",
            Name: "Stdout WF",
            Description: "",
            Trigger: new WorkflowTrigger(WorkflowTriggerKind.Manual),
            Nodes: new[]
            {
                new WorkflowNode("trigger", WorkflowNodeKind.Trigger, "Manual", 0, 0),
                new WorkflowNode("step", WorkflowNodeKind.Agent, "Step", 0, 0,
                    AgentId: "chatty-agent",
                    AgentMode: "answer"),
            },
            Edges: new[] { new WorkflowEdge("trigger", "step") },
            CreatedAt: DateTime.UtcNow,
            UpdatedAt: DateTime.UtcNow);

        var run = orchestrator.Start(wf, triggeredBy: "test");

        await WaitUntil(() => runner.LastDispatch is not null, TimeSpan.FromSeconds(30));
        runner.EmitStdout("# Weekly Agenda Snapshot\n\nMonday has a confirmed conflict.");
        runner.EmitStderr("diagnostic noise");
        runner.CompleteCurrent(AgentRunStatus.Completed, exitCode: 0);
        await WaitUntil(() => run.Status is WorkflowRunStatus.Succeeded or WorkflowRunStatus.Failed, TimeSpan.FromSeconds(30));

        run.Status.Should().Be(WorkflowRunStatus.Succeeded);
        var outputDir = WorkflowRunPaths.NodeDir(_runsDir, run.Id, "step");
        var output = await File.ReadAllTextAsync(Path.Combine(outputDir, "output.md"));
        output.Should().Contain("Monday has a confirmed conflict.");
        output.Should().NotContain("diagnostic noise");

        var manifest = await File.ReadAllTextAsync(Path.Combine(outputDir, ".doxie-artifact.json"));
        manifest.Should().Contain("\"output.md\"");
    }

    [Fact]
    public async Task Agent_node_materializes_diagnostic_when_agent_produces_no_artifact_or_stdout()
    {
        var catalog = new FakeCatalog(new AgentDescriptor(
            Id: "quiet-agent",
            Name: "Quiet Agent",
            Description: "test",
            SkillName: "quiet-agent",
            Category: AgentCategory.Builder,
            Modes: new[] { new AgentMode("run", "Run", "", "run") }));

        var runner = new FakeAgentRunner();
        var orchestrator = new OrchestratedWorkflowRunner(
            runStore: new InMemoryWorkflowRunStore(),
            workspaceStore: new EmptyWorkspaceStore(),
            agentCatalog: catalog,
            agentRunner: runner,
            runsDirectory: _runsDir);

        var wf = new WorkflowDefinition(
            Id: "quiet-wf",
            Name: "Quiet WF",
            Description: "",
            Trigger: new WorkflowTrigger(WorkflowTriggerKind.Manual),
            Nodes: new[]
            {
                new WorkflowNode("trigger", WorkflowNodeKind.Trigger, "Manual", 0, 0),
                new WorkflowNode("step", WorkflowNodeKind.Agent, "Step", 0, 0,
                    AgentId: "quiet-agent",
                    AgentMode: "run"),
            },
            Edges: new[] { new WorkflowEdge("trigger", "step") },
            CreatedAt: DateTime.UtcNow,
            UpdatedAt: DateTime.UtcNow);

        var run = orchestrator.Start(wf, triggeredBy: "test");

        await WaitUntil(() => runner.LastDispatch is not null, TimeSpan.FromSeconds(30));
        runner.CompleteCurrent(AgentRunStatus.Completed, exitCode: 0);
        await WaitUntil(() => run.Status is WorkflowRunStatus.Succeeded or WorkflowRunStatus.Failed, TimeSpan.FromSeconds(30));

        run.Status.Should().Be(WorkflowRunStatus.Succeeded);
        var outputDir = WorkflowRunPaths.NodeDir(_runsDir, run.Id, "step");
        var output = await File.ReadAllTextAsync(Path.Combine(outputDir, "output.md"));
        output.Should().Contain("did not produce an indexable artifact");

        var manifest = await File.ReadAllTextAsync(Path.Combine(outputDir, ".doxie-artifact.json"));
        manifest.Should().Contain("\"output.md\"");
    }

    [Fact]
    public async Task StartFrom_reuses_upstream_artifacts_and_dispatches_selected_node_with_guidance()
    {
        var firstAgent = new AgentDescriptor(
            Id: "first-agent",
            Name: "First Agent",
            Description: "test",
            SkillName: "first-agent",
            Category: AgentCategory.Builder,
            Modes: new[] { new AgentMode("run", "Run", "", "run") });
        var secondAgent = new AgentDescriptor(
            Id: "second-agent",
            Name: "Second Agent",
            Description: "test",
            SkillName: "second-agent",
            Category: AgentCategory.Builder,
            Modes: new[] { new AgentMode("run", "Run", "", "run") });
        var runner = new FakeAgentRunner();
        var runStore = new InMemoryWorkflowRunStore();
        var orchestrator = new OrchestratedWorkflowRunner(
            runStore,
            new EmptyWorkspaceStore(),
            new MultiFakeCatalog(firstAgent, secondAgent),
            runner,
            _runsDir);

        var wf = new WorkflowDefinition(
            Id: "rerun-wf",
            Name: "Rerun WF",
            Description: "",
            Trigger: new WorkflowTrigger(WorkflowTriggerKind.Manual),
            Nodes: new[]
            {
                new WorkflowNode("trigger", WorkflowNodeKind.Trigger, "Manual", 0, 0),
                new WorkflowNode("first", WorkflowNodeKind.Agent, "First", 0, 0,
                    AgentId: "first-agent",
                    AgentMode: "run"),
                new WorkflowNode("second", WorkflowNodeKind.Agent, "Second", 0, 0,
                    AgentId: "second-agent",
                    AgentMode: "run"),
            },
            Edges: new[]
            {
                new WorkflowEdge("trigger", "first"),
                new WorkflowEdge("first", "second"),
            },
            CreatedAt: DateTime.UtcNow,
            UpdatedAt: DateTime.UtcNow);

        var sourceRun = orchestrator.Start(wf, "test");
        await WaitUntil(() => runner.Dispatches.Count == 1, TimeSpan.FromSeconds(30));
        var firstOutputDir = runner.LastDispatch!.EnvOverrides[WorkflowRunPaths.OutputDirEnvVar];
        Directory.CreateDirectory(firstOutputDir);
        await File.WriteAllTextAsync(Path.Combine(firstOutputDir, "first.md"), "upstream artifact");
        runner.CompleteCurrent(AgentRunStatus.Completed, exitCode: 0);

        await WaitUntil(() => runner.Dispatches.Count == 2, TimeSpan.FromSeconds(30));
        runner.CompleteCurrent(AgentRunStatus.Completed, exitCode: 0);
        await WaitUntil(() => sourceRun.Status is WorkflowRunStatus.Succeeded or WorkflowRunStatus.Failed, TimeSpan.FromSeconds(30));
        sourceRun.Status.Should().Be(WorkflowRunStatus.Succeeded);

        var rerun = orchestrator.StartFrom(wf, sourceRun.Id, "second", "retry with the corrected parser");

        await WaitUntil(() => runner.Dispatches.Count == 3, TimeSpan.FromSeconds(30));
        runner.LastDispatch!.AgentId.Should().Be("second-agent");
        runner.LastDispatch.Arguments.Should().Contain("retry with the corrected parser");

        var reusedDir = WorkflowRunPaths.NodeDir(_runsDir, rerun.Id, "first");
        File.Exists(Path.Combine(reusedDir, "first.md")).Should().BeTrue();
        runner.LastDispatch.EnvOverrides[WorkflowRunPaths.InputDirsEnvVar]
            .Should().Contain(reusedDir);
        rerun.NodeRun("first").Logs.Should().Contain(log => log.Contains("[rerun-from] reused artifacts"));

        runner.CompleteCurrent(AgentRunStatus.Completed, exitCode: 0);
        await WaitUntil(() => rerun.Status is WorkflowRunStatus.Succeeded or WorkflowRunStatus.Failed, TimeSpan.FromSeconds(30));
        rerun.Status.Should().Be(WorkflowRunStatus.Succeeded);
    }

    [Fact]
    public async Task Breaking_stories_node_recovers_artifacts_from_standalone_output_folder()
    {
        var workspaceRoot = Path.Combine(_runsDir, "workspace");
        var runsRoot = Path.Combine(workspaceRoot, ".runs");
        Directory.CreateDirectory(runsRoot);

        var catalog = new FakeCatalog(new AgentDescriptor(
            Id: "breaking-stories",
            Name: "Breaking Stories",
            Description: "gate",
            SkillName: "breaking-stories",
            Category: AgentCategory.Inspector,
            Modes: new[] { new AgentMode("gate", "Gate", "", "gate") }));

        var runner = new FakeAgentRunner();
        var orchestrator = new OrchestratedWorkflowRunner(
            runStore: new InMemoryWorkflowRunStore(),
            workspaceStore: new EmptyWorkspaceStore(),
            agentCatalog: catalog,
            agentRunner: runner,
            runsDirectory: runsRoot);

        var wf = new WorkflowDefinition(
            Id: "breaking-wf",
            Name: "Breaking WF",
            Description: "",
            Trigger: new WorkflowTrigger(WorkflowTriggerKind.Manual),
            Nodes: new[]
            {
                new WorkflowNode("trigger", WorkflowNodeKind.Trigger, "Manual", 0, 0),
                new WorkflowNode("breaking", WorkflowNodeKind.Agent, "Breaking", 0, 0,
                    AgentId: "breaking-stories",
                    AgentMode: "gate"),
            },
            Edges: new[] { new WorkflowEdge("trigger", "breaking") },
            CreatedAt: DateTime.UtcNow,
            UpdatedAt: DateTime.UtcNow);

        var run = orchestrator.Start(wf, triggeredBy: "test");
        await WaitUntil(() => runner.LastDispatch is not null, TimeSpan.FromSeconds(30));

        var standaloneDir = Path.Combine(workspaceRoot, ".breaking-stories-runs", "2026-05-20-WI3940545-validation");
        Directory.CreateDirectory(standaloneDir);
        await File.WriteAllTextAsync(
            Path.Combine(standaloneDir, ".doxie-gate.json"),
            "{\"status\":\"override-ready\",\"reason\":\"existing child stories are ready\"}");
        await File.WriteAllTextAsync(
            Path.Combine(standaloneDir, "breaking-stories-report.md"),
            "# Breaking stories\n\nStatus: override-ready\n");

        runner.CompleteCurrent(AgentRunStatus.Completed, exitCode: 0);
        await WaitUntil(() => run.Status is WorkflowRunStatus.Succeeded or WorkflowRunStatus.Failed, TimeSpan.FromSeconds(30));

        run.Status.Should().Be(WorkflowRunStatus.Succeeded);
        var outputDir = WorkflowRunPaths.NodeDir(runsRoot, run.Id, "breaking");
        File.Exists(Path.Combine(outputDir, ".doxie-gate.json")).Should().BeTrue();
        File.Exists(Path.Combine(outputDir, "breaking-stories-report.md")).Should().BeTrue();
        run.NodeRun("breaking").Logs.Should().Contain(log => log.Contains("[output guardrail] recovered"));
    }

    [Fact]
    public async Task Output_node_writes_indexable_artifact_manifest_to_workspace()
    {
        var workspacePath = Path.Combine(_runsDir, "workspace-output");
        Directory.CreateDirectory(workspacePath);
        var workspace = new Workspace(
            Id: "deliveries",
            Name: "Deliveries",
            Description: "",
            CreatedAt: DateTime.UtcNow,
            MountedLibraryIds: Array.Empty<string>(),
            Path: workspacePath);

        var orchestrator = new OrchestratedWorkflowRunner(
            new InMemoryWorkflowRunStore(),
            new StaticWorkspaceStore(workspace),
            new EmptyCatalog(),
            new FakeAgentRunner(),
            _runsDir);

        var wf = new WorkflowDefinition(
            Id: "deliver-wf",
            Name: "Deliver WF",
            Description: "",
            Trigger: new WorkflowTrigger(WorkflowTriggerKind.Manual),
            Nodes: new[]
            {
                new WorkflowNode("trigger", WorkflowNodeKind.Trigger, "Manual", 0, 0),
                new WorkflowNode("output", WorkflowNodeKind.Output, "Deliver", 0, 0,
                    OutputWorkspaceId: "deliveries",
                    OutputFileName: "daily-brief.md"),
            },
            Edges: new[] { new WorkflowEdge("trigger", "output") },
            CreatedAt: DateTime.UtcNow,
            UpdatedAt: DateTime.UtcNow);

        var run = orchestrator.Start(wf, triggeredBy: "test");
        await WaitUntil(() => run.Status is WorkflowRunStatus.Succeeded or WorkflowRunStatus.Failed, TimeSpan.FromSeconds(30));

        run.Status.Should().Be(WorkflowRunStatus.Succeeded);
        File.Exists(Path.Combine(workspacePath, "daily-brief.md")).Should().BeTrue();

        var manifest = await File.ReadAllTextAsync(Path.Combine(workspacePath, ".doxie-artifact.json"));
        manifest.Should().Contain("\"schema\": \"doxie.output-artifact.v1\"");
        manifest.Should().Contain("\"producerId\": \"deliver-wf\"");
        manifest.Should().Contain("\"kind\": \"workflow-output\"");
        manifest.Should().Contain("\"daily-brief.md\"");
    }

    [Fact]
    public async Task Workflow_workspace_is_default_cwd_and_memory_context_for_agent_nodes()
    {
        var workspacePath = Path.Combine(_runsDir, "workspace-alpha");
        Directory.CreateDirectory(workspacePath);
        var workspace = new Workspace(
            Id: "alpha",
            Name: "Alpha",
            Description: "",
            CreatedAt: DateTime.UtcNow,
            MountedLibraryIds: Array.Empty<string>(),
            Path: workspacePath);
        var workspaces = new StaticWorkspaceStore(workspace);
        var agent = new AgentDescriptor(
            "fake-agent", "Fake", "", "fake-agent", AgentCategory.Builder,
            Modes: new[] { new AgentMode("default", "Default", "", "default") });
        var runner = new FakeAgentRunner();
        var orchestrator = new OrchestratedWorkflowRunner(
            new InMemoryWorkflowRunStore(),
            workspaces,
            new MultiFakeCatalog(agent),
            runner,
            _runsDir);

        var wf = new WorkflowDefinition(
            Id: "workspace-wf", Name: "Workspace WF", Description: "",
            Trigger: new WorkflowTrigger(WorkflowTriggerKind.Manual),
            Nodes: new[]
            {
                new WorkflowNode("trigger", WorkflowNodeKind.Trigger, "T", 0, 0),
                new WorkflowNode("step", WorkflowNodeKind.Agent, "S", 0, 0, AgentId: "fake-agent", AgentMode: "default"),
            },
            Edges: new[] { new WorkflowEdge("trigger", "step") },
            CreatedAt: DateTime.UtcNow,
            UpdatedAt: DateTime.UtcNow,
            WorkspaceId: "alpha");

        var run = orchestrator.Start(wf, "test");
        await WaitUntil(() => runner.LastDispatch is not null, TimeSpan.FromSeconds(30));

        runner.LastDispatch!.WorkingDirectoryOverride.Should().Be(workspacePath);
        runner.LastDispatch.EnvOverrides.Should().ContainKey(WorkflowRunPaths.WorkspaceIdEnvVar).WhoseValue.Should().Be("alpha");
        runner.LastDispatch.EnvOverrides.Should().ContainKey(WorkflowRunPaths.WorkspacePathEnvVar).WhoseValue.Should().Be(workspacePath);

        runner.CompleteCurrent(AgentRunStatus.Completed, exitCode: 0);
        await WaitUntil(() => run.Status is WorkflowRunStatus.Succeeded or WorkflowRunStatus.Failed, TimeSpan.FromSeconds(30));
        run.Status.Should().Be(WorkflowRunStatus.Succeeded);
    }

    [Fact]
    public async Task Node_workspace_can_be_selected_from_trigger_input()
    {
        var workspacePath = Path.Combine(_runsDir, "workspace-selected");
        Directory.CreateDirectory(workspacePath);
        var workspace = new Workspace(
            Id: "selected-project",
            Name: "Selected Project",
            Description: "",
            CreatedAt: DateTime.UtcNow,
            MountedLibraryIds: Array.Empty<string>(),
            Path: workspacePath);
        var agent = new AgentDescriptor(
            "fake-agent", "Fake", "", "fake-agent", AgentCategory.Builder,
            Modes: new[] { new AgentMode("default", "Default", "", "default") });
        var runner = new FakeAgentRunner();
        var orchestrator = new OrchestratedWorkflowRunner(
            new InMemoryWorkflowRunStore(),
            new StaticWorkspaceStore(workspace),
            new MultiFakeCatalog(agent),
            runner,
            _runsDir);

        var wf = new WorkflowDefinition(
            Id: "dynamic-workspace-wf", Name: "Dynamic Workspace WF", Description: "",
            Trigger: new WorkflowTrigger(
                WorkflowTriggerKind.Manual,
                Inputs: new[] { new WorkflowTriggerInputField("workspace-id", "Workspace", "", true) }),
            Nodes: new[]
            {
                new WorkflowNode("trigger", WorkflowNodeKind.Trigger, "T", 0, 0),
                new WorkflowNode("step", WorkflowNodeKind.Agent, "S", 0, 0,
                    AgentId: "fake-agent",
                    AgentMode: "default",
                    WorkspaceId: "{{trigger.workspace-id}}"),
            },
            Edges: new[] { new WorkflowEdge("trigger", "step") },
            CreatedAt: DateTime.UtcNow,
            UpdatedAt: DateTime.UtcNow);

        var run = orchestrator.Start(
            wf,
            triggeredBy: "test",
            triggerInputs: new Dictionary<string, string> { ["workspace-id"] = "selected-project" });
        await WaitUntil(() => runner.LastDispatch is not null, TimeSpan.FromSeconds(30));

        runner.LastDispatch!.WorkingDirectoryOverride.Should().Be(workspacePath);
        runner.LastDispatch.EnvOverrides.Should().ContainKey(WorkflowRunPaths.WorkspaceIdEnvVar).WhoseValue.Should().Be("selected-project");
        runner.LastDispatch.EnvOverrides.Should().ContainKey(WorkflowRunPaths.WorkspacePathEnvVar).WhoseValue.Should().Be(workspacePath);

        runner.CompleteCurrent(AgentRunStatus.Completed, exitCode: 0);
        await WaitUntil(() => run.Status is WorkflowRunStatus.Succeeded or WorkflowRunStatus.Failed, TimeSpan.FromSeconds(30));
        run.Status.Should().Be(WorkflowRunStatus.Succeeded);
    }

    [Fact]
    public async Task Custom_catalog_workflow_dispatch_marks_catalog_definition_root()
    {
        var customCatalogRoot = Path.Combine(_runsDir, "shared-team-catalog");
        var defaultWorkflowsRoot = Path.Combine(_runsDir, "default-workflows");
        var storage = new StorageOptions
        {
            WorkflowsDirectory = defaultWorkflowsRoot,
        };
        var catalogStore = new StaticDoxieCatalogStore(new[]
        {
            new DoxieCatalogRoot(
                "default",
                "Default catalog",
                _runsDir,
                IsDefault: true,
                WorkflowsPath: defaultWorkflowsRoot),
            new DoxieCatalogRoot(
                "shared-team",
                "Shared team",
                customCatalogRoot),
        });
        var agent = new AgentDescriptor(
            "fake-agent", "Fake", "", "fake-agent", AgentCategory.Builder,
            Modes: new[] { new AgentMode("default", "Default", "", "default") });
        var runner = new FakeAgentRunner();
        var orchestrator = new OrchestratedWorkflowRunner(
            new InMemoryWorkflowRunStore(),
            new EmptyWorkspaceStore(),
            new MultiFakeCatalog(agent),
            runner,
            _runsDir,
            storage,
            catalogStore);

        var wf = new WorkflowDefinition(
            Id: "custom-flow", Name: "Custom Flow", Description: "",
            Trigger: new WorkflowTrigger(WorkflowTriggerKind.Manual),
            Nodes: new[]
            {
                new WorkflowNode("trigger", WorkflowNodeKind.Trigger, "T", 0, 0),
                new WorkflowNode("step", WorkflowNodeKind.Agent, "S", 0, 0, AgentId: "fake-agent", AgentMode: "default"),
            },
            Edges: new[] { new WorkflowEdge("trigger", "step") },
            CreatedAt: DateTime.UtcNow,
            UpdatedAt: DateTime.UtcNow,
            CatalogId: "shared-team");

        var run = orchestrator.Start(wf, "test");
        await WaitUntil(() => runner.LastDispatch is not null, TimeSpan.FromSeconds(30));

        runner.LastDispatch!.EnvOverrides.Should().ContainKey(WorkflowRunPaths.WorkflowIdEnvVar).WhoseValue.Should().Be("custom-flow");
        runner.LastDispatch.EnvOverrides.Should().NotContainKey("DOXIE_WORKFLOW_IS_PRIVATE");
        runner.LastDispatch.EnvOverrides.Should().ContainKey(WorkflowRunPaths.WorkflowRootEnvVar)
            .WhoseValue.Should().Be(Path.Combine(customCatalogRoot, "workflows", "custom-flow"));

        runner.CompleteCurrent(AgentRunStatus.Completed, exitCode: 0);
        await WaitUntil(() => run.Status is WorkflowRunStatus.Succeeded or WorkflowRunStatus.Failed, TimeSpan.FromSeconds(30));
        run.Status.Should().Be(WorkflowRunStatus.Succeeded);
    }

    [Fact]
    public async Task Agent_failure_propagates_as_workflow_failure()
    {
        var catalog = new FakeCatalog(new AgentDescriptor(
            "fake-agent", "Fake", "", "fake-agent", AgentCategory.Builder,
            Modes: new[] { new AgentMode("default", "Default", "", "default") }));
        var runner = new FakeAgentRunner();
        var orchestrator = new OrchestratedWorkflowRunner(
            new InMemoryWorkflowRunStore(),
            new EmptyWorkspaceStore(),
            catalog,
            runner,
            _runsDir);

        var wf = new WorkflowDefinition(
            Id: "fail-wf", Name: "Fail", Description: "",
            Trigger: new WorkflowTrigger(WorkflowTriggerKind.Manual),
            Nodes: new[]
            {
                new WorkflowNode("trigger", WorkflowNodeKind.Trigger, "T", 0, 0),
                new WorkflowNode("step", WorkflowNodeKind.Agent, "S", 0, 0, AgentId: "fake-agent", AgentMode: "default"),
            },
            Edges: new[] { new WorkflowEdge("trigger", "step") },
            CreatedAt: DateTime.UtcNow, UpdatedAt: DateTime.UtcNow);

        var run = orchestrator.Start(wf, "test");
        await WaitUntil(() => runner.LastDispatch is not null, TimeSpan.FromSeconds(30));
        runner.CompleteCurrent(AgentRunStatus.Failed, exitCode: 1);
        await WaitUntil(() => run.Status is WorkflowRunStatus.Succeeded or WorkflowRunStatus.Failed, TimeSpan.FromSeconds(30));

        run.Status.Should().Be(WorkflowRunStatus.Failed);
        run.NodeRun("step").Status.Should().Be(WorkflowNodeRunStatus.Failed);
    }

    [Fact]
    public async Task Approval_gate_node_pauses_run_until_resumed()
    {
        // Trigger â†’ approval-gate. No FakeAgentRunner dispatch happens
        // for the gate (the runner intercepts before catalog lookup).
        var orchestrator = BuildOrchestratorWithEmptyCatalog();

        var wf = MinimalWorkflowWithGate(promptText: "Approve deploy?");

        var run = orchestrator.Start(wf, "test");

        // Pause settles within a few ticks; allow generous slack for CI.
        await WaitUntil(
            () => run.Status == WorkflowRunStatus.AwaitingApproval
                  && run.NodeRun("gate").Status == WorkflowNodeRunStatus.AwaitingApproval,
            TimeSpan.FromSeconds(30));

        run.NodeRun("gate").Logs.Should().Contain(l => l.Contains("Approve deploy?"));
        run.FinishedAt.Should().BeNull("the run is parked, not finished");
    }

    [Fact]
    public async Task Approve_resumes_the_run_to_Succeeded()
    {
        var orchestrator = BuildOrchestratorWithEmptyCatalog();
        var wf = MinimalWorkflowWithGate();
        var run = orchestrator.Start(wf, "test");
        await WaitUntil(() => run.Status == WorkflowRunStatus.AwaitingApproval, TimeSpan.FromSeconds(30));

        var result = orchestrator.ResumeApprovalGate(run.Id, "gate", approve: true, comment: "looks good");
        result.Should().Be(ApprovalGateResolveResult.Resolved);

        await WaitUntil(() => run.Status is WorkflowRunStatus.Succeeded or WorkflowRunStatus.Failed, TimeSpan.FromSeconds(30));

        run.Status.Should().Be(WorkflowRunStatus.Succeeded);
        run.NodeRun("gate").Status.Should().Be(WorkflowNodeRunStatus.Succeeded);
        run.NodeRun("gate").Logs.Should().Contain(l => l.Contains("approved") && l.Contains("looks good"));
        run.NodeRun("gate").OutputSummary.Should().Be("approved");
    }

    [Fact]
    public async Task Reject_lands_run_as_Cancelled_and_captures_reason()
    {
        var orchestrator = BuildOrchestratorWithEmptyCatalog();
        var wf = MinimalWorkflowWithGate();
        var run = orchestrator.Start(wf, "test");
        await WaitUntil(() => run.Status == WorkflowRunStatus.AwaitingApproval, TimeSpan.FromSeconds(30));

        var result = orchestrator.ResumeApprovalGate(run.Id, "gate", approve: false, comment: "needs more review");
        result.Should().Be(ApprovalGateResolveResult.Resolved);

        await WaitUntil(() => run.Status is WorkflowRunStatus.Succeeded
                                          or WorkflowRunStatus.Failed
                                          or WorkflowRunStatus.Cancelled, TimeSpan.FromSeconds(30));

        run.Status.Should().Be(WorkflowRunStatus.Cancelled);
        run.NodeRun("gate").Status.Should().Be(WorkflowNodeRunStatus.Failed);
        run.NodeRun("gate").Logs.Should().Contain(l => l.Contains("rejected") && l.Contains("needs more review"));
        run.NodeRun("gate").OutputSummary.Should().Contain("rejected");
    }

    [Fact]
    public void ResumeApprovalGate_returns_RunNotFound_for_unknown_run()
    {
        var orchestrator = BuildOrchestratorWithEmptyCatalog();
        var result = orchestrator.ResumeApprovalGate("does-not-exist", "gate", approve: true, comment: null);
        result.Should().Be(ApprovalGateResolveResult.RunNotFound);
    }

    [Fact]
    public void ResumeApprovalGate_zombie_run_after_restart_force_cancels()
    {
        // Simulate the MVP restart caveat: a run is hydrated from
        // SQLite with status=AwaitingApproval but no live TCS in the
        // runner's _pendingApprovals dict (the previous orchestrator
        // process had it; this new instance lost it on boot). Click
        // on Approve / Reject must not return 409 NotAwaitingApproval
        // — that would leave the user staring at a dead button. The
        // contract here: any resolve click on a zombie maps to
        // explicit cancel + Resolved, with the reason captured in the
        // node logs.
        var orchestrator = BuildOrchestratorWithEmptyCatalog();
        var store = (InMemoryWorkflowRunStore)typeof(OrchestratedWorkflowRunner)
            .GetField("_runStore", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(orchestrator)!;

        // Hand-craft a run already in the parked state — bypassing the
        // runner's Start path entirely so no TCS is registered.
        var nodeRuns = new Dictionary<string, WorkflowNodeRun>(StringComparer.OrdinalIgnoreCase)
        {
            ["trigger"] = WorkflowNodeRun.Hydrate("trigger", WorkflowNodeRunStatus.Succeeded, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(-1), null, Array.Empty<string>()),
            ["gate"] = WorkflowNodeRun.Hydrate("gate", WorkflowNodeRunStatus.AwaitingApproval, DateTimeOffset.UtcNow.AddMinutes(-1), null, null, new[] { "[approval-gate] paused — waiting for human decision" }),
        };
        var zombie = WorkflowRun.Hydrate(
            id: "zombie-run-id",
            workflowId: "any",
            triggeredBy: "manual",
            startedAt: DateTimeOffset.UtcNow.AddMinutes(-1),
            finishedAt: null,
            status: WorkflowRunStatus.AwaitingApproval,
            nodeRuns: nodeRuns);
        store.Add(zombie);

        var result = orchestrator.ResumeApprovalGate(
            zombie.Id, "gate", approve: false, comment: "block this");

        result.Should().Be(ApprovalGateResolveResult.Resolved,
            "zombie clicks force-cancel rather than returning 409 — keeps the UI unblocked");
        zombie.Status.Should().Be(WorkflowRunStatus.Cancelled);
        zombie.NodeRun("gate").Status.Should().Be(WorkflowNodeRunStatus.Failed);
        zombie.NodeRun("gate").Logs.Should().Contain(l => l.Contains("in-process await was lost") && l.Contains("block this"));
    }

    [Fact]
    public async Task ResumeApprovalGate_returns_NotAwaiting_when_already_resolved()
    {
        var orchestrator = BuildOrchestratorWithEmptyCatalog();
        var wf = MinimalWorkflowWithGate();
        var run = orchestrator.Start(wf, "test");
        await WaitUntil(() => run.Status == WorkflowRunStatus.AwaitingApproval, TimeSpan.FromSeconds(30));

        orchestrator.ResumeApprovalGate(run.Id, "gate", approve: true, comment: null)
            .Should().Be(ApprovalGateResolveResult.Resolved);
        // Second call after resolution finds no parked TCS.
        orchestrator.ResumeApprovalGate(run.Id, "gate", approve: true, comment: null)
            .Should().Be(ApprovalGateResolveResult.NotAwaitingApproval);
    }

    private OrchestratedWorkflowRunner BuildOrchestratorWithEmptyCatalog()
    {
        // The approval-gate path in the runner intercepts before any
        // catalog lookup, so the empty catalog is fine. FakeAgentRunner
        // is supplied because the constructor requires it; it's never
        // exercised by the gate path.
        var emptyCatalog = new EmptyCatalog();
        var emptyRunner = new FakeAgentRunner();
        var workflowRuns = new InMemoryWorkflowRunStore();
        var workspaces = new EmptyWorkspaceStore();
        return new OrchestratedWorkflowRunner(
            runStore: workflowRuns,
            workspaceStore: workspaces,
            agentCatalog: emptyCatalog,
            agentRunner: emptyRunner,
            runsDirectory: _runsDir);
    }

    private static WorkflowDefinition MinimalWorkflowWithGate(string? promptText = null)
    {
        var inputs = promptText is null
            ? null
            : new Dictionary<string, string> { ["prompt"] = promptText };
        return new WorkflowDefinition(
            Id: "gate-wf",
            Name: "Gate WF",
            Description: "",
            Trigger: new WorkflowTrigger(WorkflowTriggerKind.Manual),
            Nodes: new[]
            {
                new WorkflowNode("trigger", WorkflowNodeKind.Trigger, "T", 0, 0),
                new WorkflowNode("gate", WorkflowNodeKind.Agent, "Gate", 0, 0,
                    AgentId: "approval-gate",
                    AgentMode: "wait",
                    Inputs: inputs),
            },
            Edges: new[] { new WorkflowEdge("trigger", "gate") },
            CreatedAt: DateTime.UtcNow,
            UpdatedAt: DateTime.UtcNow);
    }

    private sealed class EmptyCatalog : IAgentCatalog
    {
        public IReadOnlyList<AgentDescriptor> GetAll() => Array.Empty<AgentDescriptor>();
        public AgentDescriptor? FindById(string id) => null;
        public void Refresh() { }
        public string? ReadSkillBody(string skillName, string? subcommand = null) => null;
    }

    [Fact]
    public async Task Trigger_input_substitutes_into_node_inputs_at_dispatch()
    {
        // Single-step workflow whose node.Inputs reference
        // {{trigger.work-item-id}}. The runner is started with that
        // trigger input set to "654321"; the FakeAgentRunner records
        // the dispatch and we verify the substituted value flowed all
        // the way through to the agent's arguments.
        var agent = new AgentDescriptor(
            "fake-agent", "Fake", "", "fake-agent", AgentCategory.Builder,
            Modes: new[]
            {
                new AgentMode(
                    Id: "implement",
                    Name: "Implement",
                    Description: "",
                    ArgumentsTemplate: "implement --work-item-id {work-item-id}",
                    Fields: new[] { new AgentModeField(Id: "work-item-id", Label: "WI", Placeholder: string.Empty, Required: true) }),
            });
        var runner = new FakeAgentRunner();
        var orchestrator = new OrchestratedWorkflowRunner(
            new InMemoryWorkflowRunStore(),
            new EmptyWorkspaceStore(),
            new MultiFakeCatalog(agent),
            runner,
            _runsDir);

        var wf = new WorkflowDefinition(
            Id: "trigger-input-wf", Name: "T", Description: "",
            Trigger: new WorkflowTrigger(
                WorkflowTriggerKind.Manual,
                Inputs: new[]
                {
                    new WorkflowTriggerInputField("work-item-id", "Work Item ID", null, true),
                }),
            Nodes: new[]
            {
                new WorkflowNode("trigger", WorkflowNodeKind.Trigger, "T", 0, 0),
                new WorkflowNode("step", WorkflowNodeKind.Agent, "Step", 0, 0,
                    AgentId: "fake-agent",
                    AgentMode: "implement",
                    Inputs: new Dictionary<string, string> { ["work-item-id"] = "{{trigger.work-item-id}}" }),
            },
            Edges: new[] { new WorkflowEdge("trigger", "step") },
            CreatedAt: DateTime.UtcNow, UpdatedAt: DateTime.UtcNow);

        var run = orchestrator.Start(
            wf,
            triggeredBy: "test",
            triggerInputs: new Dictionary<string, string> { ["work-item-id"] = "654321" });

        await WaitUntil(() => runner.LastDispatch is not null, TimeSpan.FromSeconds(30));
        runner.LastDispatch!.Arguments.Should().StartWith("implement --work-item-id 654321",
            "{{trigger.work-item-id}} must be replaced by the supplied value before dispatch");
        runner.LastDispatch.Arguments.Should().Contain("DoxieOS workflow artifact contract");

        runner.CompleteCurrent(AgentRunStatus.Completed, exitCode: 0);
        await WaitUntil(() => run.Status is WorkflowRunStatus.Succeeded or WorkflowRunStatus.Failed, TimeSpan.FromSeconds(30));
        run.Status.Should().Be(WorkflowRunStatus.Succeeded);

        // The run's TriggerInputs are also persisted (not just consumed
        // and discarded) so the dashboard can display them later.
        run.TriggerInputs.Should().ContainKey("work-item-id").WhoseValue.Should().Be("654321");
    }

    [Fact]
    public async Task Trigger_input_missing_substitutes_empty_string()
    {
        // Workflow declares a placeholder but the run is started with
        // no triggerInputs — the substitution drops the placeholder
        // (empty string) so the agent dispatches without crashing the
        // run. Easier to debug the half-filled form in the resulting
        // logs than to fail the run hard.
        var agent = new AgentDescriptor(
            "fake-agent", "Fake", "", "fake-agent", AgentCategory.Builder,
            Modes: new[]
            {
                new AgentMode("default", "D", "", "default --x \"{x}\"",
                    Fields: new[] { new AgentModeField("x", "X", string.Empty, false) }),
            });
        var runner = new FakeAgentRunner();
        var orchestrator = new OrchestratedWorkflowRunner(
            new InMemoryWorkflowRunStore(),
            new EmptyWorkspaceStore(),
            new MultiFakeCatalog(agent),
            runner,
            _runsDir);

        var wf = new WorkflowDefinition(
            Id: "no-input-wf", Name: "N", Description: "",
            Trigger: new WorkflowTrigger(WorkflowTriggerKind.Manual),
            Nodes: new[]
            {
                new WorkflowNode("trigger", WorkflowNodeKind.Trigger, "T", 0, 0),
                new WorkflowNode("step", WorkflowNodeKind.Agent, "S", 0, 0,
                    AgentId: "fake-agent",
                    AgentMode: "default",
                    Inputs: new Dictionary<string, string> { ["x"] = "{{trigger.unset}}" }),
            },
            Edges: new[] { new WorkflowEdge("trigger", "step") },
            CreatedAt: DateTime.UtcNow, UpdatedAt: DateTime.UtcNow);

        var run = orchestrator.Start(wf, "test");

        await WaitUntil(() => runner.LastDispatch is not null, TimeSpan.FromSeconds(30));
        runner.LastDispatch!.Arguments.Should().StartWith("default --x \"\"",
            "missing trigger inputs substitute empty string, not a literal placeholder");
        runner.LastDispatch.Arguments.Should().Contain("DoxieOS workflow artifact contract");

        runner.CompleteCurrent(AgentRunStatus.Completed, exitCode: 0);
        await WaitUntil(() => run.Status is WorkflowRunStatus.Succeeded or WorkflowRunStatus.Failed, TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task Trigger_input_lookup_is_case_insensitive()
    {
        // {{trigger.WorkItemId}} and "workitemid" key resolve.
        var agent = new AgentDescriptor(
            "fake-agent", "Fake", "", "fake-agent", AgentCategory.Builder,
            Modes: new[]
            {
                new AgentMode("default", "D", "", "default --id {id}",
                    Fields: new[] { new AgentModeField("id", "ID", string.Empty, true) }),
            });
        var runner = new FakeAgentRunner();
        var orchestrator = new OrchestratedWorkflowRunner(
            new InMemoryWorkflowRunStore(),
            new EmptyWorkspaceStore(),
            new MultiFakeCatalog(agent),
            runner,
            _runsDir);

        var wf = new WorkflowDefinition(
            Id: "case-wf", Name: "C", Description: "",
            Trigger: new WorkflowTrigger(WorkflowTriggerKind.Manual),
            Nodes: new[]
            {
                new WorkflowNode("trigger", WorkflowNodeKind.Trigger, "T", 0, 0),
                new WorkflowNode("step", WorkflowNodeKind.Agent, "S", 0, 0,
                    AgentId: "fake-agent",
                    AgentMode: "default",
                    Inputs: new Dictionary<string, string> { ["id"] = "{{trigger.WorkItemId}}" }),
            },
            Edges: new[] { new WorkflowEdge("trigger", "step") },
            CreatedAt: DateTime.UtcNow, UpdatedAt: DateTime.UtcNow);

        // Dictionary key uses different casing than the placeholder.
        var run = orchestrator.Start(
            wf,
            "test",
            new Dictionary<string, string> { ["workitemid"] = "999" });

        await WaitUntil(() => runner.LastDispatch is not null, TimeSpan.FromSeconds(30));
        runner.LastDispatch!.Arguments.Should().StartWith("default --id 999");
        runner.LastDispatch.Arguments.Should().Contain("DoxieOS workflow artifact contract");

        runner.CompleteCurrent(AgentRunStatus.Completed, exitCode: 0);
        await WaitUntil(() => run.Status is WorkflowRunStatus.Succeeded or WorkflowRunStatus.Failed, TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task Gate_pass_through_forwards_upstream_files_and_writes_comment_on_Approve()
    {
        // Three-node workflow: trigger â†’ analysis â†’ gate â†’ implementer.
        // The "analysis" agent is faked; we manually drop a plan.md into
        // its node dir before completing it, simulating what the real
        // technical-analysis would write to $DOXIE_WORKFLOW_OUTPUT_DIR.
        // Then we assert: (a) the gate's own dir contains the forwarded
        // plan.md plus an _approval-comment.md with the human's comment,
        // (b) the implementer's INPUT_DIRS includes the gate's dir.
        var analysisAgent = new AgentDescriptor(
            "analysis", "Analysis", "", "analysis", AgentCategory.Builder,
            Modes: new[] { new AgentMode("default", "Default", "", "default") });
        var implementerAgent = new AgentDescriptor(
            "implementer", "Implementer", "", "implementer", AgentCategory.Builder,
            Modes: new[] { new AgentMode("default", "Default", "", "default") });
        var catalog = new MultiFakeCatalog(analysisAgent, implementerAgent);
        var runner = new FakeAgentRunner();
        var orchestrator = new OrchestratedWorkflowRunner(
            new InMemoryWorkflowRunStore(),
            new EmptyWorkspaceStore(),
            catalog,
            runner,
            _runsDir);

        var wf = WorkflowWithAnalysisGateImplementer();
        var run = orchestrator.Start(wf, "test");

        // Phase 1 — analysis dispatches; we plant plan.md and complete.
        await WaitUntil(() => runner.LastDispatch?.AgentId == "analysis", TimeSpan.FromSeconds(30));
        var analysisDir = WorkflowRunPaths.NodeDir(_runsDir, run.Id, "analysis");
        Directory.CreateDirectory(analysisDir);
        await File.WriteAllTextAsync(Path.Combine(analysisDir, "plan.md"), "Plan body content");
        runner.CompleteCurrent(AgentRunStatus.Completed, exitCode: 0);

        // Phase 2 — run parks at the gate.
        await WaitUntil(() => run.Status == WorkflowRunStatus.AwaitingApproval, TimeSpan.FromSeconds(30));

        // Phase 3 — Approve with comment. Pass-through copies plan.md
        // forward and stamps _approval-comment.md.
        orchestrator.ResumeApprovalGate(run.Id, "gate", approve: true, comment: "Use FluentAssertions please")
            .Should().Be(ApprovalGateResolveResult.Resolved);

        // Phase 4 — implementer dispatches with the correct INPUT_DIRS.
        await WaitUntil(() => runner.LastDispatch?.AgentId == "implementer", TimeSpan.FromSeconds(30));

        // Assertions on the gate's dir.
        var gateDir = WorkflowRunPaths.NodeDir(_runsDir, run.Id, "gate");
        Directory.Exists(gateDir).Should().BeTrue();
        var forwardedPlanPath = Path.Combine(gateDir, "plan.md");
        File.Exists(forwardedPlanPath).Should().BeTrue("the gate must forward upstream files on Approve");
        (await File.ReadAllTextAsync(forwardedPlanPath)).Should().Be("Plan body content");
        var commentPath = Path.Combine(gateDir, "_approval-comment.md");
        File.Exists(commentPath).Should().BeTrue("the gate must stamp the approver's comment");
        (await File.ReadAllTextAsync(commentPath)).Should().Be("Use FluentAssertions please");

        // The implementer's INPUT_DIRS env var must point at the gate's
        // dir (its direct upstream), not the analysis dir.
        runner.LastDispatch!.EnvOverrides.Should().ContainKey(WorkflowRunPaths.InputDirsEnvVar);
        runner.LastDispatch.EnvOverrides[WorkflowRunPaths.InputDirsEnvVar]
            .Should().Contain(gateDir, "the gate is the implementer's direct upstream after pass-through");

        // Phase 5 — close out the implementer so the run finishes.
        runner.CompleteCurrent(AgentRunStatus.Completed, exitCode: 0);
        await WaitUntil(() => run.Status is WorkflowRunStatus.Succeeded or WorkflowRunStatus.Failed, TimeSpan.FromSeconds(30));
        run.Status.Should().Be(WorkflowRunStatus.Succeeded);
    }

    [Fact]
    public async Task Gate_does_not_forward_anything_when_Rejected()
    {
        // Same shape as above, but Reject — the implementer never
        // dispatches and the gate's dir holds no forwarded files.
        var analysisAgent = new AgentDescriptor(
            "analysis", "Analysis", "", "analysis", AgentCategory.Builder,
            Modes: new[] { new AgentMode("default", "Default", "", "default") });
        var implementerAgent = new AgentDescriptor(
            "implementer", "Implementer", "", "implementer", AgentCategory.Builder,
            Modes: new[] { new AgentMode("default", "Default", "", "default") });
        var catalog = new MultiFakeCatalog(analysisAgent, implementerAgent);
        var runner = new FakeAgentRunner();
        var orchestrator = new OrchestratedWorkflowRunner(
            new InMemoryWorkflowRunStore(),
            new EmptyWorkspaceStore(),
            catalog,
            runner,
            _runsDir);

        var wf = WorkflowWithAnalysisGateImplementer();
        var run = orchestrator.Start(wf, "test");

        await WaitUntil(() => runner.LastDispatch?.AgentId == "analysis", TimeSpan.FromSeconds(30));
        var analysisDir = WorkflowRunPaths.NodeDir(_runsDir, run.Id, "analysis");
        Directory.CreateDirectory(analysisDir);
        await File.WriteAllTextAsync(Path.Combine(analysisDir, "plan.md"), "should not be forwarded");
        runner.CompleteCurrent(AgentRunStatus.Completed, exitCode: 0);

        await WaitUntil(() => run.Status == WorkflowRunStatus.AwaitingApproval, TimeSpan.FromSeconds(30));

        orchestrator.ResumeApprovalGate(run.Id, "gate", approve: false, comment: "block")
            .Should().Be(ApprovalGateResolveResult.Resolved);

        await WaitUntil(() => run.Status is WorkflowRunStatus.Cancelled or WorkflowRunStatus.Failed, TimeSpan.FromSeconds(30));
        run.Status.Should().Be(WorkflowRunStatus.Cancelled);

        // Implementer must not have dispatched. Last dispatch is still
        // the analysis from before the gate.
        runner.LastDispatch!.AgentId.Should().Be("analysis");

        // Gate's dir must not contain the forwarded plan or comment file.
        var gateDir = WorkflowRunPaths.NodeDir(_runsDir, run.Id, "gate");
        File.Exists(Path.Combine(gateDir, "plan.md")).Should().BeFalse();
        File.Exists(Path.Combine(gateDir, "_approval-comment.md")).Should().BeFalse();
    }

    [Fact]
    public async Task Breaking_stories_gate_blocks_downstream_when_gate_json_blocks()
    {
        var breakingAgent = new AgentDescriptor(
            "breaking-stories", "Breaking Stories", "", "breaking-stories", AgentCategory.Developer,
            Modes: new[] { new AgentMode("gate", "Gate", "", "gate") });
        var deliverAgent = new AgentDescriptor(
            "feature-delivery-orchestrator", "Delivery", "", "feature-delivery-orchestrator", AgentCategory.Developer,
            Modes: new[] { new AgentMode("deliver", "Deliver", "", "deliver") });
        var catalog = new MultiFakeCatalog(breakingAgent, deliverAgent);
        var runner = new FakeAgentRunner();
        var orchestrator = new OrchestratedWorkflowRunner(
            new InMemoryWorkflowRunStore(),
            new EmptyWorkspaceStore(),
            catalog,
            runner,
            _runsDir);

        var wf = new WorkflowDefinition(
            Id: "breaking-before-delivery",
            Name: "Breaking before delivery",
            Description: "",
            Trigger: new WorkflowTrigger(WorkflowTriggerKind.Manual),
            Nodes: new[]
            {
                new WorkflowNode("trigger", WorkflowNodeKind.Trigger, "T", 0, 0),
                new WorkflowNode("breaking", WorkflowNodeKind.Agent, "Breaking", 0, 0,
                    AgentId: "breaking-stories",
                    AgentMode: "gate"),
                new WorkflowNode("deliver", WorkflowNodeKind.Agent, "Deliver", 0, 0,
                    AgentId: "feature-delivery-orchestrator",
                    AgentMode: "deliver"),
            },
            Edges: new[]
            {
                new WorkflowEdge("trigger", "breaking"),
                new WorkflowEdge("breaking", "deliver"),
            },
            CreatedAt: DateTime.UtcNow,
            UpdatedAt: DateTime.UtcNow);

        var run = orchestrator.Start(wf, "test");

        await WaitUntil(() => runner.LastDispatch?.AgentId == "breaking-stories", TimeSpan.FromSeconds(30));
        var outputDir = runner.LastDispatch!.EnvOverrides[WorkflowRunPaths.OutputDirEnvVar];
        Directory.CreateDirectory(outputDir);
        await File.WriteAllTextAsync(
            Path.Combine(outputDir, ".doxie-gate.json"),
            """{ "status": "block", "reason": "Feature sem Stories prontas." }""");

        runner.CompleteCurrent(AgentRunStatus.Completed, exitCode: 0);

        await WaitUntil(() => run.Status is WorkflowRunStatus.Succeeded or WorkflowRunStatus.Failed, TimeSpan.FromSeconds(30));

        run.Status.Should().Be(WorkflowRunStatus.Failed);
        run.NodeRun("breaking").Status.Should().Be(WorkflowNodeRunStatus.Failed);
        run.NodeRun("deliver").Status.Should().Be(WorkflowNodeRunStatus.Skipped);
        runner.LastDispatch.AgentId.Should().Be("breaking-stories");
    }

    [Fact]
    public async Task Breaking_stories_gate_can_continue_downstream_when_block_is_non_fatal()
    {
        var breakingAgent = new AgentDescriptor(
            "breaking-stories", "Breaking Stories", "", "breaking-stories", AgentCategory.Developer,
            Modes: new[] { new AgentMode("gate", "Gate", "", "gate") });
        var deliverAgent = new AgentDescriptor(
            "feature-delivery-orchestrator", "Delivery", "", "feature-delivery-orchestrator", AgentCategory.Developer,
            Modes: new[] { new AgentMode("deliver", "Deliver", "", "deliver") });
        var catalog = new MultiFakeCatalog(breakingAgent, deliverAgent);
        var runner = new FakeAgentRunner();
        var orchestrator = new OrchestratedWorkflowRunner(
            new InMemoryWorkflowRunStore(),
            new EmptyWorkspaceStore(),
            catalog,
            runner,
            _runsDir);

        var wf = new WorkflowDefinition(
            Id: "breaking-before-delivery",
            Name: "Breaking before delivery",
            Description: "",
            Trigger: new WorkflowTrigger(WorkflowTriggerKind.Manual),
            Nodes: new[]
            {
                new WorkflowNode("trigger", WorkflowNodeKind.Trigger, "T", 0, 0),
                new WorkflowNode("breaking", WorkflowNodeKind.Agent, "Breaking", 0, 0,
                    AgentId: "breaking-stories",
                    AgentMode: "gate",
                    Inputs: new Dictionary<string, string>
                    {
                        ["fail-on-breaking-block"] = "false",
                    }),
                new WorkflowNode("deliver", WorkflowNodeKind.Agent, "Deliver", 0, 0,
                    AgentId: "feature-delivery-orchestrator",
                    AgentMode: "deliver"),
            },
            Edges: new[]
            {
                new WorkflowEdge("trigger", "breaking"),
                new WorkflowEdge("breaking", "deliver"),
            },
            CreatedAt: DateTime.UtcNow,
            UpdatedAt: DateTime.UtcNow);

        var run = orchestrator.Start(wf, "test");

        await WaitUntil(() => runner.LastDispatch?.AgentId == "breaking-stories", TimeSpan.FromSeconds(30));
        var outputDir = runner.LastDispatch!.EnvOverrides[WorkflowRunPaths.OutputDirEnvVar];
        Directory.CreateDirectory(outputDir);
        await File.WriteAllTextAsync(
            Path.Combine(outputDir, ".doxie-gate.json"),
            """{ "status": "block", "reason": "Feature sem Stories prontas." }""");

        runner.CompleteCurrent(AgentRunStatus.Completed, exitCode: 0);

        await WaitUntil(() => runner.LastDispatch?.AgentId == "feature-delivery-orchestrator", TimeSpan.FromSeconds(30));

        run.NodeRun("breaking").Status.Should().Be(WorkflowNodeRunStatus.Succeeded);
        runner.LastDispatch!.AgentId.Should().Be("feature-delivery-orchestrator");
    }

    private static WorkflowDefinition WorkflowWithAnalysisGateImplementer()
    {
        return new WorkflowDefinition(
            Id: "analysis-gate-impl",
            Name: "Analysis â†’ Gate â†’ Implementer",
            Description: "",
            Trigger: new WorkflowTrigger(WorkflowTriggerKind.Manual),
            Nodes: new[]
            {
                new WorkflowNode("trigger", WorkflowNodeKind.Trigger, "T", 0, 0),
                new WorkflowNode("analysis", WorkflowNodeKind.Agent, "Analyze", 0, 0,
                    AgentId: "analysis",
                    AgentMode: "default"),
                new WorkflowNode("gate", WorkflowNodeKind.Agent, "Gate", 0, 0,
                    AgentId: "approval-gate",
                    AgentMode: "wait"),
                new WorkflowNode("implementer", WorkflowNodeKind.Agent, "Implement", 0, 0,
                    AgentId: "implementer",
                    AgentMode: "default"),
            },
            Edges: new[]
            {
                new WorkflowEdge("trigger", "analysis"),
                new WorkflowEdge("analysis", "gate"),
                new WorkflowEdge("gate", "implementer"),
            },
            CreatedAt: DateTime.UtcNow,
            UpdatedAt: DateTime.UtcNow);
    }

    private sealed class MultiFakeCatalog : IAgentCatalog
    {
        private readonly Dictionary<string, AgentDescriptor> _byId;

        public MultiFakeCatalog(params AgentDescriptor[] agents)
        {
            _byId = agents.ToDictionary(a => a.Id, StringComparer.OrdinalIgnoreCase);
        }

        public IReadOnlyList<AgentDescriptor> GetAll() => _byId.Values.ToList();

        public AgentDescriptor? FindById(string id) =>
            _byId.TryGetValue(id, out var a) ? a : null;

        public void Refresh() { }
        public string? ReadSkillBody(string skillName, string? subcommand = null) => null;
    }

    private static async Task WaitUntil(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (predicate()) return;
            await Task.Delay(20).ConfigureAwait(false);
        }
        throw new TimeoutException($"Predicate did not become true within {timeout.TotalSeconds:0.0}s");
    }

    /// <summary>Single-agent fake catalog.</summary>
    private sealed class FakeCatalog : IAgentCatalog
    {
        private readonly AgentDescriptor _agent;
        public FakeCatalog(AgentDescriptor agent) => _agent = agent;
        public IReadOnlyList<AgentDescriptor> GetAll() => new[] { _agent };
        public AgentDescriptor? FindById(string id) =>
            string.Equals(id, _agent.Id, StringComparison.OrdinalIgnoreCase) ? _agent : null;
        public void Refresh() { /* fixture is static — nothing to refresh */ }
        public string? ReadSkillBody(string skillName, string? subcommand = null) => null;
    }

    /// <summary>No-op workspace store — tests don't use workspace binding.</summary>
    private sealed class EmptyWorkspaceStore : IWorkspaceStore
    {
        public IReadOnlyList<Workspace> ListAll() => Array.Empty<Workspace>();
        public Workspace? GetById(string id) => null;
        public Workspace Create(string id, string? n, string? d, IReadOnlyList<string>? l = null) =>
            throw new NotSupportedException();
        public void Delete(string id) => throw new NotSupportedException();
        public Workspace SetMountedLibraries(string id, IReadOnlyList<string> ids) =>
            throw new NotSupportedException();
    }

    private sealed class StaticWorkspaceStore : IWorkspaceStore
    {
        private readonly Workspace _workspace;

        public StaticWorkspaceStore(Workspace workspace) => _workspace = workspace;

        public IReadOnlyList<Workspace> ListAll() => new[] { _workspace };

        public Workspace? GetById(string id) =>
            string.Equals(id, _workspace.Id, StringComparison.OrdinalIgnoreCase) ? _workspace : null;

        public Workspace Create(string id, string? n, string? d, IReadOnlyList<string>? l = null) =>
            throw new NotSupportedException();

        public void Delete(string id) => throw new NotSupportedException();

        public Workspace SetMountedLibraries(string id, IReadOnlyList<string> ids) =>
            throw new NotSupportedException();
    }

    private sealed class StaticDoxieCatalogStore : IDoxieCatalogStore
    {
        private readonly IReadOnlyList<DoxieCatalogRoot> _catalogs;

        public StaticDoxieCatalogStore(IReadOnlyList<DoxieCatalogRoot> catalogs) => _catalogs = catalogs;

        public IReadOnlyList<DoxieCatalogRoot> ListCatalogs() => _catalogs;

        public DoxieCatalogRoot? FindCatalog(string? catalogId)
        {
            var id = string.IsNullOrWhiteSpace(catalogId) ? "default" : catalogId;
            return _catalogs.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase));
        }

        public IReadOnlyList<CustomDoxieCatalog> GetCustomCatalogs() => Array.Empty<CustomDoxieCatalog>();

        public IReadOnlyList<CustomDoxieCatalog> SaveCustomCatalogs(IEnumerable<CustomDoxieCatalog> catalogs) =>
            throw new NotSupportedException();
    }

    /// <summary>
    /// Captures the most recent dispatch and lets the test drive the
    /// AgentRun's terminal state on demand. Mimics the real
    /// <c>ClaudeProcessAgentRunner</c>'s event semantics: <c>Start</c>
    /// returns immediately with a Queued/Running run; the test calls
    /// <c>CompleteCurrent</c> to flip it to a terminal status and
    /// invoke RunUpdated, which is what the workflow runner awaits.
    /// </summary>
    private sealed class FakeAgentRunner : IAgentRunner
    {
        // Tracks the most recent dispatched run by its identity fields
        // (the AgentRun reference is replaced on every status flip via
        // Hydrate, since AgentRun's setters are internal to the Agents
        // assembly).
        private string? _runId;
        private string? _agentId;
        private string? _arguments;
        private DateTimeOffset _startedAt;
        private AgentRun? _latestSnapshot;
        private readonly List<AgentRunOutputLine> _output = new();

        public DispatchRecord? LastDispatch { get; private set; }

        public List<DispatchRecord> Dispatches { get; } = new();

        public event Action<AgentRun>? RunUpdated;

        public AgentRun Start(
            string agentId,
            string arguments,
            bool keepSessionAlive = false,
            string? workingDirectoryOverride = null,
            IReadOnlyDictionary<string, string>? envOverrides = null,
            string? modeId = null,
            string? workspaceId = null,
            string? displayArguments = null)
        {
            LastDispatch = new DispatchRecord(
                agentId,
                arguments,
                keepSessionAlive,
                workingDirectoryOverride,
                envOverrides ?? new Dictionary<string, string>());
            Dispatches.Add(LastDispatch);

            _runId = Guid.NewGuid().ToString("N");
            _agentId = agentId;
            _arguments = arguments;
            _startedAt = DateTimeOffset.UtcNow;
            _output.Clear();
            _latestSnapshot = AgentRun.Hydrate(
                id: _runId,
                agentId: agentId,
                arguments: arguments,
                startedAt: _startedAt,
                status: AgentRunStatus.Running,
                finishedAt: null,
                exitCode: null,
                output: Array.Empty<AgentRunOutputLine>());
            RunUpdated?.Invoke(_latestSnapshot);
            return _latestSnapshot;
        }

        public void EmitStdout(string text) => Emit(AgentRunOutputSource.Stdout, text);

        public void EmitStderr(string text) => Emit(AgentRunOutputSource.Stderr, text);

        private void Emit(AgentRunOutputSource source, string text)
        {
            if (_runId is null) throw new InvalidOperationException("No dispatched run.");
            _output.Add(new AgentRunOutputLine(DateTimeOffset.UtcNow, source, text));
            _latestSnapshot = AgentRun.Hydrate(
                id: _runId,
                agentId: _agentId!,
                arguments: _arguments!,
                startedAt: _startedAt,
                status: AgentRunStatus.Running,
                finishedAt: null,
                exitCode: null,
                output: _output);
            RunUpdated?.Invoke(_latestSnapshot);
        }

        public void Cancel(string runId)
        {
            if (_runId is null || runId != _runId) return;
            CompleteCurrent(AgentRunStatus.Cancelled, exitCode: -1);
        }

        public AgentRun? Get(string runId) =>
            _runId == runId ? _latestSnapshot : null;

        public void CompleteCurrent(AgentRunStatus status, int exitCode)
        {
            if (_runId is null) throw new InvalidOperationException("No dispatched run.");
            _latestSnapshot = AgentRun.Hydrate(
                id: _runId,
                agentId: _agentId!,
                arguments: _arguments!,
                startedAt: _startedAt,
                status: status,
                finishedAt: DateTimeOffset.UtcNow,
                exitCode: exitCode,
                output: _output);
            RunUpdated?.Invoke(_latestSnapshot);
        }
    }

    private sealed record DispatchRecord(
        string AgentId,
        string Arguments,
        bool KeepSessionAlive,
        string? WorkingDirectoryOverride,
        IReadOnlyDictionary<string, string> EnvOverrides);
}
