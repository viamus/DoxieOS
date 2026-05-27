using MudBlazor;
using Viamus.Doxie.Orchestrator.Components.Shared;
using Viamus.Doxie.Orchestrator.Workflows;

namespace Viamus.Doxie.Orchestrator.Components.Pages;

public partial class WorkflowDetail
{
    private WorkflowNode? NodeOrNull(string id) =>
        _workflow?.Nodes.FirstOrDefault(n => string.Equals(n.Id, id, StringComparison.OrdinalIgnoreCase));

    private string EdgeColor(string fromId, string toId)
    {
        if (_activeRun is null) return "#6F7168";
        var fromStatus = _activeRun.NodeRun(fromId).Status;
        var toStatus = _activeRun.NodeRun(toId).Status;
        // Edge "lights up" once the upstream succeeds; turns muted gray
        // until the downstream actually consumes it.
        if (fromStatus == WorkflowNodeRunStatus.Succeeded && toStatus != WorkflowNodeRunStatus.Pending) return "#65B891";
        if (fromStatus == WorkflowNodeRunStatus.Succeeded) return "#D97757";
        if (fromStatus == WorkflowNodeRunStatus.Failed || toStatus == WorkflowNodeRunStatus.Skipped) return "#E06A5F";
        return "#6F7168";
    }

    private static string NodeFill(WorkflowNodeRunStatus status) => status switch
    {
        WorkflowNodeRunStatus.Running => "#17202A",
        WorkflowNodeRunStatus.Succeeded => "#17241D",
        WorkflowNodeRunStatus.Failed => "#2A1816",
        WorkflowNodeRunStatus.Skipped => "#251E14",
        WorkflowNodeRunStatus.AwaitingApproval => "#272114",
        _ => "#171816",
    };

    private static string NodeStroke(WorkflowNodeKind kind, WorkflowNodeRunStatus status)
    {
        if (status == WorkflowNodeRunStatus.Running) return "#7AA7D9";
        if (status == WorkflowNodeRunStatus.Succeeded) return "#65B891";
        if (status == WorkflowNodeRunStatus.Failed) return "#E06A5F";
        if (status == WorkflowNodeRunStatus.Skipped) return "#E1A34A";
        if (status == WorkflowNodeRunStatus.AwaitingApproval) return "#D97757";
        return kind switch
        {
            WorkflowNodeKind.Trigger => "#BBA7F2",
            WorkflowNodeKind.Aggregate => "#7FD1C4",
            WorkflowNodeKind.Output => "#D97757",
            WorkflowNodeKind.Loop => "#E1A34A",
            WorkflowNodeKind.Decision => "#DFA5D6",
            _ => "#A9A39A",
        };
    }

    /// <summary>
    /// Mirrors the runner's <c>EffectiveKind</c>: well-known connector
    /// agent ids re-render with their connector identity instead of
    /// the generic "agent" pill — the canvas matches runtime topology.
    /// </summary>
    private static WorkflowNodeKind EffectiveKindForRender(WorkflowNode node)
    {
        if (node.Kind != WorkflowNodeKind.Agent) return node.Kind;
        return node.AgentId switch
        {
            "loop" => WorkflowNodeKind.Loop,
            "aggregate" => WorkflowNodeKind.Aggregate,
            "write-to-workspace" => WorkflowNodeKind.Output,
            "if-else" => WorkflowNodeKind.Decision,
            _ => node.Kind,
        };
    }

    private static string StatusDot(WorkflowNodeRunStatus status) => status switch
    {
        WorkflowNodeRunStatus.Running => "#7AA7D9",
        WorkflowNodeRunStatus.Succeeded => "#65B891",
        WorkflowNodeRunStatus.Failed => "#E06A5F",
        WorkflowNodeRunStatus.Skipped => "#E1A34A",
        WorkflowNodeRunStatus.AwaitingApproval => "#D97757",
        _ => "#6F7168",
    };

    private static string KindLabel(WorkflowNodeKind kind) => kind switch
    {
        WorkflowNodeKind.Trigger => "trigger",
        WorkflowNodeKind.Agent => "agent",
        WorkflowNodeKind.Aggregate => "aggregate",
        WorkflowNodeKind.Output => "output",
        WorkflowNodeKind.Loop => "loop",
        WorkflowNodeKind.Decision => "if/else",
        _ => "node",
    };

    private string SubLine(WorkflowNode node) => EffectiveKindForRender(node) switch
    {
        WorkflowNodeKind.Trigger => Truncate(_workflow is null ? "" : TriggerLabel(_workflow.Trigger), 26),
        WorkflowNodeKind.Agent => Truncate(node.AgentId ?? "(unbound)", 26),
        WorkflowNodeKind.Aggregate => "join",
        WorkflowNodeKind.Output => Truncate($"â†’ {node.OutputWorkspaceId ?? "(none)"}", 26),
        WorkflowNodeKind.Loop => $"iterate · Ã—{node.Inputs?.GetValueOrDefault("concurrency") ?? "4"} · {node.Inputs?.GetValueOrDefault("on_failure") ?? "fail-fast"}",
        WorkflowNodeKind.Decision => $"{node.Inputs?.GetValueOrDefault("operator") ?? "truthy"} · {node.Inputs?.GetValueOrDefault("path") ?? "(root)"}",
        _ => "",
    };

