using Viamus.Doxie.Orchestrator.Workflows;

namespace Viamus.Doxie.Orchestrator.Components.Workflows;

public partial class WorkflowForm
{
    /// <summary>
    /// Builds the preview's nodes + edges from the in-progress draft.
    /// Always prepends a synthetic Trigger node and treats every step
    /// with no DependsOn as a downstream of the trigger so the graph
    /// stays connected even before the user wires anything up.
    /// </summary>
    private (IReadOnlyList<WorkflowNode> Nodes, IReadOnlyList<WorkflowEdge> Edges) BuildPreview()
    {
        var nodes = new List<WorkflowNode>
        {
            new(
                Id: "trigger",
                Kind: WorkflowNodeKind.Trigger,
                Label: TriggerLabel(BuildTrigger()),
                X: 0, Y: 0),
        };

        foreach (var step in _steps)
        {
            // Map well-known agent ids to the structural kinds so the
            // preview shows the same colouring conventions as a saved
            // workflow.
            var kind = step.AgentId switch
            {
                "aggregate" => WorkflowNodeKind.Aggregate,
                "write-to-workspace" => WorkflowNodeKind.Output,
                _ => WorkflowNodeKind.Agent,
            };

            nodes.Add(new WorkflowNode(
                Id: step.Id,
                Kind: kind,
                Label: step.Label,
                X: 0, Y: 0,
                AgentId: string.IsNullOrEmpty(step.AgentId) ? null : step.AgentId,
                AgentMode: string.IsNullOrEmpty(step.Mode) ? null : step.Mode,
                Inputs: step.Inputs.Count == 0 ? null : new Dictionary<string, string>(step.Inputs, StringComparer.OrdinalIgnoreCase),
                WorkspaceId: step.WorkspaceId,
                LoopId: string.IsNullOrWhiteSpace(step.LoopId) ? null : step.LoopId));
        }

        var edges = new List<WorkflowEdge>();
        foreach (var step in _steps)
        {
            if (step.DependsOn.Count == 0)
            {
                // Implicit: anything with no upstream runs right after
                // the trigger. Saves the user from wiring it explicitly.
                edges.Add(new WorkflowEdge("trigger", step.Id));
            }
            else
            {
                foreach (var dep in step.DependsOn)
                {
                    edges.Add(new WorkflowEdge(dep, step.Id));
                }
            }
        }

        var positioned = WorkflowLayout.AutoPosition(nodes, edges)
            .Select(ApplyManualPosition)
            .ToList();

        return (positioned, edges);
    }

    private WorkflowNode ApplyManualPosition(WorkflowNode node)
    {
        if (string.Equals(node.Id, "trigger", StringComparison.OrdinalIgnoreCase))
        {
            return _triggerX.HasValue && _triggerY.HasValue
                ? node with { X = _triggerX.Value, Y = _triggerY.Value }
                : node;
        }

        var step = _steps.FirstOrDefault(s => string.Equals(s.Id, node.Id, StringComparison.OrdinalIgnoreCase));
        return step?.X is int x && step.Y is int y
            ? node with { X = x, Y = y }
            : node;
    }

    private WorkflowTrigger BuildTrigger()
    {
        // Trigger inputs only land on Manual triggers — cron / event /
        // webhook ignore them (cron has no place to ask, event /
        // webhook payloads are a future story).
        IReadOnlyList<WorkflowTriggerInputField>? manualInputs = null;
        if (_triggerKind == WorkflowTriggerKind.Manual && _triggerInputs.Count > 0)
        {
            manualInputs = _triggerInputs
                .Where(i => !string.IsNullOrWhiteSpace(i.Id))
                .Select(i => new WorkflowTriggerInputField(
                    Id: i.Id.Trim(),
                    Label: string.IsNullOrWhiteSpace(i.Label) ? i.Id.Trim() : i.Label.Trim(),
                    Placeholder: string.IsNullOrWhiteSpace(i.Placeholder) ? null : i.Placeholder.Trim(),
                    Required: i.Required,
                    Type: string.IsNullOrWhiteSpace(i.Type) ? null : i.Type.Trim(),
                    Options: ParseOptions(i.Options),
                    DefaultValue: string.IsNullOrWhiteSpace(i.DefaultValue) ? null : i.DefaultValue.Trim()))
                .ToList();
            if (manualInputs.Count == 0) manualInputs = null;
        }

        return _triggerKind switch
        {
            WorkflowTriggerKind.Cron => new WorkflowTrigger(WorkflowTriggerKind.Cron, CronExpression: _triggerCron),
            WorkflowTriggerKind.Event => new WorkflowTrigger(WorkflowTriggerKind.Event, EventName: _triggerEvent),
            WorkflowTriggerKind.Webhook => new WorkflowTrigger(WorkflowTriggerKind.Webhook, WebhookPath: _triggerWebhook),
            _ => new WorkflowTrigger(WorkflowTriggerKind.Manual, Inputs: manualInputs),
        };
    }

    private static IReadOnlyList<string>? ParseOptions(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var options = raw
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(o => !string.IsNullOrWhiteSpace(o))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return options.Count == 0 ? null : options;
    }
}
