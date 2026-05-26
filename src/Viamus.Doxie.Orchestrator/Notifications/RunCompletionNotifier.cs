using Microsoft.Extensions.Hosting;
using Viamus.Doxie.Orchestrator.Agents;

namespace Viamus.Doxie.Orchestrator.Notifications;

/// <summary>
/// Hosted service that subscribes to <see cref="IAgentRunner.RunUpdated"/>
/// and publishes a Snackbar notification every time a run reaches a
/// terminal status. Centralises the "the agent finished" toast so each
/// individual skill doesn't have to curl-post one of its own — skills are
/// still free to fire mid-run notifications via <c>$DOXIE_NOTIFY_URL</c>
/// for content specific to the sweep / step / file they just processed.
/// </summary>
public sealed class RunCompletionNotifier : IHostedService, IDisposable
{
    private readonly IAgentRunner _runner;
    private readonly NotificationsBus _bus;
    private readonly IAgentCatalog _catalog;
    private readonly HashSet<string> _notifiedRunIds = new();
    private readonly object _lock = new();

    public RunCompletionNotifier(IAgentRunner runner, NotificationsBus bus, IAgentCatalog catalog)
    {
        _runner = runner;
        _bus = bus;
        _catalog = catalog;
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

    private void OnRunUpdated(AgentRun run)
    {
        if (!IsTerminal(run.Status)) return;

        // RunUpdated can fire multiple times per run (status, output lines,
        // etc.) — emit at most one notification per terminal run.
        lock (_lock)
        {
            if (!_notifiedRunIds.Add(run.Id)) return;
        }

        // Interrupted is only set during the startup reconciliation pass for
        // runs orphaned by a previous DoxieOS exit. Firing toasts for those
        // would mean a wave of warnings on every cold start.
        if (run.Status == AgentRunStatus.Interrupted) return;

        var agentName = _catalog.FindById(run.AgentId)?.Name ?? run.AgentId;
        var duration = (run.FinishedAt ?? DateTimeOffset.UtcNow) - run.StartedAt;
        var durationStr = FormatDuration(duration);

        var (severity, body) = run.Status switch
        {
            AgentRunStatus.Completed => ("success", $"Run completed · {durationStr}"),
            AgentRunStatus.Failed => ("error", $"Run failed · exit {run.ExitCode?.ToString() ?? "?"} · {durationStr}"),
            AgentRunStatus.Cancelled => ("warning", $"Run cancelled · {durationStr}"),
            _ => ("info", "Run finished"),
        };
        var content = NotificationArtifactFormatter.ForAgentRun(run, body);

        _bus.Publish(new Notification(
            Title: agentName,
            Body: content.Body,
            Severity: severity,
            AgentId: run.AgentId,
            Href: $"/agents/{Uri.EscapeDataString(run.AgentId)}?run={Uri.EscapeDataString(run.Id)}",
            Content: content.Content,
            ContentFormat: content.ContentFormat,
            SourcePath: content.SourcePath,
            ContentTruncated: content.ContentTruncated,
            ContentLength: content.ContentLength));
    }

    private static bool IsTerminal(AgentRunStatus status) =>
        status is AgentRunStatus.Completed
            or AgentRunStatus.Failed
            or AgentRunStatus.Cancelled
            or AgentRunStatus.Interrupted;

    private static string FormatDuration(TimeSpan d) =>
        d.TotalSeconds < 60 ? $"{d.TotalSeconds:0.0}s"
        : d.TotalMinutes < 60 ? $"{d.TotalMinutes:0.0}m"
        : $"{d.TotalHours:0.0}h";
}
