using System.Text.Json.Serialization;
using Viamus.Doxie.Orchestrator.Workflows;

namespace Viamus.Doxie.Orchestrator.Builder;

/// <summary>
/// Wire-shape of <c>./.draft/manifest.json</c> for a workflow-builder
/// session. Mirrors <see cref="Workflows.WorkflowDefinition"/> but kept
/// loose / forgiving — Claude is expected to keep this valid at all
/// times, but the API endpoint tolerates partial drafts so the right
/// pane shows progressive refinement instead of red errors.
/// </summary>
public sealed class WorkflowManifest
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("trigger")]
    public WorkflowManifestTrigger? Trigger { get; set; }

    [JsonPropertyName("env")]
    public Dictionary<string, string>? Env { get; set; }

    [JsonPropertyName("workspaceId")]
    public string? WorkspaceId { get; set; }

    /// <summary>
    /// Draft-time enabled state. Defaults to <c>true</c> so manual /
    /// event / webhook drafts come up enabled out of the box. Cron
    /// drafts get force-disabled at promotion time via
    /// <see cref="Workflows.WorkflowDefinition.WithCreateTimeEnabledPolicy"/>
    /// regardless of what the manifest carries — the user enables
    /// the cron manually after filling the workspace's env vars.
    /// </summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("nodes")]
    public List<WorkflowManifestNode>? Nodes { get; set; }

    [JsonPropertyName("edges")]
    public List<WorkflowManifestEdge>? Edges { get; set; }
}

public sealed class WorkflowManifestTrigger
{
    [JsonPropertyName("kind")]
    public string? Kind { get; set; }

    [JsonPropertyName("cronExpression")]
    public string? CronExpression { get; set; }

    [JsonPropertyName("eventName")]
    public string? EventName { get; set; }

    [JsonPropertyName("webhookPath")]
    public string? WebhookPath { get; set; }

    [JsonPropertyName("inputs")]
    public List<WorkflowTriggerInputField>? Inputs { get; set; }
}

public sealed class WorkflowManifestNode
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("kind")]
    public string? Kind { get; set; }

    [JsonPropertyName("label")]
    public string? Label { get; set; }

    [JsonPropertyName("agentId")]
    public string? AgentId { get; set; }

    [JsonPropertyName("agentMode")]
    public string? AgentMode { get; set; }

    [JsonPropertyName("inputs")]
    public Dictionary<string, string>? Inputs { get; set; }

    [JsonPropertyName("workspaceId")]
    public string? WorkspaceId { get; set; }

    [JsonPropertyName("loopId")]
    public string? LoopId { get; set; }

    [JsonPropertyName("outputWorkspaceId")]
    public string? OutputWorkspaceId { get; set; }

    [JsonPropertyName("outputFileName")]
    public string? OutputFileName { get; set; }
}

public sealed class WorkflowManifestEdge
{
    [JsonPropertyName("fromNodeId")]
    public string? FromNodeId { get; set; }

    [JsonPropertyName("toNodeId")]
    public string? ToNodeId { get; set; }

    [JsonPropertyName("condition")]
    public string? Condition { get; set; }
}
