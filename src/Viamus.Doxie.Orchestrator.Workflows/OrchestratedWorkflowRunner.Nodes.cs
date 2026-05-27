using System.Collections.Concurrent;
using System.Text.Json;
using Viamus.Doxie.Orchestrator.Agents;
using Viamus.Doxie.Orchestrator.Context;

namespace Viamus.Doxie.Orchestrator.Workflows;

public sealed partial class OrchestratedWorkflowRunner
{
    /// <summary>
    /// Per-node simulation. The script depends on the (effective) node
    /// kind: Trigger and Aggregate are fast structural steps; Agent
    /// fakes 800-2400ms of work with a few log lines; Output writes a
    /// real markdown stub into the workspace if one is configured (so
    /// the user sees an actual file appear on disk).
    /// </summary>
    private async Task SimulateNodeAsync(
        WorkflowNode node,
        WorkflowRun run,
        WorkflowDefinition definition,
        ConcurrentDictionary<string, string> branchDecisions,
        CancellationToken cancellation)
    {
        var nr = run.NodeRun(node.Id);
        nr.Status = WorkflowNodeRunStatus.Running;
        nr.StartedAt = DateTimeOffset.UtcNow;

        // Surface the workspace + env composition so the user can see
        // the wiring is real, even without a subprocess. A production
        // runner would build the same dictionary and pass it to
        // ProcessStartInfo.Environment instead of just logging it.
        var resolvedNodeWorkspaceId = ApplyTriggerSubstitution(node.WorkspaceId, run.TriggerInputs);
        if (!string.IsNullOrEmpty(resolvedNodeWorkspaceId))
        {
            nr.AppendLog($"cwd: workspace '{resolvedNodeWorkspaceId}'");
        }
        if (definition.Env is { Count: > 0 } env)
        {
            // Mask any value that looks like a secret so the run log
            // doesn't accidentally render a token in the UI. Heuristic
            // is intentionally loose — if a user wants the value
            // visible they can name the key without "token"/"secret".
            foreach (var pair in env)
            {
                var masked = LooksSecret(pair.Key) ? "***" : pair.Value;
                nr.AppendLog($"env: {pair.Key}={masked}");
            }
        }
        Notify(run);

        try
        {
            switch (EffectiveKind(node))
            {
                case WorkflowNodeKind.Trigger:
                    nr.AppendLog($"trigger fired ({run.TriggeredBy})");
                    Notify(run);
                    await Task.Delay(150, cancellation).ConfigureAwait(false);
                    nr.OutputSummary = $"trigger:{run.TriggeredBy} at {run.StartedAt:HH:mm:ss}";
                    break;

                case WorkflowNodeKind.Agent:
                    await SimulateAgentNodeAsync(node, run, definition, nr, cancellation).ConfigureAwait(false);
                    break;

                case WorkflowNodeKind.Aggregate:
                    await SimulateAggregateNodeAsync(node, run, definition, nr, cancellation).ConfigureAwait(false);
                    break;

                case WorkflowNodeKind.Output:
                    await SimulateOutputNodeAsync(node, run, definition, nr, cancellation).ConfigureAwait(false);
                    break;

                case WorkflowNodeKind.Loop:
                    await SimulateLoopNodeAsync(node, run, definition, nr, cancellation).ConfigureAwait(false);
                    break;

                case WorkflowNodeKind.Decision:
                    await SimulateDecisionNodeAsync(node, run, definition, nr, branchDecisions, cancellation).ConfigureAwait(false);
                    break;
            }

            nr.Status = WorkflowNodeRunStatus.Succeeded;
            nr.FinishedAt = DateTimeOffset.UtcNow;
            Notify(run);
        }
        catch (OperationCanceledException)
        {
            nr.Status = WorkflowNodeRunStatus.Skipped;
            nr.FinishedAt = DateTimeOffset.UtcNow;
            nr.AppendLog("[node cancelled]");
            Notify(run);
        }
        catch (Exception ex)
        {
            nr.Status = WorkflowNodeRunStatus.Failed;
            nr.FinishedAt = DateTimeOffset.UtcNow;
            nr.AppendLog($"[error] {ex.Message}");
            Notify(run);
        }
    }

