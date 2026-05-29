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
using Viamus.Doxie.Orchestrator.Workflows;
namespace Viamus.Doxie.Orchestrator.Components.Pages;

public partial class AgentNew
{
    [Inject] private Viamus.Doxie.Orchestrator.Builder.BuilderSessionFactory BuilderFactory { get; set; } = null!;

    /// <summary>
    /// Optional. When present, the page starts in Edit mode for the
    /// agent with this id â€” the new builder session is pre-seeded with
    /// the existing manifest and Save will overwrite the catalog entry
    /// instead of refusing on collision.
    /// </summary>
    [SupplyParameterFromQuery(Name = "edit")]
    public string? EditAgentId { get; set; }

    /// <summary>
    /// Optional. Deep-link to a specific live builder session (e.g.,
    /// from the dashboard's "Live now" panel). When set, the page skips
    /// the empty state and resumes that session directly. Silently
    /// ignored when the id doesn't resolve to a live agent-builder
    /// session â€” the empty state then offers Resume tiles for whatever
    /// is actually open.
    /// </summary>
    [SupplyParameterFromQuery(Name = "session")]
    public string? SessionQueryParam { get; set; }

    private List<BreadcrumbItem> _breadcrumbs =>
    [
        new BreadcrumbItem(Lang["Common.Home"], href: "/"),
        new BreadcrumbItem(Lang["Agents.Title"], href: "/agents"),
        new BreadcrumbItem(Lang["Common.BuildWithAi"], href: null, disabled: true),
    ];

    private Viamus.Doxie.Orchestrator.Builder.BuilderSessionInfo? _session;
    private IReadOnlyList<Viamus.Doxie.Orchestrator.Builder.BuilderSessionInfo> _existingSessions =
        Array.Empty<Viamus.Doxie.Orchestrator.Builder.BuilderSessionInfo>();

    private string _mountId = "agent-builder-mount-" + Guid.NewGuid().ToString("N")[..8];
    private string? _attachedSessionId;
    private bool _starting;
    private bool _saving;
    private bool _discarding;
    private string _selectedCatalogId = DoxieCatalogStore.DefaultCatalogId;

    private IReadOnlyList<Workspace> _workspaces = Array.Empty<Workspace>();
    private IReadOnlyList<DoxieCatalogRoot> _catalogs = Array.Empty<DoxieCatalogRoot>();
    private string _attachWorkspaceId = string.Empty;

