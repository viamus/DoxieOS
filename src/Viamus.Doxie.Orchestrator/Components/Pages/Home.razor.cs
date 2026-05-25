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

public partial class Home
{
    private IReadOnlyList<WorkflowDefinition> _workflows = Array.Empty<WorkflowDefinition>();
    private IReadOnlyList<AgentDescriptor> _agents = Array.Empty<AgentDescriptor>();
    private Dictionary<string, int> _agentsByCategory = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<Library> _libraries = Array.Empty<Library>();
    private IReadOnlyList<Workspace> _workspaces = Array.Empty<Workspace>();
    private IReadOnlyList<ConsoleSession> _consoles = Array.Empty<ConsoleSession>();
    private IReadOnlyList<WorkflowRun> _workflowRuns = Array.Empty<WorkflowRun>();
    private IReadOnlyList<AgentRun> _agentRuns = Array.Empty<AgentRun>();
    private int _liveConsoles;
    private int _userCreatedAgentCount;
    private double[] _workflowSparkline = new double[7]; // last 7 days, oldest â†’ newest
    private List<(WorkflowDefinition Workflow, DateTime FireAt)> _nextFirings = new();
    private List<ActivityEntry> _recentActivity = new();
    private bool _dashboardLoading = true;
    private bool _dashboardLoaded;
    private string? _dashboardLoadError;
    private readonly object _reloadSync = new();
    private readonly CancellationTokenSource _disposeCts = new();
    private CancellationTokenSource? _eventReloadCts;
    private bool _reloadRequested;
    private bool _reloadShowLoadingRequested;
    private bool _reloadWorkerRunning;
    private bool _disposed;


    // 7-day sparkline embedded inside the Workflows stat card. The
    // series wraps _workflowSparkline so MudChart re-renders whenever
    // Reload() rewrites the array. Single Tigerlily line — quiet
    // enough to live inside a stat card without dominating it.
    private IEnumerable<WorkflowRun> LiveWorkflowRuns =>
        _workflowRuns
            .Where(r => r.Status is WorkflowRunStatus.Queued or WorkflowRunStatus.Running)
            .OrderByDescending(r => r.StartedAt);

    private IEnumerable<AgentRun> LiveAgentRuns =>
        _agentRuns
            .Where(r => r.Status is AgentRunStatus.Queued or AgentRunStatus.Running)
            .OrderByDescending(r => r.StartedAt);

    private int LiveCount =>
        LiveWorkflowRuns.Count() + LiveAgentRuns.Count() + _liveConsoles;

    private string AgentsCategorySummary()
    {
        if (_agentsByCategory.Count == 0)
            return Lang["Home.NoAgentsYet"];

        var orderedCategories = OrderedAgentCategories().ToArray();
        var summary = orderedCategories
            .Take(3)
            .Select(g => FormatAgentCategory(g.Key, g.Value))
            .ToList();

        var remainingCategoryCount = orderedCategories.Length - summary.Count;
        if (remainingCategoryCount > 0)
            summary.Add($"+{remainingCategoryCount} {Lang["Home.More"]}");

        return string.Join(" · ", summary);
    }

    private string AgentsCategoryBreakdownTitle()
    {
        if (_agentsByCategory.Count == 0)
            return Lang["Home.NoAgentsYet"];

        return string.Join(" · ", OrderedAgentCategories().Select(g => FormatAgentCategory(g.Key, g.Value)));
    }

