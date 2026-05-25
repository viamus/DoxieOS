using System.Text;
using System.Text.RegularExpressions;
using Viamus.Doxie.Orchestrator.Common;

namespace Viamus.Doxie.Orchestrator.Agents.Doxie;

/// <summary>
/// Loads agent canons from <c>.doxie/agents/&lt;id&gt;.md</c>. Each agent is
/// a single markdown file with YAML frontmatter — same layout Claude Code
/// itself reads from <c>.claude/agents/</c>, so the canonical and the Claude
/// shim are nearly byte-equivalent (the shim emitter is essentially a copy
/// once we round-trip the frontmatter back out).
/// </summary>
public sealed class DoxieAgentReader
{
    private readonly string _doxieRoot;
    private readonly Func<IReadOnlyList<DoxieCatalogRoot>>? _catalogRootsProvider;

    public DoxieAgentReader(string doxieRoot, Func<IReadOnlyList<DoxieCatalogRoot>>? catalogRootsProvider = null)
    {
        _doxieRoot = doxieRoot ?? throw new ArgumentNullException(nameof(doxieRoot));
        _catalogRootsProvider = catalogRootsProvider;
    }

    public IReadOnlyList<DoxieAgentCanon> LoadAll()
    {
        var agents = new Dictionary<string, DoxieAgentCanon>(StringComparer.Ordinal);
        foreach (var agentsDir in AgentRoots())
        {
            if (!Directory.Exists(agentsDir)) continue;

            foreach (var file in Directory.EnumerateFiles(agentsDir, "*.md").OrderBy(p => p, StringComparer.Ordinal))
            {
                var canon = TryLoadAgent(file);
                if (canon is not null)
                {
                    agents[canon.Manifest.Id] = canon;
                }
            }
        }
        return agents.Values
            .OrderBy(a => a.Manifest.Id, StringComparer.Ordinal)
            .ToList();
    }

    private IEnumerable<string> AgentRoots()
    {
        if (_catalogRootsProvider is not null)
        {
            foreach (var root in _catalogRootsProvider())
            {
                yield return root.AgentsDirectory;
            }
            yield break;
        }

        yield return Path.Combine(_doxieRoot, "agents");
    }

    private static DoxieAgentCanon? TryLoadAgent(string path)
    {
        var content = File.ReadAllText(path, Encoding.UTF8);
        var (frontmatter, body) = SplitFrontmatter(content);
        if (frontmatter is null)
        {
            // No frontmatter at all — not a real agent definition.
            return null;
        }

        var fields = ParseFrontmatter(frontmatter);
        var name = fields.GetValueOrDefault("name") ?? Path.GetFileNameWithoutExtension(path);
        var description = fields.GetValueOrDefault("description") ?? string.Empty;

        var fileId = Path.GetFileNameWithoutExtension(path);
        if (!string.Equals(name, fileId, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Agent file '{path}' has frontmatter name '{name}' which does not match " +
                $"the filename '{fileId}.md'. Filename is the canonical id; rename one to match.");
        }

        if (string.IsNullOrWhiteSpace(description))
        {
            throw new InvalidDataException(
                $"Agent file '{path}' is missing required frontmatter field 'description'.");
        }

        DoxieAgentIsolation? isolation = fields.TryGetValue("isolation", out var iso)
            ? Enum.TryParse<DoxieAgentIsolation>(iso, ignoreCase: true, out var parsed) ? parsed : null
            : null;

        var manifest = new DoxieAgentManifest
        {
            Id = name,
            Description = description,
            Tools = fields.GetValueOrDefault("tools"),
            Model = fields.GetValueOrDefault("model"),
            Isolation = isolation,
        };
        return new DoxieAgentCanon(manifest, body);
    }

    private static (string? Frontmatter, string Body) SplitFrontmatter(string content)
    {
        if (!content.StartsWith("---", StringComparison.Ordinal))
        {
            return (null, content);
        }
        var afterOpening = content.AsSpan(3);
        var closingIndex = afterOpening.IndexOf("\n---");
        if (closingIndex < 0) return (null, content);

        var fm = afterOpening.Slice(0, closingIndex).ToString().TrimStart('\n');
        // Body starts after the closing "\n---" plus the trailing newline.
        var afterClose = afterOpening.Slice(closingIndex + 4);
        var body = afterClose.ToString().TrimStart('\n');
        return (fm, body);
    }

    private static IReadOnlyDictionary<string, string> ParseFrontmatter(string fm)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in fm.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#')) continue;
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var key = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            if (value.Length >= 2 && (value[0] == '"' || value[0] == '\'') && value[^1] == value[0])
            {
                value = value[1..^1];
            }
            result[key] = value;
        }
        return result;
    }
}