    private async Task SimulateAgentNodeAsync(WorkflowNode node, WorkflowRun run, WorkflowDefinition definition, WorkflowNodeRun nr, CancellationToken cancellation)
    {
        if (string.IsNullOrEmpty(node.AgentId))
        {
            throw new InvalidOperationException($"Agent node '{node.Id}' has no agentId bound.");
        }

        // Approval-gate connector — same intercept pattern as aggregate
        // and write-to-workspace, but the work is "park until a human
        // clicks Approve / Reject" rather than spawning a process.
        if (string.Equals(node.AgentId, ApprovalGateAgentId, StringComparison.OrdinalIgnoreCase))
        {
            await SimulateApprovalGateAsync(node, run, definition, nr, cancellation).ConfigureAwait(false);
            return;
        }

        // Default-scope dispatch: output dir per-node, upstream dirs
        // derived from the workflow's edges. Loop's iteration logic
        // calls DispatchAgentInScopeAsync directly with iteration-scoped
        // folders + extra env vars instead of going through here.
        var nodeDir = WorkflowRunPaths.NodeDir(_runsDirectory, run.Id, node.Id);
        Directory.CreateDirectory(nodeDir);

        var agentUpstreamIds = definition.Edges
            .Where(e => string.Equals(e.ToNodeId, node.Id, StringComparison.OrdinalIgnoreCase))
            .Select(e => e.FromNodeId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var agentUpstreamDirs = agentUpstreamIds
            .Select(id => WorkflowRunPaths.NodeDir(_runsDirectory, run.Id, id))
            .Where(Directory.Exists)
            .ToList();

        await DispatchAgentInScopeAsync(
            node, run, definition, nr,
            outputDir: nodeDir,
            upstreamDirs: agentUpstreamDirs,
            extraEnv: null,
            cancellation: cancellation).ConfigureAwait(false);
    }

    /// <summary>
    /// Core agent-dispatch path. Resolves the agent + mode, builds env
    /// overrides (workflow.Env + DOXIE_WORKFLOW_OUTPUT_DIR + INPUT_DIRS
    /// + caller-supplied <paramref name="extraEnv"/>), substitutes
    /// trigger placeholders into Inputs, and awaits the subprocess via
    /// the agent runner. Used both by the default per-node scope (one
    /// dispatch per workflow run) and by the Loop primitive's iteration
    /// logic (one dispatch per iteration with iteration-scoped folders
    /// + LOOP_ITEM/INDEX/TOTAL env). The caller controls path scoping;
    /// this method does no path computation of its own.
    /// </summary>
    private async Task DispatchAgentInScopeAsync(
        WorkflowNode node,
        WorkflowRun run,
        WorkflowDefinition definition,
        WorkflowNodeRun nr,
        string outputDir,
        IReadOnlyList<string> upstreamDirs,
        IReadOnlyDictionary<string, string>? extraEnv,
        CancellationToken cancellation)
    {
        var agent = _agentCatalog.FindById(node.AgentId!)
            ?? throw new InvalidOperationException($"Unknown agent '{node.AgentId}' in node '{node.Id}'.");

        var displayId = agent.Name;
        var modeId = node.AgentMode ?? agent.Modes?.FirstOrDefault()?.Id ?? string.Empty;
        var mode = agent.Modes?.FirstOrDefault(m => string.Equals(m.Id, modeId, StringComparison.OrdinalIgnoreCase));

        Directory.CreateDirectory(outputDir);

        Workspace? workflowWorkspace = null;
        if (!string.IsNullOrWhiteSpace(definition.WorkspaceId))
        {
            workflowWorkspace = _workspaceStore.GetById(definition.WorkspaceId);
            if (workflowWorkspace is null)
            {
                throw new InvalidOperationException(
                    $"Workflow '{definition.Id}' is bound to workspace '{definition.WorkspaceId}' which does not exist.");
            }
        }

        // Build the env merge: workflow's own block + caller-supplied
        // extras (Loop iteration vars) + the workflow-runtime convention
        // vars (DOXIE_WORKFLOW_OUTPUT_DIR + INPUT_DIRS). Workspace-level
        // .env layering is a future improvement — for now the workspace
        // is just the cwd.
        var envOverrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (definition.Env is { Count: > 0 } wfEnv)
        {
            foreach (var pair in wfEnv) envOverrides[pair.Key] = pair.Value;
        }
        if (extraEnv is { Count: > 0 })
        {
            foreach (var pair in extraEnv) envOverrides[pair.Key] = pair.Value;
        }
        var workflowRoot = ResolveWorkflowRoot(definition);
        envOverrides[WorkflowRunPaths.WorkflowIdEnvVar] = definition.Id;
        envOverrides[WorkflowRunPaths.WorkflowRootEnvVar] = workflowRoot;
        envOverrides[WorkflowRunPaths.OutputDirEnvVar] = outputDir;
        if (workflowWorkspace is not null)
        {
            envOverrides[WorkflowRunPaths.WorkspaceIdEnvVar] = workflowWorkspace.Id;
            envOverrides[WorkflowRunPaths.WorkspacePathEnvVar] = workflowWorkspace.Path;
        }
        if (upstreamDirs.Count > 0)
        {
            envOverrides[WorkflowRunPaths.InputDirsEnvVar] = string.Join(';', upstreamDirs);
        }

        nr.AppendLog($"env: {WorkflowRunPaths.WorkflowIdEnvVar}={definition.Id}");
        nr.AppendLog($"env: {WorkflowRunPaths.WorkflowRootEnvVar}={workflowRoot}");
        nr.AppendLog($"env: {WorkflowRunPaths.OutputDirEnvVar}={outputDir}");
        if (workflowWorkspace is not null)
        {
            nr.AppendLog($"env: {WorkflowRunPaths.WorkspaceIdEnvVar}={workflowWorkspace.Id}");
            nr.AppendLog($"env: {WorkflowRunPaths.WorkspacePathEnvVar}={workflowWorkspace.Path}");
        }
        if (upstreamDirs.Count > 0)
        {
            nr.AppendLog($"env: {WorkflowRunPaths.InputDirsEnvVar}={string.Join(';', upstreamDirs)}");
        }
        if (definition.Env is { Count: > 0 } wfEnvLog)
        {
            foreach (var pair in wfEnvLog)
            {
                var masked = LooksSecret(pair.Key) ? "***" : pair.Value;
                nr.AppendLog($"env: {pair.Key}={masked}");
            }
        }
        if (extraEnv is { Count: > 0 })
        {
            foreach (var pair in extraEnv)
            {
                var masked = LooksSecret(pair.Key) ? "***" : pair.Value;
                nr.AppendLog($"env: {pair.Key}={masked}");
            }
        }

        // Resolve cwd from the node workspace, or the workflow workspace
        // when the node does not override it. This makes the attached
        // workspace's CLAUDE.md / AGENTS.md / mounted libraries act as
        // iterative memory for every agent in the workflow.
        string? cwd = null;
        var resolvedNodeWorkspaceId = ApplyInputSubstitution(node.WorkspaceId, run.TriggerInputs, extraEnv);
        var dispatchWorkspaceId = !string.IsNullOrWhiteSpace(resolvedNodeWorkspaceId)
            ? resolvedNodeWorkspaceId
            : workflowWorkspace?.Id;
        if (!string.IsNullOrEmpty(dispatchWorkspaceId))
        {
            var ws = string.Equals(dispatchWorkspaceId, workflowWorkspace?.Id, StringComparison.OrdinalIgnoreCase)
                ? workflowWorkspace
                : _workspaceStore.GetById(dispatchWorkspaceId);
            if (ws is null)
            {
                throw new InvalidOperationException(
                    $"Node '{node.Id}' is bound to workspace '{dispatchWorkspaceId}' which does not exist.");
            }
            cwd = ws.Path;
            envOverrides[WorkflowRunPaths.WorkspaceIdEnvVar] = ws.Id;
            envOverrides[WorkflowRunPaths.WorkspacePathEnvVar] = ws.Path;
            nr.AppendLog($"cwd: workspace '{dispatchWorkspaceId}' ({ws.Path})");
        }

        // Substitute {{trigger.<id>}} placeholders in node.Inputs so
        // the agent receives the user-supplied values. Done once here
        // (before BuildArguments + log lines) so the dispatched
        // arguments and the displayed log both reflect the resolved
        // string instead of leaking the placeholder.
        var effectiveInputs = ApplyInputSubstitutions(node.Inputs, run.TriggerInputs, extraEnv);

        // Build the arguments string by substituting effectiveInputs
        // into the mode's template. Falls back to whatever inputs are
        // present formatted naively if no mode is registered.
        var arguments = mode is not null
            ? mode.BuildArguments(effectiveInputs)
            : string.Join(' ', (effectiveInputs ?? new Dictionary<string, string>()).Select(p => $"--{p.Key} {p.Value}"));
        arguments = AppendWorkflowRuntimeGuidance(arguments);
        arguments = AppendOperatorHints(arguments, SnapshotOperatorHints(run.Id));

        nr.AppendLog($"dispatching agent '{displayId}' (mode: {(string.IsNullOrEmpty(modeId) ? "default" : modeId)})");
        if (!string.IsNullOrEmpty(arguments))
        {
            nr.AppendLog(arguments.Length > 2000
                ? $"  args: {arguments[..2000]}... [truncated; full payload is passed to the agent runner]"
                : $"  args: {arguments}");
        }
        Notify(run);

        // Subscribe to the agent runner's events so we can mirror its
        // output lines into this workflow node's log as they stream in,
        // and detect terminal status transitions to release the await.
        var tcs = new TaskCompletionSource<AgentRunStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        string? agentRunId = null;
        string? agentReportedOutputDir = null;
        var mirroredLineCount = 0;
        var stdoutLines = new List<string>();

        void OnAgentUpdated(AgentRun ar)
        {
            // Filter — the agent runner publishes events for every run it
            // owns, including any other workflow steps in flight.
            if (agentRunId is null || !string.Equals(ar.Id, agentRunId, StringComparison.OrdinalIgnoreCase)) return;

            if (!string.IsNullOrWhiteSpace(ar.OutputDir))
            {
                agentReportedOutputDir = ar.OutputDir;
            }

            // Mirror only newly-arrived lines to keep the log monotonic.
            if (ar.Output.Count > mirroredLineCount)
            {
                for (var i = mirroredLineCount; i < ar.Output.Count; i++)
                {
                    var line = ar.Output[i];
                    var prefix = line.Source == AgentRunOutputSource.Stderr ? "[stderr] " : string.Empty;
                    nr.AppendLog(prefix + line.Text);
                    if (line.Source == AgentRunOutputSource.Stdout && !string.IsNullOrWhiteSpace(line.Text))
                    {
                        stdoutLines.Add(line.Text);
                    }
                }
                mirroredLineCount = ar.Output.Count;
                Notify(run);
            }

            if (ar.Status is AgentRunStatus.Completed
                          or AgentRunStatus.Failed
                          or AgentRunStatus.Cancelled
                          or AgentRunStatus.Interrupted)
            {
                tcs.TrySetResult(ar.Status);
            }
        }

        _agentRunner.RunUpdated += OnAgentUpdated;
        try
        {
            var operatorHintSnapshot = SnapshotOperatorHints(run.Id);
            var agentRun = _agentRunner.Start(
                agentId: agent.Id,
                arguments: arguments,
                keepSessionAlive: mode?.KeepSessionAlive ?? false,
                workingDirectoryOverride: cwd,
                envOverrides: envOverrides,
                modeId: mode?.Id,
                workspaceId: dispatchWorkspaceId);
            agentRunId = agentRun.Id;
            _activeAgentRunsByWorkflowRunId
                .GetOrAdd(run.Id, _ => new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase))[nr.NodeId] = agentRun.Id;

            foreach (var hint in operatorHintSnapshot)
            {
                _agentRunner.AddOperatorHint(agentRun.Id, $"Workflow guidance before node `{node.Id}`: {hint}");
            }

            // If the workflow run is cancelled, propagate to the agent
            // subprocess via the runner. Don't OperationCanceledException
            // ourselves — the agent's own status transition will end the wait.
            using var ctReg = cancellation.Register(() =>
            {
                _agentRunner.Cancel(agentRunId);
            });

            var finalStatus = await tcs.Task.ConfigureAwait(false);

            if (finalStatus == AgentRunStatus.Completed)
            {
                await RecoverAgentOutputArtifactsAsync(agent.Id, outputDir, agentReportedOutputDir, nr, cancellation)
                    .ConfigureAwait(false);
                await EnforceAgentGateContractAsync(agent.Id, outputDir, effectiveInputs, cancellation).ConfigureAwait(false);
                await WorkflowOutputArtifacts.MaterializeStdoutAsync(outputDir, stdoutLines, cancellation).ConfigureAwait(false);
                await WorkflowOutputArtifacts.EnsureManifestAsync(
                    outputDir,
                    producerId: agent.Id,
                    producerName: agent.Name,
                    kind: "workflow-agent-node",
                    title: $"{agent.Name} output",
                    tags: new[] { "workflow", "agent", definition.Id, node.Id, agent.Id, modeId },
                    cancellation).ConfigureAwait(false);
                nr.AppendLog($"agent run completed (exit ok)");
                nr.OutputSummary = $"{displayId} â†’ {outputDir}";
            }
            else if (finalStatus == AgentRunStatus.Cancelled)
            {
                // Translate to OperationCanceledException so the outer
                // try/catch in SimulateNodeAsync flips the node status to
                // Skipped (matches user-cancelled semantics).
                throw new OperationCanceledException(cancellation);
            }
            else
            {
                throw new InvalidOperationException($"agent run ended with status {finalStatus}");
            }
        }
        finally
        {
            if (agentRunId is not null
                && _activeAgentRunsByWorkflowRunId.TryGetValue(run.Id, out var activeAgents))
            {
                activeAgents.TryRemove(nr.NodeId, out _);
            }
            _agentRunner.RunUpdated -= OnAgentUpdated;
        }
    }

