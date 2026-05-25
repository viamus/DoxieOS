using Viamus.Doxie.Orchestrator.Agents;
using Viamus.Doxie.Orchestrator.Agents.Doxie;
using Viamus.Doxie.Orchestrator.Context;
using Viamus.Doxie.Orchestrator.Workflows;

namespace Viamus.Doxie.Orchestrator.Builder;

/// <summary>
/// Bundles the two-step "allocate sandbox + spawn console" flow used by
/// the Agent / Workflow Builder API endpoints. Pure orchestration — no
/// state of its own, lifecycle entirely tracked by the underlying
/// <see cref="IConsoleSessionStore"/>.
///
/// Builder session id == console session id. The sandbox tag is encoded
/// as the synthetic <see cref="ConsoleSession.WorkspaceId"/>, so any
/// caller holding a console session can recover the sandbox path from
/// <see cref="ConsoleSession.WorkspacePath"/> and the manifest path
/// from <see cref="ConsoleSession.WorkspacePath"/>/.draft/manifest.json.
/// </summary>
public sealed class BuilderSessionFactory
{
    // Skill ids the two builder kinds load on first turn — also the
    // slash-command Claude resolves natively. Centralised here so the
    // Codex bootstrap path inlines the right body.md.
    private const string AgentBuilderSkillId = "agent-builder";
    private const string WorkflowBuilderSkillId = "workflow-builder";

    private readonly BuilderSandboxAllocator _allocator;
    private readonly IConsoleSessionStore _consoleStore;
    private readonly IWorkspaceStore _workspaceStore;
    private readonly IAgentCatalog _agentCatalog;
    private readonly IWorkflowStore _workflowStore;
    private readonly IAgentProviderResolver _providerResolver;
    private readonly DoxieSkillReader _skillReader;

    public BuilderSessionFactory(
        BuilderSandboxAllocator allocator,
        IConsoleSessionStore consoleStore,
        IWorkspaceStore workspaceStore,
        IAgentCatalog agentCatalog,
        IWorkflowStore workflowStore,
        IAgentProviderResolver providerResolver,
        DoxieSkillReader skillReader)
    {
        _allocator = allocator;
        _consoleStore = consoleStore;
        _workspaceStore = workspaceStore;
        _agentCatalog = agentCatalog;
        _workflowStore = workflowStore;
        _providerResolver = providerResolver;
        _skillReader = skillReader;
    }

    /// <summary>
    /// Asks the active provider how to "wake up" a freshly-spawned PTY
    /// in the persona of <paramref name="skillId"/>. Loads the canonical
    /// body if available so providers without slash-command resolution
    /// (Codex) can inline the persona; providers that resolve natively
    /// (Claude Code) ignore the body and just emit "/<id>\r".
    /// </summary>
    private string FormatBootstrap(string skillId)
    {
        var body = _skillReader.TryLoadBody(skillId);
        return _providerResolver.Resolve().FormatBuilderBootstrap(skillId, body);
    }

