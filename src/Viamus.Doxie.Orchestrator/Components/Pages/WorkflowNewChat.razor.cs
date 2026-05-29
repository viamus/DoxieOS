using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using MudBlazor;
using Viamus.Doxie.Orchestrator.Agents;
using Viamus.Doxie.Orchestrator.Agents.Doxie;
using Viamus.Doxie.Orchestrator.Builder;
using Viamus.Doxie.Orchestrator.Common;
using Viamus.Doxie.Orchestrator.Context;
using Viamus.Doxie.Orchestrator.Workflows;
namespace Viamus.Doxie.Orchestrator.Components.Pages;

public partial class WorkflowNewChat
{
    [Inject] private Viamus.Doxie.Orchestrator.Builder.BuilderSessionFactory BuilderFactory { get; set; } = null!;

    /// <summary>
    /// Optional. When present, the page starts in Edit mode for the
    /// workflow with this id â€” sandbox is pre-seeded and Save will
    /// overwrite the catalog entry.
    /// </summary>
    [SupplyParameterFromQuery(Name = "edit")]
    public string? EditWorkflowId { get; set; }

    /// <summary>
    /// Optional. Deep-link to a live workflow-builder session (e.g.,
    /// from the dashboard's "Live now" panel). When set, the page skips
    /// the empty state and resumes that session directly. Silently
    /// ignored when the id doesn't resolve to a live workflow-builder
    /// session.
    /// </summary>
    [SupplyParameterFromQuery(Name = "session")]
    public string? SessionQueryParam { get; set; }

    private List<BreadcrumbItem> _breadcrumbs =>
    [
        new BreadcrumbItem(Lang["Common.Home"], href: "/"),
        new BreadcrumbItem(Lang["Workflows.Title"], href: "/workflows"),
        new BreadcrumbItem(Lang["Common.BuildWithAi"], href: null, disabled: true),
    ];

    private Viamus.Doxie.Orchestrator.Builder.BuilderSessionInfo? _session;
    private IReadOnlyList<Viamus.Doxie.Orchestrator.Builder.BuilderSessionInfo> _existingSessions =
        Array.Empty<Viamus.Doxie.Orchestrator.Builder.BuilderSessionInfo>();

    private string _mountId = "workflow-builder-mount-" + Guid.NewGuid().ToString("N")[..8];
    private string? _attachedSessionId;
    private bool _starting;
    private bool _saving;
    private bool _discarding;
    private string _selectedCatalogId = DoxieCatalogStore.DefaultCatalogId;

    private IReadOnlyList<Workspace> _workspaces = Array.Empty<Workspace>();
    private IReadOnlyList<DoxieCatalogRoot> _catalogs = Array.Empty<DoxieCatalogRoot>();
    private string _attachWorkspaceId = string.Empty;

    private Viamus.Doxie.Orchestrator.Builder.WorkflowManifest? _manifest;
    private List<NewAgentEntry> _newAgents = new();
    private bool _manifestPresent;
    private bool _manifestValid;
    private string? _manifestError;
    private IReadOnlyList<string> _manifestErrors = Array.Empty<string>();
    private DateTime _lastManifestUtc = DateTime.MinValue;

    private System.Threading.Timer? _manifestTimer;

    private string AttachWorkspaceLabel() => Lang.CurrentLanguage switch
    {
        "pt-BR" => "Anexar área de trabalho (opcional)",
        "es" => "Adjuntar espacio de trabajo (opcional)",
        _ => "Attach a workspace (optional)",
    };

    private string AttachWorkspaceDescription() => Lang.CurrentLanguage switch
    {
        "pt-BR" => "As bibliotecas montadas da área de trabalho entram no sandbox para dar contexto de projeto ao CLI ativo enquanto ele desenha o fluxo.",
        "es" => "Las bibliotecas montadas del espacio de trabajo entran en el sandbox para dar contexto de proyecto al CLI activo mientras diseña el flujo.",
        _ => "The workspace's mounted libraries are inlined into the sandbox so the active CLI has project context when designing the workflow.",
    };

