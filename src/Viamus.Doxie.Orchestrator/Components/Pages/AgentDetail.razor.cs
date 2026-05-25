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

public partial class AgentDetail
{
    [Parameter] public string Id { get; set; } = "";

    [SupplyParameterFromQuery(Name = "run")]
    public string? RunId { get; set; }

    [SupplyParameterFromQuery(Name = "workspace")]
    public string? WorkspaceQueryParam { get; set; }

    private AgentDescriptor? _agent;
    private string _arguments = "";
    private AgentRun? _currentRun;
    private IReadOnlyList<AgentRun> _history = Array.Empty<AgentRun>();
    private List<BreadcrumbItem> _breadcrumbs = new();
    private AgentMode? _selectedMode;
    private IReadOnlyList<DoxieSkillMemory> _memories = Array.Empty<DoxieSkillMemory>();
    private readonly Dictionary<string, string> _fieldValues = new();
    private string _extraArgs = "";
    private bool _historyLoading;
    private bool _currentRunLoading;
    private int _runLoadGeneration;
    private const int OutputPanelLineLimit = 500;
    private const int OutputPanelLineChars = 4_000;
    private readonly Dictionary<AgentRequirement, AgentRequirementStatus> _requirementStatuses = new();
    private IReadOnlyList<Workspace> _workspaces = Array.Empty<Workspace>();
    private string _selectedWorkspaceId = string.Empty;
    private readonly List<EnvVarDraft> _envVars = new();
    private string _operatorHintDraft = string.Empty;
    private bool _sendingOperatorHint;

    // ASCII art credit: hjw (signature kept on the canvas).
    private const string DoxieAscii =
"""
                              __
       ,                    ," e`--o  <Woof> 
      ((                   (  | __,'      <Woof>
       \\~----------------' \_;/      <Woof>
       (                      /
       /) ._______________.  )
      (( (               (( (
       ``-'               ``-'
""";

    private string ResolvedArguments
    {
        get
        {
            if (_selectedMode is null) return string.Empty;
            if (_selectedMode.Fields is { Count: > 0 })
            {
                return _selectedMode.BuildArguments(_fieldValues);
            }
            var template = _selectedMode.ArgumentsTemplate.Trim();
            return string.IsNullOrWhiteSpace(_extraArgs)
                ? template
                : $"{template} {_extraArgs.Trim()}";
        }
    }

    private bool IsRunning =>
        _currentRun is { Status: AgentRunStatus.Queued or AgentRunStatus.Running };

    private string DefaultArgs => Id switch
    {
        "sample-watch" => "check",
        _ => "",
    };

    private string RunInsideWorkspaceLabel(bool required) => Lang.CurrentLanguage switch
    {
        "pt-BR" => $"Executar em Ã¡rea de trabalho ({(required ? "obrigatÃ³rio" : "opcional")})",
        "es" => $"Ejecutar en espacio de trabajo ({(required ? "obligatorio" : "opcional")})",
        _ => $"Run inside Workspace ({(required ? "required" : "optional")})",
    };

    private string WorkspaceRequiredError() => Lang.CurrentLanguage switch
    {
        "pt-BR" => "Este agente precisa rodar em uma Ã¡rea de trabalho. Escolha uma acima.",
        "es" => "Este agente debe ejecutarse en un espacio de trabajo. Elige uno arriba.",
        _ => "This agent must run inside a Workspace - pick one above.",
    };

    private string WorkspaceRequiredHelper() => Lang.CurrentLanguage switch
    {
        "pt-BR" => "Este agente lÃª as bibliotecas montadas da Ã¡rea de trabalho como contexto e/ou escreve a saÃ­da nela.",
        "es" => "Este agente lee las bibliotecas montadas del espacio de trabajo como contexto y/o escribe la salida allÃ­.",
        _ => "This agent reads the workspace's mounted libraries as context and/or writes output back into it.",
    };