    // Manifest preview state
    private Viamus.Doxie.Orchestrator.Builder.AgentManifest? _manifest;
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
        "pt-BR" => "As bibliotecas montadas da área de trabalho escolhida entram no contexto do sandbox. Deixe vazio para uma sessão isolada.",
        "es" => "Las bibliotecas montadas del espacio de trabajo elegido entran en el contexto del sandbox. Déjalo vacío para una sesión aislada.",
        _ => "The chosen workspace's mounted libraries are inlined into the sandbox context. Leave empty for an isolated session.",
    };

    protected override void OnInitialized()
    {
        _existingSessions = BuilderFactory.List(Viamus.Doxie.Orchestrator.Builder.BuilderKind.Agent);
        _workspaces = WorkspaceStore.ListAll();
        _catalogs = CatalogStore.ListCatalogs();

        // If we landed via /agents/new?edit=<id>, validate the target
        // agent exists; otherwise drop the param and fall back to the
        // standard create flow with a friendly snackbar.
        if (!string.IsNullOrEmpty(EditAgentId) && AgentCatalog.FindById(EditAgentId) is null)
        {
            Snackbar.AddDoxieToast($"Agent '{EditAgentId}' not found â€” starting a fresh session instead.", Severity.Warning);
            EditAgentId = null;
        }
        else if (!string.IsNullOrEmpty(EditAgentId))
        {
            _selectedCatalogId = AgentCatalog.FindById(EditAgentId)?.CatalogId ?? DoxieCatalogStore.DefaultCatalogId;
        }

        // Deep-link from the dashboard: /agents/new?session=<id> auto-
        // resumes the matching builder session. Skip silently if the id
        // is unknown or belongs to a workflow builder.
        if (!string.IsNullOrWhiteSpace(SessionQueryParam))
        {
            var info = BuilderFactory.Find(SessionQueryParam);
            if (info is { Kind: Viamus.Doxie.Orchestrator.Builder.BuilderKind.Agent })
            {
                _session = info;
                LoadCatalogFromSession(info);
                _mountId = "agent-builder-mount-" + Guid.NewGuid().ToString("N")[..8];
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

            // The bootstrap (e.g. "/agent-builder\r" for Claude or the
            // inlined skill body for Codex) is now injected server-side
            // by ConsoleSession.Spawn() via the initialInput it received
            // from BuilderSessionFactory. Nothing to do client-side.

            // Begin manifest polling. 1.5s is gentle on the filesystem
            // and quick enough that the user sees their changes within
            // a perception-of-instant window.
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
            var editArg = string.IsNullOrEmpty(EditAgentId) ? null : EditAgentId;
            var result = await Js.InvokeAsync<StartResult?>("doxieOs.startAgentBuilder", workspaceArg, editArg);
            if (result is null || !result.Ok || string.IsNullOrEmpty(result.SessionId))
            {
                Snackbar.AddDoxieToast($"Could not start builder session: {result?.Message ?? "unknown error"}", Severity.Error);
                return;
            }
            // Re-pull the session info from the server so we have the
            // canonical sandbox path / label / kind set rather than
            // patching a half-built record together client-side.
            var info = BuilderFactory.Find(result.SessionId);
            if (info is null)
            {
                Snackbar.AddDoxieToast("Builder session vanished right after creation â€” try again.", Severity.Error);
                return;
            }
            _session = info;
            LoadCatalogFromSession(info);
            _mountId = "agent-builder-mount-" + Guid.NewGuid().ToString("N")[..8];
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
        _mountId = "agent-builder-mount-" + Guid.NewGuid().ToString("N")[..8];
        _attachedSessionId = null;
    }

    private void LoadCatalogFromSession(Viamus.Doxie.Orchestrator.Builder.BuilderSessionInfo info)
    {
        _selectedCatalogId = !string.IsNullOrEmpty(info.EditAgentId)
            ? AgentCatalog.FindById(info.EditAgentId)?.CatalogId ?? DoxieCatalogStore.DefaultCatalogId
            : _selectedCatalogId;
    }

    private async Task PollManifestAsync()
    {
        if (_session is null) return;
        try
        {
            var result = await Js.InvokeAsync<ManifestEnvelope?>("doxieOs.readBuilderManifest", _session.SessionId);
            if (result is null) return;

            // Skip the StateHasChanged churn if nothing meaningful
            // changed since last poll (file timestamp + presence flag +
            // error count, since semantic errors can change without the
            // file mtime moving).
            var newErrors = result.Errors ?? new List<string>();
            var changed = result.UpdatedAt != _lastManifestUtc
                          || _manifestPresent != result.Present
                          || _manifestValid != result.Valid
                          || _manifestErrors.Count != newErrors.Count
                          || !_manifestErrors.SequenceEqual(newErrors);
            if (!changed) return;

            _manifestPresent = result.Present;
            _manifestValid = result.Valid;
            _manifestError = result.Error;
            _manifestErrors = newErrors;
            _manifest = result.Manifest;
            _lastManifestUtc = result.UpdatedAt ?? DateTime.MinValue;

            await InvokeAsync(StateHasChanged);
        }
        catch (Microsoft.JSInterop.JSException) { /* page navigated away */ }
        catch (Microsoft.JSInterop.JSDisconnectedException) { /* circuit gone */ }
    }

    private bool CanSave() =>
        _session is not null
        && _manifestPresent
        && _manifestValid
        && _manifestErrors.Count == 0
        && _manifest is not null
        && !string.IsNullOrWhiteSpace(_manifest.Id)
        && !string.IsNullOrWhiteSpace(_manifest.Name)
        && !_saving;

    private async Task HandleSave()
    {
        if (_session is null || _manifest is null) return;
        _saving = true;
        try
        {
            // Edit sessions always overwrite â€” by definition the target
            // agent already exists; the user clicked Edit specifically
            // to mutate it. Create sessions never overwrite so a stale
            // id collision raises a friendly error instead of a silent
            // clobber.
            var overwrite = !string.IsNullOrEmpty(_session.EditAgentId);
            var result = await Js.InvokeAsync<SaveResult?>("doxieOs.saveBuilderAgent", _session.SessionId, overwrite, _selectedCatalogId);
            if (result is null)
            {
                Snackbar.AddDoxieToast("Save failed: no response", Severity.Error);
                return;
            }
            if (result.Ok)
            {
                Snackbar.AddDoxieToast($"Saved agent \"{result.AgentId}\"", Severity.Success);
                if (!string.IsNullOrEmpty(result.Href))
                {
                    Nav.NavigateTo(result.Href);
                }
            }
            else
            {
                // Multi-line snackbar so the user sees every blocking
                // problem at once instead of fixing one and re-Saving
                // for the next.
                var detail = (result.Errors is { Count: > 0 })
                    ? string.Join(" Â� ", result.Errors)
                    : (result.Message ?? "unknown");
                Snackbar.AddDoxieToast($"Save failed: {detail}", Severity.Error);
            }
        }
        finally
        {
            _saving = false;
        }
    }

    private async Task OpenDraftIconPickerAsync()
    {
        if (_session is null || _manifest is null) return;
        var dialog = await DialogService.ShowAsync<AgentIconPickerDialog>(
            "Choose agent icon",
            new DialogParameters
            {
                [nameof(AgentIconPickerDialog.Title)] = $"Choose icon for {_manifest.Name ?? "draft agent"}",
                [nameof(AgentIconPickerDialog.CurrentIcon)] = _manifest.Icon,
            },
            new DialogOptions { MaxWidth = MaxWidth.Medium, FullWidth = true, CloseButton = true });
        var result = await dialog.Result;
        if (result is not { Canceled: false }) return;

        var selected = result.Data as string;
        if (!string.IsNullOrWhiteSpace(selected) && !AgentIconPalette.Contains(selected)) return;

        var update = await Js.InvokeAsync<DraftIconUpdateResult?>(
            "doxieOs.updateBuilderManifestIcon", _session.SessionId, selected);
        if (update is { Ok: true })
        {
            _manifest.Icon = selected;
            _lastManifestUtc = update.UpdatedAt ?? DateTime.MinValue;
            Snackbar.AddDoxieToast(string.IsNullOrWhiteSpace(selected) ? "Agent icon reset to category default" : "Agent icon updated", Severity.Success);
            await InvokeAsync(StateHasChanged);
        }
        else
        {
            Snackbar.AddDoxieToast($"Icon update failed: {update?.Message ?? "unknown error"}", Severity.Error);
        }
    }

    private async Task HandleDiscard()
    {
        if (_session is null) return;
        bool confirmed;
        try
        {
            confirmed = await Js.InvokeAsync<bool>("doxieOs.confirm",
                "Discard this builder session?\n\nThe PTY is killed, the manifest is NOT promoted, and the sandbox folder is left on disk for the housekeeping pass.");
        }
        catch (Microsoft.JSInterop.JSException) { confirmed = false; }
        if (!confirmed) return;

        _discarding = true;
        try
        {
            try { await Js.InvokeVoidAsync("doxieOs.deleteConsole", _session.SessionId); }
            catch (Microsoft.JSInterop.JSException) { /* surface as snackbar */ }
            Snackbar.AddDoxieToast($"Ended builder session \"{_session.SandboxTag}\"", Severity.Info);
            Nav.NavigateTo("/agents");
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

    private static string RequirementIcon(string? kind) => (kind ?? "").ToLowerInvariant() switch
    {
        "env" => Icons.Material.Filled.Key,
        "mcp" => Icons.Material.Filled.Cable,
        "tool" => Icons.Material.Filled.Build,
        _ => Icons.Material.Filled.Verified,
    };

    private void SetSelectedCatalog(string? value)
    {
        _selectedCatalogId = string.IsNullOrWhiteSpace(value) ? DoxieCatalogStore.DefaultCatalogId : value;
    }

    private string SelectedCatalogLabel() =>
        _catalogs.FirstOrDefault(c => string.Equals(c.Id, _selectedCatalogId, StringComparison.OrdinalIgnoreCase))?.Name
        ?? "Default catalog";

    private static string AgentManifestIcon(Viamus.Doxie.Orchestrator.Builder.AgentManifest manifest) =>
        AgentIconPalette.Resolve(manifest.Icon) ?? CategoryIcon(manifest.Category);

    private static string CategoryIcon(string? category)
    {
        if (Enum.TryParse<AgentCategory>(category, ignoreCase: true, out var cat))
        {
            return cat switch
            {
                AgentCategory.Doxie => Icons.Material.Filled.Pets,
                AgentCategory.Developer => Icons.Material.Filled.Code,
                AgentCategory.Fixer => Icons.Material.Filled.Build,
                AgentCategory.Builder => Icons.Material.Filled.School,
                AgentCategory.Inspector => Icons.Material.Filled.RuleFolder,
                AgentCategory.Connector => Icons.Material.Filled.Cable,
                _ => Icons.Material.Filled.SmartToy,
            };
        }
        return Icons.Material.Filled.BookmarkBorder;
    }

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

    private sealed record StartResult(bool Ok, int Status, string? SessionId, string? SandboxTag, string? Label, string? AttachedWorkspaceId, string? EditAgentId, string? Message);
    private sealed record SaveResult(bool Ok, int Status, string? AgentId, string? CatalogId, string? Href, string? Message, List<string>? Errors);
    private sealed record ManifestEnvelope(bool Present, bool Valid, Viamus.Doxie.Orchestrator.Builder.AgentManifest? Manifest, string? Error, List<string>? Errors, DateTime? UpdatedAt);
    private sealed record DraftIconUpdateResult(bool Ok, int Status, DateTime? UpdatedAt, string? Message);
}



