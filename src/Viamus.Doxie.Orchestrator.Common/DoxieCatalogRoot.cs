namespace Viamus.Doxie.Orchestrator.Common;

public sealed record DoxieCatalogRoot(
    string Id,
    string Name,
    string RootPath,
    bool IsDefault = false,
    string? AgentsPath = null,
    string? SkillsPath = null,
    string? WorkflowsPath = null,
    string? LibrariesPath = null)
{
    public string AgentsDirectory => AgentsPath ?? Path.Combine(RootPath, "agents");
    public string SkillsDirectory => SkillsPath ?? Path.Combine(RootPath, "skills");
    public string WorkflowsDirectory => WorkflowsPath ?? Path.Combine(RootPath, "workflows");
    public string LibrariesDirectory => LibrariesPath ?? Path.Combine(RootPath, "libraries");
}