    private string WorkspaceOptionalHelper() => Lang.CurrentLanguage switch
    {
        "pt-BR" => "Define o diretÃ³rio de trabalho do agente para essa Ã¡rea de trabalho. Deixe em branco para usar a raiz do projeto.",
        "es" => "Define el directorio de trabajo del agente en ese espacio de trabajo. DÃ©jalo vacÃ­o para usar la raÃ­z del proyecto.",
        _ => "Sets the agent's working directory to that Workspace. Leave blank for the project root.",
    };

    private string NoWorkspaceLabel() => Lang.CurrentLanguage switch
    {
        "pt-BR" => "(nenhuma - raiz do projeto)",
        "es" => "(ninguno - raÃ­z del proyecto)",
        _ => "(none - project root)",
    };

    protected override void OnInitialized()
    {
        _workspaces = WorkspaceStore.ListAll();
        // Honour ?workspace=<id> deep-links from the Workspaces page so
        // the dropdown is pre-selected when the user clicks "Run agent
        // here". Falls back to "(none)" when the id doesn't match.
        if (!string.IsNullOrWhiteSpace(WorkspaceQueryParam)
            && _workspaces.Any(w => string.Equals(w.Id, WorkspaceQueryParam, StringComparison.OrdinalIgnoreCase)))
        {
            _selectedWorkspaceId = WorkspaceQueryParam;
        }

        _agent = Catalog.FindById(Id);
        if (_agent is not null)
        {
            _breadcrumbs =
            [
                new BreadcrumbItem(Lang["Common.Home"], href: "/"),
                new BreadcrumbItem(Lang["Agents.Title"], href: "/agents"),
                new BreadcrumbItem(_agent.Name, href: null, disabled: true),
            ];
            // Memories list is read on every page load (cheap Ã¢â‚¬â€ N small
            // markdown files). Refreshing without a restart requires
            // navigating away and back, which is fine for the alpha-grade
            // "drop a file" workflow.
            _memories = SkillReader.LoadMemories(_agent.SkillName);
            RecomputeRequirementStatuses();
            if (_agent.Modes is { Count: > 0 } modes)
            {
                _selectedMode = modes[0];
            }
            else
            {
                _arguments = DefaultArgs;
            }
            _historyLoading = true;
            _currentRunLoading = true;
            _ = LoadInitialRunDataAsync();
        }

        Runner.RunUpdated += OnRunUpdated;
    }

    private async Task LoadInitialRunDataAsync()
    {
        try
        {
            await LoadHistoryAsync(showSpinner: true);
            await SelectInitialRunAsync();
        }
        catch (Exception ex)
        {
            _historyLoading = false;
            _currentRunLoading = false;
            Snackbar.Add($"Could not load run history: {ex.Message}", Severity.Error);
        }
        finally
        {
            await InvokeAsync(StateHasChanged);
        }
    }

    private async Task LoadHistoryAsync(bool showSpinner)
    {
        if (_agent is null) return;
        if (showSpinner)
        {
            _historyLoading = true;
        }

        var agentId = _agent.Id;
        try
        {
            _history = await Task.Run(() => Store.ListByAgent(agentId));
        }
        finally
        {
            _historyLoading = false;
        }
    }

    private async Task SelectInitialRunAsync()
    {
        if (!string.IsNullOrWhiteSpace(RunId))
        {
            await SelectRunAsync(RunId);
            return;
        }

        if (_history.FirstOrDefault() is { } mostRecent)
        {
            await SelectRunAsync(mostRecent.Id, mostRecent);
            return;
        }

        _currentRunLoading = false;
    }

    private async Task SelectRunAsync(string runId, AgentRun? summary = null)
    {
        var live = Runner.Get(runId);
        if (live is not null)
        {
            _currentRun = live;
            _currentRunLoading = false;
            return;
        }

        _currentRun = summary ?? _history.FirstOrDefault(r => r.Id == runId);
        _currentRunLoading = true;
        var generation = ++_runLoadGeneration;
        await InvokeAsync(StateHasChanged);

        var loaded = await Task.Run(() => Store.Get(runId, OutputPanelLineLimit));
        if (generation != _runLoadGeneration)
        {
            return;
        }

        if (loaded is not null)
        {
            _currentRun = loaded;
        }
        _currentRunLoading = false;
        await InvokeAsync(StateHasChanged);
    }

