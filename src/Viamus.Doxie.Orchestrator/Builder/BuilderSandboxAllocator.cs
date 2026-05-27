using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Viamus.Doxie.Orchestrator.Agents;
using Viamus.Doxie.Orchestrator.Agents.Doxie;
using Viamus.Doxie.Orchestrator.Context;
using Viamus.Doxie.Orchestrator.Workflows;

namespace Viamus.Doxie.Orchestrator.Builder;

/// <summary>
/// Allocates a fresh per-session sandbox folder under
/// <c>./.sandbox/&lt;tag&gt;-&lt;n&gt;/</c> for an Agent / Workflow Builder
/// session. Mirrors the <c>max+1</c> rule the <c>/sandbox</c> skill uses
/// for tagged sandboxes — never reuses an ordinal, even if older
/// sandboxes get cleaned up. The session keeps its allocated path for
/// its lifetime.
/// </summary>
public sealed class BuilderSandboxAllocator
{
    private static readonly object _allocLock = new();

    private readonly StorageOptions _storage;
    private readonly DoxieSkillReader _skillReader;

    public BuilderSandboxAllocator(StorageOptions storage, DoxieSkillReader skillReader)
    {
        _storage = storage;
        _skillReader = skillReader;
    }

    /// <summary>
    /// Allocates a new sandbox folder for a builder session and returns
    /// its absolute path. The folder is created on disk along with a
    /// <c>SANDBOX.md</c> stub plus provider-local context files:
    /// <c>CLAUDE.md</c> for Claude Code and <c>AGENTS.md</c> for Codex.
    /// </summary>
    /// <param name="tag">
    /// Slug prefix for the sandbox folder, e.g. <c>"builder-agent"</c>
    /// or <c>"builder-workflow"</c>. Must match
    /// <c>^[a-z0-9]+(-[a-z0-9]+)*$</c>.
    /// </param>
    /// <param name="attachedWorkspace">
    /// Optional user-owned Workspace whose context (path + mounted
    /// libraries via its auto-generated provider context files) should
    /// be inlined into the sandbox. When set, the agent can read files
    /// from that workspace and sees its library memories. When null,
    /// the sandbox is fully isolated.
    /// </param>
    public BuilderSandboxAllocation Allocate(
        string tag,
        Workspace? attachedWorkspace = null,
        AgentDescriptor? editingAgent = null,
        WorkflowDefinition? editingWorkflow = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tag);
        if (!Regex.IsMatch(tag, "^[a-z0-9]+(-[a-z0-9]+)*$"))
        {
            throw new ArgumentException($"Invalid tag '{tag}' (must be kebab-case).", nameof(tag));
        }

        var sandboxRoot = StorageOptions.ResolvePath(_storage.SandboxesDirectory);
        Directory.CreateDirectory(sandboxRoot);