    protected override void OnInitialized()
    {
        _existingSessions = BuilderFactory.List(Viamus.Doxie.Orchestrator.Builder.BuilderKind.Workflow);
        _workspaces = WorkspaceStore.ListAll();
        _catalogs = CatalogStore.ListCatalogs();

        if (!string.IsNullOrEmpty(EditWorkflowId) && WorkflowStore.GetById(EditWorkflowId) is null)
        {
            Snackbar.AddDoxieToast($"Workflow '{EditWorkflowId}' not found â€” starting a fresh session instead.", Severity.Warning);
            EditWorkflowId = null;
        }
        else if (!string.IsNullOrEmpty(EditWorkflowId))
        {
            _selectedCatalogId = WorkflowStore.GetById(EditWorkflowId)?.CatalogId ?? DoxieCatalogStore.DefaultCatalogId;
        }

        // Deep-link from the dashboard: /workflows/new/chat?session=<id>
        // auto-resumes the matching workflow-builder session. Skip
        // silently if the id is unknown or belongs to an agent builder.
        if (!string.IsNullOrWhiteSpace(SessionQueryParam))
        {
            var info = BuilderFactory.Find(SessionQueryParam);
            if (info is { Kind: Viamus.Doxie.Orchestrator.Builder.BuilderKind.Workflow })
            {
                _session = info;
                LoadCatalogFromSession(info);
                _mountId = "workflow-builder-mount-" + Guid.NewGuid().ToString("N")[..8];
            }
        }
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_session is null) return;
        if (string.Equals(_attachedSessionId, _session.SessionId, StringComparison.Ordinal)) return;
        try
        {
            await Js.InvokeVoidAsync("doxieOs.terminals.attach", _mountId, _session.SessionId);
            _attachedSessionId = _session.SessionId;

            // Bootstrap (slash command for Claude, inlined skill body for
            // Codex) is now injected server-side by ConsoleSession.Spawn()
            // via the initialInput it received from BuilderSessionFactory.

            _manifestTimer = new System.Threading.Timer(_ => _ = PollManifestAsync(),
                state: null, dueTime: 800, period: 1500);
        }
        catch (Microsoft.JSInterop.JSException ex)
        {
            Snackbar.AddDoxieToast($"Could not attach console: {ex.Message}", Severity.Error);
        }
    }

    private async Task StartNewSession()
    {
        _starting = true;
        try
        {
            var workspaceArg = string.IsNullOrEmpty(_attachWorkspaceId) ? null : _attachWorkspaceId;
            var editArg = string.IsNullOrEmpty(EditWorkflowId) ? null : EditWorkflowId;
            var result = await Js.InvokeAsync<StartResult?>("doxieOs.startWorkflowBuilder", workspaceArg, editArg);
            if (result is null || !result.Ok || string.IsNullOrEmpty(result.SessionId))
            {
                Snackbar.AddDoxieToast($"Could not start builder session: {result?.Message ?? "unknown error"}", Severity.Error);
                return;
            }
            var info = BuilderFactory.Find(result.SessionId);
            if (info is null)
            {
                Snackbar.AddDoxieToast("Builder session vanished right after creation â€” try again.", Severity.Error);
                return;
            }
            _session = info;
            LoadCatalogFromSession(info);
            _mountId = "workflow-builder-mount-" + Guid.NewGuid().ToString("N")[..8];
            _attachedSessionId = null;
        }
        finally
        {
            _starting = false;
        }
    }

    private void ResumeSession(Viamus.Doxie.Orchestrator.Builder.BuilderSessionInfo info)
    {
        _session = info;
        LoadCatalogFromSession(info);
        _mountId = "workflow-builder-mount-" + Guid.NewGuid().ToString("N")[..8];
        _attachedSessionId = null;
    }

    private void LoadCatalogFromSession(Viamus.Doxie.Orchestrator.Builder.BuilderSessionInfo info)
    {
        _selectedCatalogId = !string.IsNullOrEmpty(info.EditWorkflowId)
            ? WorkflowStore.GetById(info.EditWorkflowId)?.CatalogId ?? DoxieCatalogStore.DefaultCatalogId
            : _selectedCatalogId;
    }

    private async Task PollManifestAsync()
    {
        if (_session is null) return;
        try
        {
            var result = await Js.InvokeAsync<ManifestEnvelope?>("doxieOs.readWorkflowManifest", _session.SessionId);
            if (result is null) return;

            // Always refresh the new-agents list â€” those can change
            // without the workflow manifest itself being touched.
            var newAgents = result.NewAgents?.ToList() ?? new List<NewAgentEntry>();
            var newErrors = result.Errors ?? new List<string>();

            var changed = result.UpdatedAt != _lastManifestUtc
                          || _manifestPresent != result.Present
                          || _manifestValid != result.Valid
                          || _newAgents.Count != newAgents.Count
                          || _manifestErrors.Count != newErrors.Count
                          || !_manifestErrors.SequenceEqual(newErrors);
            if (!changed) return;

            _manifestPresent = result.Present;
            _manifestValid = result.Valid;
            _manifestError = result.Error;
            _manifestErrors = newErrors;
            _manifest = result.Manifest;
            _newAgents = newAgents;
            _lastManifestUtc = result.UpdatedAt ?? DateTime.MinValue;

            await InvokeAsync(StateHasChanged);
        }
        catch (Microsoft.JSInterop.JSException) { }
        catch (Microsoft.JSInterop.JSDisconnectedException) { }
    }

    private bool CanSave() =>
        _session is not null
        && _manifestPresent
        && _manifestValid
        && _manifestErrors.Count == 0
        && (_newAgents is null || _newAgents.All(na => na.Valid && (na.Errors is null || na.Errors.Count == 0)))
        && _manifest is not null
        && !string.IsNullOrWhiteSpace(_manifest.Id)
        && !string.IsNullOrWhiteSpace(_manifest.Name)
        && _manifest.Trigger is not null
        && _manifest.Nodes is { Count: > 0 }
        && !_saving;

    private async Task HandleSave()
    {
        if (_session is null || _manifest is null) return;
        _saving = true;
        try
        {
            var overwrite = !string.IsNullOrEmpty(_session.EditWorkflowId);
            var result = await Js.InvokeAsync<SaveResult?>("doxieOs.saveBuilderWorkflow", _session.SessionId, overwrite, _selectedCatalogId);
            if (result is null)
            {
                Snackbar.AddDoxieToast("Save failed: no response", Severity.Error);
                return;
            }
            if (result.Ok)
            {
                if (result.AgentResults is { Count: > 0 })
                {
                    var promotedAgents = result.AgentResults.Count(r => r.Ok);
                    if (promotedAgents > 0)
                    {
                        Snackbar.AddDoxieToast($"Cross-created {promotedAgents} agent(s) alongside the workflow", Severity.Info);
                    }
                }
                Snackbar.AddDoxieToast($"Saved workflow \"{result.WorkflowId}\"", Severity.Success);
                if (!string.IsNullOrEmpty(result.Href))
                {
                    Nav.NavigateTo(result.Href);
                }
            }
            else
            {
                // If some agents were promoted before the workflow
                // failed, surface that â€” the user needs to know they
                // exist now.
                var promoted = result.AgentResults?.Where(r => r.Ok).Select(r => r.AgentId).ToList() ?? new List<string?>();
                var detail = (result.Errors is { Count: > 0 })
                    ? string.Join(" Â� ", result.Errors)
                    : (result.Message ?? "unknown");
                if (promoted.Count > 0)
                {
                    detail += $" â€” but {promoted.Count} agent(s) already promoted: {string.Join(", ", promoted)}";
                }
                Snackbar.AddDoxieToast($"Save failed: {detail}", Severity.Error);
            }
        }
        finally
        {
            _saving = false;
        }
    }

    private async Task HandleDiscard()
    {
        if (_session is null) return;
        bool confirmed;
        try
        {
            confirmed = await Js.InvokeAsync<bool>("doxieOs.confirm",
                "Discard this workflow builder session?\n\nThe PTY is killed, the manifest is NOT promoted, and any pending new-agent drafts are abandoned. The sandbox folder stays on disk for housekeeping.");
        }
        catch (Microsoft.JSInterop.JSException) { confirmed = false; }
        if (!confirmed) return;

        _discarding = true;
        try
        {
            try { await Js.InvokeVoidAsync("doxieOs.deleteConsole", _session.SessionId); }
            catch (Microsoft.JSInterop.JSException) { }
            Snackbar.AddDoxieToast($"Ended workflow builder session \"{_session.SandboxTag}\"", Severity.Info);
            Nav.NavigateTo("/workflows");
        }
        finally
        {
            _discarding = false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_manifestTimer is not null)
        {
            await _manifestTimer.DisposeAsync();
            _manifestTimer = null;
        }
        if (_attachedSessionId is not null)
        {
            try { await Js.InvokeVoidAsync("doxieOs.terminals.detach", _mountId); }
            catch (Microsoft.JSInterop.JSException) { }
            catch (Microsoft.JSInterop.JSDisconnectedException) { }
        }
    }

    private static string TriggerLabel(Viamus.Doxie.Orchestrator.Builder.WorkflowManifestTrigger t) =>
        (t.Kind ?? "manual").ToLowerInvariant() switch
        {
            "cron" => $"cron Â� {t.CronExpression ?? "?"}",
            "event" => $"event Â� {t.EventName ?? "?"}",
            "webhook" => $"hook Â� {t.WebhookPath ?? "?"}",
            _ => "manual",
        };

    private static string TriggerIcon(string? kind) => (kind ?? "").ToLowerInvariant() switch
    {
        "cron" => Icons.Material.Filled.Schedule,
        "event" => Icons.Material.Filled.Bolt,
        "webhook" => Icons.Material.Filled.Webhook,
        _ => Icons.Material.Filled.PlayArrow,
    };

    private static Color TriggerColor(string? kind) => (kind ?? "").ToLowerInvariant() switch
    {
        "cron" => Color.Info,
        "event" => Color.Warning,
        "webhook" => Color.Secondary,
        _ => Color.Default,
    };

    private static string NodeIcon(string? kind) => (kind ?? "").ToLowerInvariant() switch
    {
        "aggregate" => Icons.Material.Filled.MergeType,
        "decision" => Icons.Material.Filled.CallSplit,
        "loop" => Icons.Material.Filled.Sync,
        "output" => Icons.Material.Filled.Folder,
        _ => Icons.Material.Filled.SmartToy,
    };

    private static string NodeAccent(string? kind) => (kind ?? "").ToLowerInvariant() switch
    {
        "aggregate" => "#26A69A",
        "decision" => "#DFA5D6",
        "loop" => "#E1A34A",
        "output" => "#D97757",
        _ => "#5A5A56",
    };

    private void SetSelectedCatalog(string? value)
    {
        _selectedCatalogId = string.IsNullOrWhiteSpace(value) ? DoxieCatalogStore.DefaultCatalogId : value;
    }

    private string SelectedCatalogLabel() =>
        _catalogs.FirstOrDefault(c => string.Equals(c.Id, _selectedCatalogId, StringComparison.OrdinalIgnoreCase))?.Name
        ?? "Default catalog";

    private static string RelativeTime(DateTimeOffset utcWhen)
    {
        var delta = DateTimeOffset.UtcNow - utcWhen;
        if (delta.TotalSeconds < 60) return "just now";
        if (delta.TotalMinutes < 60) return $"{(int)delta.TotalMinutes}m ago";
        if (delta.TotalHours < 24) return $"{(int)delta.TotalHours}h ago";
        return $"{(int)delta.TotalDays}d ago";
    }

    /// <summary>
    /// Visual "headâ€¦tail" truncation for long ids â€” preserves the
    /// suffix so the user can still recognise it. Full value lives in
    /// the chip's tooltip.
    /// </summary>
    private static string TruncateMid(string s, int max)
    {
        if (s.Length <= max) return s;
        var keep = (max - 1) / 2;
        return s[..keep] + "â€¦" + s[^keep..];
    }

    private sealed record StartResult(bool Ok, int Status, string? SessionId, string? SandboxTag, string? Label, string? AttachedWorkspaceId, string? EditWorkflowId, string? Message);
    private sealed record SaveResult(bool Ok, int Status, string? WorkflowId, string? CatalogId, string? Href, List<AgentSubResultView>? AgentResults, string? Message, List<string>? Errors);
    private sealed record AgentSubResultView(string FileName, string? AgentId, bool Ok, string? Error);
    private sealed record ManifestEnvelope(bool Present, bool Valid, Viamus.Doxie.Orchestrator.Builder.WorkflowManifest? Manifest, string? Error, List<string>? Errors, DateTime? UpdatedAt, List<NewAgentEntry>? NewAgents);
    private sealed record NewAgentEntry(string FileName, bool Valid, Viamus.Doxie.Orchestrator.Builder.AgentManifest? Manifest, string? Error, List<string>? Errors);
}