    private void UpsertHistoryRun(AgentRun run)
    {
        var next = _history.Where(r => r.Id != run.Id).ToList();
        next.Add(run);
        _history = next
            .OrderByDescending(r => r.StartedAt)
            .ThenByDescending(r => r.Id, StringComparer.Ordinal)
            .ToList();
    }

    private void OnModeChanged(AgentMode? mode)
    {
        _selectedMode = mode;
        _fieldValues.Clear();
        _extraArgs = "";
    }

    private void OnWorkspaceChanged(string value)
    {
        _selectedWorkspaceId = value ?? string.Empty;
    }

    private string GetFieldValue(string fieldId) =>
        _fieldValues.TryGetValue(fieldId, out var value) ? value : string.Empty;

    private void SetFieldValue(string fieldId, string value)
    {
        _fieldValues[fieldId] = value;
    }

    public void Dispose()
    {
        Runner.RunUpdated -= OnRunUpdated;
    }

    private void StartRun()
    {
        if (_agent is null) return;
        var args = _agent.Modes is { Count: > 0 } && _selectedMode is not null
            ? ResolvedArguments
            : _arguments;
        var keepAlive = _selectedMode?.KeepSessionAlive ?? false;
        var cwdOverride = string.IsNullOrEmpty(_selectedWorkspaceId)
            ? null
            : WorkspaceStore.GetById(_selectedWorkspaceId)?.Path;

        // Per-run env: drop empty keys, dedupe (last wins), pass to runner.
        // Nothing persisted Ã¢â‚¬â€ the next page load starts with an empty list.
        IReadOnlyDictionary<string, string>? envOverrides = null;
        if (_envVars.Count > 0)
        {
            var dict = _envVars
                .Where(v => !string.IsNullOrWhiteSpace(v.Key))
                .GroupBy(v => v.Key.Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Last().Value ?? string.Empty, StringComparer.OrdinalIgnoreCase);
            if (dict.Count > 0) envOverrides = dict;
        }

        _currentRunLoading = false;
        var started = Runner.Start(
            agentId: _agent.Id,
            arguments: args,
            keepSessionAlive: keepAlive,
            workingDirectoryOverride: cwdOverride,
            envOverrides: envOverrides,
            modeId: _selectedMode?.Id,
            workspaceId: string.IsNullOrEmpty(_selectedWorkspaceId) ? null : _selectedWorkspaceId);
        _currentRun = started;
        UpsertHistoryRun(started);
    }

    private void AddEnvVar()
    {
        _envVars.Add(new EnvVarDraft());
        RecomputeRequirementStatuses();
    }

    private void OnEnvVarChanged()
    {
        // Called whenever the user types in a key or value, or removes a
        // row. Re-runs prerequisite checks so a token chip flips green
        // the moment the user fills in the matching key Ã¢â‚¬â€ no Run-then-
        // discover-it-was-missing surprise.
        RecomputeRequirementStatuses();
    }

    private void RecomputeRequirementStatuses()
    {
        if (_agent?.Requirements is not { } reqs) return;
        var supplied = BuildSuppliedEnvDict();
        foreach (var req in reqs)
        {
            _requirementStatuses[req] = RequirementChecker.Check(req, supplied);
        }
    }

    private IReadOnlyDictionary<string, string>? BuildSuppliedEnvDict()
    {
        if (_envVars.Count == 0) return null;
        var dict = _envVars
            .Where(v => !string.IsNullOrWhiteSpace(v.Key))
            .GroupBy(v => v.Key.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Last().Value ?? string.Empty, StringComparer.OrdinalIgnoreCase);
        return dict.Count == 0 ? null : dict;
    }

