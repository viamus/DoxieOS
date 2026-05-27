using System.Collections.Concurrent;
using System.Text.Json;
using Viamus.Doxie.Orchestrator.Agents;
using Viamus.Doxie.Orchestrator.Agents.Doxie;
using Viamus.Doxie.Orchestrator.Context;

namespace Viamus.Doxie.Orchestrator.Workflows;

/// <summary>
/// Workflow engine that orchestrates real agent dispatches via
/// <see cref="IAgentRunner"/>. Each node spawns a Task that awaits its
/// upstream dependencies' completion gates before doing any work — so
/// nodes whose deps all live at depth N start in parallel as soon as
/// depth N-1 finishes.
///
/// <para>Per-kind execution:
/// <list type="bullet">
/// <item><b>Trigger</b> — synthetic "fired" log line; no subprocess. Pure entry point.</item>
/// <item><b>Agent</b> — dispatched through <c>IAgentRunner</c>. The runner
///   pre-creates the per-node output folder, builds <c>envOverrides</c>
///   carrying <c>DOXIE_WORKFLOW_OUTPUT_DIR</c> + the workflow's <c>Env</c>,
///   chooses the cwd from the bound workspace (if any), and awaits the
///   subprocess via a TaskCompletionSource hooked into
///   <c>RunUpdated</c>. Every stdout/stderr line from the agent is
///   mirrored into <see cref="WorkflowNodeRun.Logs"/> as it arrives.</item>
/// <item><b>Aggregate</b> — in-process workflow primitive. Walks the
///   direct upstream nodes' folders (per <see cref="WorkflowRunPaths"/>),
///   concatenates every file into <c>merged.md</c> inside its own folder.
///   Logs <c>DOXIE_WORKFLOW_INPUT_DIRS</c> for transparency.</item>
/// <item><b>Output</b> — in-process workflow primitive. Composes the
///   delivered file from upstream node folders' content and writes it
///   into the bound workspace.</item>
/// </list></para>
///
/// <para>Concurrency: each run executes on a Task.Run continuation.
/// Multiple concurrent runs of the same workflow are supported (each
/// gets its own WorkflowRun instance). Cancel is cooperative — the
/// CancellationToken propagates into every node's wait, and any
/// in-flight agent dispatch is forwarded a Cancel through
/// <see cref="IAgentRunner.Cancel"/>.</para>
/// </summary>
public sealed partial class OrchestratedWorkflowRunner : IWorkflowRunner
{
    private readonly IWorkflowRunStore _runStore;
    private readonly IWorkspaceStore _workspaceStore;
    private readonly IAgentCatalog _agentCatalog;
    private readonly IAgentRunner _agentRunner;
    private readonly string _runsDirectory;
    private readonly StorageOptions _storage;
    private readonly IDoxieCatalogStore? _catalogStore;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _runCancellation = new();
    private readonly ConcurrentDictionary<string, ConcurrentQueue<string>> _operatorHintsByRunId = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, string>> _activeAgentRunsByWorkflowRunId = new();

    // Per-(runId, nodeId) parking lot for nodes parked at an
    // approval-gate connector. The TCS is set by ResumeApprovalGate
    // when the human clicks Approve / Reject; the gate's Task is
    // awaiting it. MVP: lives only in-process — orchestrator restart
    // drops parked runs, see SKILL.md caveat.
    private readonly ConcurrentDictionary<(string RunId, string NodeId), TaskCompletionSource<ApprovalDecision>> _pendingApprovals = new();

    // Well-known agent id that maps to the human-in-the-loop pause
    // primitive. Like aggregate / write-to-workspace, the runner
    // intercepts dispatch before it would spawn a subprocess.
    private const string ApprovalGateAgentId = "approval-gate";

    private readonly record struct ApprovalDecision(bool Approve, string? Comment);

    private sealed record RerunFromOptions(
        string SourceRunId,
        string RequestedNodeId,
        string StartNodeId,
        string? Guidance,
        WorkflowRun SourceRun,
        IReadOnlySet<string> NodesToExecute);

