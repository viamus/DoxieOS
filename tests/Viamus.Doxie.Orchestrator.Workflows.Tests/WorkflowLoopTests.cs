using System.Text.Json;
using FluentAssertions;
using Viamus.Doxie.Orchestrator.Agents;
using Viamus.Doxie.Orchestrator.Context;

namespace Viamus.Doxie.Orchestrator.Workflows.Tests;

/// <summary>
/// Coverage for the Loop / for-each primitive — body sub-graph
/// orchestration, env propagation, fail-fast vs continue, and the
/// per-iteration NodeRun decomposition that surfaces in the UI.
/// </summary>
public sealed class WorkflowLoopTests : IDisposable
{
    private readonly string _runsDir;

    public WorkflowLoopTests()
    {
        _runsDir = Path.Combine(Path.GetTempPath(), $"doxie-loop-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_runsDir);
    }

    public void Dispose()
    {
        if (!Directory.Exists(_runsDir)) return;
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try { Directory.Delete(_runsDir, recursive: true); return; }
            catch (IOException) when (attempt < 9) { Thread.Sleep(100); }
            catch (UnauthorizedAccessException) when (attempt < 9) { Thread.Sleep(100); }
        }
    }

    [Fact]
    public async Task Empty_body_emits_empty_results_json()
    {
        // No body nodes (no node has LoopId) — loop is degenerate.
        var orchestrator = NewOrchestrator(out var runner);

        var wf = new WorkflowDefinition(
            Id: "wf-empty-body",
            Name: "Empty Body",
            Description: "",
            Trigger: new WorkflowTrigger(WorkflowTriggerKind.Manual),
            Nodes: new[]
            {
                NewNode("trigger", WorkflowNodeKind.Trigger),
                LoopNode("loop"),
            },
            Edges: new[] { new WorkflowEdge("trigger", "loop") },
            CreatedAt: DateTime.UtcNow, UpdatedAt: DateTime.UtcNow);

        var run = orchestrator.Start(wf, "test");
        await WaitForTerminal(run);

        run.Status.Should().Be(WorkflowRunStatus.Succeeded);
        run.NodeRun("loop").Status.Should().Be(WorkflowNodeRunStatus.Succeeded);
        run.NodeRun("loop").OutputSummary.Should().Be("loop: empty body");

        var resultsPath = Path.Combine(WorkflowRunPaths.NodeDir(_runsDir, run.Id, "loop"), "results.json");
        File.Exists(resultsPath).Should().BeTrue();
        File.ReadAllText(resultsPath).Trim().Should().Be("[]");
    }

    [Fact]
    public async Task Empty_array_runs_zero_iterations()
    {
        // Body exists, but no upstream produced result.json — runner
        // treats array as empty and writes [] without dispatching body.
        var orchestrator = NewOrchestrator(out var runner);

        var wf = WorkflowWithSingleNodeBody();

        var run = orchestrator.Start(wf, "test");
        await WaitForTerminal(run);

        run.Status.Should().Be(WorkflowRunStatus.Succeeded);
        runner.Dispatches.Should().BeEmpty("body should not have been dispatched for an empty array");
        run.NodeRun("body-step").Status.Should().Be(WorkflowNodeRunStatus.Skipped,
            "body never ran — its NR should reflect that");

        var resultsPath = Path.Combine(WorkflowRunPaths.NodeDir(_runsDir, run.Id, "loop"), "results.json");
        File.ReadAllText(resultsPath).Trim().Should().Be("[]");
    }