    /// <summary>
    /// Starts a new Agent Builder session: allocates a fresh sandbox under
    /// <c>./.sandbox/builder-agent-N/</c>, primes the sandbox-local
    /// provider context files (optionally inlining a user workspace's
    /// mounted libraries), and spawns a PTY-backed CLI inside it.
    /// </summary>
    /// <param name="attachedWorkspaceId">
    /// Optional id of a user-owned workspace whose context (path + mounted
    /// library memories) should be attached to the session. Throws if the
    /// id is supplied but doesn't resolve to a known workspace.
    /// </param>
    public async Task<BuilderSessionInfo> StartAgentBuilderAsync(
        string? attachedWorkspaceId = null,
        string? editAgentId = null,
        CancellationToken ct = default)
    {
        Workspace? workspace = null;
        if (!string.IsNullOrWhiteSpace(attachedWorkspaceId))
        {
            workspace = _workspaceStore.GetById(attachedWorkspaceId)
                ?? throw new InvalidOperationException(
                    $"Workspace '{attachedWorkspaceId}' not found — cannot attach to builder session.");

            // Refresh the workspace's provider context files so the
            // sandbox picks up the latest mounted-library set.
            // Idempotent and best-effort — skip silently if it fails.
            try { _workspaceStore.SetMountedLibraries(workspace.Id, workspace.MountedLibraryIds); }
            catch { /* keep going with the existing context files */ }
        }

        AgentDescriptor? editingAgent = null;
        if (!string.IsNullOrWhiteSpace(editAgentId))
        {
            editingAgent = _agentCatalog.FindById(editAgentId)
                ?? throw new InvalidOperationException(
                    $"Agent '{editAgentId}' not found — cannot start an Edit session for it.");
        }

        var sandbox = _allocator.Allocate("builder-agent", workspace, editingAgent);
        var labelSuffix = workspace is null ? string.Empty : $" · {workspace.Name}";
        var editSuffix = editingAgent is null ? string.Empty : $" · âœŽ {editingAgent.Id}";
        var label = $"Agent Builder · {sandbox.Tag}{labelSuffix}{editSuffix}";
        var session = await _consoleStore.CreateForBuilderAsync(
            syntheticWorkspaceId: sandbox.Tag,
            cwd: sandbox.Path,
            label: label,
            initialInput: FormatBootstrap(AgentBuilderSkillId),
            ct: ct);

        return new BuilderSessionInfo(
            SessionId: session.Id,
            Kind: BuilderKind.Agent,
            SandboxTag: sandbox.Tag,
            SandboxPath: sandbox.Path,
            ManifestPath: sandbox.ManifestPath,
            Label: label,
            AttachedWorkspaceId: workspace?.Id,
            EditAgentId: editingAgent?.Id,
            EditWorkflowId: null,
            StartedAt: session.StartedAt);
    }

    /// <summary>
    /// Workflow Builder counterpart of <see cref="StartAgentBuilderAsync"/>.
    /// Same lifecycle, different schema. The workflow-builder skill that
    /// runs inside can also write <c>./.draft/new-agents/&lt;id&gt;.json</c>
    /// files for net-new agents the workflow needs — the Save endpoint
    /// promotes those before the workflow itself.
    /// </summary>
    public async Task<BuilderSessionInfo> StartWorkflowBuilderAsync(
        string? attachedWorkspaceId = null,
        string? editWorkflowId = null,
        CancellationToken ct = default)
    {
        Workspace? workspace = null;
        if (!string.IsNullOrWhiteSpace(attachedWorkspaceId))
        {
            workspace = _workspaceStore.GetById(attachedWorkspaceId)
                ?? throw new InvalidOperationException(
                    $"Workspace '{attachedWorkspaceId}' not found — cannot attach to workflow builder session.");
            try { _workspaceStore.SetMountedLibraries(workspace.Id, workspace.MountedLibraryIds); }
            catch { /* keep going with the existing context files */ }
        }

        WorkflowDefinition? editingWorkflow = null;
        if (!string.IsNullOrWhiteSpace(editWorkflowId))
        {
            editingWorkflow = _workflowStore.GetById(editWorkflowId)
                ?? throw new InvalidOperationException(
                    $"Workflow '{editWorkflowId}' not found — cannot start an Edit session for it.");
        }

        var sandbox = _allocator.Allocate(
            tag: "builder-workflow",
            attachedWorkspace: workspace,
            editingAgent: null,
            editingWorkflow: editingWorkflow);

        var labelSuffix = workspace is null ? string.Empty : $" · {workspace.Name}";
        var editSuffix = editingWorkflow is null ? string.Empty : $" · âœŽ {editingWorkflow.Id}";
        var label = $"Workflow Builder · {sandbox.Tag}{labelSuffix}{editSuffix}";
        var session = await _consoleStore.CreateForBuilderAsync(
            syntheticWorkspaceId: sandbox.Tag,
            cwd: sandbox.Path,
            label: label,
            initialInput: FormatBootstrap(WorkflowBuilderSkillId),
            ct: ct);

        return new BuilderSessionInfo(
            SessionId: session.Id,
            Kind: BuilderKind.Workflow,
            SandboxTag: sandbox.Tag,
            SandboxPath: sandbox.Path,
            ManifestPath: sandbox.ManifestPath,
            Label: label,
            AttachedWorkspaceId: workspace?.Id,
            EditAgentId: null,
            EditWorkflowId: editingWorkflow?.Id,
            StartedAt: session.StartedAt);
    }