        // Walking the directory + creating the folder must be atomic to
        // avoid two parallel allocations picking the same ordinal. The
        // contention here is low (a user can only click "New" so fast)
        // so a process-wide lock is fine.
        lock (_allocLock)
        {
            var ordinal = NextOrdinal(sandboxRoot, tag);
            var folder = Path.Combine(sandboxRoot, $"{tag}-{ordinal}");
            Directory.CreateDirectory(folder);
            var draftDir = Path.Combine(folder, ".draft");
            Directory.CreateDirectory(draftDir);

            var primer = BuildSandboxClaudeMd(tag, ordinal, attachedWorkspace, editingAgent, editingWorkflow);
            File.WriteAllText(Path.Combine(folder, "CLAUDE.md"), primer);
            // Codex reads AGENTS.md from the working directory on TUI
            // start (analogue of Claude's CLAUDE.md). The primer alone
            // is not enough — Codex doesn't have slash-command resolution,
            // so we ALSO inline the skill body that maps to this builder
            // tag (builder-agent â†’ agent-builder, builder-workflow â†’
            // workflow-builder). Claude ignores AGENTS.md so the duplication
            // is harmless on that side. Single-source means we don't need
            // a provider-aware switch in the allocator — both CLIs read
            // their own file.
            var bootstrapSkillId = SkillIdFor(tag);
            // Pre-create the memories folder for the builder skill so it
            // can persist learnings (`<projectRoot>/.doxie/skills/<id>/memories/`)
            // without needing Bash/mkdir. Best-effort — read side tolerates
            // a missing folder, so this is convenience only.
            if (bootstrapSkillId is not null)
            {
                var projectRoot = StorageOptions.ResolvePath(".");
                try
                {
                    Directory.CreateDirectory(Path.Combine(
                        projectRoot, ".doxie", "skills", bootstrapSkillId, "memories"));
                }
                catch (IOException) { /* best-effort */ }
                catch (UnauthorizedAccessException) { /* best-effort */ }
            }
            var skillBody = bootstrapSkillId is null ? null : _skillReader.TryLoadBody(bootstrapSkillId);
            var attachedWorkspaceAgents = ReadAttachedWorkspaceAgentsMd(attachedWorkspace);
            var agentsMd = string.IsNullOrWhiteSpace(skillBody)
                ? primer
                : $"{primer}\n\n---\n\n# Skill body: `{bootstrapSkillId}`\n\nThe following is the canonical body of the skill driving this builder session. Treat it as your system context — follow it turn-by-turn.\n\n{skillBody.TrimEnd()}\n";
            if (!string.IsNullOrWhiteSpace(attachedWorkspaceAgents))
            {
                agentsMd = $"{agentsMd.TrimEnd()}\n\n---\n\n# Attached workspace context for Codex\n\n{attachedWorkspaceAgents.TrimEnd()}\n";
            }
            File.WriteAllText(Path.Combine(folder, "AGENTS.md"), agentsMd);

            File.WriteAllText(
                Path.Combine(folder, "SANDBOX.md"),
                $"""
                # {tag}-{ordinal}

                Builder sandbox for a DoxieOS authoring session. The orchestrator
                allocated this folder so Claude could draft a manifest interactively.

                The draft lives at `./.draft/manifest.json`. When the user clicks
                **Save** in DoxieOS, the orchestrator promotes that manifest into
                the canonical catalog and the session ends.
                """);

            // Edit mode (agent): pre-seed the manifest with what we
            // already know from the AgentDescriptor and write a meta
            // marker so Resume recognises the session even after the
            // page reloads.
            if (editingAgent is not null)
            {
                File.WriteAllText(
                    Path.Combine(draftDir, "builder-meta.json"),
                    JsonSerializer.Serialize(
                        new BuilderMeta { Kind = "agent", EditAgentId = editingAgent.Id },
                        new JsonSerializerOptions { WriteIndented = true }));

                var stub = BuildEditStubManifest(editingAgent);
                File.WriteAllText(
                    Path.Combine(draftDir, "manifest.json"),
                    JsonSerializer.Serialize(stub, new JsonSerializerOptions
                    {
                        WriteIndented = true,
                        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
                    }));
            }
            // Edit mode (workflow): same pattern. The workflow definition
            // is fully structured (no SKILL.md to enrich from), so the
            // round-trip is faithful from the very first render.
            else if (editingWorkflow is not null)
            {
                File.WriteAllText(
                    Path.Combine(draftDir, "builder-meta.json"),
                    JsonSerializer.Serialize(
                        new BuilderMeta { Kind = "workflow", EditWorkflowId = editingWorkflow.Id },
                        new JsonSerializerOptions { WriteIndented = true }));

                Directory.CreateDirectory(Path.Combine(draftDir, "new-agents"));

                var stub = BuildEditStubWorkflowManifest(editingWorkflow);
                File.WriteAllText(
                    Path.Combine(draftDir, "manifest.json"),
                    JsonSerializer.Serialize(stub, new JsonSerializerOptions
                    {
                        WriteIndented = true,
                        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
                    }));
            }
            // Workflow create: pre-create the new-agents directory so the
            // skill doesn't have to mkdir it on first cross-creation.
            else if (string.Equals(tag, "builder-workflow", StringComparison.Ordinal))
            {
                Directory.CreateDirectory(Path.Combine(draftDir, "new-agents"));
            }

            return new BuilderSandboxAllocation(
                Tag: $"{tag}-{ordinal}",
                Path: folder,
                ManifestPath: Path.Combine(folder, ".draft", "manifest.json"),
                AttachedWorkspaceId: attachedWorkspace?.Id,
                EditAgentId: editingAgent?.Id,
                EditWorkflowId: editingWorkflow?.Id);
        }
    }

    /// <summary>
    /// Builds the workflow Edit-mode stub manifest from a saved
    /// <see cref="WorkflowDefinition"/>. The synthetic Trigger node is
    /// stripped (the runner re-prepends it on every save) and edges
    /// originating at "trigger" become edges from no upstream so the
    /// round-trip is idempotent.
    /// </summary>
    private static WorkflowManifest BuildEditStubWorkflowManifest(WorkflowDefinition w) => new()
    {
        Id = w.Id,
        Name = w.Name,
        Description = w.Description,
        WorkspaceId = w.WorkspaceId,
        Enabled = w.Enabled,
        Trigger = new WorkflowManifestTrigger
        {
            Kind = w.Trigger.Kind.ToString(),
            CronExpression = w.Trigger.CronExpression,
            EventName = w.Trigger.EventName,
            WebhookPath = w.Trigger.WebhookPath,
            Inputs = w.Trigger.Inputs is null ? null : w.Trigger.Inputs.ToList(),
        },
        Env = w.Env is null ? null : new Dictionary<string, string>(w.Env, StringComparer.OrdinalIgnoreCase),
        Nodes = w.Nodes
            .Where(n => n.Kind != WorkflowNodeKind.Trigger)
            .Select(n => new WorkflowManifestNode
            {
                Id = n.Id,
                Kind = n.Kind.ToString(),
                Label = n.Label,
                AgentId = n.AgentId,
                AgentMode = n.AgentMode,
                Inputs = n.Inputs is null ? null : new Dictionary<string, string>(n.Inputs, StringComparer.OrdinalIgnoreCase),
                WorkspaceId = n.WorkspaceId,
                LoopId = n.LoopId,
                OutputWorkspaceId = n.OutputWorkspaceId,
                OutputFileName = n.OutputFileName,
            })
            .ToList(),
        Edges = w.Edges
            .Where(e => !string.Equals(e.FromNodeId, "trigger", StringComparison.OrdinalIgnoreCase))
            .Select(e => new WorkflowManifestEdge { FromNodeId = e.FromNodeId, ToNodeId = e.ToNodeId, Condition = e.Condition })
            .ToList(),
    };

    /// <summary>
    /// Builds the initial manifest stub for an Edit session — fields we
    /// can derive from the live <see cref="AgentDescriptor"/>. Tools and
    /// system prompt aren't part of the descriptor (they live in the
    /// SKILL.md body), so they're left null for Claude to fill in by
    /// reading the original SKILL.md before iterating with the user.
    /// </summary>
    private static AgentManifest BuildEditStubManifest(AgentDescriptor d) => new()
    {
        Id = d.Id,
        Name = d.Name,
        Description = d.Description,
        Category = d.DisplayCategory,
        Icon = d.Icon,
        SkillName = d.SkillName,
        SystemPrompt = null,
        Tools = null,
        Modes = d.Modes?.Select(m => new AgentManifestMode
        {
            Id = m.Id,
            Name = m.Name,
            Description = m.Description,
            RequiresWorkspace = m.RequiresWorkspace,
        }).ToList(),
        Requirements = d.Requirements?.Select(r => new AgentManifestRequirement
        {
            Kind = r.Kind.ToString(),
            Name = r.Name,
            Required = r.Required,
            Purpose = r.Purpose,
        }).ToList(),
    };

    /// <summary>
    /// Builds the sandbox-local CLAUDE.md. Claude Code auto-loads it as
    /// part of the initial system prompt; if a workspace is attached,
    /// the workspace's own CLAUDE.md is pulled in via @-import so the
    /// libraries it mounts ship with this session too. If an existing
    /// agent is being edited, an "edit mode" section is appended that
    /// points Claude at the original SKILL.md so it can enrich the
    /// pre-seeded manifest with the bits the descriptor doesn't carry
    /// (tools, system prompt).
    /// </summary>
    private static string BuildSandboxClaudeMd(string tag, int ordinal, Workspace? ws, AgentDescriptor? editingAgent, WorkflowDefinition? editingWorkflow)
    {
        var skillSlash = SkillSlashFor(tag);
        var human = HumanFor(tag);

        var sb = new StringBuilder();
        sb.AppendLine($"# DoxieOS {human} session");
        sb.AppendLine();
        sb.AppendLine($"You are running inside a DoxieOS **{human}** session. The user opened this");
        sb.AppendLine("PTY from the DoxieOS web UI — they're watching a live preview of");
        sb.AppendLine("`./.draft/manifest.json` in the right pane.");
        sb.AppendLine();
        sb.AppendLine($"On your first turn, invoke `{skillSlash}` (the DoxieOS web UI also injects");
        sb.AppendLine("this slash command automatically right after attach, so by the time you read");
        sb.AppendLine("this, the skill should already be loaded). Then follow that skill's");
        sb.AppendLine("instructions — greet the user in PT-BR, gather intent, propose a draft, and");
        sb.AppendLine("persist to `./.draft/manifest.json`.");
        sb.AppendLine();
        sb.AppendLine("Do not write outside this sandbox folder *except* for files inside the");
        sb.AppendLine("attached workspace — and even there, prefer reading over writing unless");
        sb.AppendLine("the user explicitly asks you to modify a workspace file.");
        sb.AppendLine();
        sb.AppendLine("## Conversation guide");
        sb.AppendLine();
        sb.AppendLine("Treat each follow-up message from the user as live design guidance, not");
        sb.AppendLine("small talk. Fold clear guidance into `./.draft/manifest.json` and any");
        sb.AppendLine("supporting draft files as soon as enough information is available. When");
        sb.AppendLine("instructions conflict, prefer the newest explicit instruction unless it");
        sb.AppendLine("would be unsafe, and ask one concise question only when truly blocked.");

        if (ws is not null)
        {
            sb.AppendLine();
            sb.AppendLine("## Attached workspace");
            sb.AppendLine();
            sb.AppendLine($"This session is bound to the user's workspace **{ws.Name}** (`{ws.Id}`),");
            sb.AppendLine($"absolute path: `{ws.Path}`. You may read files there to ground your");
            sb.AppendLine("design decisions in the user's actual codebase.");

            if (ws.MountedLibraryIds is { Count: > 0 })
            {
                sb.AppendLine();
                sb.AppendLine("The workspace's `CLAUDE.md` is included below; it inlines the");
                sb.AppendLine($"DoxieOS libraries the user mounted ({string.Join(", ", ws.MountedLibraryIds)}).");
            }

            // Relative @-import from the sandbox folder (./.sandbox/<tag>-<n>/)
            // up to the project root and back down into the workspace.
            // Forward slashes — Claude's @-import is path-style, not OS-aware.
            sb.AppendLine();
            sb.AppendLine("### Workspace CLAUDE.md (inlined)");
            sb.AppendLine();
            sb.AppendLine($"@../../.workspace/{ws.Id}/CLAUDE.md");
        }

        if (editingAgent is not null)
        {
            sb.AppendLine();
            sb.AppendLine("## Edit mode");
            sb.AppendLine();
            sb.AppendLine($"You are **editing** an existing agent: `{editingAgent.Id}` ({editingAgent.Name}).");
            sb.AppendLine($"The canonical definition lives at `../../.doxie/skills/{editingAgent.Id}/`");
            sb.AppendLine($"(`manifest.json` for metadata + `body.md` for the natural-language instructions).");
            sb.AppendLine();
            sb.AppendLine("`./.draft/manifest.json` was pre-seeded for you with the fields the orchestrator");
            sb.AppendLine("could derive from the in-memory descriptor (id, name, description, category,");
            sb.AppendLine("modes, requirements). Two fields are intentionally **null** because they're not");
            sb.AppendLine("structured in the descriptor:");
            sb.AppendLine();
            sb.AppendLine($"- `tools` — listed prose-style under `## Tools` in `../../.doxie/skills/{editingAgent.Id}/body.md`");
            sb.AppendLine($"- `systemPrompt` — typically the body / `## System prompt` section in the same `body.md`");
            sb.AppendLine();
            sb.AppendLine("**Before greeting the user, do this in one tool round:**");
            sb.AppendLine();
            sb.AppendLine($"1. Read `../../.doxie/skills/{editingAgent.Id}/body.md` and `../../.doxie/skills/{editingAgent.Id}/manifest.json`");
            sb.AppendLine("2. Extract `tools` (list) and `systemPrompt` (multiline string)");
            sb.AppendLine("3. Update `./.draft/manifest.json` with those fields filled in");
            sb.AppendLine();
            sb.AppendLine("Then greet the user in PT-BR and confirm what they want to change. On Save the");
            sb.AppendLine("orchestrator promotes with `overwrite=true` since the agent already exists.");
        }

        if (editingWorkflow is not null)
        {
            sb.AppendLine();
            sb.AppendLine("## Edit mode");
            sb.AppendLine();
            sb.AppendLine($"You are **editing** an existing workflow: `{editingWorkflow.Id}` ({editingWorkflow.Name}).");
            sb.AppendLine($"`./.draft/manifest.json` was pre-seeded with the saved definition (synthetic");
            sb.AppendLine("trigger node stripped, edges from `trigger` removed — the runner re-adds those).");
            sb.AppendLine("Greet the user in PT-BR and ask what they want to change. On Save the orchestrator");
            sb.AppendLine("promotes with `overwrite=true` since the workflow already exists.");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Tiny marker file persisted alongside the manifest so a Resume
    /// recognises an in-progress edit session even after the page is
    /// closed and reopened. Read by <see cref="BuilderSessionFactory.Find"/>.
    /// </summary>
    public sealed class BuilderMeta
    {
        public string? Kind { get; set; }
        public string? EditAgentId { get; set; }
        public string? EditWorkflowId { get; set; }
    }

    private static int NextOrdinal(string sandboxRoot, string tag)
    {
        var prefix = $"{tag}-";
        var pattern = new Regex("^" + Regex.Escape(prefix) + @"(\d+)$");
        var max = 0;
        foreach (var dir in Directory.EnumerateDirectories(sandboxRoot))
        {
            var name = Path.GetFileName(dir);
            var m = pattern.Match(name);
            if (m.Success && int.TryParse(m.Groups[1].Value, out var n) && n > max)
            {
                max = n;
            }
        }
        return max + 1;
    }

    private static string? ReadAttachedWorkspaceAgentsMd(Workspace? workspace)
    {
        if (workspace is null) return null;
        var path = Path.Combine(workspace.Path, "AGENTS.md");
        if (!File.Exists(path)) return null;
        try { return File.ReadAllText(path); }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static string SkillSlashFor(string tag) => tag switch
    {
        "builder-agent" => "/agent-builder",
        "builder-workflow" => "/workflow-builder",
        _ => $"/{tag}",
    };

    /// <summary>
    /// Skill id (without the leading slash) that drives the given builder
    /// tag. Returns null for unknown tags — those don't get a skill body
    /// inlined into AGENTS.md, only the primer.
    /// </summary>
    private static string? SkillIdFor(string tag) => tag switch
    {
        "builder-agent" => "agent-builder",
        "builder-workflow" => "workflow-builder",
        _ => null,
    };

    private static string HumanFor(string tag) => tag switch
    {
        "builder-agent" => "Agent Builder",
        "builder-workflow" => "Workflow Builder",
        _ => tag,
    };
}

public sealed record BuilderSandboxAllocation(
    string Tag,
    string Path,
    string ManifestPath,
    string? AttachedWorkspaceId,
    string? EditAgentId,
    string? EditWorkflowId);
