using Viamus.Doxie.Orchestrator.Workflows;

namespace Viamus.Doxie.Orchestrator.Components.Workflows;

public partial class WorkflowForm
{
    /// <summary>
    /// Mutable draft state used by the form. Converted to a
    /// <see cref="WorkflowDefinition"/> only at preview/save time.
    /// </summary>
    private sealed class StepDraft
    {
        public string Id { get; set; } = string.Empty;
        public string Label { get; set; } = string.Empty;
        public string AgentId { get; set; } = string.Empty;
        public string Mode { get; set; } = string.Empty;
        public string? WorkspaceId { get; set; }
        public int? X { get; set; }
        public int? Y { get; set; }
        public HashSet<string> DependsOn { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Inputs { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Mutable draft for the workflow's env table. Two-way binding
    /// directly off this class so the user's edits flow back without
    /// us having to maintain a parallel dictionary.
    /// </summary>
    private sealed class EnvVarDraft
    {
        public string Key { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;
    }

    /// <summary>
    /// Mutable draft for one row of the trigger-inputs table. Mirrors
    /// <see cref="WorkflowTriggerInputField"/> but with mutable
    /// properties so MudTextField round-trips them. Rebuilt into an
    /// immutable record on save.
    /// </summary>
    private sealed class TriggerInputDraft
    {
        public string Id { get; set; } = string.Empty;
        public string Label { get; set; } = string.Empty;
        public string Placeholder { get; set; } = string.Empty;
        public bool Required { get; set; }
        public string Type { get; set; } = string.Empty;
        public string Options { get; set; } = string.Empty;
        public string DefaultValue { get; set; } = string.Empty;
    }
}