    private static async Task EnforceAgentGateContractAsync(
        string agentId,
        string outputDir,
        IReadOnlyDictionary<string, string>? nodeInputs,
        CancellationToken cancellation)
    {
        if (!string.Equals(agentId, "breaking-stories", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var gate = await TryReadGateJsonAsync(outputDir, cancellation).ConfigureAwait(false)
            ?? await TryReadBreakingStoriesReportAsync(outputDir, cancellation).ConfigureAwait(false);

        if (gate is null)
        {
            throw new InvalidOperationException(
                "breaking-stories did not produce .doxie-gate.json or breaking-stories-report.md with a Status line.");
        }

        if (IsGatePass(gate.Value.Status))
        {
            return;
        }

        if (!ShouldFailOnBreakingBlock(nodeInputs))
        {
            return;
        }

        var reason = string.IsNullOrWhiteSpace(gate.Value.Reason) ? string.Empty : $": {gate.Value.Reason}";
        throw new InvalidOperationException($"breaking-stories blocked delivery ({gate.Value.Status} from {gate.Value.Source}){reason}");
    }

    private async Task RecoverAgentOutputArtifactsAsync(
        string agentId,
        string outputDir,
        string? reportedOutputDir,
        WorkflowNodeRun nr,
        CancellationToken cancellation)
    {
        if (HasIndexableFiles(outputDir))
        {
            return;
        }

        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(reportedOutputDir))
        {
            candidates.Add(reportedOutputDir);
        }
        candidates.AddRange(FindStandaloneOutputCandidates(agentId, outputDir, nr.StartedAt));

        foreach (var candidate in candidates
                     .Where(Directory.Exists)
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .Where(candidate => !PathsEqual(candidate, outputDir))
                     .OrderByDescending(Directory.GetLastWriteTimeUtc))
        {
            var copied = await CopyOutputArtifactsAsync(candidate, outputDir, cancellation).ConfigureAwait(false);
            if (copied == 0)
            {
                continue;
            }

            nr.AppendLog($"[output guardrail] recovered {copied} artifact file(s) from {candidate}");
            return;
        }
    }

    private static IReadOnlyList<string> FindStandaloneOutputCandidates(
        string agentId,
        string outputDir,
        DateTimeOffset? startedAt)
    {
        var workspaceRoot = TryFindWorkspaceRootFromRunsPath(outputDir);
        if (workspaceRoot is null || !Directory.Exists(workspaceRoot))
        {
            return Array.Empty<string>();
        }

        var cutoff = (startedAt ?? DateTimeOffset.UtcNow.AddMinutes(-30)).UtcDateTime.AddMinutes(-10);
        var safeAgentId = new string(agentId.Select(ch => char.IsLetterOrDigit(ch) || ch == '-' ? ch : '-').ToArray()).Trim('-');
        var roots = Directory.EnumerateDirectories(workspaceRoot, $".{safeAgentId}*runs", SearchOption.TopDirectoryOnly)
            .Where(dir => Directory.GetLastWriteTimeUtc(dir) >= cutoff)
            .ToList();

        return roots
            .SelectMany(root => Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly).DefaultIfEmpty(root))
            .Where(dir => Directory.GetLastWriteTimeUtc(dir) >= cutoff)
            .Where(HasIndexableFiles)
            .OrderByDescending(Directory.GetLastWriteTimeUtc)
            .Take(5)
            .ToList();
    }