    private IEnumerable<KeyValuePair<string, int>> OrderedAgentCategories() =>
        _agentsByCategory
            .OrderByDescending(g => g.Value)
            .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase);

    private static string FormatAgentCategory(string category, int count) =>
        $"{count} {category.ToLowerInvariant()}";

    private string LibraryMemorySummary(int totalMemories)
    {
        if (_libraries.Count == 0)
        {
            return Lang["Home.NoMemoryPacks"];
        }

        return Lang.CurrentLanguage switch
        {
            "pt-BR" => $"{totalMemories} {Lang["Home.MemoryEntriesTotal"]}",
            "es" => $"{totalMemories} {Lang["Home.MemoryEntriesTotal"]}",
            _ => $"{totalMemories} memory entr{(totalMemories == 1 ? "y" : "ies")} total",
        };
    }

    private string LiveConsolesSummary()
    {
        if (_liveConsoles == 0)
        {
            return Lang["Home.NoLiveConsoles"];
        }

        return Lang.CurrentLanguage switch
        {
            "pt-BR" or "es" => $"{_liveConsoles} {Lang["Home.LiveConsolesCount"]}",
            _ => $"{_liveConsoles} live console{(_liveConsoles == 1 ? "" : "s")}",
        };
    }

    protected override void OnInitialized()
    {
        // Re-render the live panels whenever any of the underlying
        // sources fire. Console store events use ConsoleSession arg;
        // runner events use the run record. Both just trigger a
        // full refresh — cheap, the page is small.
        AgentRunner.RunUpdated += OnAgentRunUpdated;
        WorkflowRunner.RunUpdated += OnWorkflowRunUpdated;
        ConsoleStore.SessionCreated += OnSessionChanged;
        ConsoleStore.SessionRemoved += OnSessionChanged;
        ConsoleStore.SessionUpdated += OnSessionChanged;
        Lang.Changed += OnLanguageChanged;

        QueueDashboardReload(showLoading: true);
    }

    public void Dispose()
    {
        _disposed = true;
        _disposeCts.Cancel();
        _eventReloadCts?.Cancel();

        AgentRunner.RunUpdated -= OnAgentRunUpdated;
        WorkflowRunner.RunUpdated -= OnWorkflowRunUpdated;
        ConsoleStore.SessionCreated -= OnSessionChanged;
        ConsoleStore.SessionRemoved -= OnSessionChanged;
        ConsoleStore.SessionUpdated -= OnSessionChanged;
        Lang.Changed -= OnLanguageChanged;
    }

    private void OnAgentRunUpdated(AgentRun _) => QueueDashboardReloadDebounced();
    private void OnWorkflowRunUpdated(WorkflowRun _) => QueueDashboardReloadDebounced();
    private void OnSessionChanged(ConsoleSession? _) => QueueDashboardReloadDebounced();
    private void OnLanguageChanged() => InvokeAsync(StateHasChanged);

    private void QueueDashboardReloadDebounced()
    {
        if (_disposed) return;

        var previous = _eventReloadCts;
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_disposeCts.Token);
        _eventReloadCts = cts;
        previous?.Cancel();

        _ = DebouncedReloadAsync(cts);
    }

    private async Task DebouncedReloadAsync(CancellationTokenSource cts)
    {
        try
        {
            await Task.Delay(750, cts.Token);
            QueueDashboardReload(showLoading: false);
        }
        catch (OperationCanceledException)
        {
            // A newer dashboard event superseded this refresh.
        }
        finally
        {
            if (ReferenceEquals(_eventReloadCts, cts))
            {
                _eventReloadCts = null;
            }
            cts.Dispose();
        }
    }

    private void QueueDashboardReload(bool showLoading)
    {
        if (_disposed) return;

        var shouldStartWorker = false;
        lock (_reloadSync)
        {
            _reloadRequested = true;
            _reloadShowLoadingRequested |= showLoading;
            if (!_reloadWorkerRunning)
            {
                _reloadWorkerRunning = true;
                shouldStartWorker = true;
            }
        }

        if (shouldStartWorker)
        {
            _ = RunReloadWorkerAsync();
        }
    }

    private async Task RunReloadWorkerAsync()
    {
        while (!_disposeCts.IsCancellationRequested)
        {
            bool showLoading;
            lock (_reloadSync)
            {
                if (!_reloadRequested)
                {
                    _reloadWorkerRunning = false;
                    return;
                }

                _reloadRequested = false;
                showLoading = _reloadShowLoadingRequested;
                _reloadShowLoadingRequested = false;
            }

            await ReloadOnceAsync(showLoading, _disposeCts.Token);
        }

        lock (_reloadSync)
        {
            _reloadWorkerRunning = false;
        }
    }

    private async Task ReloadOnceAsync(bool showLoading, CancellationToken cancellationToken)
    {
        if (showLoading)
        {
            await InvokeAsync(() =>
            {
                if (_disposed) return;
                _dashboardLoading = true;
                _dashboardLoadError = null;
                StateHasChanged();
            });
        }

        try
        {
            var snapshot = await Task.Run(() => BuildDashboardSnapshot(cancellationToken), cancellationToken);
            await InvokeAsync(() =>
            {
                if (_disposed || cancellationToken.IsCancellationRequested) return;
                ApplyDashboardSnapshot(snapshot);
                _dashboardLoaded = true;
                _dashboardLoadError = null;
                _dashboardLoading = false;
                StateHasChanged();
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Navigation away cancels pending dashboard work.
        }
        catch (Exception ex)
        {
            await InvokeAsync(() =>
            {
                if (_disposed) return;
                _dashboardLoadError = $"Could not load dashboard: {ex.Message}";
                _dashboardLoading = false;
                StateHasChanged();
            });
        }
    }

    private DashboardSnapshot BuildDashboardSnapshot(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var workflows = WorkflowStore.ListAll();
        var agents = AgentCatalog.GetAll();
        var libraries = LibraryStore.ListAll();
        var workspaces = WorkspaceStore.ListAll();
        var consoles = ConsoleStore.ListAll();
        var workflowRuns = WorkflowRunStore.ListAll();
        var agentRuns = AgentRunStore.ListAll();
        cancellationToken.ThrowIfCancellationRequested();

        var agentsByCategory = agents.GroupBy(a => a.DisplayCategory, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        // Empty-state CTA gates on user-created agents only — Doxie kit
        // (the framework) and workflow Connectors (aggregate, etc.) are
        // present in every install, so counting them would mask the
        // first-launch case the welcome panel is meant for. A custom
        // category ALWAYS counts as user-created (the user defined it),
        // so the check guards against the typed enum only.
        var userCreatedAgentCount = agents.Count(a =>
            a.CustomCategory is not null
            || a.Category is not AgentCategory.Doxie and not AgentCategory.Connector);
        var liveConsoles = consoles.Count(s => s.Status == ConsoleSessionStatus.Running);

        // Failures pill — counts both workflow + agent failures in the
        // 24h window. Cancelled / interrupted intentionally don't count
        // (they're user-initiated stops, not "something broke").
        // Sparkline buckets: 7 days, oldest first, all run sources merged.
        // Day boundary is local-midnight so the rightmost bucket reflects
        // "today" intuitively for the user.
        var workflowSparkline = new double[7];
        var todayStart = DateTimeOffset.Now.Date;
        var sparklineStart = new DateTimeOffset(todayStart, DateTimeOffset.Now.Offset).AddDays(-6);
        foreach (var r in workflowRuns)
        {
            if (r.StartedAt < sparklineStart) continue;
            var dayIndex = (int)(r.StartedAt.Date - sparklineStart.Date).TotalDays;
            if (dayIndex >= 0 && dayIndex < 7) workflowSparkline[dayIndex]++;
        }
        foreach (var r in agentRuns)
        {
            if (r.StartedAt < sparklineStart) continue;
            var dayIndex = (int)(r.StartedAt.Date - sparklineStart.Date).TotalDays;
            if (dayIndex >= 0 && dayIndex < 7) workflowSparkline[dayIndex]++;
        }

        // Compute the next cron firing per enabled cron workflow, sort
        // ascending so the soonest is on top. Workflows whose cron is
        // unparseable just don't show up.
        var now = DateTime.UtcNow;
        var nextFirings = workflows
            .Where(w => w.Enabled && w.Trigger.Kind == WorkflowTriggerKind.Cron)
            .Select(w => (Workflow: w, FireAt: CronExpression.NextOccurrenceAfter(w.Trigger.CronExpression, now)))
            .Where(t => t.FireAt is not null)
            .Select(t => (t.Workflow, FireAt: t.FireAt!.Value))
            .OrderBy(t => t.FireAt)
            .ToList();

        var recentActivity = BuildRecentActivity(workflowRuns, agentRuns, workflows, agents);
        var providerSpend = BuildProviderSpend(agentRuns, out var costTotalUsd, out var costTotalTokens, out var costInputTokens,
            out var costOutputTokens, out var costCacheReadTokens, out var costCacheCreationTokens, out var costRunCount);

        return new DashboardSnapshot(
            workflows,
            agents,
            agentsByCategory,
            libraries,
            workspaces,
            consoles,
            workflowRuns,
            agentRuns,
            liveConsoles,
            userCreatedAgentCount,
            workflowSparkline,
            nextFirings,
            recentActivity,
            providerSpend,
            costTotalUsd,
            costTotalTokens,
            costInputTokens,
            costOutputTokens,
            costCacheReadTokens,
            costCacheCreationTokens,
            costRunCount);
    }

    private void ApplyDashboardSnapshot(DashboardSnapshot snapshot)
    {
        _workflows = snapshot.Workflows;
        _agents = snapshot.Agents;
        _agentsByCategory = snapshot.AgentsByCategory;
        _libraries = snapshot.Libraries;
        _workspaces = snapshot.Workspaces;
        _consoles = snapshot.Consoles;
        _workflowRuns = snapshot.WorkflowRuns;
        _agentRuns = snapshot.AgentRuns;
        _liveConsoles = snapshot.LiveConsoles;
        _userCreatedAgentCount = snapshot.UserCreatedAgentCount;
        _workflowSparkline = snapshot.WorkflowSparkline;
        _nextFirings = snapshot.NextFirings;
        _recentActivity = snapshot.RecentActivity;
        _providerSpend = snapshot.ProviderSpend;
        _costTotalUsd = snapshot.CostTotalUsd;
        _costTotalTokens = snapshot.CostTotalTokens;
        _costInputTokens = snapshot.CostInputTokens;
        _costOutputTokens = snapshot.CostOutputTokens;
        _costCacheReadTokens = snapshot.CostCacheReadTokens;
        _costCacheCreationTokens = snapshot.CostCacheCreationTokens;
        _costRunCount = snapshot.CostRunCount;
    }

    private sealed record DashboardSnapshot(
        IReadOnlyList<WorkflowDefinition> Workflows,
        IReadOnlyList<AgentDescriptor> Agents,
        Dictionary<string, int> AgentsByCategory,
        IReadOnlyList<Library> Libraries,
        IReadOnlyList<Workspace> Workspaces,
        IReadOnlyList<ConsoleSession> Consoles,
        IReadOnlyList<WorkflowRun> WorkflowRuns,
        IReadOnlyList<AgentRun> AgentRuns,
        int LiveConsoles,
        int UserCreatedAgentCount,
        double[] WorkflowSparkline,
        List<(WorkflowDefinition Workflow, DateTime FireAt)> NextFirings,
        List<ActivityEntry> RecentActivity,
        IReadOnlyList<ProviderSpend> ProviderSpend,
        decimal CostTotalUsd,
        long CostTotalTokens,
        long CostInputTokens,
        long CostOutputTokens,
        long CostCacheReadTokens,
        long CostCacheCreationTokens,
        int CostRunCount);

    private static double SparklineBarHeight(double value, double max) =>
        max <= 0 ? 3 : Math.Clamp(3 + (value / max * 29), 3, 32);


}

