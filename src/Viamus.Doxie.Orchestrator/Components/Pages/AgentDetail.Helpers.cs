using Microsoft.JSInterop;
using MudBlazor;
using Viamus.Doxie.Orchestrator.Agents;
using Viamus.Doxie.Orchestrator.Agents.Doxie;
using Viamus.Doxie.Orchestrator.Components.Shared;


namespace Viamus.Doxie.Orchestrator.Components.Pages;

public partial class AgentDetail
{
    private static Color MemoryPriorityColor(DoxieSkillMemoryPriority p) => p switch
    {
        DoxieSkillMemoryPriority.High => Color.Primary,
        DoxieSkillMemoryPriority.Medium => Color.Default,
        _ => Color.Dark,
    };

    /// <summary>
    /// Opens the per-skill memories folder in the OS shell. Best-effort â€”
    /// a JS interop failure (e.g. orchestrator running headless without
    /// a desktop) just falls back to a snackbar showing the path so the
    /// user can copy it manually.
    /// </summary>
    private async Task OpenMemoriesFolderAsync()
    {
        if (_agent is null) return;
        var path = $".doxie/skills/{_agent.SkillName}/memories";
        try
        {
            await Js.InvokeVoidAsync("doxieOs.openFolder", path);
        }
        catch (JSException)
        {
            Snackbar.AddDoxieToast($"Memories folder: {path}", Severity.Info);
        }
    }

    private static string RequirementKindLabel(AgentRequirementKind kind) => kind switch
    {
        AgentRequirementKind.Mcp => "MCP server",
        AgentRequirementKind.Env => "env var",
        AgentRequirementKind.Tool => "tool",
        AgentRequirementKind.Permission => "permission",
        _ => kind.ToString().ToLowerInvariant(),
    };

    private static string StatusIcon(AgentRequirementStatus status) => status switch
    {
        AgentRequirementStatus.Ok => Icons.Material.Filled.CheckCircle,
        AgentRequirementStatus.Missing => Icons.Material.Filled.Cancel,
        _ => Icons.Material.Filled.HelpOutline,
    };

    private static MudBlazor.Color StatusChipColor(AgentRequirementStatus status, bool required) => status switch
    {
        AgentRequirementStatus.Ok => MudBlazor.Color.Success,
        // Missing + optional is just a note (warning); missing + required is a hard error.
        AgentRequirementStatus.Missing => required ? MudBlazor.Color.Error : MudBlazor.Color.Warning,
        _ => MudBlazor.Color.Default,
    };

    private static string StatusLabel(AgentRequirementStatus status, bool required) => status switch
    {
        AgentRequirementStatus.Ok => "configured",
        AgentRequirementStatus.Missing => required ? "missing" : "not configured",
        _ => "unknown",
    };

    private string RowStyleFunc(AgentRun run, int index) =>
        run.Id == _currentRun?.Id
            ? "background-color: rgba(217, 119, 87, 0.12);"
            : "";

    private static string FormatDuration(AgentRun run)
    {
        var end = run.FinishedAt ?? DateTimeOffset.UtcNow;
        var duration = end - run.StartedAt;
        return duration.TotalSeconds < 60
            ? $"{duration.TotalSeconds:0.0}s"
            : duration.TotalMinutes < 60
                ? $"{duration.TotalMinutes:0.0}m"
                : $"{duration.TotalHours:0.0}h";
    }

    /// <summary>
    /// Renders a token count compactly so the UI doesn't drown in
    /// digits â€” 1234 â†’ 1.2k, 1234567 â†’ 1.2M. Zero is shown verbatim
    /// (it's the most useful "nothing happened" signal).
    /// </summary>
    private static string FormatArguments(string? arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments)) return "â€”";
        var compact = System.Text.RegularExpressions.Regex.Replace(arguments.Trim(), @"\s+", " ");
        return compact.Length <= 120 ? compact : compact[..117] + "...";
    }

    private static IReadOnlyList<AgentRunOutputLine> VisibleOutputLines(IReadOnlyList<AgentRunOutputLine> output) =>
        output.Count <= OutputPanelLineLimit
            ? output
            : output.Skip(output.Count - OutputPanelLineLimit).ToList();

    private static IReadOnlyList<RunLogViewerEntry> AgentLogEntries(
        IReadOnlyList<AgentRunOutputLine> output,
        int omittedLineCount) =>
        output
            .Select((line, index) =>
            {
                var kind = line.Source == AgentRunOutputSource.Stderr
                    ? "stderr"
                    : IsSystemOutput(line.Text) ? "system" : "stdout";
                return new RunLogViewerEntry(
                    omittedLineCount + index + 1,
                    line.Timestamp,
                    line.Text,
                    kind);
            })
            .ToList();

    private static bool IsSystemOutput(string text) =>
        text.StartsWith("[orchestrator]", StringComparison.OrdinalIgnoreCase)
        || text.StartsWith("[operator hint]", StringComparison.OrdinalIgnoreCase);

    private static string FormatTokens(long count)
    {
        if (count <= 0) return "0";
        if (count < 1_000) return count.ToString();
        if (count < 1_000_000) return $"{count / 1000.0:0.#}k";
        return $"{count / 1_000_000.0:0.##}M";
    }

    /// <summary>
    /// Renders cost in USD with enough precision to show sub-cent
    /// runs (most pre-cache Claude calls land at $0.001â€“$0.05).
    /// Null means the provider didn't report cost (e.g. Codex today).
    /// </summary>
    private static string FormatCost(decimal? usd)
    {
        if (usd is null) return "â€”";
        var v = usd.Value;
        if (v == 0) return "$0";
        if (v < 0.01m) return $"${v:0.0000}";
        if (v < 1m) return $"${v:0.000}";
        return $"${v:0.00}";
    }

    private static bool IsEstimatedCost(AgentRun run) =>
        string.Equals(run.ProviderId, "codex", StringComparison.OrdinalIgnoreCase);
}