    /// <summary>
    /// Reverses the lookup — given a console session id, returns the
    /// builder info derived from it. Returns null if the session doesn't
    /// exist or isn't a builder session. Reads the on-disk
    /// <c>.draft/builder-meta.json</c> so a Resume picks up Edit mode
    /// correctly even after a page reload.
    /// </summary>
    public BuilderSessionInfo? Find(string sessionId)
    {
        var session = _consoleStore.Get(sessionId);
        if (session is null || session.Kind != ConsoleSessionKind.Builder) return null;

        var kind = session.WorkspaceId.StartsWith("builder-agent-", StringComparison.Ordinal)
            ? BuilderKind.Agent
            : session.WorkspaceId.StartsWith("builder-workflow-", StringComparison.Ordinal)
                ? BuilderKind.Workflow
                : (BuilderKind?)null;

        if (kind is null) return null;

        return MakeInfo(session, kind.Value);
    }

    /// <summary>
    /// Lists every live builder session of a given kind, most-recent first.
    /// Used by the builder pages to populate the "resume" sidebar.
    /// </summary>
    public IReadOnlyList<BuilderSessionInfo> List(BuilderKind kind)
    {
        var prefix = kind switch
        {
            BuilderKind.Agent => "builder-agent-",
            BuilderKind.Workflow => "builder-workflow-",
            _ => string.Empty,
        };

        return _consoleStore.ListAll()
            .Where(s => s.Kind == ConsoleSessionKind.Builder && s.WorkspaceId.StartsWith(prefix, StringComparison.Ordinal))
            .Select(s => MakeInfo(s, kind))
            .ToList();
    }

    private BuilderSessionInfo MakeInfo(ConsoleSession session, BuilderKind kind)
    {
        var sandboxPath = session.WorkspacePath;
        var meta = ReadMeta(sandboxPath);

        return new BuilderSessionInfo(
            SessionId: session.Id,
            Kind: kind,
            SandboxTag: session.WorkspaceId,
            SandboxPath: sandboxPath,
            ManifestPath: Path.Combine(sandboxPath, ".draft", "manifest.json"),
            Label: session.Label,
            AttachedWorkspaceId: ParseAttachedWorkspaceFromLabel(session.Label),
            EditAgentId: meta?.EditAgentId,
            EditWorkflowId: meta?.EditWorkflowId,
            StartedAt: session.StartedAt);
    }

    /// <summary>
    /// Reads <c>./.draft/builder-meta.json</c>. Returns null on any error
    /// (file missing, unreadable, malformed) — Edit mode is just absent
    /// then, which is exactly the "create-new" default. Keeps the Find /
    /// List paths fault-tolerant so a corrupted marker file doesn't take
    /// the whole sidebar down.
    /// </summary>
    private static BuilderSandboxAllocator.BuilderMeta? ReadMeta(string sandboxPath)
    {
        var path = Path.Combine(sandboxPath, ".draft", "builder-meta.json");
        if (!File.Exists(path)) return null;
        try
        {
            using var fs = File.OpenRead(path);
            return System.Text.Json.JsonSerializer.Deserialize<BuilderSandboxAllocator.BuilderMeta>(fs,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch { return null; }
    }

    /// <summary>
    /// Recovers the attached-workspace name from the session label
    /// (format <c>"Agent Builder · &lt;tag&gt; · &lt;workspace-name&gt;[ · âœŽ &lt;edit-agent-id&gt;]"</c>),
    /// then looks it up by display name. Returns the workspace id if a
    /// match is found, null otherwise.
    /// </summary>
    private string? ParseAttachedWorkspaceFromLabel(string label)
    {
        var parts = label.Split(" · ");
        // Label shape: "Agent Builder", "<tag>", optional "<workspace-name>", optional "âœŽ <edit-id>".
        // The third token is the workspace name when present and not the edit marker.
        if (parts.Length < 3) return null;
        var third = parts[2].Trim();
        if (third.StartsWith("âœŽ ", StringComparison.Ordinal)) return null;
        return _workspaceStore.ListAll()
            .FirstOrDefault(w => string.Equals(w.Name, third, StringComparison.OrdinalIgnoreCase))
            ?.Id;
    }
}

public enum BuilderKind
{
    Agent,
    Workflow,
}

public sealed record BuilderSessionInfo(
    string SessionId,
    BuilderKind Kind,
    string SandboxTag,
    string SandboxPath,
    string ManifestPath,
    string Label,
    string? AttachedWorkspaceId,
    string? EditAgentId,
    string? EditWorkflowId,
    DateTimeOffset StartedAt);
