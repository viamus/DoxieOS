using Viamus.Doxie.Orchestrator.Components.Shared;
using Viamus.Doxie.Orchestrator.Workflows;

namespace Viamus.Doxie.Orchestrator.Components.Workflows;

public partial class WorkflowForm
{
    private static int CanvasWidth(IReadOnlyList<WorkflowNode> nodes) =>
        WorkflowGraphGeometry.CanvasWidth(nodes, WorkflowLayout.NodeWidth, WorkflowLayout.NodeHeight);

    private static int CanvasHeight(IReadOnlyList<WorkflowNode> nodes) =>
        WorkflowGraphGeometry.CanvasHeight(nodes, WorkflowLayout.NodeWidth, WorkflowLayout.NodeHeight);

    private static string TriggerLabel(WorkflowTrigger t) => t.Kind switch
    {
        WorkflowTriggerKind.Cron => $"cron · {t.CronExpression ?? "?"}",
        WorkflowTriggerKind.Event => $"event · {t.EventName ?? "?"}",
        WorkflowTriggerKind.Webhook => $"hook · {t.WebhookPath ?? "?"}",
        _ => "manual",
    };

    /// <summary>
    /// Mirrors the runner's <c>EffectiveKind</c>: well-known connector
    /// agent ids (<c>loop</c>, <c>aggregate</c>, <c>write-to-workspace</c>)
    /// re-render with their connector identity instead of the generic
    /// "agent" pill — so the canvas matches the runtime topology rather
    /// than the on-disk Kind field.
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

    private static string NodeStroke(WorkflowNodeKind kind) => kind switch
    {
        WorkflowNodeKind.Trigger => "#BBA7F2",
        WorkflowNodeKind.Aggregate => "#7FD1C4",
        WorkflowNodeKind.Output => "#D97757",
        WorkflowNodeKind.Loop => "#E1A34A",
        WorkflowNodeKind.Decision => "#DFA5D6",
        _ => "#A9A39A",
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

    private static string SubLine(WorkflowNode node) => EffectiveKindForRender(node) switch
    {
        WorkflowNodeKind.Trigger => Truncate(node.Label, 26),
        WorkflowNodeKind.Agent => Truncate(node.AgentId ?? "(pick agent)", 26),
        WorkflowNodeKind.Aggregate => "join",
        WorkflowNodeKind.Output => Truncate(node.Inputs?.GetValueOrDefault("workspace") is { Length: > 0 } w ? $"â†’ {w}" : "(no target)", 26),
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

    private string? WorkspaceForBadge(WorkflowNode node)
    {
        var workspace = !string.IsNullOrEmpty(node.WorkspaceId) ? node.WorkspaceId
            : !string.IsNullOrEmpty(node.OutputWorkspaceId) ? node.OutputWorkspaceId
            : node.Inputs?.GetValueOrDefault("workspace") is { Length: > 0 } inputWorkspace ? inputWorkspace
            : string.IsNullOrEmpty(_workflowWorkspaceId) ? null : _workflowWorkspaceId;

        return CleanWorkspaceBadge(workspace);
    }

    private static string? CleanWorkspaceBadge(string? workspace) =>
        string.IsNullOrWhiteSpace(workspace) || workspace.Contains("{{", StringComparison.Ordinal)
            ? null
            : workspace;

    /// <summary>
    /// Heuristic match for "this env-var holds a secret" — drives the
    /// password-style input mask in the env editor. Same rules as
    /// <c>SimulatedWorkflowRunner.LooksSecret</c> /
    /// <c>WorkflowDetail.LooksSecretKey</c> /
    /// <c>AgentDetail.LooksSecretKey</c> so behaviour is consistent
    /// everywhere a workflow env value is rendered.
    /// </summary>
    private static bool LooksSecretKey(string? key) =>
        !string.IsNullOrEmpty(key) && (
            key.Contains("token", StringComparison.OrdinalIgnoreCase)
            || key.Contains("secret", StringComparison.OrdinalIgnoreCase)
            || key.Contains("password", StringComparison.OrdinalIgnoreCase)
            || key.Contains("key", StringComparison.OrdinalIgnoreCase)
            || key.EndsWith("_pat", StringComparison.OrdinalIgnoreCase));
}