    private static string? TryFindWorkspaceRootFromRunsPath(string outputDir)
    {
        var dir = new DirectoryInfo(Path.GetFullPath(outputDir));
        while (dir is not null)
        {
            if (string.Equals(dir.Name, ".runs", StringComparison.OrdinalIgnoreCase))
            {
                return dir.Parent?.FullName;
            }
            dir = dir.Parent;
        }
        return null;
    }

    private static async Task<int> CopyOutputArtifactsAsync(
        string sourceDir,
        string outputDir,
        CancellationToken cancellation)
    {
        var sourceFull = Path.GetFullPath(sourceDir);
        var outputFull = Path.GetFullPath(outputDir);
        Directory.CreateDirectory(outputFull);

        var copied = 0;
        foreach (var sourceFile in Directory.EnumerateFiles(sourceFull, "*", SearchOption.AllDirectories))
        {
            cancellation.ThrowIfCancellationRequested();

            var relative = Path.GetRelativePath(sourceFull, sourceFile);
            if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
            {
                continue;
            }
            if (string.Equals(Path.GetFileName(relative), ".doxie-artifact.json", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var destination = Path.GetFullPath(Path.Combine(outputFull, relative));
            if (!destination.StartsWith(outputFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var parent = Path.GetDirectoryName(destination);
            if (!string.IsNullOrWhiteSpace(parent))
            {
                Directory.CreateDirectory(parent);
            }

            await using var input = File.OpenRead(sourceFile);
            await using var output = File.Create(destination);
            await input.CopyToAsync(output, cancellation).ConfigureAwait(false);
            copied++;
        }

        return copied;
    }

    private static bool HasIndexableFiles(string outputDir) =>
        Directory.Exists(outputDir)
        && Directory.EnumerateFiles(outputDir, "*.*", SearchOption.AllDirectories)
            .Select(file => Path.GetRelativePath(outputDir, file))
            .Any(IsWorkflowIndexableFile);

    private static bool IsWorkflowIndexableFile(string relativePath)
    {
        var file = Path.GetFileName(relativePath);
        if (file.StartsWith(".", StringComparison.Ordinal)
            && !string.Equals(file, ".doxie-gate.json", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var extension = Path.GetExtension(file);
        return extension is ".md" or ".json" or ".txt" or ".yaml" or ".yml" or ".csv" or ".html" or ".xml" or ".log";
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    private static bool ShouldFailOnBreakingBlock(IReadOnlyDictionary<string, string>? nodeInputs)
    {
        if (nodeInputs is null)
        {
            return true;
        }

        if (!nodeInputs.TryGetValue("fail-on-breaking-block", out var raw)
            && !nodeInputs.TryGetValue("fail-on-block", out raw))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        return raw.Trim() switch
        {
            "0" => false,
            var value when value.Equals("false", StringComparison.OrdinalIgnoreCase) => false,
            var value when value.Equals("no", StringComparison.OrdinalIgnoreCase) => false,
            var value when value.Equals("nao", StringComparison.OrdinalIgnoreCase) => false,
            var value when value.Equals("nÃ£o", StringComparison.OrdinalIgnoreCase) => false,
            var value when value.Equals("off", StringComparison.OrdinalIgnoreCase) => false,
            _ => true,
        };
    }

    private static async Task<(string Status, string Source, string? Reason)?> TryReadGateJsonAsync(
        string outputDir,
        CancellationToken cancellation)
    {
        var path = Path.Combine(outputDir, ".doxie-gate.json");
        if (!File.Exists(path))
        {
            return null;
        }

        await using var stream = File.OpenRead(path);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellation).ConfigureAwait(false);
        if (!doc.RootElement.TryGetProperty("status", out var statusElement))
        {
            return null;
        }

        var status = statusElement.GetString();
        if (string.IsNullOrWhiteSpace(status))
        {
            return null;
        }

        var reason = doc.RootElement.TryGetProperty("reason", out var reasonElement)
            ? reasonElement.GetString()
            : null;
        return (status.Trim(), ".doxie-gate.json", reason);
    }

    private static async Task<(string Status, string Source, string? Reason)?> TryReadBreakingStoriesReportAsync(
        string outputDir,
        CancellationToken cancellation)
    {
        var path = Path.Combine(outputDir, "breaking-stories-report.md");
        if (!File.Exists(path))
        {
            return null;
        }

        var lines = await File.ReadAllLinesAsync(path, cancellation).ConfigureAwait(false);
        foreach (var line in lines)
        {
            var marker = line.IndexOf("Status", StringComparison.OrdinalIgnoreCase);
            if (marker < 0)
            {
                continue;
            }

            var colon = line.IndexOf(':', marker);
            if (colon < 0 || colon == line.Length - 1)
            {
                continue;
            }

            var status = line[(colon + 1)..].Trim().Trim('*', '`', ' ');
            if (!string.IsNullOrWhiteSpace(status))
            {
                return (status, "breaking-stories-report.md", null);
            }
        }

        return null;
    }

    private static bool IsGatePass(string status)
    {
        return string.Equals(status, "pass", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "delivery-ready", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "override", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "override-ready", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Park the workflow at this node until a human clicks Approve /
    /// Reject in the run detail UI. Returns cleanly on Approve (the
    /// outer wrapper then sets node Status=Succeeded). On Reject,
    /// throws so the outer catch lands the node as Failed and we
    /// trigger run-wide cancellation here.
    /// </summary>
    private async Task SimulateAggregateNodeAsync(
        WorkflowNode node,
        WorkflowRun run,
        WorkflowDefinition definition,
        WorkflowNodeRun nr,
        CancellationToken cancellation)
    {
        // Walk the workflow's edges to identify the *direct* upstream
        // nodes (counting all-succeeded across the run is misleading
        // when the graph has multiple branches that don't actually
        // converge here).
        var upstreamIds = definition.Edges
            .Where(e => string.Equals(e.ToNodeId, node.Id, StringComparison.OrdinalIgnoreCase))
            .Select(e => e.FromNodeId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var upstreamDirs = upstreamIds
            .Select(id => WorkflowRunPaths.NodeDir(_runsDirectory, run.Id, id))
            .Where(Directory.Exists)
            .ToList();

        nr.AppendLog($"env: {WorkflowRunPaths.InputDirsEnvVar}={string.Join(';', upstreamDirs)}");
        nr.AppendLog($"collecting payloads from {upstreamDirs.Count} upstream node(s)…");
        Notify(run);
        await Task.Delay(250, cancellation).ConfigureAwait(false);

        // Concatenate every file from every upstream folder into a
        // single merged.md artefact under this aggregate's own folder.
        // A real aggregator would do something more thoughtful — type
        // detection, structured merging — but for the simulator
        // string concatenation is the contract.
        var aggDir = WorkflowRunPaths.NodeDir(_runsDirectory, run.Id, node.Id);
        Directory.CreateDirectory(aggDir);

        var allFiles = upstreamDirs
            .SelectMany(d => Directory.EnumerateFiles(d))
            .ToList();
        var sections = new List<string>();
        foreach (var file in allFiles)
        {
            var rel = Path.GetFileName(file);
            var body = await File.ReadAllTextAsync(file, cancellation).ConfigureAwait(false);
            sections.Add($"<!-- from: {rel} -->\n{body}");
        }
        var merged = sections.Count == 0
            ? "(no upstream files to merge)"
            : string.Join("\n\n---\n\n", sections);
        await File.WriteAllTextAsync(Path.Combine(aggDir, "merged.md"), merged, cancellation).ConfigureAwait(false);
        await WorkflowOutputArtifacts.WriteManifestAsync(
            aggDir,
            new WorkflowOutputArtifactManifest(
                "doxie.output-artifact.v1",
                definition.Id,
                definition.Name,
                "workflow-aggregate",
                $"{definition.Name} aggregate",
                new[] { "workflow", "aggregate", definition.Id, node.Id },
                new[] { "merged.md" }),
            cancellation).ConfigureAwait(false);

        nr.AppendLog($"merged {allFiles.Count} file(s) â†’ merged.md");
        nr.OutputSummary = $"aggregated {allFiles.Count} file(s) from {upstreamDirs.Count} upstream(s)";
    }

    private string ResolveWorkflowRoot(WorkflowDefinition definition)
    {
        var catalog = _catalogStore?.FindCatalog(definition.CatalogId);
        if (catalog is not null)
        {
            return Path.Combine(catalog.WorkflowsDirectory, definition.Id);
        }

        return Path.Combine(StorageOptions.ResolvePath(_storage.WorkflowsDirectory), definition.Id);
    }

    private async Task SimulateOutputNodeAsync(
        WorkflowNode node,
        WorkflowRun run,
        WorkflowDefinition definition,
        WorkflowNodeRun nr,
        CancellationToken cancellation)
    {
        // Same upstream-folder convention as Aggregate: collect direct
        // upstream node folders so the delivered artifact reflects the
        // workflow's actual produced content, not a stub.
        var upstreamIds = definition.Edges
            .Where(e => string.Equals(e.ToNodeId, node.Id, StringComparison.OrdinalIgnoreCase))
            .Select(e => e.FromNodeId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var upstreamDirs = upstreamIds
            .Select(id => WorkflowRunPaths.NodeDir(_runsDirectory, run.Id, id))
            .Where(Directory.Exists)
            .ToList();
        if (upstreamDirs.Count > 0)
        {
            nr.AppendLog($"env: {WorkflowRunPaths.InputDirsEnvVar}={string.Join(';', upstreamDirs)}");
        }

        nr.AppendLog("preparing final payload…");
        Notify(run);
        await Task.Delay(200, cancellation).ConfigureAwait(false);

        // Substitute trigger placeholders so a workspace / filename
        // pulled from a trigger input resolves correctly.
        var resolvedInputs = ApplyTriggerSubstitutions(node.Inputs, run.TriggerInputs);

        // Resolution order: dedicated fields (legacy structural Output
        // node) â†’ mode-input fields named "workspace" / "filename" (set
        // by the builder when the user picks the write-to-workspace
        // agent). Lets both authoring paths point at the same target.
        var workspaceId = !string.IsNullOrEmpty(node.OutputWorkspaceId)
            ? node.OutputWorkspaceId
            : resolvedInputs?.GetValueOrDefault("workspace");

        if (string.IsNullOrEmpty(workspaceId))
        {
            nr.AppendLog("(no target workspace bound — payload discarded)");
            nr.OutputSummary = "discarded — no workspace bound";
            return;
        }

        var workspace = _workspaceStore.GetById(workspaceId);
        if (workspace is null)
        {
            // Don't fail the run for a missing workspace in the prototype —
            // the demo seed may run before a workspace is created. Log it
            // so the user can see what they need to do.
            nr.AppendLog($"workspace '{workspaceId}' not found — file would have been written there");
            nr.OutputSummary = $"missing workspace: {workspaceId}";
            return;
        }

        var configuredFileName = !string.IsNullOrEmpty(node.OutputFileName)
            ? node.OutputFileName
            : resolvedInputs?.GetValueOrDefault("filename");
        var fileName = string.IsNullOrEmpty(configuredFileName)
            ? $"workflow-{DateTime.UtcNow:yyyyMMdd-HHmmss}.md"
            : configuredFileName;

        // Compose the delivered file's body from real upstream content
        // when available; fall back to a stub if nothing flowed in (e.g.
        // an Output node wired straight off the trigger).
        var allUpstreamFiles = upstreamDirs
            .SelectMany(d => Directory.EnumerateFiles(d))
            .ToList();
        var contents = allUpstreamFiles.Count == 0
            ? $"""
              # Workflow output

              Generated by DoxieOS workflow simulator at {DateTime.UtcNow:O}.

              (No upstream content to include — this Output node had no
              upstream files. A typical workflow ends with an Aggregate
              feeding the Output node, which then merges every prior
              step's produced files here.)
              """
            : await WorkflowOutputArtifacts.ComposeFromUpstreamAsync(allUpstreamFiles, run, cancellation).ConfigureAwait(false);

        var path = Path.Combine(workspace.Path, fileName);
        try
        {
            await File.WriteAllTextAsync(path, contents, cancellation).ConfigureAwait(false);
            await WorkflowOutputArtifacts.WriteManifestAsync(
                workspace.Path,
                new WorkflowOutputArtifactManifest(
                    "doxie.output-artifact.v1",
                    definition.Id,
                    definition.Name,
                    "workflow-output",
                    Path.GetFileNameWithoutExtension(fileName),
                    new[] { "workflow", "workspace-output", definition.Id, node.Id, workspace.Id },
                    new[] { fileName }),
                cancellation).ConfigureAwait(false);
            nr.AppendLog($"wrote {fileName} into workspace '{workspace.Id}' ({allUpstreamFiles.Count} upstream file(s))");
            nr.OutputSummary = $"{workspace.Id}/{fileName}";
        }
        catch (IOException ex)
        {
            nr.AppendLog($"[warn] could not write {fileName}: {ex.Message}");
            nr.OutputSummary = "(write failed — see logs)";
        }
    }
}