    public OrchestratedWorkflowRunner(
        IWorkflowRunStore runStore,
        IWorkspaceStore workspaceStore,
        IAgentCatalog agentCatalog,
        IAgentRunner agentRunner,
        string runsDirectory,
        StorageOptions? storage = null,
        IDoxieCatalogStore? catalogStore = null)
    {
        _runStore = runStore;
        _workspaceStore = workspaceStore;
        _agentCatalog = agentCatalog;
        _agentRunner = agentRunner;
        _runsDirectory = runsDirectory;
        _storage = storage ?? new StorageOptions();
        _catalogStore = catalogStore;
    }

    public event Action<WorkflowRun>? RunUpdated;

    public WorkflowRun Start(
        WorkflowDefinition definition,
        string triggeredBy,
        IReadOnlyDictionary<string, string>? triggerInputs = null)
    {
        if (!definition.Enabled)
        {
            // Disabled workflows refuse Start outright. Callers (the
            // REST endpoint, the future cron daemon) translate this
            // into a 409 / "skipped — disabled" log line.
            throw new InvalidOperationException(
                $"Workflow '{definition.Id}' is disabled.");
        }

        var runId = Guid.NewGuid().ToString("N").Substring(0, 12);
        // Normalise to case-insensitive lookup so {{trigger.WorkItemId}}
        // and {{trigger.workitemid}} resolve identically — matches the
        // tolerance the rest of the orchestrator extends to env var keys.
        var normalisedInputs = triggerInputs is null
            ? null
            : new Dictionary<string, string>(triggerInputs, StringComparer.OrdinalIgnoreCase);
        var run = new WorkflowRun(
            id: runId,
            workflowId: definition.Id,
            triggeredBy: triggeredBy,
            startedAt: DateTimeOffset.UtcNow,
            nodeIds: definition.Nodes.Select(n => n.Id),
            triggerInputs: normalisedInputs);

        _runStore.Add(run);

        var cts = new CancellationTokenSource();
        _runCancellation[runId] = cts;

        _ = Task.Run(() => ExecuteAsync(definition, run, cts.Token));

        return run;
    }