    [Fact]
    public async Task Single_node_body_runs_once_per_array_element()
    {
        var orchestrator = NewOrchestrator(out var runner);
        var wf = WorkflowWithSingleNodeBody();

        var run = orchestrator.Start(wf, "test");
        // Stage upstream array — runs in the ~150ms trigger-delay window
        // before the loop tries to read the file.
        await StageUpstreamArrayAsync(wf, run.Id, "trigger",
            JsonSerializer.Serialize(new[] { "a", "b", "c" }));

        await WaitForTerminal(run);

        run.Status.Should().Be(WorkflowRunStatus.Succeeded);
        runner.Dispatches.Count.Should().Be(3, "body dispatched once per array element");

        // Per-iteration NodeRuns visible.
        run.NodeRuns.Keys.Should().Contain(new[] { "body-step#iter-0", "body-step#iter-1", "body-step#iter-2" });
        run.NodeRun("body-step#iter-0").Status.Should().Be(WorkflowNodeRunStatus.Succeeded);

        // Body original NR aggregates.
        run.NodeRun("body-step").Status.Should().Be(WorkflowNodeRunStatus.Succeeded);
        run.NodeRun("body-step").OutputSummary.Should().Contain("3/3");

        // Loop's results.json carries 3 entries.
        var resultsPath = Path.Combine(WorkflowRunPaths.NodeDir(_runsDir, run.Id, "loop"), "results.json");
        var resultsJson = await File.ReadAllTextAsync(resultsPath);
        using var doc = JsonDocument.Parse(resultsJson);
        doc.RootElement.GetArrayLength().Should().Be(3);
    }

    [Fact]
    public async Task Body_subprocess_receives_LOOP_ITEM_INDEX_TOTAL_env()
    {
        var orchestrator = NewOrchestrator(out var runner);
        var wf = WorkflowWithSingleNodeBody();

        var run = orchestrator.Start(wf, "test");
        await StageUpstreamArrayAsync(wf, run.Id, "trigger",
            JsonSerializer.Serialize(new[] { 100, 200 }));

        await WaitForTerminal(run);

        runner.Dispatches.Count.Should().Be(2);

        var dispatch0 = runner.Dispatches[0];
        dispatch0.EnvOverrides.Should().ContainKey(WorkflowRunPaths.LoopIndexEnvVar);
        dispatch0.EnvOverrides[WorkflowRunPaths.LoopIndexEnvVar].Should().Be("0");
        dispatch0.EnvOverrides[WorkflowRunPaths.LoopTotalEnvVar].Should().Be("2");
        dispatch0.EnvOverrides[WorkflowRunPaths.LoopItemEnvVar].Should().Be("100");

        var dispatch1 = runner.Dispatches[1];
        dispatch1.EnvOverrides[WorkflowRunPaths.LoopIndexEnvVar].Should().Be("1");
        dispatch1.EnvOverrides[WorkflowRunPaths.LoopItemEnvVar].Should().Be("200");

        // The body's INPUT_DIRS points at _loop-input/ where item.json lives.
        dispatch0.EnvOverrides.Should().ContainKey(WorkflowRunPaths.InputDirsEnvVar);
        var inputDirs = dispatch0.EnvOverrides[WorkflowRunPaths.InputDirsEnvVar].Split(';');
        inputDirs.Should().Contain(d => d.EndsWith("_loop-input"));

        // item.json was written.
        var itemPath = Path.Combine(
            WorkflowRunPaths.IterationInputDir(_runsDir, run.Id, "loop", 0),
            "item.json");
        File.Exists(itemPath).Should().BeTrue();
        File.ReadAllText(itemPath).Should().Be("100");
    }

