using MudBlazor;

namespace Viamus.Doxie.Orchestrator.Components.Shared;

public sealed record AgentIconChoice(string Key, string Icon, string Label);

public static class AgentIconPalette
{
    public static readonly IReadOnlyList<AgentIconChoice> Choices =
    [
        new("SmartToy", Icons.Material.Filled.SmartToy, "Agent"),
        new("Code", Icons.Material.Filled.Code, "Code"),
        new("Build", Icons.Material.Filled.Build, "Build"),
        new("School", Icons.Material.Filled.School, "Learn"),
        new("RuleFolder", Icons.Material.Filled.RuleFolder, "Inspect"),
        new("Psychology", Icons.Material.Filled.Psychology, "Reason"),
        new("AutoAwesome", Icons.Material.Filled.AutoAwesome, "Creative"),
        new("Hub", Icons.Material.Filled.Hub, "Orchestrate"),
        new("Terminal", Icons.Material.Filled.Terminal, "Terminal"),
        new("MenuBook", Icons.Material.Filled.MenuBook, "Knowledge"),
        new("Cable", Icons.Material.Filled.Cable, "Connect"),
        new("Bookmark", Icons.Material.Filled.Bookmark, "Bookmark"),
        new("Architecture", Icons.Material.Filled.Architecture, "Architecture"),
        new("AccountTree", Icons.Material.Filled.AccountTree, "Workflow"),
        new("Analytics", Icons.Material.Filled.Analytics, "Analytics"),
        new("Api", Icons.Material.Filled.Api, "API"),
        new("BugReport", Icons.Material.Filled.BugReport, "Debug"),
        new("Checklist", Icons.Material.Filled.Checklist, "Checklist"),
        new("CloudQueue", Icons.Material.Filled.CloudQueue, "Cloud"),
        new("DataObject", Icons.Material.Filled.DataObject, "Data"),
        new("DesignServices", Icons.Material.Filled.DesignServices, "Design"),
        new("FactCheck", Icons.Material.Filled.FactCheck, "Review"),
        new("FolderSpecial", Icons.Material.Filled.FolderSpecial, "Library"),
        new("Insights", Icons.Material.Filled.Insights, "Insights"),
        new("IntegrationInstructions", Icons.Material.Filled.IntegrationInstructions, "Integration"),
        new("Lock", Icons.Material.Filled.Lock, "Security"),
        new("Memory", Icons.Material.Filled.Memory, "Memory"),
        new("MonitorHeart", Icons.Material.Filled.MonitorHeart, "Observability"),
        new("RocketLaunch", Icons.Material.Filled.RocketLaunch, "Launch"),
        new("Schema", Icons.Material.Filled.Schema, "Schema"),
        new("Science", Icons.Material.Filled.Science, "Experiment"),
        new("Speed", Icons.Material.Filled.Speed, "Performance"),
        new("Storage", Icons.Material.Filled.Storage, "Storage"),
        new("SyncAlt", Icons.Material.Filled.SyncAlt, "Handoff"),
        new("Tune", Icons.Material.Filled.Tune, "Tune"),
        new("Verified", Icons.Material.Filled.Verified, "Quality"),
        new("Webhook", Icons.Material.Filled.Webhook, "Webhook"),
    ];

    public static IReadOnlySet<string> Keys { get; } =
        Choices.Select(c => c.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);

    public static string? Resolve(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        return Choices.FirstOrDefault(c => string.Equals(c.Key, key.Trim(), StringComparison.OrdinalIgnoreCase))?.Icon;
    }

    public static bool Contains(string? key) =>
        !string.IsNullOrWhiteSpace(key) && Keys.Contains(key.Trim());
}
