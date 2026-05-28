using System.Text;

namespace Viamus.Doxie.Orchestrator.Agents.Doxie;

/// <summary>
/// Emits the OpenAI Codex shim — a single <c>.codex/AGENTS.md</c> consolidating
/// every canonical skill into a catalog Codex reads on session start.
///
/// Codex has no slash-command surface and no <c>SKILL.md</c> per-skill convention.
/// Two complementary mechanisms feed it our skills:
/// <list type="bullet">
/// <item>This shim — static catalog, gives Codex *awareness* of what skills exist.</item>
/// <item><see cref="CodexProvider.FormatPrompt"/> — inlines the specific skill's
///       body into the prompt at invocation time, so behavior matches Claude.</item>
/// </list>
/// </summary>
public sealed class CodexShimEmitter : IShimEmitter
{
    private const string GeneratedHeader =
        "<!-- doxie/generated — edit .doxie/skills/<id>/{manifest.json,body.md} instead. -->";

    private const string RootAgentsFileName = "AGENTS.md";

    public string ShimRoot => ".codex";

    public void Emit(DoxieRegistry registry, string workspaceRoot)
    {
        var dir = Path.Combine(workspaceRoot, ShimRoot);
        Directory.CreateDirectory(dir);

        var content = BuildAgentsMd(registry);
        var bytes = Encoding.UTF8.GetBytes(content);
        var path = Path.Combine(dir, "AGENTS.md");

        if (File.Exists(path))
        {
            var existing = File.ReadAllBytes(path);
            if (existing.Length == bytes.Length && existing.AsSpan().SequenceEqual(bytes))
            {
                EmitRootAgentsMd(workspaceRoot, bytes);
                return;
            }
        }
        File.WriteAllBytes(path, bytes);
        EmitRootAgentsMd(workspaceRoot, bytes);
    }

    private static string BuildAgentsMd(DoxieRegistry registry)
    {
        var sb = new StringBuilder();
        sb.Append(GeneratedHeader).Append('\n');
        sb.Append('\n');
        sb.Append("# DoxieOS catalog\n\n");
        sb.Append("## Doxie resolution rules\n\n");
        sb.Append("- Treat kebab-case identifiers that appear in this catalog as Doxie skills, agents, or workflows before treating them as shell commands or repository files.\n");
        sb.Append("- If the user asks to run, execute, inspect, or continue `backend-platform-architect` (or any similar id), first consult `.doxie/skills/<id>/manifest.json` and `.doxie/skills/<id>/body.md`.\n");
        sb.Append("- Workflow `agentId` values are Doxie skill ids. Load the matching local definition and follow its handoff contract instead of trying to execute the id in the shell.\n");
        sb.Append("- Only fall back to shell command or root filename lookup after the local Doxie catalog does not contain that id.\n\n");
        sb.Append("This file is the Codex-side index of skills and subagents authored under ");
        sb.Append("`.doxie/`. When the user invokes a slash command (e.g. `/sandbox new`), the ");
        sb.Append("orchestrator inlines the corresponding skill's instructions into the prompt — ");
        sb.Append("so Codex sees the same body Claude Code reads from `.claude/skills/<id>/SKILL.md`. ");
        sb.Append("Use this catalog to know what's available without re-reading the per-skill body ");
        sb.Append("each turn.\n\n");

        if (registry.Skills.Count == 0 && registry.Agents.Count == 0 && registry.Workflows.Count == 0)
        {
            sb.Append("_Nothing authored yet._\n");
            return sb.ToString();
        }

        if (registry.Skills.Count > 0)
        {
            sb.Append("## Skills\n\n");
            foreach (var skill in registry.Skills)
            {
                AppendSkill(sb, skill);
            }
        }

        if (registry.Agents.Count > 0)
        {
            sb.Append("## Subagents\n\n");
            sb.Append("Dispatched via the Task tool. Each entry is a specialised agent the ");
            sb.Append("orchestrator can hand off to mid-conversation.\n\n");
            foreach (var agent in registry.Agents)
            {
                AppendAgent(sb, agent);
            }
        }

        if (registry.Workflows.Count > 0)
        {
            sb.Append("## Workflows\n\n");
            sb.Append("Compositions of agents wired into a graph and dispatched by the orchestrator. ");
            sb.Append("Listed here so Codex can suggest the right one when the user describes a task ");
            sb.Append("that maps to an existing workflow — invoke them via the orchestrator UI or the ");
            sb.Append("`POST /api/workflows/<id>/run` endpoint, not from the chat surface.\n\n");
            foreach (var workflow in registry.Workflows)
            {
                AppendWorkflow(sb, workflow);
            }
        }

        return sb.ToString();
    }

