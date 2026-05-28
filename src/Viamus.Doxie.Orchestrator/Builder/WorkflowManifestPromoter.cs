using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Viamus.Doxie.Orchestrator.Agents;
using Viamus.Doxie.Orchestrator.Workflows;

namespace Viamus.Doxie.Orchestrator.Builder;

/// <summary>
/// Promotes a workflow-builder draft into the canonical stores. Two
/// stages:
///
/// 1. **Cross-creation** — every <c>./.draft/new-agents/*.json</c> file
///    is treated as an agent manifest and promoted via
///    <see cref="AgentManifestPromoter"/>. This runs first so the
///    workflow's node references resolve cleanly against a freshly
///    populated catalog.
///
/// 2. **Workflow promotion** — the manifest is converted to a
///    <see cref="WorkflowDefinition"/> and saved through
///    <see cref="IWorkflowStore"/>.
///
/// No transactional rollback. If step 1 promotes 2 of 3 agents and the
/// 3rd fails, the 2 stay promoted and the report tells the caller
/// what landed and what didn't. Single-user dev tool — full ACID is
/// more cost than benefit here.
/// </summary>
public sealed class WorkflowManifestPromoter
{
    private static readonly Regex KebabCase = new("^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.Compiled);

    /// <summary>
    /// Lenient parser used by the live-preview pipeline. Tolerates
    /// partial drafts and unknown properties.
    /// </summary>
    public static readonly JsonSerializerOptions ParseOptionsLenient = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>
    /// Strict parser used at Save time. Refuses unknown properties so
    /// typos surface as deserialization errors instead of being silently
    /// dropped.
    /// </summary>
    public static readonly JsonSerializerOptions ParseOptionsStrict = new()
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private readonly IWorkflowStore _workflowStore;
    private readonly AgentManifestPromoter _agentPromoter;
    private readonly IAgentCatalog _agentCatalog;

    public WorkflowManifestPromoter(
        IWorkflowStore workflowStore,
        AgentManifestPromoter agentPromoter,
        IAgentCatalog agentCatalog)
    {
        _workflowStore = workflowStore;
        _agentPromoter = agentPromoter;
        _agentCatalog = agentCatalog;
    }

    /// <summary>
    /// Reads the workflow manifest at <paramref name="manifestPath"/>,
    /// optionally promotes any sibling new-agent manifests under
    /// <c>../new-agents/</c>, then promotes the workflow itself.
    /// Returns a per-stage report.
    /// </summary>
    public WorkflowPromotionResult Promote(string manifestPath, bool overwrite = false, string? catalogId = null)
    {
        var draftDir = Path.GetDirectoryName(manifestPath)!;
        var newAgentsDir = Path.Combine(draftDir, "new-agents");

        // Stage 1: cross-creation. Promotes each <id>.json under
        // .draft/new-agents/ via the existing agent promoter. Failures
        // accumulate but don't short-circuit the workflow itself —
        // the workflow may still be useful even if one agent didn't land
        // (the user gets a clear report).
        var agentResults = new List<AgentSubResult>();
        if (Directory.Exists(newAgentsDir))
        {
            foreach (var file in Directory.EnumerateFiles(newAgentsDir, "*.json"))
            {
                var fileName = Path.GetFileName(file);
                AgentManifest? agentManifest;
                try
                {
                    using var fs = File.OpenRead(file);
                    agentManifest = JsonSerializer.Deserialize<AgentManifest>(fs, AgentManifestPromoter.ParseOptionsStrict);
                }
                catch (JsonException ex)
                {
                    agentResults.Add(new AgentSubResult(fileName, null, false,
                        $"invalid JSON or unknown field: {ex.Message}"));
                    continue;
                }
                if (agentManifest is null)
                {
                    agentResults.Add(new AgentSubResult(fileName, null, false, "manifest is empty"));
                    continue;
                }
                // Cross-creation overwrites by default — the workflow
                // builder is treating these as part of a single bundle.
                var sub = _agentPromoter.Promote(agentManifest, overwrite: true, catalogId: catalogId);
                agentResults.Add(new AgentSubResult(
                    fileName,
                    sub.AgentId ?? agentManifest.Id,
                    sub.Ok,
                    sub.Errors.Count == 0 ? sub.Error : string.Join("; ", sub.Errors)));
            }
        }

        // Stage 2: the workflow itself.
        WorkflowManifest? wfManifest;
        try
        {
            using var fs = File.OpenRead(manifestPath);
            wfManifest = JsonSerializer.Deserialize<WorkflowManifest>(fs, ParseOptionsStrict);
        }
        catch (JsonException ex)
        {
            var msg = $"invalid JSON or unknown field: {ex.Message}";
            return new WorkflowPromotionResult(false, null, msg, new[] { msg }, agentResults);
        }
        if (wfManifest is null)
        {
            return new WorkflowPromotionResult(false, null, "workflow manifest is empty",
                new[] { "workflow manifest is empty" }, agentResults);
        }

        var errors = Validate(wfManifest);
        if (errors.Count > 0)
        {
            return new WorkflowPromotionResult(false, null, JoinErrors(errors), errors, agentResults);
        }

        if (_workflowStore.GetById(wfManifest.Id!) is not null && !overwrite)
        {
            var collision = $"Workflow '{wfManifest.Id}' already exists. Pass overwrite=true to replace it.";
            return new WorkflowPromotionResult(false, null, collision, new[] { collision }, agentResults);
        }

        WorkflowDefinition definition;
        try
        {
            definition = ToDefinition(wfManifest, catalogId);
        }
        catch (InvalidOperationException ex)
        {
            return new WorkflowPromotionResult(false, null, ex.Message, new[] { ex.Message }, agentResults);
        }

        try
        {
            _workflowStore.Save(definition);
        }
        catch (InvalidOperationException ex)
        {
            return new WorkflowPromotionResult(false, null, ex.Message, new[] { ex.Message }, agentResults);
        }

        return new WorkflowPromotionResult(true, definition.Id, null, Array.Empty<string>(), agentResults);
    }

    /// <summary>
    /// Runs the full workflow validation suite. Public so the
    /// live-preview API endpoint can surface the same checks the Save
    /// endpoint will run. Cross-references the agent catalog, so
    /// errors include "agent X is not in the catalog" and "mode Y is
    /// not a known mode of agent X". Returns an empty list on success.
    /// </summary>
    public IReadOnlyList<string> Validate(WorkflowManifest? m)
    {
        var errors = new List<string>();

        if (m is null)
        {
            errors.Add("manifest is empty");
            return errors;
        }

        // Identity
        if (string.IsNullOrWhiteSpace(m.Id)) errors.Add("id is required");
        else if (!KebabCase.IsMatch(m.Id!)) errors.Add($"id '{m.Id}' must be kebab-case");

        if (string.IsNullOrWhiteSpace(m.Name)) errors.Add("name is required");
        if (!string.IsNullOrWhiteSpace(m.Category)
            && !AgentCategoryPalette.CustomLabelPattern.IsMatch(m.Category.Trim()))
        {
            errors.Add($"category '{m.Category}' is invalid - use 1-32 letters, digits, spaces, or hyphens, starting with a letter");
        }

        // Trigger
        if (m.Trigger is null)
        {
            errors.Add("trigger is required");
        }
        else
        {
            if (string.IsNullOrWhiteSpace(m.Trigger.Kind))
            {
                errors.Add("trigger.kind is required");
            }
            else if (!Enum.TryParse<WorkflowTriggerKind>(m.Trigger.Kind, ignoreCase: true, out var triggerKind))
            {
                errors.Add($"trigger.kind '{m.Trigger.Kind}' must be one of: Manual, Cron, Event, Webhook");
            }
            else if (triggerKind == WorkflowTriggerKind.Cron && !CronExpression.IsValid(m.Trigger.CronExpression))
            {
                errors.Add($"invalid cron expression '{m.Trigger.CronExpression}' (expected 5 fields)");
            }
            else if (triggerKind == WorkflowTriggerKind.Event && string.IsNullOrWhiteSpace(m.Trigger.EventName))
            {
                errors.Add("trigger.eventName is required for Event triggers");
            }
            else if (triggerKind == WorkflowTriggerKind.Webhook && string.IsNullOrWhiteSpace(m.Trigger.WebhookPath))
            {
                errors.Add("trigger.webhookPath is required for Webhook triggers");
            }
        }

        // Nodes
        var nodeIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (m.Nodes is null || m.Nodes.Count == 0)
        {
            errors.Add("at least one node is required");
        }
        else
        {
            for (var i = 0; i < m.Nodes.Count; i++)
            {
                var n = m.Nodes[i];
                var label = string.IsNullOrWhiteSpace(n.Id) ? $"nodes[{i}]" : $"node '{n.Id}'";

                if (string.IsNullOrWhiteSpace(n.Id))
                {
                    errors.Add($"{label}: id is required");
                    continue;
                }
                if (string.Equals(n.Id, "trigger", StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add($"{label}: id 'trigger' is reserved — the runner adds it implicitly");
                    continue;
                }
                if (!nodeIds.Add(n.Id!))
                {
                    errors.Add($"{label}: duplicate node id");
                    continue;
                }

                WorkflowNodeKind? parsedKind = null;
                if (string.IsNullOrWhiteSpace(n.Kind))
                {
                    errors.Add($"{label}: kind is required");
                }
                else if (Enum.TryParse<WorkflowNodeKind>(n.Kind, ignoreCase: true, out var k))
                {
                    parsedKind = k;
                }
                else
                {
                    var knownKinds = string.Join(", ", Enum.GetNames<WorkflowNodeKind>().Where(k => k != nameof(WorkflowNodeKind.Trigger)));
                    errors.Add($"{label}: kind '{n.Kind}' must be one of: {knownKinds}");
                }

                // Per-kind required-field checks
                if (parsedKind == WorkflowNodeKind.Agent)
                {
                    if (string.IsNullOrWhiteSpace(n.AgentId))
                    {
                        errors.Add($"{label}: agentId is required for Agent nodes");
                    }
                    else
                    {
                        var agent = _agentCatalog.FindById(n.AgentId!);
                        if (agent is null)
                        {
                            errors.Add($"{label}: agentId '{n.AgentId}' is not in the catalog");
                        }
                        else if (!string.IsNullOrWhiteSpace(n.AgentMode))
                        {
                            var hasMode = agent.Modes?.Any(mm => string.Equals(mm.Id, n.AgentMode, StringComparison.OrdinalIgnoreCase)) ?? false;
                            if (!hasMode)
                            {
                                var knownModes = agent.Modes is { Count: > 0 }
                                    ? string.Join(", ", agent.Modes.Select(mm => mm.Id))
                                    : "(none defined)";
                                errors.Add($"{label}: agentMode '{n.AgentMode}' is not a mode of agent '{n.AgentId}' (known: {knownModes})");
                            }
                        }
                    }
                }
                else if (parsedKind == WorkflowNodeKind.Output)
                {
                    if (string.IsNullOrWhiteSpace(n.OutputWorkspaceId))
                        errors.Add($"{label}: outputWorkspaceId is required for Output nodes");
                    if (string.IsNullOrWhiteSpace(n.OutputFileName))
                        errors.Add($"{label}: outputFileName is required for Output nodes");
                }
            }
        }

        // Edges
        if (m.Edges is { Count: > 0 } edges)
        {
            for (var i = 0; i < edges.Count; i++)
            {
                var e = edges[i];
                var label = $"edges[{i}]";
                if (string.IsNullOrWhiteSpace(e.FromNodeId) || string.IsNullOrWhiteSpace(e.ToNodeId))
                {
                    errors.Add($"{label}: must have both fromNodeId and toNodeId");
                    continue;
                }
                // 'trigger' references survive — they refer to the
                // implicit trigger node prepended by the runner.
                var isTriggerFrom = string.Equals(e.FromNodeId, "trigger", StringComparison.OrdinalIgnoreCase);
                if (!isTriggerFrom && !nodeIds.Contains(e.FromNodeId!))
                    errors.Add($"{label}: fromNodeId '{e.FromNodeId}' refers to an unknown node");
                if (!nodeIds.Contains(e.ToNodeId!))
                    errors.Add($"{label}: toNodeId '{e.ToNodeId}' refers to an unknown node");
            }
        }

        return errors;
    }

    private static string JoinErrors(IReadOnlyList<string> errors) =>
        errors.Count == 1 ? errors[0] : string.Join("; ", errors);

    private static WorkflowDefinition ToDefinition(WorkflowManifest m, string? catalogId)
    {
        var triggerKind = Enum.Parse<WorkflowTriggerKind>(m.Trigger!.Kind!, ignoreCase: true);
        var trigger = triggerKind switch
        {
            WorkflowTriggerKind.Cron => new WorkflowTrigger(WorkflowTriggerKind.Cron, CronExpression: m.Trigger.CronExpression),
            WorkflowTriggerKind.Event => new WorkflowTrigger(WorkflowTriggerKind.Event, EventName: m.Trigger.EventName),
            WorkflowTriggerKind.Webhook => new WorkflowTrigger(WorkflowTriggerKind.Webhook, WebhookPath: m.Trigger.WebhookPath),
            _ => new WorkflowTrigger(WorkflowTriggerKind.Manual, Inputs: m.Trigger.Inputs),
        };

        var nodes = m.Nodes!.Select(n => new WorkflowNode(
            Id: n.Id!,
            Kind: Enum.Parse<WorkflowNodeKind>(n.Kind!, ignoreCase: true),
            Label: n.Label ?? n.Id!,
            X: 0, Y: 0,
            AgentId: string.IsNullOrEmpty(n.AgentId) ? null : n.AgentId,
            AgentMode: string.IsNullOrEmpty(n.AgentMode) ? null : n.AgentMode,
            Inputs: n.Inputs is { Count: > 0 } ? new Dictionary<string, string>(n.Inputs, StringComparer.OrdinalIgnoreCase) : null,
            OutputWorkspaceId: string.IsNullOrEmpty(n.OutputWorkspaceId) ? null : n.OutputWorkspaceId,
            OutputFileName: string.IsNullOrEmpty(n.OutputFileName) ? null : n.OutputFileName,
            WorkspaceId: string.IsNullOrEmpty(n.WorkspaceId) ? null : n.WorkspaceId,
            LoopId: string.IsNullOrEmpty(n.LoopId) ? null : n.LoopId)).ToList();

        var edges = (m.Edges ?? new List<WorkflowManifestEdge>())
            .Select(e => new WorkflowEdge(
                e.FromNodeId!,
                e.ToNodeId!,
                string.IsNullOrWhiteSpace(e.Condition) ? null : e.Condition.Trim()))
            .ToList();

        // Auto-position so newly authored workflows render cleanly on
        // first open even though the manifest carries no x/y. The store
        // itself doesn't do layout — that lives in the API edge today.
        var positioned = WorkflowLayout.AutoPosition(nodes, edges);

        var ts = DateTime.UtcNow;
        return new WorkflowDefinition(
            Id: m.Id!,
            Name: m.Name!,
            Description: m.Description ?? string.Empty,
            Trigger: trigger,
            Nodes: positioned,
            Edges: edges,
            CreatedAt: ts,
            UpdatedAt: ts,
            WorkspaceId: string.IsNullOrEmpty(m.WorkspaceId) ? null : m.WorkspaceId,
            Env: m.Env is { Count: > 0 } ? new Dictionary<string, string>(m.Env, StringComparer.OrdinalIgnoreCase) : null,
            Enabled: m.Enabled,
            IsPrivate: false,
            CatalogId: string.IsNullOrWhiteSpace(catalogId) ? "default" : catalogId.Trim(),
            Category: NormaliseWorkflowCategory(m.Category)).WithCreateTimeEnabledPolicy();
    }

    private static string NormaliseWorkflowCategory(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "Other";
        var trimmed = raw.Trim();
        return AgentCategoryPalette.CustomLabelPattern.IsMatch(trimmed) ? trimmed : "Other";
    }
}

public sealed record WorkflowPromotionResult(
    bool Ok,
    string? WorkflowId,
    string? Error,
    IReadOnlyList<string> Errors,
    IReadOnlyList<AgentSubResult> AgentSubResults);

public sealed record AgentSubResult(
    string FileName,
    string? AgentId,
    bool Ok,
    string? Error);