    public WorkflowRun StartFrom(
        WorkflowDefinition definition,
        string sourceRunId,
        string nodeId,
        string? guidance = null)
    {
        if (!definition.Enabled)
        {
            throw new InvalidOperationException(
                $"Workflow '{definition.Id}' is disabled.");
        }

        var sourceRun = _runStore.Get(sourceRunId)
            ?? throw new InvalidOperationException($"Workflow run '{sourceRunId}' was not found.");
        if (!string.Equals(sourceRun.WorkflowId, definition.Id, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Run '{sourceRunId}' belongs to workflow '{sourceRun.WorkflowId}', not '{definition.Id}'.");
        }

        var requestedNode = definition.Nodes.FirstOrDefault(n =>
            string.Equals(n.Id, nodeId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Workflow node '{nodeId}' was not found.");
        var startNodeId = string.IsNullOrWhiteSpace(requestedNode.LoopId)
            ? requestedNode.Id
            : requestedNode.LoopId!;
        var nodesToExecute = FindDownstreamNodeIds(definition, startNodeId);

        var triggerInputs = new Dictionary<string, string>(sourceRun.TriggerInputs, StringComparer.OrdinalIgnoreCase);
        var runId = Guid.NewGuid().ToString("N").Substring(0, 12);
        var run = new WorkflowRun(
            id: runId,
            workflowId: definition.Id,
            triggeredBy: $"rerun:{sourceRunId}:{requestedNode.Id}",
            startedAt: DateTimeOffset.UtcNow,
            nodeIds: definition.Nodes.Select(n => n.Id),
            triggerInputs: triggerInputs);

        _runStore.Add(run);

        if (!string.IsNullOrWhiteSpace(guidance))
        {
            _operatorHintsByRunId
                .GetOrAdd(runId, _ => new ConcurrentQueue<string>())
                .Enqueue(NormalizeOperatorHint(guidance));
        }

        var cts = new CancellationTokenSource();
        _runCancellation[runId] = cts;

        var options = new RerunFromOptions(
            sourceRunId,
            requestedNode.Id,
            startNodeId,
            string.IsNullOrWhiteSpace(guidance) ? null : NormalizeOperatorHint(guidance),
            sourceRun,
            nodesToExecute);
        _ = Task.Run(() => ExecuteAsync(definition, run, cts.Token, options));

        return run;
    }

    public void Cancel(string runId)
    {
        if (_runCancellation.TryRemove(runId, out var cts))
        {
            cts.Cancel();
        }
    }

    public bool AddOperatorHint(string runId, string hint)
    {
        if (string.IsNullOrWhiteSpace(hint)) return false;
        var run = _runStore.Get(runId);
        if (run is null || IsTerminal(run.Status)) return false;

        var clean = NormalizeOperatorHint(hint);
        _operatorHintsByRunId
            .GetOrAdd(runId, _ => new ConcurrentQueue<string>())
            .Enqueue(clean);

        var forwarded = false;
        if (_activeAgentRunsByWorkflowRunId.TryGetValue(runId, out var activeAgents))
        {
            foreach (var agentRunId in activeAgents.Values.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                forwarded |= _agentRunner.AddOperatorHint(agentRunId, clean);
            }
        }

        var targets = run.NodeRuns.Values
            .Where(n => n.Status is WorkflowNodeRunStatus.Running or WorkflowNodeRunStatus.AwaitingApproval)
            .ToList();
        if (targets.Count == 0)
        {
            targets = run.NodeRuns.Values.Take(1).ToList();
        }

        foreach (var nr in targets)
        {
            nr.AppendLog($"[operator hint] {clean}");
            nr.AppendLog(forwarded
                ? "[orchestrator] hint forwarded to active agent run."
                : "[orchestrator] hint queued for active/future workflow nodes.");
        }

        Notify(run);
        try { _runStore.Save(run); } catch { /* best-effort persist */ }
        return true;
    }

    public ApprovalGateResolveResult ResumeApprovalGate(
        string runId,
        string nodeId,
        bool approve,
        string? comment)
    {
        // Run must exist (covers both "never started" and "already
        // ended" — the SQLite store keeps terminal runs around so the
        // dashboard can show history, but their TCS is gone).
        var run = _runStore.Get(runId);
        if (run is null) return ApprovalGateResolveResult.RunNotFound;

        var key = (RunId: runId, NodeId: nodeId);
        if (_pendingApprovals.TryGetValue(key, out var tcs))
        {
            if (tcs.TrySetResult(new ApprovalDecision(approve, comment)))
            {
                return ApprovalGateResolveResult.Resolved;
            }
            // TCS exists but was already set by an earlier Resume — this
            // is a follow-up click before the workflow continuation has
            // finished propagating the resolution and removed the TCS.
            // Report "not awaiting" rather than falling through to the
            // zombie path, otherwise a quick double-click would
            // accidentally cancel a workflow that's actually progressing.
            // The genuine zombie case (orchestrator restart with a parked
            // run) hits the TryGetValue=false path below and is handled
            // there.
            return ApprovalGateResolveResult.NotAwaitingApproval;
        }

        // Zombie state: the run / node is still flagged
        // AwaitingApproval in the store but the in-process TCS that
        // would unpark it is gone. Happens after an orchestrator
        // restart with a parked run on disk (the documented MVP
        // caveat). Force-resolve by cancelling the run so the user
        // isn't stuck staring at a dead button — they can re-run the
        // workflow fresh.
        if (run.Status == WorkflowRunStatus.AwaitingApproval
            && run.NodeRuns.TryGetValue(nodeId, out var nr)
            && nr.Status == WorkflowNodeRunStatus.AwaitingApproval)
        {
            var verb = approve ? "approve" : "reject";
            var commentTail = string.IsNullOrWhiteSpace(comment) ? "" : $" — comment: {comment}";
            nr.AppendLog($"[approval-gate] {verb} clicked but the in-process await was lost (orchestrator restart while parked). Run cancelled — start a fresh run to retry.{commentTail}");
            nr.Status = WorkflowNodeRunStatus.Failed;
            nr.FinishedAt = DateTimeOffset.UtcNow;
            nr.OutputSummary = $"zombie: {verb} after restart";

            run.Status = WorkflowRunStatus.Cancelled;
            run.FinishedAt = DateTimeOffset.UtcNow;
            Notify(run);
            try { _runStore.Save(run); } catch { /* best-effort persist */ }
            return ApprovalGateResolveResult.Resolved;
        }

        return ApprovalGateResolveResult.NotAwaitingApproval;
    }

    private async Task ExecuteAsync(
        WorkflowDefinition definition,
        WorkflowRun run,
        CancellationToken cancellation,
        RerunFromOptions? rerunFrom = null)
    {
        try
        {
            run.Status = WorkflowRunStatus.Running;
            Notify(run);

            // Body nodes (LoopId != null) are NOT scheduled by the main
            // loop — their owning Loop node spawns them per-iteration.
            // We still expose them in the run record so the UI can show
            // their status, but the iteration logic adds per-iter
            // sub-records (e.g. {bodyId}#iter-0) for granular drill-down.
            var bodyNodeIds = definition.Nodes
                .Where(n => !string.IsNullOrEmpty(n.LoopId))
                .Select(n => n.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            // Map from body-node id â†’ its owning Loop's node id, used to
            // rewrite "outside-of-body depends on body-exit" edges into
            // "outside-of-body depends on Loop" so downstream consumers
            // gate on the whole iteration set rather than on a body
            // node that the main scheduler never fires.
            var bodyToLoopOwner = definition.Nodes
                .Where(n => !string.IsNullOrEmpty(n.LoopId))
                .ToDictionary(n => n.Id, n => n.LoopId!, StringComparer.OrdinalIgnoreCase);

            // Schedulable nodes = everything except body nodes.
            var schedulableNodes = definition.Nodes
                .Where(n => !bodyNodeIds.Contains(n.Id))
                .ToList();

            // One TaskCompletionSource per schedulable node — its Result
            // is "did the node succeed". Each node's Task awaits the TCS
            // of every dep before starting, so nodes naturally run as
            // soon as their upstreams resolve. Nothing about the topology
            // is hard-coded: parallel branches just happen.
            //
            // Edge rewriting: any dep on a body node is remapped to the
            // body's Loop owner. Body-internal edges are dropped from
            // this dict entirely (they're consumed by the iteration
            // logic, not the main scheduler).
            var dependencyEdges = schedulableNodes.ToDictionary(
                n => n.Id,
                n => definition.Edges
                    .Where(e => string.Equals(e.ToNodeId, n.Id, StringComparison.OrdinalIgnoreCase))
                    .Select(e => bodyToLoopOwner.TryGetValue(e.FromNodeId, out var owner)
                        ? e with { FromNodeId = owner }
                        : e)
                    .Where(e => !bodyNodeIds.Contains(e.FromNodeId)) // defensive: shouldn't happen post-remap
                    .GroupBy(e => $"{e.FromNodeId}\u0000{NormalizeEdgeCondition(e.Condition) ?? ""}", StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.First())
                    .ToList(),
                StringComparer.OrdinalIgnoreCase);

            var completion = schedulableNodes.ToDictionary(
                n => n.Id,
                _ => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously),
                StringComparer.OrdinalIgnoreCase);
            var branchDecisions = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (rerunFrom is not null)
            {
                PrepareRerunFrom(definition, run, rerunFrom, bodyNodeIds);
            }

            var nodeTasks = schedulableNodes
                .Select(node => Task.Run(async () =>
                {
                    var nr = run.NodeRun(node.Id);

                    if (rerunFrom is not null
                        && !rerunFrom.NodesToExecute.Contains(node.Id))
                    {
                        var reusedOk = ReuseNodeFromSource(run, rerunFrom, node.Id);
                        completion[node.Id].TrySetResult(reusedOk);
                        Notify(run);
                        return;
                    }

                    // Wait for upstream gates. If any failed/skipped,
                    // skip this node too — the result propagates
                    // downstream automatically.
                    var depEdges = dependencyEdges[node.Id];
                    var depNodeIds = depEdges
                        .Select(e => e.FromNodeId)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    var upstreamResults = depNodeIds.Count == 0
                        ? Array.Empty<bool>()
                        : await Task.WhenAll(depNodeIds.Select(d => completion[d].Task)).ConfigureAwait(false);
                    var upstreamById = depNodeIds
                        .Select((id, index) => (id, ok: upstreamResults[index]))
                        .ToDictionary(x => x.id, x => x.ok, StringComparer.OrdinalIgnoreCase);

                    if (cancellation.IsCancellationRequested)
                    {
                        nr.Status = WorkflowNodeRunStatus.Skipped;
                        nr.AppendLog("[skipped — run cancelled]");
                        Notify(run);
                        completion[node.Id].TrySetResult(false);
                        return;
                    }

                    if (TryFindUnsatisfiedDependency(depEdges, upstreamById, branchDecisions, out var blockedEdge, out var skipReason))
                    {
                        nr.Status = WorkflowNodeRunStatus.Skipped;
                        nr.AppendLog(skipReason ?? "[skipped — upstream failed]");
                        Notify(run);
                        completion[node.Id].TrySetResult(false);
                        return;
                    }

                    await SimulateNodeAsync(node, run, definition, branchDecisions, cancellation).ConfigureAwait(false);
                    completion[node.Id].TrySetResult(nr.Status == WorkflowNodeRunStatus.Succeeded);
                }, cancellation))
                .ToList();

            await Task.WhenAll(nodeTasks).ConfigureAwait(false);

            // Final overall status is the worst of the per-node statuses.
            // Loop iteration sub-records (ids containing "#iter-") are
            // sub-state owned by the Loop node — its own NR aggregates
            // them into the visible workflow status. In Continue mode a
            // failed iter must NOT drag the run to Failed; that contract
            // is honoured by FinalizeBodyNodeRuns + the loop's own NR.
            var anyFailed = run.NodeRuns.Values
                .Where(n => !n.NodeId.Contains("#iter-", StringComparison.Ordinal))
                .Any(n => n.Status == WorkflowNodeRunStatus.Failed);
            var anyCancelled = cancellation.IsCancellationRequested;
            run.Status = anyCancelled
                ? WorkflowRunStatus.Cancelled
                : (anyFailed ? WorkflowRunStatus.Failed : WorkflowRunStatus.Succeeded);
            run.FinishedAt = DateTimeOffset.UtcNow;
            Notify(run);
        }
        catch (OperationCanceledException)
        {
            run.Status = WorkflowRunStatus.Cancelled;
            run.FinishedAt = DateTimeOffset.UtcNow;
            Notify(run);
        }
        catch (Exception ex)
        {
            run.Status = WorkflowRunStatus.Failed;
            run.FinishedAt = DateTimeOffset.UtcNow;
            // Attach the exception to the first still-pending node so
            // the user can see what blew up rather than just "Failed".
            var firstPending = run.NodeRuns.Values.FirstOrDefault(n => n.Status == WorkflowNodeRunStatus.Pending);
            if (firstPending is not null)
            {
                firstPending.Status = WorkflowNodeRunStatus.Failed;
                firstPending.AppendLog($"[engine error] {ex.Message}");
            }
            Notify(run);
        }
        finally
        {
            _runCancellation.TryRemove(run.Id, out _);
            _operatorHintsByRunId.TryRemove(run.Id, out _);
            _activeAgentRunsByWorkflowRunId.TryRemove(run.Id, out _);
            // Snapshot to whatever backing store the run store uses.
            // For the SQLite store this writes the run + node rows so
            // the dashboard sees this run after an orchestrator restart;
            // the in-memory store treats Save as a no-op (it already
            // holds the live reference).
            try { _runStore.Save(run); }
            catch { /* persist failure shouldn't tear down the run loop */ }
        }
    }

    // Well-known agent ids whose semantics are special-cased by the
    // simulator. The builder lets the user pick them like any other
    // catalog agent (Kind=Agent) and the runner routes them to the
    // built-in Aggregate / Output / Loop simulation paths so behaviour
    // is identical regardless of how the node was constructed.
    private const string AggregateAgentId = "aggregate";
    private const string WriteToWorkspaceAgentId = "write-to-workspace";
    private const string LoopAgentId = "loop";
    private const string IfElseAgentId = "if-else";

    private static WorkflowNodeKind EffectiveKind(WorkflowNode node)
    {
        if (node.Kind == WorkflowNodeKind.Agent)
        {
            if (string.Equals(node.AgentId, AggregateAgentId, StringComparison.OrdinalIgnoreCase))
                return WorkflowNodeKind.Aggregate;
            if (string.Equals(node.AgentId, WriteToWorkspaceAgentId, StringComparison.OrdinalIgnoreCase))
                return WorkflowNodeKind.Output;
            if (string.Equals(node.AgentId, LoopAgentId, StringComparison.OrdinalIgnoreCase))
                return WorkflowNodeKind.Loop;
            if (string.Equals(node.AgentId, IfElseAgentId, StringComparison.OrdinalIgnoreCase))
                return WorkflowNodeKind.Decision;
        }
        return node.Kind;
    }

    private static bool TryFindUnsatisfiedDependency(
        IReadOnlyList<WorkflowEdge> dependencyEdges,
        IReadOnlyDictionary<string, bool> upstreamById,
        IReadOnlyDictionary<string, string> branchDecisions,
        out WorkflowEdge? blockedEdge,
        out string? skipReason)
    {
        blockedEdge = null;
        skipReason = null;

        foreach (var edge in dependencyEdges)
        {
            if (!upstreamById.TryGetValue(edge.FromNodeId, out var upstreamOk) || !upstreamOk)
            {
                blockedEdge = edge;
                skipReason = "[skipped — upstream failed]";
                return true;
            }

            var expected = NormalizeEdgeCondition(edge.Condition);
            if (expected is null) continue;

            if (!branchDecisions.TryGetValue(edge.FromNodeId, out var selected))
            {
                blockedEdge = edge;
                skipReason = $"[skipped — conditional upstream '{edge.FromNodeId}' did not publish a decision for branch '{expected}']";
                return true;
            }

            var normalizedSelected = NormalizeEdgeCondition(selected) ?? selected.Trim();
            if (!string.Equals(normalizedSelected, expected, StringComparison.OrdinalIgnoreCase))
            {
                blockedEdge = edge;
                skipReason = $"[skipped — condition '{expected}' not selected by '{edge.FromNodeId}' (selected '{normalizedSelected}')]";
                return true;
            }
        }

        return false;
    }

    private static string? NormalizeEdgeCondition(string? condition)
    {
        if (string.IsNullOrWhiteSpace(condition)) return null;
        var value = condition.Trim();
        return value.ToLowerInvariant() switch
        {
            "always" => null,
            "any" => null,
            "then" => "true",
            "yes" => "true",
            "y" => "true",
            "pass" => "true",
            "passed" => "true",
            "match" => "true",
            "matched" => "true",
            "else" => "false",
            "no" => "false",
            "n" => "false",
            "fail" => "false",
            "failed" => "false",
            "unmatched" => "false",
            _ => value,
        };
    }

    private IReadOnlyList<string> SnapshotOperatorHints(string runId)
    {
        return _operatorHintsByRunId.TryGetValue(runId, out var queue)
            ? queue.ToArray()
            : Array.Empty<string>();
    }

    private static string AppendOperatorHints(string arguments, IReadOnlyList<string> hints)
    {
        if (hints.Count == 0) return arguments;

        var block = string.Join(
            Environment.NewLine,
            hints.Select((hint, index) => $"{index + 1}. {hint}"));
        return $"""
            {arguments}

            ## Live operator guidance already received during this workflow run

            Treat these hints as high-priority human steering for this node. Check for this section at the start of every work cycle/instruction batch, apply the newest safe instruction when hints conflict with earlier workflow inputs, and mention the conflict briefly in your artifact.

            {block}
            """;
    }

    private static string AppendWorkflowRuntimeGuidance(string arguments)
    {
        return $"""
            {arguments}

            ## DoxieOS workflow artifact contract

            This invocation is running inside a workflow. The environment variable `DOXIE_WORKFLOW_OUTPUT_DIR` points to this node's output folder. Before finishing, write at least one indexable artifact file directly under that folder, such as `output.md`, `report.md`, `result.json`, or the file name required by your skill contract.

            Downstream workflow nodes and aggregates read files from `DOXIE_WORKFLOW_OUTPUT_DIR`; they do not treat chat text or stdout as the durable handoff. If there is no substantive data to return, still write `output.md` explaining the limitation and the evidence checked.
            """;
    }

    private static string NormalizeOperatorHint(string hint)
    {
        var clean = hint.Trim();
        return clean.Length <= 4_000 ? clean : clean[..4_000].TrimEnd() + "...";
    }

    private static bool IsTerminal(WorkflowRunStatus status) =>
        status is WorkflowRunStatus.Succeeded
            or WorkflowRunStatus.Failed
            or WorkflowRunStatus.Cancelled;

    private static string? NullIfEmpty(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s;

    private static int ParseIntOrDefault(string? s, int @default) =>
        int.TryParse(s, out var v) ? v : @default;

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s.Substring(0, max) + "…";

    /// <summary>
    /// Resolves a dotted JSON path against the root element. Empty / null
    /// path returns the root unchanged. Each segment is treated as an
    /// object key — array indexing isn't supported in this V1 (the
    /// caller's array is the result of the whole walk; if the user wants
    /// to index, they restructure the upstream JSON instead).
    /// </summary>
    private static JsonElement ResolveJsonPath(JsonElement root, string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return root;
        var current = root;
        foreach (var segment in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            if (current.ValueKind != JsonValueKind.Object
                || !current.TryGetProperty(segment, out var next))
            {
                throw new InvalidOperationException(
                    $"JSON path segment '{segment}' not found (in path '{path}').");
            }
            current = next;
        }
        return current;
    }

    private void Notify(WorkflowRun run) => RunUpdated?.Invoke(run);

    private static bool LooksSecret(string key) =>
        key.Contains("token", StringComparison.OrdinalIgnoreCase)
        || key.Contains("secret", StringComparison.OrdinalIgnoreCase)
        || key.Contains("password", StringComparison.OrdinalIgnoreCase)
        || key.Contains("key", StringComparison.OrdinalIgnoreCase)
        || key.EndsWith("_pat", StringComparison.OrdinalIgnoreCase);

    private void PrepareRerunFrom(
        WorkflowDefinition definition,
        WorkflowRun run,
        RerunFromOptions options,
        ISet<string> bodyNodeIds)
    {
        var startLabel = options.RequestedNodeId.Equals(options.StartNodeId, StringComparison.OrdinalIgnoreCase)
            ? options.StartNodeId
            : $"{options.RequestedNodeId} (via loop {options.StartNodeId})";

        foreach (var node in definition.Nodes.Where(n => !options.NodesToExecute.Contains(n.Id)))
        {
            ReuseNodeFromSource(run, options, node.Id);
        }

        foreach (var nodeId in bodyNodeIds.Where(id => !options.NodesToExecute.Contains(id)))
        {
            ReuseNodeFromSource(run, options, nodeId);
        }

        if (run.NodeRuns.TryGetValue(options.StartNodeId, out var startNr))
        {
            startNr.AppendLog($"[rerun-from] replaying from '{startLabel}' using run {options.SourceRunId}");
            if (!string.IsNullOrWhiteSpace(options.Guidance))
            {
                startNr.AppendLog($"[rerun-from guidance] {options.Guidance}");
            }
        }
    }

    private bool ReuseNodeFromSource(
        WorkflowRun run,
        RerunFromOptions options,
        string nodeId)
    {
        var nr = run.NodeRun(nodeId);
        if (nr.Status != WorkflowNodeRunStatus.Pending)
        {
            return nr.Status == WorkflowNodeRunStatus.Succeeded;
        }

        if (!options.SourceRun.NodeRuns.TryGetValue(nodeId, out var sourceNr))
        {
            nr.Status = WorkflowNodeRunStatus.Skipped;
            nr.StartedAt = DateTimeOffset.UtcNow;
            nr.FinishedAt = DateTimeOffset.UtcNow;
            nr.AppendLog($"[rerun-from] skipped: source run {options.SourceRunId} has no record for this node");
            return false;
        }

        if (sourceNr.Status != WorkflowNodeRunStatus.Succeeded)
        {
            nr.Status = WorkflowNodeRunStatus.Skipped;
            nr.StartedAt = sourceNr.StartedAt ?? DateTimeOffset.UtcNow;
            nr.FinishedAt = DateTimeOffset.UtcNow;
            nr.OutputSummary = $"not reused: source was {sourceNr.Status}";
            nr.AppendLog($"[rerun-from] skipped: source node was {sourceNr.Status}");
            return false;
        }

        CopyNodeArtifacts(options.SourceRunId, run.Id, nodeId);
        nr.Status = WorkflowNodeRunStatus.Succeeded;
        nr.StartedAt = sourceNr.StartedAt;
        nr.FinishedAt = sourceNr.FinishedAt ?? DateTimeOffset.UtcNow;
        nr.OutputSummary = string.IsNullOrWhiteSpace(sourceNr.OutputSummary)
            ? $"reused from {options.SourceRunId}/{nodeId}"
            : sourceNr.OutputSummary;
        nr.AppendLog($"[rerun-from] reused artifacts from run {options.SourceRunId}, node {nodeId}");
        return true;
    }

    private void CopyNodeArtifacts(string sourceRunId, string targetRunId, string nodeId)
    {
        var sourceDir = WorkflowRunPaths.NodeDir(_runsDirectory, sourceRunId, nodeId);
        if (!Directory.Exists(sourceDir))
        {
            return;
        }

        var targetDir = WorkflowRunPaths.NodeDir(_runsDirectory, targetRunId, nodeId);
        Directory.CreateDirectory(targetDir);

        foreach (var sourceFile in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceDir, sourceFile);
            if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
            {
                continue;
            }

            var targetFile = Path.GetFullPath(Path.Combine(targetDir, relative));
            if (!targetFile.StartsWith(targetDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(targetFile, targetDir, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var parent = Path.GetDirectoryName(targetFile);
            if (!string.IsNullOrWhiteSpace(parent))
            {
                Directory.CreateDirectory(parent);
            }

            File.Copy(sourceFile, targetFile, overwrite: true);
        }
    }

    private static IReadOnlySet<string> FindDownstreamNodeIds(WorkflowDefinition definition, string startNodeId)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>();
        queue.Enqueue(startNodeId);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!result.Add(current))
            {
                continue;
            }

            foreach (var next in definition.Edges
                         .Where(e => string.Equals(e.FromNodeId, current, StringComparison.OrdinalIgnoreCase))
                         .Select(e => e.ToNodeId))
            {
                queue.Enqueue(next);
            }
        }

        return result;
    }

}
