using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using MudBlazor;
using Viamus.Doxie.Orchestrator.Agents;
using Viamus.Doxie.Orchestrator.Agents.Doxie;
using Viamus.Doxie.Orchestrator.Builder;
using Viamus.Doxie.Orchestrator.Common;
using Viamus.Doxie.Orchestrator.Components.Shared;
using Viamus.Doxie.Orchestrator.Context;
using Viamus.Doxie.Orchestrator.Notifications;
using Viamus.Doxie.Orchestrator.Theme;
using Viamus.Doxie.Orchestrator.Workflows;
namespace Viamus.Doxie.Orchestrator.Components.Workflows;

public partial class WorkflowForm
{
    /// <summary>
    /// When non-null, the form is in edit mode: drafts hydrate from this
    /// definition on init and the id field becomes read-only.
    /// </summary>
    [Parameter] public WorkflowDefinition? Existing { get; set; }

    /// <summary>
    /// Invoked once the user clicks Save and the form has validated the
    /// payload. Host page is responsible for the actual persistence
    /// (POST vs PUT) and post-save navigation.
    /// </summary>
    [Parameter] public EventCallback<WorkflowDefinition> OnSave { get; set; }

    /// <summary>Header icon — defaults to a Create-ish AddCircle.</summary>
    [Parameter] public string HeaderIcon { get; set; } = Icons.Material.Filled.AddCircle;

    /// <summary>Header text shown next to the icon.</summary>
    [Parameter] public string HeaderText { get; set; } = "New workflow";

    /// <summary>Save button label — varies between Create and Update.</summary>
    [Parameter] public string SaveLabel { get; set; } = "Save workflow";

    /// <summary>Where the Cancel button navigates to.</summary>
    [Parameter] public string CancelHref { get; set; } = "/workflows";

    private string _id = string.Empty;
    private string _name = string.Empty;
    private string _description = string.Empty;
    private string _workflowWorkspaceId = string.Empty;
    private string _selectedCatalogId = DoxieCatalogStore.DefaultCatalogId;

    private WorkflowTriggerKind _triggerKind = WorkflowTriggerKind.Manual;
    private string _triggerCron = "0 8 * * *";
    private string _triggerEvent = string.Empty;
    private string _triggerWebhook = string.Empty;
    private readonly List<TriggerInputDraft> _triggerInputs = new();

    private readonly List<StepDraft> _steps = new();
    private readonly List<EnvVarDraft> _envVars = new();
    private IReadOnlyList<AgentDescriptor> _catalog = Array.Empty<AgentDescriptor>();
    private IReadOnlyList<Workspace> _workspaces = Array.Empty<Workspace>();
    private IReadOnlyList<DoxieCatalogRoot> _catalogs = Array.Empty<DoxieCatalogRoot>();
    private bool _saving;
    private int _stepCounter;
    private int? _triggerX;
    private int? _triggerY;
    private NodeDragState? _drag;
    private LoopBodyDragState? _loopBodyDrag;

    protected override void OnInitialized()
    {
        _catalog = AgentCatalog.GetAll();
        _workspaces = WorkspaceStore.ListAll();
        _catalogs = CatalogStore.ListCatalogs();

        if (Existing is not null)
        {
            HydrateFrom(Existing);
        }
    }

