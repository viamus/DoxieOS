using System.Collections.Concurrent;
using System.Text.Json;

namespace Viamus.Doxie.Orchestrator.Workflows;

public sealed partial class OrchestratedWorkflowRunner
{
    private async Task SimulateLoopNodeAsync(
        WorkflowNode loopNode,
        WorkflowRun run,
        WorkflowDefinition definition,
        WorkflowNodeRun nr,
        CancellationToken cancellation)
    {
        var loopDir = WorkflowRunPaths.NodeDir(_runsDirectory, run.Id, loopNode.Id);
        Directory.CreateDirectory(loopDir);

        // â”€â”€ 1. Identify body nodes â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        var bodyNodes = definition.Nodes
            .Where(n => string.Equals(n.LoopId, loopNode.Id, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (bodyNodes.Count == 0)
        {
            nr.AppendLog("[loop] no body nodes (no node has LoopId == this loop's id) â€” nothing to iterate");
            await File.WriteAllTextAsync(
                Path.Combine(loopDir, "results.json"),
                "[]",
                cancellation).ConfigureAwait(false);
            nr.OutputSummary = "loop: empty body";
            return;
        }

        var bodyNodeIds = bodyNodes.Select(n => n.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Surface "the body is being driven by this loop" on each body
        // node's original NodeRun. Without this they'd stay Pending
        // forever (they're excluded from the main scheduler) and the
        // UI would lie about progress. Per-iteration detail lives in
        // the {bodyId}#iter-{i} sub-records.
        foreach (var bn in bodyNodes)
        {
            if (run.NodeRuns.TryGetValue(bn.Id, out var bnr))
            {
                bnr.Status = WorkflowNodeRunStatus.Running;
                bnr.StartedAt = DateTimeOffset.UtcNow;
                bnr.AppendLog($"[loop] iterating via '{loopNode.Id}' â€” see {bn.Id}#iter-* sub-runs for per-iteration detail");
            }
        }
        Notify(run);

        // â”€â”€ 2. Topology â€” body-entry + body-exit â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        var bodyEntries = definition.Edges
            .Where(e => string.Equals(e.FromNodeId, loopNode.Id, StringComparison.OrdinalIgnoreCase)
                     && bodyNodeIds.Contains(e.ToNodeId))
            .Select(e => e.ToNodeId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (bodyEntries.Count != 1)
        {
            throw new InvalidOperationException(
                $"Loop '{loopNode.Id}' must have exactly one body-entry (a body node directly downstream of the Loop). Found: {bodyEntries.Count}.");
        }
        var bodyEntryId = bodyEntries[0];

        var bodyExits = definition.Edges
            .Where(e => bodyNodeIds.Contains(e.FromNodeId) && !bodyNodeIds.Contains(e.ToNodeId))
            .Select(e => e.FromNodeId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (bodyExits.Count > 1)
        {
            throw new InvalidOperationException(
                $"Loop '{loopNode.Id}' body must have at most one exit (a body node with a downstream edge leaving the body). Found: {bodyExits.Count}.");
        }

        // No external exit edges â†’ exit is whichever body node has no
        // downstream body-internal edge (i.e. terminal within the body).
        // For a single-node body, this is the entry itself.
        string bodyExitId;
        if (bodyExits.Count == 1)
        {
            bodyExitId = bodyExits[0];
        }
        else
        {
            var terminals = bodyNodes
                .Where(n => !definition.Edges.Any(e =>
                    string.Equals(e.FromNodeId, n.Id, StringComparison.OrdinalIgnoreCase)
                    && bodyNodeIds.Contains(e.ToNodeId)))
                .Select(n => n.Id)
                .ToList();
            bodyExitId = terminals.FirstOrDefault() ?? bodyEntryId;
        }

        // â”€â”€ 3. Resolve config from loop's Inputs â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        var inputs = ApplyTriggerSubstitutions(loopNode.Inputs, run.TriggerInputs);
        var arraySource = NullIfEmpty(inputs?.GetValueOrDefault("array_source")) ?? "result.json";
        var arrayPath = NullIfEmpty(inputs?.GetValueOrDefault("array_path"));
        var concurrency = ParseIntOrDefault(NullIfEmpty(inputs?.GetValueOrDefault("concurrency")), 4);
        if (concurrency < 1) concurrency = 1;
        var onFailureRaw = NullIfEmpty(inputs?.GetValueOrDefault("on_failure")) ?? "fail-fast";
        var onFailure = onFailureRaw.Equals("continue", StringComparison.OrdinalIgnoreCase)
            ? LoopFailureMode.Continue
            : LoopFailureMode.FailFast;

        nr.AppendLog($"[loop] config: array_source={arraySource}, array_path={arrayPath ?? "(root)"}, concurrency={concurrency}, on_failure={onFailureRaw}");

        // â”€â”€ 4. Locate + parse the upstream array â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        var loopUpstreamIds = definition.Edges
            .Where(e => string.Equals(e.ToNodeId, loopNode.Id, StringComparison.OrdinalIgnoreCase))
            .Select(e => e.FromNodeId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        string? arrayFilePath = null;
        foreach (var upId in loopUpstreamIds)
        {
            var candidate = Path.Combine(
                WorkflowRunPaths.NodeDir(_runsDirectory, run.Id, upId),
                arraySource);
            if (File.Exists(candidate))
            {
                arrayFilePath = candidate;
                break;
            }
        }

        JsonElement[] items;
        if (arrayFilePath is null)
        {
            nr.AppendLog($"[loop] no upstream produced '{arraySource}' â€” treating as empty array");
            items = Array.Empty<JsonElement>();
        }
        else
        {
            nr.AppendLog($"[loop] reading array from {arrayFilePath}");
            var json = await File.ReadAllTextAsync(arrayFilePath, cancellation).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var arrayElement = ResolveJsonPath(root, arrayPath);
            if (arrayElement.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidOperationException(
                    $"Loop array source '{arrayFilePath}'{(arrayPath is null ? "" : $" at path '{arrayPath}'")} is not a JSON array (got {arrayElement.ValueKind}).");
            }
            items = arrayElement.EnumerateArray().Select(e => e.Clone()).ToArray();
        }

        nr.AppendLog($"[loop] iterating {items.Length} item(s)");
        Notify(run);

        if (items.Length == 0)
        {
            await File.WriteAllTextAsync(
                Path.Combine(loopDir, "results.json"),
                "[]",
                cancellation).ConfigureAwait(false);
            nr.OutputSummary = "loop: 0 iterations";
            FinalizeBodyNodeRuns(run, bodyNodes, items.Length);
            return;
        }

        // â”€â”€ 5. Run iterations with concurrency cap â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        var outcomes = new ConcurrentDictionary<int, IterationOutcome>();
        using var failFastCts = onFailure == LoopFailureMode.FailFast
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellation)
            : null;
        var iterCancel = failFastCts?.Token ?? cancellation;
        var totalCount = items.Length;

        if (concurrency == 1)
        {
            // Sequential path â€” preserves strict iteration order. Avoids
            // the SemaphoreSlim+Task.Run race where two iterations queue
            // simultaneously and the OS thread pool may grant out of
            // order. Cheaper too: no semaphore, no extra threads.
            for (var idx = 0; idx < items.Length; idx++)
            {
                if (iterCancel.IsCancellationRequested)
                {
                    outcomes[idx] = new IterationOutcome(
                        idx, false,
                        WorkflowRunPaths.IterationDir(_runsDirectory, run.Id, loopNode.Id, idx),
                        "cancelled before start",
                        Array.Empty<string>());
                    continue;
                }
                try
                {
                    var outcome = await RunIterationAsync(
                        loopNode, bodyNodes, bodyEntryId, bodyExitId,
                        idx, totalCount, items[idx], run, definition,
                        iterCancel).ConfigureAwait(false);
                    outcomes[idx] = outcome;
                    if (!outcome.Success && onFailure == LoopFailureMode.FailFast)
                    {
                        nr.AppendLog($"[loop] iter-{idx} failed and on_failure=fail-fast â€” cancelling remaining iterations");
                        failFastCts?.Cancel();
                    }
                }
                catch (OperationCanceledException)
                {
                    outcomes[idx] = new IterationOutcome(
                        idx, false,
                        WorkflowRunPaths.IterationDir(_runsDirectory, run.Id, loopNode.Id, idx),
                        "cancelled mid-iteration",
                        Array.Empty<string>());
                }
                catch (Exception ex)
                {
                    outcomes[idx] = new IterationOutcome(
                        idx, false,
                        WorkflowRunPaths.IterationDir(_runsDirectory, run.Id, loopNode.Id, idx),
                        ex.Message,
                        Array.Empty<string>());
                    if (onFailure == LoopFailureMode.FailFast)
                    {
                        failFastCts?.Cancel();
                    }
                }
            }
        }
        else
        {
            using var sem = new SemaphoreSlim(concurrency);
            var iterTasks = items.Select((item, idx) => Task.Run(async () =>
            {
                try
                {
                    await sem.WaitAsync(iterCancel).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    outcomes[idx] = new IterationOutcome(
                        idx, false,
                        WorkflowRunPaths.IterationDir(_runsDirectory, run.Id, loopNode.Id, idx),
                        "cancelled before start",
                        Array.Empty<string>());
                    return;
                }
                try
                {
                    var outcome = await RunIterationAsync(
                        loopNode, bodyNodes, bodyEntryId, bodyExitId,
                        idx, totalCount, items[idx], run, definition,
                        iterCancel).ConfigureAwait(false);
                    outcomes[idx] = outcome;
                    if (!outcome.Success && onFailure == LoopFailureMode.FailFast)
                    {
                        nr.AppendLog($"[loop] iter-{idx} failed and on_failure=fail-fast â€” cancelling remaining iterations");
                        failFastCts?.Cancel();
                    }
                }
                catch (OperationCanceledException)
                {
                    outcomes[idx] = new IterationOutcome(
                        idx, false,
                        WorkflowRunPaths.IterationDir(_runsDirectory, run.Id, loopNode.Id, idx),
                        "cancelled mid-iteration",
                        Array.Empty<string>());
                }
                catch (Exception ex)
                {
                    outcomes[idx] = new IterationOutcome(
                        idx, false,
                        WorkflowRunPaths.IterationDir(_runsDirectory, run.Id, loopNode.Id, idx),
                        ex.Message,
                        Array.Empty<string>());
                    if (onFailure == LoopFailureMode.FailFast)
                    {
                        failFastCts?.Cancel();
                    }
                }
                finally
                {
                    sem.Release();
                }
            }, CancellationToken.None)).ToList();

            await Task.WhenAll(iterTasks).ConfigureAwait(false);
        }

        // â”€â”€ 6. Aggregate into results.json â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        var results = new List<object?>();
        int succeeded = 0, failed = 0;
        for (int i = 0; i < items.Length; i++)
        {
            if (outcomes.TryGetValue(i, out var o))
            {
                if (o.Success) succeeded++;
                else failed++;

                if (!o.Success && onFailure == LoopFailureMode.Continue)
                {
                    // Continue mode: skip failed iterations from results
                    nr.AppendLog($"[loop] iter-{i} failed (continue mode) â€” excluded from results.json: {o.ErrorMessage}");
                    continue;
                }

                results.Add(new
                {
                    index = o.Index,
                    success = o.Success,
                    outputDir = o.OutputDir,
                    error = o.ErrorMessage,
                    files = o.ProducedFiles,
                });
            }
            else
            {
                failed++;
                if (onFailure == LoopFailureMode.Continue) continue;
                results.Add(new
                {
                    index = i,
                    success = false,
                    outputDir = WorkflowRunPaths.IterationDir(_runsDirectory, run.Id, loopNode.Id, i),
                    error = "not started",
                    files = Array.Empty<string>(),
                });
            }
        }

        var resultsJson = JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(
            Path.Combine(loopDir, "results.json"),
            resultsJson,
            cancellation).ConfigureAwait(false);

        nr.AppendLog($"[loop] {items.Length} iteration(s) total â€” {succeeded} succeeded, {failed} failed â†’ results.json");
        nr.OutputSummary = $"loop: {succeeded}/{items.Length} succeeded";

        FinalizeBodyNodeRuns(run, bodyNodes, items.Length, onFailure);
        Notify(run);

        if (failed > 0 && onFailure == LoopFailureMode.FailFast)
        {
            throw new InvalidOperationException(
                $"Loop failed: {failed}/{items.Length} iteration(s) failed (on_failure=fail-fast).");
        }
    }

    /// <summary>
    /// Aggregates each body node's per-iteration sub-runs into the
    /// body node's original <see cref="WorkflowNodeRun"/>. The body
    /// nodes were excluded from the main scheduler so without this
    /// they'd stay Pending forever. In FailFast mode any iteration
    /// failure flips the body NR to Failed; in Continue mode a body
    /// NR ends Succeeded as long as at least one iteration ran ok
    /// (the failed iters are noted in the summary but don't drag the
    /// node to Failed â€” that contract belongs to results.json).
    /// </summary>
    private static void FinalizeBodyNodeRuns(
        WorkflowRun run,
        IReadOnlyList<WorkflowNode> bodyNodes,
        int totalItems,
        LoopFailureMode onFailure = LoopFailureMode.FailFast)
    {
        foreach (var bn in bodyNodes)
        {
            if (!run.NodeRuns.TryGetValue(bn.Id, out var bnr)) continue;
            var iterStatuses = Enumerable.Range(0, totalItems)
                .Select(i => run.NodeRuns.TryGetValue($"{bn.Id}#iter-{i}", out var iterNr) ? (WorkflowNodeRunStatus?)iterNr.Status : null)
                .ToList();
            var succeededCount = iterStatuses.Count(s => s == WorkflowNodeRunStatus.Succeeded);
            var failedCount = iterStatuses.Count(s => s == WorkflowNodeRunStatus.Failed);
            var anyRan = succeededCount > 0 || failedCount > 0;

            bnr.Status = (onFailure, anyRan, failedCount, succeededCount) switch
            {
                (_, false, _, _) => WorkflowNodeRunStatus.Skipped,                  // never ran
                (LoopFailureMode.FailFast, _, > 0, _) => WorkflowNodeRunStatus.Failed,
                (LoopFailureMode.Continue, _, _, > 0) => WorkflowNodeRunStatus.Succeeded,
                (LoopFailureMode.Continue, _, > 0, 0) => WorkflowNodeRunStatus.Failed, // all iters failed
                _ => WorkflowNodeRunStatus.Succeeded,
            };
            bnr.FinishedAt = DateTimeOffset.UtcNow;
            bnr.OutputSummary = $"loop body: {succeededCount}/{totalItems} iter(s) succeeded";
        }
    }

    /// <summary>
    /// Runs the body sub-graph for one loop iteration. Each body node
    /// gets an iteration-scoped NodeRun (<c>{nodeId}#iter-{i}</c>) and
    /// an iteration-scoped output folder. Body-internal edges drive
    /// the local dep graph; the body-entry's only upstream is the
    /// iteration's <c>_loop-input/</c> folder (where item.json lives).
    /// </summary>
    private async Task<IterationOutcome> RunIterationAsync(
        WorkflowNode loopNode,
        IReadOnlyList<WorkflowNode> bodyNodes,
        string bodyEntryId,
        string bodyExitId,
        int iterIdx,
        int totalIters,
        JsonElement item,
        WorkflowRun run,
        WorkflowDefinition definition,
        CancellationToken cancellation)
    {
        var iterDir = WorkflowRunPaths.IterationDir(_runsDirectory, run.Id, loopNode.Id, iterIdx);
        Directory.CreateDirectory(iterDir);

        var loopInputDir = WorkflowRunPaths.IterationInputDir(_runsDirectory, run.Id, loopNode.Id, iterIdx);
        Directory.CreateDirectory(loopInputDir);

        var itemJson = item.GetRawText();
        await File.WriteAllTextAsync(
            Path.Combine(loopInputDir, "item.json"),
            itemJson,
            cancellation).ConfigureAwait(false);

        // Loop-scoped env vars layered into every body node's dispatch.
        var loopEnv = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [WorkflowRunPaths.LoopItemEnvVar] = itemJson,
            [WorkflowRunPaths.LoopIndexEnvVar] = iterIdx.ToString(),
            [WorkflowRunPaths.LoopTotalEnvVar] = totalIters.ToString(),
        };

        // Body-internal edges only.
        var bodyNodeIds = bodyNodes.Select(n => n.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var bodyEdges = definition.Edges
            .Where(e => bodyNodeIds.Contains(e.FromNodeId) && bodyNodeIds.Contains(e.ToNodeId))
            .ToList();

        // Iteration-scoped completion gates.
        var iterCompletion = bodyNodes.ToDictionary(
            n => n.Id,
            _ => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously),
            StringComparer.OrdinalIgnoreCase);

        var iterDependencyEdges = bodyNodes.ToDictionary(
            n => n.Id,
            n => bodyEdges
                .Where(e => string.Equals(e.ToNodeId, n.Id, StringComparison.OrdinalIgnoreCase))
                .ToList(),
            StringComparer.OrdinalIgnoreCase);
        var branchDecisions = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Spawn one task per body node.
        var bodyTasks = bodyNodes.Select(bodyNode => Task.Run(async () =>
        {
            var iterNodeRunKey = $"{bodyNode.Id}#iter-{iterIdx}";
            var bodyNr = run.AddNodeRun(iterNodeRunKey);

            try
            {
                var depEdges = iterDependencyEdges[bodyNode.Id];
                var depNodeIds = depEdges
                    .Select(e => e.FromNodeId)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                var depResults = depNodeIds.Count == 0
                    ? Array.Empty<bool>()
                    : await Task.WhenAll(depNodeIds.Select(d => iterCompletion[d].Task)).ConfigureAwait(false);
                var upstreamById = depNodeIds
                    .Select((id, index) => (id, ok: depResults[index]))
                    .ToDictionary(x => x.id, x => x.ok, StringComparer.OrdinalIgnoreCase);

                if (cancellation.IsCancellationRequested)
                {
                    bodyNr.Status = WorkflowNodeRunStatus.Skipped;
                    bodyNr.AppendLog("[skipped â€” iteration cancelled]");
                    Notify(run);
                    iterCompletion[bodyNode.Id].TrySetResult(false);
                    return;
                }

                if (TryFindUnsatisfiedDependency(depEdges, upstreamById, branchDecisions, out _, out var skipReason))
                {
                    bodyNr.Status = WorkflowNodeRunStatus.Skipped;
                    bodyNr.AppendLog(skipReason ?? "[skipped — body-upstream failed]");
                    Notify(run);
                    iterCompletion[bodyNode.Id].TrySetResult(false);
                    return;
                }

                bodyNr.Status = WorkflowNodeRunStatus.Running;
                bodyNr.StartedAt = DateTimeOffset.UtcNow;
                bodyNr.AppendLog($"[loop] iter-{iterIdx} of {totalIters} (item: {Truncate(itemJson, 200)})");
                Notify(run);

                // Iteration-scoped paths for this body node.
                var bodyOutputDir = WorkflowRunPaths.IterationNodeDir(
                    _runsDirectory, run.Id, loopNode.Id, iterIdx, bodyNode.Id);
                Directory.CreateDirectory(bodyOutputDir);

                IReadOnlyList<string> bodyUpstreamDirs;
                if (string.Equals(bodyNode.Id, bodyEntryId, StringComparison.OrdinalIgnoreCase))
                {
                    // Entry sees the iteration's _loop-input/ folder
                    // (where item.json is) as its upstream â€” so an agent
                    // can read item.json via DOXIE_WORKFLOW_INPUT_DIRS.
                    bodyUpstreamDirs = new[] { loopInputDir };
                }
                else
                {
                    bodyUpstreamDirs = bodyEdges
                        .Where(e => string.Equals(e.ToNodeId, bodyNode.Id, StringComparison.OrdinalIgnoreCase))
                        .Select(e => WorkflowRunPaths.IterationNodeDir(
                            _runsDirectory, run.Id, loopNode.Id, iterIdx, e.FromNodeId))
                        .Where(Directory.Exists)
                        .ToList();
                }

                switch (EffectiveKind(bodyNode))
                {
                    case WorkflowNodeKind.Agent:
                        if (string.IsNullOrEmpty(bodyNode.AgentId))
                        {
                            throw new InvalidOperationException(
                                $"Loop body node '{bodyNode.Id}' has no agentId bound.");
                        }

                        await DispatchAgentInScopeAsync(
                            bodyNode, run, definition, bodyNr,
                            outputDir: bodyOutputDir,
                            upstreamDirs: bodyUpstreamDirs,
                            extraEnv: loopEnv,
                            cancellation: cancellation).ConfigureAwait(false);
                        break;

                    case WorkflowNodeKind.Decision:
                        await SimulateDecisionNodeInScopeAsync(
                            bodyNode, run, definition, bodyNr,
                            outputDir: bodyOutputDir,
                            upstreamDirs: bodyUpstreamDirs,
                            extraEnv: loopEnv,
                            branchDecisions: branchDecisions,
                            cancellation: cancellation).ConfigureAwait(false);
                        break;

                    default:
                        throw new InvalidOperationException(
                            $"Loop body node '{bodyNode.Id}' has Kind={EffectiveKind(bodyNode)}; only Agent and Decision body nodes are supported.");
                }

                bodyNr.Status = WorkflowNodeRunStatus.Succeeded;
                bodyNr.FinishedAt = DateTimeOffset.UtcNow;
                Notify(run);
                iterCompletion[bodyNode.Id].TrySetResult(true);
            }
            catch (OperationCanceledException)
            {
                bodyNr.Status = WorkflowNodeRunStatus.Skipped;
                bodyNr.FinishedAt = DateTimeOffset.UtcNow;
                bodyNr.AppendLog("[node cancelled]");
                Notify(run);
                iterCompletion[bodyNode.Id].TrySetResult(false);
            }
            catch (Exception ex)
            {
                bodyNr.Status = WorkflowNodeRunStatus.Failed;
                bodyNr.FinishedAt = DateTimeOffset.UtcNow;
                bodyNr.AppendLog($"[error] {ex.Message}");
                Notify(run);
                iterCompletion[bodyNode.Id].TrySetResult(false);
            }
        }, CancellationToken.None)).ToList();

        await Task.WhenAll(bodyTasks).ConfigureAwait(false);

        var exitOk = iterCompletion[bodyExitId].Task.Result;
        var exitDir = WorkflowRunPaths.IterationNodeDir(
            _runsDirectory, run.Id, loopNode.Id, iterIdx, bodyExitId);
        var producedFiles = Directory.Exists(exitDir)
            ? Directory.EnumerateFiles(exitDir).Select(Path.GetFileName).Where(s => s is not null).ToList()!
            : new List<string>();

        if (exitOk)
        {
            return new IterationOutcome(iterIdx, true, exitDir, null, producedFiles!);
        }

        // Find an error message from the body node-runs (best-effort).
        string? err = null;
        foreach (var bn in bodyNodes)
        {
            var key = $"{bn.Id}#iter-{iterIdx}";
            if (run.NodeRuns.TryGetValue(key, out var br) && br.Status == WorkflowNodeRunStatus.Failed)
            {
                err = br.Logs.LastOrDefault(l => l.StartsWith("[error]", StringComparison.OrdinalIgnoreCase));
                if (err is not null) break;
            }
        }
        return new IterationOutcome(iterIdx, false, exitDir, err ?? "iteration failed", producedFiles!);
    }

    private sealed record IterationOutcome(
        int Index,
        bool Success,
        string OutputDir,
        string? ErrorMessage,
        IReadOnlyList<string> ProducedFiles);

    private enum LoopFailureMode { FailFast, Continue }
}