    [Fact]
    public async Task Multi_node_body_chain_runs_in_dep_order_per_iteration()
    {
        // Body: step-a â†’ step-b. Both run per iteration.
        var orchestrator = NewOrchestrator(out var runner);

        var wf = new WorkflowDefinition(
            Id: "wf-multi-body",
            Name: "Multi", Description: "",
            Trigger: new WorkflowTrigger(WorkflowTriggerKind.Manual),
            Nodes: new[]
            {
                NewNode("trigger", WorkflowNodeKind.Trigger),
                LoopNode("loop"),
                NewAgent("step-a", loopId: "loop"),
                NewAgent("step-b", loopId: "loop"),
                NewAgent("after"),
            },
            Edges: new[]
            {
                new WorkflowEdge("trigger", "loop"),
                new WorkflowEdge("loop", "step-a"),
                new WorkflowEdge("step-a", "step-b"),
                new WorkflowEdge("step-b", "after"),
            },
            CreatedAt: DateTime.UtcNow, UpdatedAt: DateTime.UtcNow);

        var run = orchestrator.Start(wf, "test");
        await StageUpstreamArrayAsync(wf, run.Id, "trigger",
            JsonSerializer.Serialize(new[] { 1, 2 }));

        await WaitForTerminal(run);

        run.Status.Should().Be(WorkflowRunStatus.Succeeded);

        // 2 iterations Ã— 2 body nodes + 1 'after' node = 5 dispatches.
        runner.Dispatches.Count.Should().Be(5);

        // Each iteration registered both body nodes.
        run.NodeRuns.Keys.Should().Contain("step-a#iter-0").And.Contain("step-b#iter-0");
        run.NodeRuns.Keys.Should().Contain("step-a#iter-1").And.Contain("step-b#iter-1");

        // step-b sees step-a's iteration-scoped output dir.
        var stepBDispatches = runner.Dispatches.Where(d => d.AgentId == "step-b").ToList();
        stepBDispatches.Should().HaveCount(2);
        var stepBIter0Inputs = stepBDispatches[0].EnvOverrides[WorkflowRunPaths.InputDirsEnvVar];
        stepBIter0Inputs.Should().Contain("step-a");
        stepBIter0Inputs.Should().Contain("iter-0");
    }

    [Fact]
    public async Task Fail_fast_aborts_remaining_iterations_and_fails_loop()
    {
        // First iteration fails â†’ fail-fast cancels rest.
        // We use a runner that fails any dispatch whose LOOP_ITEM == "bad".
        var runner = new AutoCompletingAgentRunner(d =>
            d.EnvOverrides.TryGetValue(WorkflowRunPaths.LoopItemEnvVar, out var v) && v == "\"bad\""
                ? AgentRunStatus.Failed
                : AgentRunStatus.Completed);
        var orchestrator = BuildOrchestrator(runner);

        var wf = WorkflowWithSingleNodeBody();
        var run = orchestrator.Start(wf, "test");
        await StageUpstreamArrayAsync(wf, run.Id, "trigger",
            JsonSerializer.Serialize(new[] { "bad", "good", "also-good" }));

        await WaitForTerminal(run);

        run.Status.Should().Be(WorkflowRunStatus.Failed);
        run.NodeRun("loop").Status.Should().Be(WorkflowNodeRunStatus.Failed);
        run.NodeRun("loop").Logs.Should().Contain(l => l.Contains("fail-fast"));
    }

    [Fact]
    public async Task Continue_mode_excludes_failed_iterations_from_results()
    {
        // Same scenario but on_failure=continue â†’ loop succeeds with
        // failed iterations excluded from results.json.
        var runner = new AutoCompletingAgentRunner(d =>
            d.EnvOverrides.TryGetValue(WorkflowRunPaths.LoopItemEnvVar, out var v) && v == "\"bad\""
                ? AgentRunStatus.Failed
                : AgentRunStatus.Completed);
        var orchestrator = BuildOrchestrator(runner);

        var wf = WorkflowWithSingleNodeBody(onFailure: "continue");
        var run = orchestrator.Start(wf, "test");
        await StageUpstreamArrayAsync(wf, run.Id, "trigger",
            JsonSerializer.Serialize(new[] { "good-1", "bad", "good-2" }));

        await WaitForTerminal(run);

        run.Status.Should().Be(WorkflowRunStatus.Succeeded,
            "continue mode lets the loop succeed despite a failed iteration");
        run.NodeRun("loop").OutputSummary.Should().Contain("2/3");

        var resultsPath = Path.Combine(WorkflowRunPaths.NodeDir(_runsDir, run.Id, "loop"), "results.json");
        var resultsJson = await File.ReadAllTextAsync(resultsPath);
        using var doc = JsonDocument.Parse(resultsJson);
        doc.RootElement.GetArrayLength().Should().Be(2,
            "results.json should only contain the 2 successful iterations");
    }

