using Microsoft.AspNetCore.Http;
using Viamus.Doxie.Orchestrator.Agents;
using Viamus.Doxie.Orchestrator.Agents.Doxie;
using Viamus.Doxie.Orchestrator.Builder;
using Viamus.Doxie.Orchestrator.Common;
using Viamus.Doxie.Orchestrator.Context;
using Viamus.Doxie.Orchestrator.Components.Shared;
using Viamus.Doxie.Orchestrator.Notifications;
using Viamus.Doxie.Orchestrator.Runtime;
using Viamus.Doxie.Orchestrator.Services;
using Viamus.Doxie.Orchestrator.Workflows;

namespace Viamus.Doxie.Orchestrator.Api;

internal static class WorkflowApiDto
{
    public static object SerializeRun(WorkflowRun run) => new
    {
        id = run.Id,
        workflowId = run.WorkflowId,
        triggeredBy = run.TriggeredBy,
        status = run.Status.ToString(),
        startedAt = run.StartedAt,
        finishedAt = run.FinishedAt,
        nodes = run.NodeRuns.Values.Select(nr => new
        {
            nodeId = nr.NodeId,
            status = nr.Status.ToString(),
            startedAt = nr.StartedAt,
            finishedAt = nr.FinishedAt,
            outputSummary = nr.OutputSummary,
            logs = nr.Logs,
        }),
    };
}

internal static class AgentApiPaths
{
    public static string ResolveSkillsRoot(StorageOptions storageOptions, AgentDescriptor agent) =>
        string.IsNullOrWhiteSpace(agent.CatalogRoot)
            ? StorageOptions.ResolvePath(storageOptions.SkillsDirectory)
            : Path.Combine(agent.CatalogRoot, "skills");

    public static string ResolveAgentsRoot(StorageOptions storageOptions, AgentDescriptor agent) =>
        string.IsNullOrWhiteSpace(agent.CatalogRoot)
            ? StorageOptions.ResolvePath(storageOptions.AgentsDirectory)
            : Path.Combine(agent.CatalogRoot, "agents");
}

internal sealed record WorkspaceCreateRequest(string Id, string? Name, string? Description, List<string>? Libraries, List<string>? Agents);
internal sealed record WorkspaceLibrariesRequest(List<string>? Libraries);
internal sealed record WorkspaceAgentsRequest(List<string>? Agents);
internal sealed record ConsoleCreateRequest(string WorkspaceId);
internal sealed record ProviderConsoleCreateRequest(string ProviderId);
internal sealed record WorkflowEnabledRequest(bool Enabled);
internal sealed record RunFileOpenRequest(string RootKind, string Identifier, string? RelativePath);
internal sealed record AgentCategoryUpdate(string Category, string? Icon);
internal sealed record AgentDraftIconUpdate(string? Icon);
internal sealed record SaveAgentBody(bool Overwrite, string? CatalogId);
internal sealed record StartAgentBuilderBody(string? WorkspaceId, string? EditAgentId);
internal sealed record SaveWorkflowBody(bool Overwrite, string? CatalogId);
internal sealed record StartWorkflowBuilderBody(string? WorkspaceId, string? EditWorkflowId);
internal sealed record ApproveBody(string? Comment);
internal sealed record RunWorkflowBody(Dictionary<string, string>? TriggerInputs);
internal sealed record RerunWorkflowFromBody(string NodeId, string? Guidance);
