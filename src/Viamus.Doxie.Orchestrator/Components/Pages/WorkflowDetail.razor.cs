using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using MudBlazor;
using Viamus.Doxie.Orchestrator.Agents;
using Viamus.Doxie.Orchestrator.Agents.Doxie;
using Viamus.Doxie.Orchestrator.Builder;
using Viamus.Doxie.Orchestrator.Components.Shared;
using Viamus.Doxie.Orchestrator.Context;
using Viamus.Doxie.Orchestrator.Notifications;
using Viamus.Doxie.Orchestrator.Theme;
using Viamus.Doxie.Orchestrator.Workflows;
namespace Viamus.Doxie.Orchestrator.Components.Pages;

public partial class WorkflowDetail
{
    [Parameter] public string Id { get; set; } = string.Empty;

    [SupplyParameterFromQuery(Name = "run")]
    public string? PreselectRunId { get; set; }

    private const int NodeWidth = 200;
    private const int NodeHeight = 76;

    private static readonly (string Color, string Label)[] _legend =
    [
        ("#6F7168", "pending"),
        ("#7AA7D9", "running"),
        ("#65B891", "succeeded"),
        ("#E06A5F", "failed"),
        ("#E1A34A", "skipped"),
    ];

    private WorkflowDefinition? _workflow;
    private WorkflowRun? _activeRun;
    private bool _filesDialogOpen;
    private string _approvalComment = string.Empty;
    private bool _resolvingGate;
    private string _operatorHintDraft = string.Empty;
    private bool _sendingOperatorHint;

    private bool CanSendWorkflowHint =>
        _activeRun?.Status is WorkflowRunStatus.Running or WorkflowRunStatus.AwaitingApproval;

    private void OpenFilesDialog()
    {
        if (_activeRun is null) return;
        _filesDialogOpen = true;
    }

    /// <summary>
    /// Trigger from the Browse-files icon embedded in each Recent-runs
    /// row: select that run AND pop the files dialog in one click.
    /// </summary>
    private void OpenFilesForRun(WorkflowRun r)
    {
        SelectRun(r);
        _filesDialogOpen = true;
    }
    private string? _selectedNodeId;
    private List<BreadcrumbItem> _breadcrumbs = new();

    private WorkflowNode? _selectedNode =>
        _selectedNodeId is null
            ? null
            : _workflow?.Nodes.FirstOrDefault(n => n.Id == _selectedNodeId);

    private int CanvasWidth => _workflow is null
        ? 800
        : WorkflowGraphGeometry.CanvasWidth(_workflow.Nodes, NodeWidth, NodeHeight);

    private int CanvasHeight => _workflow is null
        ? 440
        : WorkflowGraphGeometry.CanvasHeight(_workflow.Nodes, NodeWidth, NodeHeight);

    protected override void OnInitialized()
    {
        WorkflowRunner.RunUpdated += OnRunUpdated;
        Reload();
    }

    protected override void OnParametersSet()
    {
        Reload();
        if (!string.IsNullOrEmpty(PreselectRunId))
        {
            var r = WorkflowRunStore.Get(PreselectRunId);
            if (r is not null) _activeRun = r;
        }
    }

    public void Dispose()
    {
        WorkflowRunner.RunUpdated -= OnRunUpdated;
    }

    private async void OnRunUpdated(WorkflowRun run)
    {
        if (run.WorkflowId != Id) return;
        if (_activeRun is null || _activeRun.Id == run.Id)
        {
            _activeRun = run;
        }
        await InvokeAsync(StateHasChanged);
    }

    private void Reload()
    {
        _workflow = WorkflowStore.GetById(Id);
        _breadcrumbs =
        [
            new BreadcrumbItem("Home", href: "/"),
            new BreadcrumbItem("Workflows", href: "/workflows"),
            new BreadcrumbItem(_workflow?.Name ?? Id, href: null, disabled: true),
        ];
        // Default-select first agent node so the right pane isn't empty.
        if (_selectedNodeId is null)
        {
            _selectedNodeId = _workflow?.Nodes.FirstOrDefault(n => n.Kind == WorkflowNodeKind.Agent)?.Id
                              ?? _workflow?.Nodes.FirstOrDefault()?.Id;
        }
    }

    private Task StartManualRun() => StartRunWithSeed(seedFromRun: null);

    /// <summary>
    /// "Run again" path — seeds the dialog with the trigger inputs of
    /// a specific past run. The user can adjust before firing or just
    /// hit Run to repeat.
    /// </summary>
    private Task RerunWith(WorkflowRun seed) => StartRunWithSeed(seedFromRun: seed);

