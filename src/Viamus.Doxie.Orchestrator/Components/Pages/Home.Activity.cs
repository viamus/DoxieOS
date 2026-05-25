using MudBlazor;
using Viamus.Doxie.Orchestrator.Agents;
using Viamus.Doxie.Orchestrator.Context;
using Viamus.Doxie.Orchestrator.Workflows;

namespace Viamus.Doxie.Orchestrator.Components.Pages;

public partial class Home
{
    private static List<ActivityEntry> BuildRecentActivity(
        IReadOnlyList<WorkflowRun> workflowRuns,
        IReadOnlyList<AgentRun> agentRuns,
        IReadOnlyList<WorkflowDefinition> workflows,
        IReadOnlyList<AgentDescriptor> agents)
    {
        var list = new List<ActivityEntry>();

        foreach (var r in workflowRuns)
        {
            var wf = workflows.FirstOrDefault(w => w.Id == r.WorkflowId);
            list.Add(new ActivityEntry(
                IsWorkflow: true,
                Name: wf?.Name ?? r.WorkflowId,
                StartedAt: r.StartedAt,
                FinishedAt: r.FinishedAt,
                StatusLabel: r.Status.ToString(),
                StatusColor: WorkflowRunStatusColor(r.Status),
                DurationLabel: FormatDuration(r.StartedAt, r.FinishedAt),
                Href: $"/workflows/{r.WorkflowId}?run={r.Id}"));
        }

        foreach (var r in agentRuns)
        {
            // Skip agent runs that are part of a workflow run — those are
            // already represented by their wrapping workflow row, and
            // surfacing them twice clutters the table.
            // (The runner doesn't currently tag this directly; for the
            // prototype we just show every agent run. A future tag
            // could let us hide the workflow-spawned ones.)
            var agent = agents.FirstOrDefault(a => a.Id == r.AgentId);
            list.Add(new ActivityEntry(
                IsWorkflow: false,
                Name: agent?.Name ?? r.AgentId,
                StartedAt: r.StartedAt,
                FinishedAt: r.FinishedAt,
                StatusLabel: r.Status.ToString(),
                StatusColor: AgentRunStatusColor(r.Status),
                DurationLabel: FormatDuration(r.StartedAt, r.FinishedAt),
                Href: $"/agents/{r.AgentId}?run={r.Id}"));
        }

        return list
            .OrderByDescending(e => e.StartedAt)
            .Take(15)
            .ToList();
    }

    private static Color WorkflowRunStatusColor(WorkflowRunStatus status) => status switch
    {
        WorkflowRunStatus.Queued => Color.Default,
        WorkflowRunStatus.Running => Color.Info,
        WorkflowRunStatus.Succeeded => Color.Success,
        WorkflowRunStatus.Failed => Color.Error,
        WorkflowRunStatus.Cancelled => Color.Warning,
        _ => Color.Default,
    };

    private static Color AgentRunStatusColor(AgentRunStatus status) => status switch
    {
        AgentRunStatus.Queued => Color.Default,
        AgentRunStatus.Running => Color.Info,
        AgentRunStatus.Completed => Color.Success,
        AgentRunStatus.Failed => Color.Error,
        AgentRunStatus.Cancelled => Color.Warning,
        AgentRunStatus.Interrupted => Color.Dark,
        _ => Color.Default,
    };

    private static string RelativeTime(DateTimeOffset when)
    {
        var delta = DateTimeOffset.UtcNow - when;
        if (delta.TotalSeconds < 0) return "just now";
        if (delta.TotalSeconds < 60) return $"{(int)delta.TotalSeconds}s ago";
        if (delta.TotalMinutes < 60) return $"{(int)delta.TotalMinutes}m ago";
        if (delta.TotalHours < 24) return $"{(int)delta.TotalHours}h ago";
        return $"{(int)delta.TotalDays}d ago";
    }

    private static string RelativeTime(DateTime whenUtc) =>
        RelativeTime(new DateTimeOffset(whenUtc, TimeSpan.Zero));

    private static string FormatRemaining(TimeSpan ts)
    {
        if (ts.TotalSeconds < 0) return "now";
        if (ts.TotalMinutes < 1) return $"{(int)ts.TotalSeconds}s";
        if (ts.TotalMinutes < 60) return $"{(int)ts.TotalMinutes}m";
        if (ts.TotalHours < 24) return $"{ts.TotalHours:0.0}h";
        return $"{ts.TotalDays:0.0}d";
    }

    private static string FormatDuration(DateTimeOffset start, DateTimeOffset? end)
    {
        var finish = end ?? DateTimeOffset.UtcNow;
        var d = finish - start;
        if (d.TotalSeconds < 60) return $"{d.TotalSeconds:0.0}s";
        if (d.TotalMinutes < 60) return $"{d.TotalMinutes:0.0}m";
        return $"{d.TotalHours:0.0}h";
    }

    private string LocalizeStatus(string status) => status switch
    {
        "Queued" => Lang.CurrentLanguage switch { "pt-BR" => "Na fila", "es" => "En cola", _ => status },
        "Running" => Lang["Common.Running"],
        "Succeeded" => Lang.CurrentLanguage switch { "pt-BR" => "ConcluÃ­do", "es" => "Completado", _ => status },
        "Completed" => Lang.CurrentLanguage switch { "pt-BR" => "ConcluÃ­do", "es" => "Completado", _ => status },
        "Failed" => Lang.CurrentLanguage switch { "pt-BR" => "Falhou", "es" => "FallÃ³", _ => status },
        "Cancelled" => Lang.CurrentLanguage switch { "pt-BR" => "Cancelado", "es" => "Cancelado", _ => status },
        "Interrupted" => Lang.CurrentLanguage switch { "pt-BR" => "Interrompido", "es" => "Interrumpido", _ => status },
        _ => status,
    };

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) || s.Length <= max ? s : s.Substring(0, max - 1) + "…";

    // Builder-kind PTYs don't live on /consoles — that page filters them
    // out (see Consoles.razor's Reload). Route by Kind + the WorkspaceId
    // prefix the BuilderSandboxAllocator stamps on the session so the
    // user lands on the page that actually renders the terminal.
    private static (string Href, string Subtitle, string Pill) ConsoleRouteFor(ConsoleSession s)
    {
        if (s.Kind == ConsoleSessionKind.Builder)
        {
            var sid = Uri.EscapeDataString(s.Id);
            if (s.WorkspaceId.StartsWith("builder-agent-", StringComparison.Ordinal))
                return ($"/agents/new?session={sid}", $"agent builder · {s.WorkspaceId}", "Builder");
            if (s.WorkspaceId.StartsWith("builder-workflow-", StringComparison.Ordinal))
                return ($"/workflows/new/chat?session={sid}", $"workflow builder · {s.WorkspaceId}", "Builder");
        }
        return ($"/consoles?id={Uri.EscapeDataString(s.Id)}", $"console {s.WorkspaceId}", "PTY");
    }

    private sealed record ActivityEntry(
        bool IsWorkflow,
        string Name,
        DateTimeOffset StartedAt,
        DateTimeOffset? FinishedAt,
        string StatusLabel,
        Color StatusColor,
        string DurationLabel,
        string Href);
}