    [Fact]
    public async Task Body_kind_must_be_Agent_otherwise_iteration_fails()
    {
        // A body node tagged as Aggregate (not Agent) violates V1's
        // body-must-be-agent rule. Iteration logic catches it and the
        // loop fails fast.
        var orchestrator = NewOrchestrator(out _);

        var wf = new WorkflowDefinition(
            Id: "wf-bad-body-kind",
            Name: "Bad", Description: "",
            Trigger: new WorkflowTrigger(WorkflowTriggerKind.Manual),
            Nodes: new[]
            {
                NewNode("trigger", WorkflowNodeKind.Trigger),
                LoopNode("loop"),
                // Body has Kind=Aggregate — not allowed in V1.
                new WorkflowNode("body-agg", WorkflowNodeKind.Aggregate, "Bad", 0, 0,
                    LoopId: "loop"),
            },
            Edges: new[]
            {
                new WorkflowEdge("trigger", "loop"),
                new WorkflowEdge("loop", "body-agg"),
            },
            CreatedAt: DateTime.UtcNow, UpdatedAt: DateTime.UtcNow);

        var run = orchestrator.Start(wf, "test");
        await StageUpstreamArrayAsync(wf, run.Id, "trigger",
            JsonSerializer.Serialize(new[] { 1 }));

        await WaitForTerminal(run);

        run.Status.Should().Be(WorkflowRunStatus.Failed);
        run.NodeRun("loop").Status.Should().Be(WorkflowNodeRunStatus.Failed);
    }

    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€ Helpers â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    private OrchestratedWorkflowRunner NewOrchestrator(out AutoCompletingAgentRunner runner)
    {
        runner = new AutoCompletingAgentRunner();
        return BuildOrchestrator(runner);
    }

    private OrchestratedWorkflowRunner BuildOrchestrator(IAgentRunner runner)
    {
        var catalog = new MultiAgentCatalog(
            FakeAgentDescriptor("body-step"),
            FakeAgentDescriptor("step-a"),
            FakeAgentDescriptor("step-b"),
            FakeAgentDescriptor("after"));
        return new OrchestratedWorkflowRunner(
            runStore: new InMemoryWorkflowRunStore(),
            workspaceStore: new EmptyWorkspaceStore(),
            agentCatalog: catalog,
            agentRunner: runner,
            runsDirectory: _runsDir);
    }

    private WorkflowDefinition WorkflowWithSingleNodeBody(string onFailure = "fail-fast") =>
        new WorkflowDefinition(
            Id: "wf-single-body",
            Name: "Single", Description: "",
            Trigger: new WorkflowTrigger(WorkflowTriggerKind.Manual),
            Nodes: new[]
            {
                NewNode("trigger", WorkflowNodeKind.Trigger),
                LoopNode("loop", onFailure: onFailure),
                NewAgent("body-step", loopId: "loop"),
            },
            Edges: new[]
            {
                new WorkflowEdge("trigger", "loop"),
                new WorkflowEdge("loop", "body-step"),
            },
            CreatedAt: DateTime.UtcNow, UpdatedAt: DateTime.UtcNow);

