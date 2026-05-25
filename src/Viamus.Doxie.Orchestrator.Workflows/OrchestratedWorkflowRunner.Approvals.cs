using System.Net.Http.Json;
using System.Text.RegularExpressions;

namespace Viamus.Doxie.Orchestrator.Workflows;

public sealed partial class OrchestratedWorkflowRunner
{
    private async Task SimulateApprovalGateAsync(
        WorkflowNode node,
        WorkflowRun run,
        WorkflowDefinition definition,
        WorkflowNodeRun nr,
        CancellationToken cancellation)
    {
        nr.Status = WorkflowNodeRunStatus.AwaitingApproval;
        // Substitute {{trigger.<id>}} so the prompt the human sees
        // reflects the actual run values, not a literal placeholder.
        var resolvedInputs = ApplyTriggerSubstitutions(node.Inputs, run.TriggerInputs);
        var prompt = resolvedInputs?.GetValueOrDefault("prompt");
        nr.AppendLog("[approval-gate] paused Ã¢â‚¬â€ waiting for human decision");
        if (!string.IsNullOrWhiteSpace(prompt))
        {
            nr.AppendLog($"[approval-gate] prompt: {prompt}");
        }

        // Bump run-level status so the dashboard shows the parked
        // state. Snapshot to disk so a tab refresh reflects it.
        run.Status = WorkflowRunStatus.AwaitingApproval;
        Notify(run);
        try { _runStore.Save(run); } catch { /* persist failure shouldn't tear down the run */ }

        // Best-effort fan-out: ping the local notifications endpoint so
        // any open DoxieOS tab fires a Snackbar. Same convention agents
        // use via DOXIE_NOTIFY_URL Ã¢â‚¬â€ fire-and-forget, swallow errors so
        // a missing endpoint never breaks the gate.
        _ = Task.Run(() => TryNotifyApprovalGateAsync(run, node, prompt));

        // Park.
        var tcs = new TaskCompletionSource<ApprovalDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        var key = (RunId: run.Id, NodeId: node.Id);
        _pendingApprovals[key] = tcs;
        try
        {
            // If the run is cancelled (user clicks Cancel on the run, or
            // a sibling reject triggers cancellation), unpark by
            // throwing OperationCanceledException Ã¢â‚¬â€ the outer catch
            // marks this node Skipped via the standard cancel path.
            using var ctReg = cancellation.Register(() => tcs.TrySetCanceled(cancellation));
            var decision = await tcs.Task.ConfigureAwait(false);

            if (!decision.Approve)
            {
                var reason = string.IsNullOrWhiteSpace(decision.Comment)
                    ? "(no reason given)"
                    : decision.Comment;
                nr.AppendLog($"[approval-gate] rejected Ã¢â‚¬â€ {reason}");
                nr.OutputSummary = $"rejected: {reason}";

                // Cancel the rest of the run (other branches still
                // running, downstream nodes pending). The throw lands
                // this node as Failed in the outer catch.
                Cancel(run.Id);
                throw new InvalidOperationException($"approval gate rejected: {reason}");
            }

            // Approved Ã¢â‚¬â€ flip run status back to Running so subsequent
            // notify cycles don't show a stale AwaitingApproval. (If
            // another gate is also parked elsewhere it'll re-flip on
            // its own dispatch.)
            run.Status = WorkflowRunStatus.Running;
            var who = string.IsNullOrWhiteSpace(decision.Comment) ? "" : $" Ã¢â‚¬â€ {decision.Comment}";
            nr.AppendLog($"[approval-gate] approved{who}");
            nr.OutputSummary = "approved";

            // Pass-through: forward upstream payload (verbatim copy of
            // each direct upstream node's output dir) into our own dir,
            // plus stamp _approval-comment.md with whatever the human
            // typed at Approve time. Downstream nodes wired after the
            // gate then see the original payload + the comment in their
            // DOXIE_WORKFLOW_INPUT_DIRS Ã¢â‚¬â€ letting the implementer use
            // the comment as last-mile feedback on the plan it consumes.
            await ForwardUpstreamThroughGateAsync(node, run, definition, decision.Comment, nr, cancellation)
                .ConfigureAwait(false);
        }
        finally
        {
            _pendingApprovals.TryRemove(key, out _);
        }
    }