    private static bool LooksSecretKey(string? key) =>
        !string.IsNullOrEmpty(key) && (
            key.Contains("token", StringComparison.OrdinalIgnoreCase)
            || key.Contains("secret", StringComparison.OrdinalIgnoreCase)
            || key.Contains("password", StringComparison.OrdinalIgnoreCase)
            || key.Contains("key", StringComparison.OrdinalIgnoreCase)
            || key.EndsWith("_pat", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Mutable draft for one row of the per-run env table. Lives only in
    /// page state Ã¢â‚¬â€ never persisted. A future improvement could persist
    /// a per-agent default set, but for now sensitive secrets stay
    /// in-memory and out of disk.
    /// </summary>
    private sealed class EnvVarDraft
    {
        public string Key { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;
    }

    private void CancelRun()
    {
        if (_currentRun is null) return;
        Runner.Cancel(_currentRun.Id);
    }

    private async Task SendOperatorHint()
    {
        if (_currentRun is null || string.IsNullOrWhiteSpace(_operatorHintDraft) || _sendingOperatorHint) return;
        _sendingOperatorHint = true;
        try
        {
            var hint = _operatorHintDraft.Trim();
            if (Runner.AddOperatorHint(_currentRun.Id, hint))
            {
                _operatorHintDraft = string.Empty;
                Snackbar.Add("Hint sent to the running agent", Severity.Info);
            }
            else
            {
                Snackbar.Add("Could not send hint: this agent run is no longer active.", Severity.Warning);
            }
        }
        finally
        {
            _sendingOperatorHint = false;
            await InvokeAsync(StateHasChanged);
        }
    }

    private async Task OnHistoryClick(TableRowClickEventArgs<AgentRun> args)
    {
        if (args.Item is not null)
        {
            // Prefer the live in-memory ref if the run is still active; otherwise
            // hydrate only the latest visible output tail from disk.
            await SelectRunAsync(args.Item.Id, args.Item);
        }
    }

    private async void OnRunUpdated(AgentRun run)
    {
        if (_agent is null || run.AgentId != _agent.Id) return;
        UpsertHistoryRun(run);
        if (_currentRun?.Id == run.Id)
        {
            _currentRun = run;
            _currentRunLoading = false;
        }
        await InvokeAsync(StateHasChanged);
    }

    private static MudBlazor.Color StatusColor(AgentRunStatus status) => status switch
    {
        AgentRunStatus.Queued => MudBlazor.Color.Default,
        AgentRunStatus.Running => MudBlazor.Color.Info,
        AgentRunStatus.Completed => MudBlazor.Color.Success,
        AgentRunStatus.Failed => MudBlazor.Color.Error,
        AgentRunStatus.Cancelled => MudBlazor.Color.Warning,
        AgentRunStatus.Interrupted => MudBlazor.Color.Dark,
        _ => MudBlazor.Color.Default,
    };

    /// <summary>
    /// Extracts the sandbox tag (last path segment) from an absolute
    /// OutputDir path that lives under <c>./.sandbox/&lt;tag&gt;/</c>.
    /// Returns null for paths that don't match (e.g. workflow node dirs
    /// under <c>./.runs/</c> Ã¢â‚¬â€ those are browsed via WorkflowDetail).
    /// </summary>
    private static string? TryExtractSandboxTag(string? outputDir)
    {
        if (string.IsNullOrWhiteSpace(outputDir)) return null;
        // Normalize to forward slashes so we don't have to handle both
        // separator conventions inline.
        var normalized = outputDir.Replace('\\', '/').TrimEnd('/');
        var idx = normalized.LastIndexOf("/.sandbox/", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;
        var afterMarker = normalized[(idx + "/.sandbox/".Length)..];
        if (string.IsNullOrEmpty(afterMarker)) return null;
        // Sandbox tags are flat Ã¢â‚¬â€ never nested. Take the first segment.
        var slashIdx = afterMarker.IndexOf('/');
        var tag = slashIdx < 0 ? afterMarker : afterMarker[..slashIdx];
        return string.IsNullOrEmpty(tag) ? null : tag;
    }

    /// <summary>
    /// Icon for a category label. Built-ins get their typed icon;
    /// custom user-defined labels get a generic bookmark icon so they
    /// stand out as user content.
    /// </summary>
}

