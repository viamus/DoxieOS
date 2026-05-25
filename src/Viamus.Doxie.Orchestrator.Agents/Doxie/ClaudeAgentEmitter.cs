using System.Text;

namespace Viamus.Doxie.Orchestrator.Agents.Doxie;

/// <summary>
/// Emits the Claude Code subagent shim under <c>.claude/agents/&lt;id&gt;.md</c>.
/// Format is provider-neutral (YAML frontmatter + body), so the shim is
/// effectively a round-tripped copy of the canonical file. Idempotent —
/// content-aware writes skip unchanged files; managed orphans are deleted.
///
/// Marker side-channel: the agent body itself is the user-authored prompt,
/// so we can't drop a marker file there without changing semantics. Instead,
/// we track managed agents via a sibling <c>.doxie-managed</c> manifest
/// listing every id we own — present-but-unmanaged files are left alone.
/// </summary>
public sealed class ClaudeAgentEmitter : IShimEmitter
{
    private const string ManagedManifestFile = ".doxie-managed";

    public string ShimRoot => ".claude/agents";

    public void Emit(DoxieRegistry registry, string workspaceRoot)
    {
        var agentsRoot = Path.Combine(workspaceRoot, ".claude", "agents");
        Directory.CreateDirectory(agentsRoot);

        var managedNow = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var agent in registry.Agents)
        {
            var path = Path.Combine(agentsRoot, agent.Manifest.Id + ".md");
            var bytes = Encoding.UTF8.GetBytes(BuildAgentMd(agent));
            WriteIfChanged(path, bytes);
            managedNow.Add(agent.Manifest.Id);
        }

        // Read previous managed-set, delete any that disappeared from the
        // registry, then write the new manifest.
        var previousManagedPath = Path.Combine(agentsRoot, ManagedManifestFile);
        if (File.Exists(previousManagedPath))
        {
            foreach (var prevId in File.ReadAllLines(previousManagedPath))
            {
                var trimmed = prevId.Trim();
                if (string.IsNullOrEmpty(trimmed)) continue;
                if (managedNow.Contains(trimmed)) continue;
                var orphanPath = Path.Combine(agentsRoot, trimmed + ".md");
                if (File.Exists(orphanPath)) File.Delete(orphanPath);
            }
        }

        var manifestContent = string.Join('\n', managedNow.OrderBy(id => id, StringComparer.Ordinal)) + "\n";
        WriteIfChanged(previousManagedPath, Encoding.UTF8.GetBytes(manifestContent));
    }

    private static string BuildAgentMd(DoxieAgentCanon agent)
    {
        var m = agent.Manifest;
        var sb = new StringBuilder();
        sb.Append("---\n");
        sb.Append("name: ").Append(m.Id).Append('\n');
        sb.Append("description: ").Append(FlattenForFrontmatter(m.Description)).Append('\n');
        if (!string.IsNullOrWhiteSpace(m.Tools))
        {
            sb.Append("tools: ").Append(m.Tools).Append('\n');
        }
        if (!string.IsNullOrWhiteSpace(m.Model))
        {
            sb.Append("model: ").Append(m.Model).Append('\n');
        }
        if (m.Isolation is { } iso)
        {
            sb.Append("isolation: ").Append(iso.ToString().ToLowerInvariant()).Append('\n');
        }
        sb.Append("---\n\n");
        sb.Append(agent.SystemPrompt.TrimStart('\n'));
        if (!sb.ToString().EndsWith('\n')) sb.Append('\n');
        return sb.ToString();
    }

    private static string FlattenForFrontmatter(string value) =>
        value.Replace("\r\n", " ").Replace("\n", " ").Replace("\r", " ").Trim();

    private static void WriteIfChanged(string path, byte[] desired)
    {
        if (File.Exists(path))
        {
            var existing = File.ReadAllBytes(path);
            if (existing.Length == desired.Length && existing.AsSpan().SequenceEqual(desired))
            {
                return;
            }
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, desired);
    }
}
