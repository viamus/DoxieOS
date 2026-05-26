using Microsoft.Extensions.Hosting;
using Viamus.Doxie.Orchestrator.Agents;
using Viamus.Doxie.Orchestrator.Workflows;

namespace Viamus.Doxie.Orchestrator.Notifications;

/// <summary>
/// Hosted service that subscribes to <see cref="IWorkflowRunner.RunUpdated"/>
/// and publishes a Snackbar notification every time a workflow run reaches
/// a terminal status. Mirrors <see cref="RunCompletionNotifier"/> (which
/// does the same for individual agent runs) so the user always sees a
/// toast (and a history entry) when something finishes — workflows
/// shouldn't fall silent just because they're a layer up.
///
/// <para>The toast for an in-workflow agent run is suppressed by
/// <see cref="RunCompletionNotifier"/> picking it up first; we don't
/// double-notify for the same logical event. The workflow-level toast
/// supersedes the per-step ones (compact, summary).</para>
/// </summary>
public sealed class WorkflowCompletionNotifier : IHostedService, IDisposable
{
    private readonly IWorkflowRunner _runner;
    private readonly IWorkflowStore _workflowStore;
    private readonly NotificationsBus _bus;
    private readonly string _runsRoot;
    private readonly HashSet<string> _notifiedRunIds = new();
    private readonly object _lock = new();

    public WorkflowCompletionNotifier(
        IWorkflowRunner runner,
        IWorkflowStore workflowStore,
        NotificationsBus bus,
        StorageOptions storage)
    {
        _runner = runner;
        _workflowStore = workflowStore;
        _bus = bus;
        _runsRoot = StorageOptions.ResolvePath(storage.WorkflowRunsDirectory);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _runner.RunUpdated += OnRunUpdated;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _runner.RunUpdated -= OnRunUpdated;
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _runner.RunUpdated -= OnRunUpdated;
    }

    private void OnRunUpdated(WorkflowRun run)
    {
        if (!IsTerminal(run.Status)) return;

        // RunUpdated fires on every node-status transition + log line;
        // de-dupe so we emit at most one notification per terminal run.
        lock (_lock)
        {
            if (!_notifiedRunIds.Add(run.Id)) return;
        }

        var workflow = _workflowStore.GetById(run.WorkflowId);
        var name = workflow?.Name ?? run.WorkflowId;
        var duration = (run.FinishedAt ?? DateTimeOffset.UtcNow) - run.StartedAt;
        var durationStr = FormatDuration(duration);

        // Per-node tally so the toast carries useful detail without
        // burying the user — "5/6 succeeded · 1 failed" is much more
        // actionable than just "Failed".
        var nodes = run.NodeRuns.Values.ToList();
        var ok = nodes.Count(n => n.Status == WorkflowNodeRunStatus.Succeeded);
        var failed = nodes.Count(n => n.Status == WorkflowNodeRunStatus.Failed);
        var skipped = nodes.Count(n => n.Status == WorkflowNodeRunStatus.Skipped);
        var nodeSummary = $"{ok}/{nodes.Count} succeeded"
            + (failed > 0 ? $" · {failed} failed" : string.Empty)
            + (skipped > 0 ? $" · {skipped} skipped" : string.Empty);

        var (severity, headline) = run.Status switch
        {
            WorkflowRunStatus.Succeeded => ("success", $"Workflow finished · {durationStr}"),
            WorkflowRunStatus.Failed => ("error", $"Workflow failed · {durationStr}"),
            WorkflowRunStatus.Cancelled => ("warning", $"Workflow cancelled · {durationStr}"),
            _ => ("info", $"Workflow finished · {durationStr}"),
        };

        var content = NotificationArtifactFormatter.ForWorkflowRun(
            run,
            _runsRoot,
            $"{headline} · {nodeSummary} ({run.TriggeredBy})");

        _bus.Publish(new Notification(
            Title: name,
            Body: content.Body,
            Severity: severity,
            AgentId: $"workflow:{run.WorkflowId}",
            Href: $"/workflows/{Uri.EscapeDataString(run.WorkflowId)}?run={Uri.EscapeDataString(run.Id)}",
            Content: content.Content,
            ContentFormat: content.ContentFormat,
            SourcePath: content.SourcePath,
            ContentTruncated: content.ContentTruncated,
            ContentLength: content.ContentLength));
    }

    private static bool IsTerminal(WorkflowRunStatus status) =>
        status is WorkflowRunStatus.Succeeded
            or WorkflowRunStatus.Failed
            or WorkflowRunStatus.Cancelled;

    private static string FormatDuration(TimeSpan d) =>
        d.TotalSeconds < 60 ? $"{d.TotalSeconds:0.0}s"
        : d.TotalMinutes < 60 ? $"{d.TotalMinutes:0.0}m"
        : $"{d.TotalHours:0.0}h";
}
