namespace Viamus.Doxie.Orchestrator.Workflows;

/// <summary>
/// One typed input that a manual-trigger workflow asks the user to
/// provide before a run starts. The orchestrator's UI renders a field
/// per entry inside a MudDialog at Run-time; the values land on
/// <see cref="WorkflowRun.TriggerInputs"/> and are interpolated into
/// <c>node.Inputs</c> via the <c>{{trigger.&lt;id&gt;}}</c> placeholder
/// pattern before dispatch.
/// </summary>
/// <param name="Id">Stable key the workflow definition uses to refer
/// back to this input (e.g. <c>"work-item-id"</c>). Must match the
/// fragment inside <c>{{trigger.&lt;id&gt;}}</c> in node inputs.</param>
/// <param name="Label">User-facing label rendered above the field.</param>
/// <param name="Placeholder">Optional hint shown inside the empty
/// field. Pure UI — never substituted as a default value.</param>
/// <param name="Required">If true, the API rejects a Run request that
/// doesn't supply a value for this input.</param>
/// <param name="Type">Optional UI hint. Supported value today:
/// <c>"select"</c>; anything else renders as a text field.</param>
/// <param name="Options">Allowed option values for select-style fields.</param>
/// <param name="DefaultValue">Optional value pre-filled by the UI and used
/// by the run API when the caller omits this input.</param>
public sealed record WorkflowTriggerInputField(
    string Id,
    string Label,
    string? Placeholder = null,
    bool Required = false,
    string? Type = null,
    IReadOnlyList<string>? Options = null,
    string? DefaultValue = null);