    private static void EmitRootAgentsMd(string workspaceRoot, byte[] bytes)
    {
        var rootPath = Path.Combine(workspaceRoot, RootAgentsFileName);
        if (File.Exists(rootPath))
        {
            var existingText = File.ReadAllText(rootPath, Encoding.UTF8);
            if (!existingText.StartsWith(GeneratedHeader, StringComparison.Ordinal))
            {
                return;
            }

            var existing = Encoding.UTF8.GetBytes(existingText);
            if (existing.Length == bytes.Length && existing.AsSpan().SequenceEqual(bytes))
            {
                return;
            }
        }

        File.WriteAllBytes(rootPath, bytes);
    }

    private static void AppendAgent(StringBuilder sb, DoxieAgentCanon agent)
    {
        var m = agent.Manifest;
        sb.Append("### ").Append(m.Id).Append('\n').Append('\n');
        sb.Append(FlattenSingleLine(m.Description)).Append("\n\n");
        if (!string.IsNullOrWhiteSpace(m.Tools))
        {
            sb.Append("**Tools:** ").Append(m.Tools).Append("\n\n");
        }
        if (!string.IsNullOrWhiteSpace(m.Model))
        {
            sb.Append("**Model:** ").Append(m.Model).Append("\n\n");
        }
    }

    private static void AppendSkill(StringBuilder sb, DoxieSkillCanon skill)
    {
        var m = skill.Manifest;
        sb.Append("## ").Append('/').Append(m.Id).Append('\n').Append('\n');
        sb.Append(FlattenSingleLine(m.Description)).Append("\n\n");

        if (m.Modes is { Count: > 0 })
        {
            sb.Append("**Subcommands:**\n\n");
            foreach (var (modeId, mode) in m.Modes.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                if (mode.Hidden) continue;
                sb.Append("- `").Append(modeId).Append('`');
                if (!string.IsNullOrWhiteSpace(mode.Description))
                {
                    sb.Append(" — ").Append(FlattenSingleLine(mode.Description!));
                }
                sb.Append('\n');
            }
            sb.Append('\n');
        }

        if (m.Requirements is { Count: > 0 })
        {
            sb.Append("**Requires:** ");
            sb.Append(string.Join(", ", m.Requirements
                .Where(r => !string.IsNullOrWhiteSpace(r.Name))
                .Select(r => r.Required ? r.Name : $"{r.Name} (optional)")));
            sb.Append("\n\n");
        }
    }

    private static void AppendWorkflow(StringBuilder sb, DoxieWorkflowCanon w)
    {
        sb.Append("### ").Append(w.Id);
        if (!w.Enabled)
        {
            sb.Append(" _(disabled)_");
        }
        sb.Append('\n').Append('\n');
        if (!string.IsNullOrWhiteSpace(w.Name) && !string.Equals(w.Name, w.Id, StringComparison.Ordinal))
        {
            sb.Append("**Name:** ").Append(w.Name).Append("\n\n");
        }
        if (!string.IsNullOrWhiteSpace(w.Description))
        {
            sb.Append(FlattenSingleLine(w.Description)).Append("\n\n");
        }
        sb.Append("**Category:** ").Append(w.DisplayCategory).Append("\n\n");
        sb.Append("**Trigger:** ").Append(w.TriggerKind);
        if (string.Equals(w.TriggerKind, "cron", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(w.CronExpression))
        {
            sb.Append(" `").Append(w.CronExpression).Append('`');
        }
        sb.Append("\n\n");
    }

    private static string FlattenSingleLine(string value) =>
        value.Replace("\r\n", " ").Replace("\n", " ").Replace("\r", " ").Trim();
}