    /// <summary>
    /// Reverses <c>BuildPreview</c>: turns a saved <see cref="WorkflowDefinition"/>
    /// back into the mutable draft state the form binds against. The synthetic
    /// "trigger" node is filtered out (it's reconstructed on every save) and
    /// each step's incoming edges become its DependsOn set.
    /// </summary>
    private void HydrateFrom(WorkflowDefinition wf)
    {
        _id = wf.Id;
        _name = wf.Name;
        _description = wf.Description;
        _workflowWorkspaceId = wf.WorkspaceId ?? string.Empty;
        _selectedCatalogId = wf.CatalogId;

        _triggerKind = wf.Trigger.Kind;
        _triggerCron = wf.Trigger.CronExpression ?? "0 8 * * *";
        _triggerEvent = wf.Trigger.EventName ?? string.Empty;
        _triggerWebhook = wf.Trigger.WebhookPath ?? string.Empty;
        var triggerNode = wf.Nodes.FirstOrDefault(n =>
            string.Equals(n.Id, "trigger", StringComparison.OrdinalIgnoreCase)
            || n.Kind == WorkflowNodeKind.Trigger);
        _triggerX = triggerNode?.X;
        _triggerY = triggerNode?.Y;

        _triggerInputs.Clear();
        if (wf.Trigger.Inputs is { Count: > 0 } existingInputs)
        {
            foreach (var f in existingInputs)
            {
                _triggerInputs.Add(new TriggerInputDraft
                {
                    Id = f.Id,
                    Label = f.Label,
                    Placeholder = f.Placeholder ?? string.Empty,
                    Required = f.Required,
                    Type = f.Type ?? string.Empty,
                    Options = f.Options is { Count: > 0 } ? string.Join(", ", f.Options) : string.Empty,
                    DefaultValue = f.DefaultValue ?? string.Empty,
                });
            }
        }

        _envVars.Clear();
        if (wf.Env is { Count: > 0 } env)
        {
            foreach (var pair in env)
            {
                _envVars.Add(new EnvVarDraft { Key = pair.Key, Value = pair.Value });
            }
        }

        _steps.Clear();
        foreach (var node in wf.Nodes.Where(n => n.Kind != WorkflowNodeKind.Trigger))
        {
            var dependsOn = wf.Edges
                .Where(e => string.Equals(e.ToNodeId, node.Id, StringComparison.OrdinalIgnoreCase))
                .Select(e => e.FromNodeId)
                // Edges from the synthetic trigger become "no dependency"
                // in the draft so the round-trip is idempotent.
                .Where(id => !string.Equals(id, "trigger", StringComparison.OrdinalIgnoreCase))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            _steps.Add(new StepDraft
            {
                Id = node.Id,
                Label = node.Label,
                AgentId = node.AgentId ?? string.Empty,
                Mode = node.AgentMode ?? string.Empty,
                WorkspaceId = node.WorkspaceId,
                LoopId = node.LoopId,
                X = node.X,
                Y = node.Y,
                DependsOn = dependsOn,
                Inputs = node.Inputs is { Count: > 0 }
                    ? new Dictionary<string, string>(node.Inputs, StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            });
        }

        // Bump the auto-generated step id counter past any existing
        // step-N ids so AddStep doesn't collide.
        _stepCounter = _steps
            .Select(s => s.Id)
            .Where(id => id.StartsWith("step-", StringComparison.OrdinalIgnoreCase))
            .Select(id => int.TryParse(id.Substring("step-".Length), out var n) ? n : 0)
            .DefaultIfEmpty(0)
            .Max();
    }

    private void AddEnvVar() => _envVars.Add(new EnvVarDraft());

    private void AddTriggerInput()
    {
        // Pre-fill the id with a sensible counter so multi-add works
        // before the user types — matches the AddStep ergonomics.
        var counter = _triggerInputs.Count + 1;
        _triggerInputs.Add(new TriggerInputDraft { Id = $"input-{counter}" });
    }

    private void OnTriggerKindChanged(WorkflowTriggerKind kind) => _triggerKind = kind;

    private void OnWorkflowWorkspaceChanged(string? value)
    {
        _workflowWorkspaceId = value ?? string.Empty;
    }

    private void AddStep()
    {
        _stepCounter++;
        _steps.Add(new StepDraft
        {
            Id = $"step-{_stepCounter}",
            Label = $"Step {_stepCounter}",
            AgentId = string.Empty,
            Mode = string.Empty,
            DependsOn = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            Inputs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
        });
    }

    private void RemoveStep(StepDraft step)
    {
        _steps.Remove(step);
        // Drop dangling dependency references on remaining steps so the
        // graph stays consistent.
        foreach (var s in _steps)
        {
            s.DependsOn.Remove(step.Id);
        }
    }

    private void OnStepLabelChanged(StepDraft step, string value)
    {
        step.Label = value ?? string.Empty;
    }

    private void OnStepAgentChanged(StepDraft step, string agentId)
    {
        step.AgentId = agentId ?? string.Empty;
        step.Inputs.Clear();
        var agent = string.IsNullOrEmpty(agentId) ? null : AgentCatalog.FindById(agentId);
        // Auto-pick the first mode so the user doesn't have to chase a
        // second dropdown when an agent only has one anyway.
        step.Mode = agent?.Modes is { Count: > 0 } modes ? modes[0].Id : string.Empty;
    }

    private void OnStepModeChanged(StepDraft step, string modeId)
    {
        step.Mode = modeId ?? string.Empty;
        step.Inputs.Clear();
    }

    private void SetStepInput(StepDraft step, string fieldId, string? value)
    {
        step.Inputs[fieldId] = value ?? string.Empty;
    }

    private void OnStepDependsChanged(StepDraft step, IEnumerable<string>? values)
    {
        step.DependsOn = values is null
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : values.Where(v => !string.IsNullOrEmpty(v)).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private void OnStepWorkspaceChanged(StepDraft step, string? value)
    {
        step.WorkspaceId = string.IsNullOrEmpty(value) ? null : value;
    }

    private string StepWorkspaceHelperText(bool requiresWorkspace)
    {
        if (!string.IsNullOrEmpty(_workflowWorkspaceId))
        {
            return requiresWorkspace
                ? $"Inherits `{_workflowWorkspaceId}` unless overridden here."
                : $"Optional override. Blank steps inherit `{_workflowWorkspaceId}`.";
        }

        return requiresWorkspace
            ? "This mode needs a workspace. Set a default workspace above or choose an override here."
            : "Optional override. Blank steps run from the project root when no default workspace is set.";
    }

    private async Task HandleSave()
    {
        var rawId = (_id ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(rawId) || !System.Text.RegularExpressions.Regex.IsMatch(rawId, "^[a-z0-9]+(-[a-z0-9]+)*$"))
        {
            Snackbar.Add("Id is required and must be kebab-case (lowercase letters, digits, hyphens).", Severity.Error);
            return;
        }
        if (_steps.Count == 0)
        {
            Snackbar.Add("Add at least one step before saving.", Severity.Error);
            return;
        }
        if (_steps.Any(s => string.IsNullOrEmpty(s.AgentId)))
        {
            Snackbar.Add("Every step needs an agent picked.", Severity.Error);
            return;
        }

        // Validate per-step workspace requirements before sending.
        var missingWs = _steps.Where(s =>
        {
            var mode = string.IsNullOrEmpty(s.AgentId)
                ? null
                : AgentCatalog.FindById(s.AgentId)?.Modes?.FirstOrDefault(m => m.Id == s.Mode);
            return mode?.RequiresWorkspace == true
                && string.IsNullOrEmpty(s.WorkspaceId)
                && string.IsNullOrEmpty(_workflowWorkspaceId);
        }).ToList();
        if (missingWs.Count > 0)
        {
            Snackbar.Add($"{missingWs.Count} step(s) need a workspace bound (their mode requires it).", Severity.Error);
            return;
        }

        var env = _envVars
            .Where(v => !string.IsNullOrEmpty(v.Key))
            .ToDictionary(v => v.Key.Trim(), v => v.Value ?? string.Empty, StringComparer.OrdinalIgnoreCase);

        var (nodes, edges) = BuildPreview();
        // Preserve CreatedAt across edits — only the create path stamps a
        // brand-new value. Server-side handlers re-stamp UpdatedAt on
        // every save anyway, so the client value here is informational.
        var createdAt = Existing?.CreatedAt ?? DateTime.UtcNow;
        var payload = new WorkflowDefinition(
            Id: rawId,
            Name: string.IsNullOrWhiteSpace(_name) ? rawId : _name.Trim(),
            Description: _description?.Trim() ?? string.Empty,
            Trigger: BuildTrigger(),
            Nodes: nodes,
            Edges: edges,
            CreatedAt: createdAt,
            UpdatedAt: DateTime.UtcNow,
            WorkspaceId: string.IsNullOrEmpty(_workflowWorkspaceId) ? null : _workflowWorkspaceId,
            Env: env.Count == 0 ? null : env,
            IsPrivate: false,
            CatalogId: _selectedCatalogId);

        _saving = true;
        try
        {
            await OnSave.InvokeAsync(payload);
        }
        finally
        {
            _saving = false;
        }
    }

    private void SetSelectedCatalog(string? value)
    {
        _selectedCatalogId = string.IsNullOrWhiteSpace(value) ? DoxieCatalogStore.DefaultCatalogId : value;
    }

    private sealed record NodeDragState(
        string NodeId,
        double StartClientX,
        double StartClientY,
        int StartX,
        int StartY);


}