    private async Task StartRunWithSeed(WorkflowRun? seedFromRun)
    {
        if (_workflow is null) return;
        if (!_workflow.Enabled)
        {
            // Defence-in-depth: button is also Disabled when off, but
            // the keyboard / programmatic path lands here too.
            Snackbar.Add("Workflow is disabled. Toggle it on first.", Severity.Warning);
            return;
        }

        // Workflow declares trigger inputs â†’ collect via dialog before
        // firing. Pre-fill from the explicit seed run if given, else
        // from the most recent run, else empty.
        Dictionary<string, string>? triggerInputs = null;
        if (_workflow.Trigger.Inputs is { Count: > 0 })
        {
            var prefill = seedFromRun?.TriggerInputs
                ?? WorkflowRunStore.ListByWorkflow(_workflow.Id).FirstOrDefault()?.TriggerInputs;
            var parameters = new DialogParameters
            {
                ["Workflow"] = _workflow,
                ["InitialValues"] = prefill,
            };
            var dialog = await Dialog.ShowAsync<Viamus.Doxie.Orchestrator.Components.Workflows.WorkflowRunInputsDialog>(
                "Run", parameters,
                new DialogOptions { CloseOnEscapeKey = true, MaxWidth = MaxWidth.Small, FullWidth = true });
            var result = await dialog.Result;
            if (result is null || result.Canceled) return;
            triggerInputs = result.Data as Dictionary<string, string>;
        }

        var run = WorkflowRunner.Start(_workflow, "manual", triggerInputs);
        _activeRun = run;
        Snackbar.Add("Run started", Severity.Info);
    }

    private async Task OnEnabledToggled(bool enabled)
    {
        if (_workflow is null) return;
        try
        {
            var result = await Js.InvokeAsync<EnableResult?>("doxieOs.setWorkflowEnabled", _workflow.Id, enabled);
            if (result is null || !result.Ok)
            {
                var detail = result is null ? "no response" : (string.IsNullOrEmpty(result.Message) ? $"HTTP {result.Status}" : result.Message);
                Snackbar.Add($"Could not toggle: {detail}", Severity.Error);
                return;
            }
            // Re-read the definition so the UI reflects the new state +
            // bumped UpdatedAt without a page refresh.
            Reload();
            Snackbar.Add(enabled ? "Workflow enabled" : "Workflow disabled", enabled ? Severity.Success : Severity.Warning);
        }
        catch (JSException ex)
        {
            Snackbar.Add($"Could not toggle: {ex.Message}", Severity.Error);
        }
    }

    private sealed record EnableResult(bool Ok, int Status, bool Enabled, string? Message);

    private void CancelActiveRun()
    {
        if (_activeRun is null) return;
        WorkflowRunner.Cancel(_activeRun.Id);
        Snackbar.Add("Cancel signalled", Severity.Warning);
    }

    private async Task SendWorkflowHint()
    {
        if (_activeRun is null || string.IsNullOrWhiteSpace(_operatorHintDraft) || _sendingOperatorHint) return;
        _sendingOperatorHint = true;
        try
        {
            var hint = _operatorHintDraft.Trim();
            if (WorkflowRunner.AddOperatorHint(_activeRun.Id, hint))
            {
                _operatorHintDraft = string.Empty;
                Snackbar.Add("Hint sent to the running workflow", Severity.Info);
            }
            else
            {
                Snackbar.Add("Could not send hint: this workflow run is no longer active.", Severity.Warning);
            }
        }
        finally
        {
            _sendingOperatorHint = false;
            await InvokeAsync(StateHasChanged);
        }
    }

    private void SelectNode(string nodeId) => _selectedNodeId = nodeId;
    private void SelectRun(WorkflowRun run) => _activeRun = run;

    /// <summary>
    /// Approve / reject the currently-selected approval-gate node. The
    /// runner picks the click up via <see cref="IWorkflowRunner.ResumeApprovalGate"/>;
    /// the run's <c>RunUpdated</c> event will then fire and re-render
    /// this page automatically. The optional comment is captured on the
    /// node's logs (audit trail).
    /// </summary>
    private async Task ResolveGate(bool approve)
    {
        if (_workflow is null || _activeRun is null || _selectedNode is null) return;
        if (_resolvingGate) return;
        _resolvingGate = true;
        try
        {
            var path = approve ? "approve" : "reject";
            var result = await Js.InvokeAsync<ResolveGateResult?>(
                "doxieOs.resolveWorkflowGate",
                _workflow.Id, _activeRun.Id, _selectedNode.Id, path, _approvalComment);

            if (result is { Ok: true })
            {
                Snackbar.Add(approve ? "Approved" : "Rejected — run cancelling", approve ? Severity.Success : Severity.Warning);
                _approvalComment = string.Empty;
            }
            else
            {
                var msg = result?.Message ?? "(no response)";
                Snackbar.Add($"Could not {(approve ? "approve" : "reject")}: {msg}", Severity.Error);
            }
        }
        catch (JSException ex)
        {
            Snackbar.Add($"Could not resolve gate: {ex.Message}", Severity.Error);
        }
        finally
        {
            _resolvingGate = false;
        }
    }

    private sealed record ResolveGateResult(bool Ok, int Status, string? Message);


}