    private static string EdgeConditionLabel(WorkflowEdge edge) =>
        string.IsNullOrWhiteSpace(edge.Condition)
            ? string.Empty
            : edge.Condition.Trim().Equals("false", StringComparison.OrdinalIgnoreCase)
                ? "else"
                : edge.Condition.Trim().Equals("true", StringComparison.OrdinalIgnoreCase)
                    ? "true"
                    : edge.Condition.Trim();

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s.Substring(0, max - 1) + "…";

    /// <summary>
    /// The workspace this node is bound to, for the SVG badge:
    /// (1) explicit per-step <see cref="WorkflowNode.WorkspaceId"/>,
    /// (2) the legacy <see cref="WorkflowNode.OutputWorkspaceId"/> on
    ///     structural Output nodes,
    /// (3) the <c>workspace</c> input on the write-to-workspace agent.
    /// Returns null when the step has no workspace association.
    /// </summary>
    private static bool LooksSecretKey(string key) =>
        key.Contains("token", StringComparison.OrdinalIgnoreCase)
        || key.Contains("secret", StringComparison.OrdinalIgnoreCase)
        || key.Contains("password", StringComparison.OrdinalIgnoreCase)
        || key.Contains("key", StringComparison.OrdinalIgnoreCase)
        || key.EndsWith("_pat", StringComparison.OrdinalIgnoreCase);

    private string? WorkspaceForBadge(WorkflowNode node)
    {
        var workspace = !string.IsNullOrEmpty(node.WorkspaceId) ? node.WorkspaceId
            : !string.IsNullOrEmpty(node.OutputWorkspaceId) ? node.OutputWorkspaceId
            : node.Inputs?.GetValueOrDefault("workspace") is { Length: > 0 } inputWorkspace ? inputWorkspace
            : _workflow?.WorkspaceId;

        return CleanWorkspaceBadge(workspace);
    }

    private static string? CleanWorkspaceBadge(string? workspace) =>
        string.IsNullOrWhiteSpace(workspace) || workspace.Contains("{{", StringComparison.Ordinal)
            ? null
            : workspace;

    private static Color NodeRunChipColor(WorkflowNodeRunStatus status) => status switch
    {
        WorkflowNodeRunStatus.Running => Color.Info,
        WorkflowNodeRunStatus.Succeeded => Color.Success,
        WorkflowNodeRunStatus.Failed => Color.Error,
        WorkflowNodeRunStatus.Skipped => Color.Warning,
        WorkflowNodeRunStatus.AwaitingApproval => Color.Secondary,
        _ => Color.Default,
    };

    private static Color RunStatusColor(WorkflowRunStatus status) => status switch
    {
        WorkflowRunStatus.Running => Color.Info,
        WorkflowRunStatus.Succeeded => Color.Success,
        WorkflowRunStatus.Failed => Color.Error,
        WorkflowRunStatus.Cancelled => Color.Warning,
        WorkflowRunStatus.AwaitingApproval => Color.Secondary,
        _ => Color.Default,
    };

    private static IReadOnlyList<RunLogViewerEntry> WorkflowLogEntries(IReadOnlyList<string> logs) =>
        logs
            .Select((line, index) => new RunLogViewerEntry(
                index + 1,
                null,
                line,
                WorkflowLogKind(line)))
            .ToList();

    private static string WorkflowLogKind(string line)
    {
        if (line.StartsWith("[stderr]", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("[error]", StringComparison.OrdinalIgnoreCase)
            || line.Contains("failed", StringComparison.OrdinalIgnoreCase))
        {
            return "stderr";
        }

        if (line.StartsWith("[", StringComparison.Ordinal))
        {
            return "system";
        }

        return "stdout";
    }

    private static string TriggerLabel(WorkflowTrigger t) => t.Kind switch
    {
        WorkflowTriggerKind.Cron => $"cron · {t.CronExpression ?? "?"}",
        WorkflowTriggerKind.Event => $"event · {t.EventName ?? "?"}",
        WorkflowTriggerKind.Webhook => $"hook · {t.WebhookPath ?? "?"}",
        _ => "manual",
    };

    private static string TriggerIcon(WorkflowTriggerKind kind) => kind switch
    {
        WorkflowTriggerKind.Cron => Icons.Material.Filled.Schedule,
        WorkflowTriggerKind.Event => Icons.Material.Filled.Bolt,
        WorkflowTriggerKind.Webhook => Icons.Material.Filled.Webhook,
        _ => Icons.Material.Filled.PlayArrow,
    };

    private static Color TriggerColor(WorkflowTriggerKind kind) => kind switch
    {
        WorkflowTriggerKind.Cron => Color.Info,
        WorkflowTriggerKind.Event => Color.Warning,
        WorkflowTriggerKind.Webhook => Color.Secondary,
        _ => Color.Default,
    };

    private static string FormatDuration(WorkflowRun r)
    {
        var end = r.FinishedAt ?? DateTimeOffset.UtcNow;
        var d = end - r.StartedAt;
        return d.TotalSeconds < 60 ? $"{d.TotalSeconds:0.0}s"
            : d.TotalMinutes < 60 ? $"{d.TotalMinutes:0.0}m"
            : $"{d.TotalHours:0.0}h";
    }
}