    /// <summary>
    /// Plants <c>result.json</c> in the trigger's output folder so the
    /// loop can find an upstream array. The trigger node is in-process
    /// (no subprocess writes the file) so we have to fake it.
    /// </summary>
    private async Task StageUpstreamArrayAsync(WorkflowDefinition wf, string runId, string upstreamNodeId, string arrayJson)
    {
        var dir = WorkflowRunPaths.NodeDir(_runsDir, runId, upstreamNodeId);
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "result.json"), arrayJson);
    }

    private async Task WaitForTerminal(WorkflowRun run)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (run.Status is WorkflowRunStatus.Succeeded
                           or WorkflowRunStatus.Failed
                           or WorkflowRunStatus.Cancelled)
            {
                return;
            }
            await Task.Delay(20);
        }
        throw new TimeoutException($"Run did not reach terminal state — last status: {run.Status}");
    }

    private static WorkflowNode NewNode(string id, WorkflowNodeKind kind) =>
        new WorkflowNode(id, kind, id, 0, 0);

    private static WorkflowNode NewAgent(string id, string? loopId = null) =>
        new WorkflowNode(id, WorkflowNodeKind.Agent, id, 0, 0,
            AgentId: id, AgentMode: "default", LoopId: loopId);

    private static WorkflowNode LoopNode(string id, string onFailure = "fail-fast") =>
        new WorkflowNode(id, WorkflowNodeKind.Agent, id, 0, 0,
            AgentId: "loop",
            AgentMode: "iterate",
            Inputs: new Dictionary<string, string>
            {
                ["array_source"] = "result.json",
                ["concurrency"] = "1",
                ["on_failure"] = onFailure,
            });

    private static AgentDescriptor FakeAgentDescriptor(string id) =>
        new AgentDescriptor(id, id, "", id, AgentCategory.Builder,
            Modes: new[] { new AgentMode("default", "Default", "", "default") });

    private sealed class MultiAgentCatalog : IAgentCatalog
    {
        private readonly Dictionary<string, AgentDescriptor> _byId;
        public MultiAgentCatalog(params AgentDescriptor[] agents) =>
            _byId = agents.ToDictionary(a => a.Id, StringComparer.OrdinalIgnoreCase);
        public IReadOnlyList<AgentDescriptor> GetAll() => _byId.Values.ToList();
        public AgentDescriptor? FindById(string id) => _byId.TryGetValue(id, out var a) ? a : null;
        public void Refresh() { }
        public string? ReadSkillBody(string skillName, string? subcommand = null) => null;
    }

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

    /// <summary>
    /// Auto-completes every dispatched run on a tiny delay (so the
    /// orchestrator has time to subscribe + capture the run id).
    /// Status per dispatch is decided by an optional callback —
    /// default Completed. Tracks every dispatch in a thread-safe list
    /// so loop tests can assert on per-iteration env / args.
    /// </summary>
    private sealed class AutoCompletingAgentRunner : IAgentRunner
    {
        private readonly Func<DispatchRecord, AgentRunStatus> _statusFor;
        private readonly List<DispatchRecord> _dispatches = new();
        private readonly object _lock = new();

        public IReadOnlyList<DispatchRecord> Dispatches
        {
            get { lock (_lock) return _dispatches.ToList(); }
        }

        public event Action<AgentRun>? RunUpdated;

        public AutoCompletingAgentRunner(Func<DispatchRecord, AgentRunStatus>? statusFor = null)
        {
            _statusFor = statusFor ?? (_ => AgentRunStatus.Completed);
        }

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
            var record = new DispatchRecord(
                agentId, arguments, keepSessionAlive, workingDirectoryOverride,
                envOverrides ?? new Dictionary<string, string>());
            lock (_lock) { _dispatches.Add(record); }

            var runId = Guid.NewGuid().ToString("N");
            var startedAt = DateTimeOffset.UtcNow;
            var running = AgentRun.Hydrate(
                id: runId, agentId: agentId, arguments: arguments,
                startedAt: startedAt, status: AgentRunStatus.Running,
                finishedAt: null, exitCode: null,
                output: Array.Empty<AgentRunOutputLine>());
            RunUpdated?.Invoke(running);

            // Fire terminal event asynchronously so the orchestrator's
            // OnAgentUpdated handler is fully wired (agentRunId captured)
            // by the time the matching update arrives.
            _ = Task.Run(async () =>
            {
                await Task.Delay(20).ConfigureAwait(false);
                var status = _statusFor(record);
                var done = AgentRun.Hydrate(
                    id: runId, agentId: agentId, arguments: arguments,
                    startedAt: startedAt, status: status,
                    finishedAt: DateTimeOffset.UtcNow,
                    exitCode: status == AgentRunStatus.Completed ? 0 : 1,
                    output: Array.Empty<AgentRunOutputLine>());
                RunUpdated?.Invoke(done);
            });

            return running;
        }

        public void Cancel(string runId) { /* no-op for tests */ }
        public AgentRun? Get(string runId) => null;
    }

    public sealed record DispatchRecord(
        string AgentId,
        string Arguments,
        bool KeepSessionAlive,
        string? WorkingDirectoryOverride,
        IReadOnlyDictionary<string, string> EnvOverrides);
}