    /// <summary>
    /// Copy every file from each direct upstream node's output dir
    /// into the gate's own output dir, then write
    /// <c>_approval-comment.md</c> with the human's comment (if any).
    /// Skips the comment file's name on the inbound side so we don't
    /// double-stamp if a previous run had one. Best-effort on errors Ã¢â‚¬â€
    /// failing to forward shouldn't fail the whole run, the user will
    /// see the warning in the gate node's logs.
    /// </summary>
    private async Task ForwardUpstreamThroughGateAsync(
        WorkflowNode node,
        WorkflowRun run,
        WorkflowDefinition definition,
        string? comment,
        WorkflowNodeRun nr,
        CancellationToken cancellation)
    {
        var ownDir = WorkflowRunPaths.NodeDir(_runsDirectory, run.Id, node.Id);
        Directory.CreateDirectory(ownDir);

        var upstreamIds = definition.Edges
            .Where(e => string.Equals(e.ToNodeId, node.Id, StringComparison.OrdinalIgnoreCase))
            .Select(e => e.FromNodeId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var copiedCount = 0;
        foreach (var upstreamId in upstreamIds)
        {
            var srcDir = WorkflowRunPaths.NodeDir(_runsDirectory, run.Id, upstreamId);
            if (!Directory.Exists(srcDir)) continue;
            foreach (var srcFile in Directory.EnumerateFiles(srcDir))
            {
                var fileName = Path.GetFileName(srcFile);
                // Don't carry forward a previous run's comment file Ã¢â‚¬â€
                // we always write a fresh one (or skip writing) below.
                if (string.Equals(fileName, ApprovalCommentFileName, StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    using var src = File.OpenRead(srcFile);
                    using var dst = File.Create(Path.Combine(ownDir, fileName));
                    await src.CopyToAsync(dst, cancellation).ConfigureAwait(false);
                    copiedCount++;
                }
                catch (IOException ex)
                {
                    nr.AppendLog($"[approval-gate] [warn] could not forward '{fileName}': {ex.Message}");
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(comment))
        {
            try
            {
                await File.WriteAllTextAsync(
                    Path.Combine(ownDir, ApprovalCommentFileName),
                    comment,
                    cancellation).ConfigureAwait(false);
            }
            catch (IOException ex)
            {
                nr.AppendLog($"[approval-gate] [warn] could not stamp comment file: {ex.Message}");
            }
        }

        var commentNote = string.IsNullOrWhiteSpace(comment) ? "" : $" + {ApprovalCommentFileName}";
        nr.AppendLog($"[approval-gate] forwarded {copiedCount} upstream file(s){commentNote} to {ownDir}");
    }

    /// <summary>
    /// Well-known filename written into the approval-gate's output dir
    /// at Approve time when the human supplied a comment. Downstream
    /// agents (e.g. implement-work-item:from-plan) check for this
    /// file alongside the upstream payload to fold last-mile feedback
    /// into their behaviour.
    /// </summary>
    private const string ApprovalCommentFileName = "_approval-comment.md";

    private static readonly System.Text.RegularExpressions.Regex TriggerPlaceholder =
        new(@"\{\{\s*trigger\.([a-zA-Z][\w-]*)\s*\}\}",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Returns a copy of <paramref name="inputs"/> with every
    /// <c>{{trigger.&lt;id&gt;}}</c> placeholder replaced by the
    /// matching value from <paramref name="triggerInputs"/>. Missing
    /// inputs substitute the empty string Ã¢â‚¬â€ the placeholder vanishes
    /// rather than the dispatch failing Ã¢â‚¬â€ so the user can debug a
    /// half-filled form by reading the rendered logs. Returns null
    /// when <paramref name="inputs"/> is null (preserves the
    /// "no inputs declared" semantic).
    /// </summary>
    private static IReadOnlyDictionary<string, string>? ApplyTriggerSubstitutions(
        IReadOnlyDictionary<string, string>? inputs,
        IReadOnlyDictionary<string, string> triggerInputs)
    {
        if (inputs is null) return null;
        if (inputs.Count == 0) return inputs;

        // Cheap fast path: skip the dictionary copy when no value in
        // the node's inputs even contains a "{{trigger." token.
        var anyMatch = inputs.Values.Any(v => v is not null && v.Contains("{{trigger.", StringComparison.OrdinalIgnoreCase));
        if (!anyMatch) return inputs;

        var result = new Dictionary<string, string>(inputs.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in inputs)
        {
            result[key] = value is null
                ? string.Empty
                : TriggerPlaceholder.Replace(value, m =>
                {
                    var id = m.Groups[1].Value;
                    return triggerInputs.TryGetValue(id, out var v) ? v : string.Empty;
                });
        }
        return result;
    }

    private static string? ApplyTriggerSubstitution(string? value, IReadOnlyDictionary<string, string> triggerInputs)
    {
        if (string.IsNullOrEmpty(value) || !value.Contains("{{trigger.", StringComparison.OrdinalIgnoreCase))
        {
            return value;
        }

        return TriggerPlaceholder.Replace(value, m =>
        {
            var id = m.Groups[1].Value;
            return triggerInputs.TryGetValue(id, out var v) ? v : string.Empty;
        });
    }

    /// <summary>
    /// Best-effort POST to the DoxieOS notifications endpoint so any
    /// open browser tab gets a Snackbar prompt to act on the parked
    /// run. Mirrors the convention agents use via
    /// <c>$env:DOXIE_NOTIFY_URL</c>. Swallows every error Ã¢â‚¬â€ a missing
    /// endpoint or refused connection must never break the gate.
    /// </summary>
    private static async Task TryNotifyApprovalGateAsync(WorkflowRun run, WorkflowNode node, string? prompt)
    {
        var url = Environment.GetEnvironmentVariable("DOXIE_NOTIFY_URL");
        // Default to the orchestrator's own well-known endpoint (same
        // as ClaudeProcessAgentRunner sets for spawned agents).
        if (string.IsNullOrWhiteSpace(url)) url = "http://localhost:5034/api/notifications";

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            var body = new
            {
                title = "Approval needed",
                body = string.IsNullOrWhiteSpace(prompt)
                    ? $"Workflow run {run.Id} parked at gate '{node.Id}'."
                    : prompt,
                severity = "info",
                agentId = "approval-gate",
            };
            await http.PostAsJsonAsync(url, body).ConfigureAwait(false);
        }
        catch
        {
            // Silent Ã¢â‚¬â€ caller never observes the notification result.
        }
    }

}
