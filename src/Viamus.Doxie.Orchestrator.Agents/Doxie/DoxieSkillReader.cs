using System.Text;
using System.Text.Json;
using Viamus.Doxie.Orchestrator.Common;

namespace Viamus.Doxie.Orchestrator.Agents.Doxie;

/// <summary>
/// Loads skill canons from <c>.doxie/skills/&lt;id&gt;/</c>. Each subdirectory
/// containing a <c>manifest.json</c> AND a <c>body.md</c> is one skill.
/// Folders missing either file are skipped silently — partial drafts won't
/// crash the regen pipeline.
///
/// Strict on schema: <c>manifest.json</c> with invalid JSON or missing
/// required fields throws — the canonical is human-authored, so failures
/// must surface loudly rather than silently producing degraded shims.
/// </summary>
public sealed class DoxieSkillReader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly string _doxieRoot;
    private readonly Func<IReadOnlyList<DoxieCatalogRoot>>? _catalogRootsProvider;

    public DoxieSkillReader(string doxieRoot, Func<IReadOnlyList<DoxieCatalogRoot>>? catalogRootsProvider = null)
    {
        _doxieRoot = doxieRoot ?? throw new ArgumentNullException(nameof(doxieRoot));
        _catalogRootsProvider = catalogRootsProvider;
    }

    /// <summary>
    /// Walks <c>.doxie/skills/</c> and returns every skill found. Order is
    /// stable (alphabetical by id) so emitters produce deterministic output —
    /// critical for the byte-identical idempotency guarantee.
    /// </summary>
    public IReadOnlyList<DoxieSkillCanon> LoadAll()
    {
        var skills = new Dictionary<string, DoxieSkillCanon>(StringComparer.Ordinal);
        foreach (var root in EnumerateSkillRootsInPrecedenceOrder())
        {
            if (!Directory.Exists(root.SkillsDirectory)) continue;

            foreach (var dir in Directory.EnumerateDirectories(root.SkillsDirectory).OrderBy(p => p, StringComparer.Ordinal))
            {
                var canon = TryLoadSkill(dir, root);
                if (canon is not null)
                {
                    skills[canon.Manifest.Id] = canon;
                }
            }
        }

        return skills.Values
            .OrderBy(c => c.Manifest.Id, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Reads only the body of a single skill — meant for callers that
    /// need the natural-language instructions but not the full canon
    /// (e.g. the Builder bootstrap path inlining the body into a Codex
    /// PTY's first message). Returns null if the skill folder, manifest,
    /// or body file is missing — callers can degrade to a generic preface.
    /// </summary>
    public string? TryLoadBody(string skillId)
    {
        if (string.IsNullOrWhiteSpace(skillId)) return null;
        foreach (var root in EnumerateSkillRootsInPrecedenceOrder().Reverse())
        {
            var bodyPath = Path.Combine(root.SkillsDirectory, skillId, "body.md");
            if (!File.Exists(bodyPath)) continue;
            try { return File.ReadAllText(bodyPath, Encoding.UTF8); }
            catch (IOException) { return null; }
        }
        return null;
    }

    /// <summary>
    /// Reads the per-skill memories at <c>.doxie/skills/&lt;id&gt;/memories/*.md</c>.
    /// Each file is one memory entry; YAML frontmatter (optional) carries
    /// <c>name</c>, <c>priority</c> and <c>condition</c>. Files without
    /// frontmatter still load as Medium-priority always-on memories.
    /// Malformed files are skipped silently — one bad memory shouldn't
    /// block the whole dispatch. Returns an empty list when the folder
    /// doesn't exist (the common case for skills with no memories yet).
    /// </summary>
    public IReadOnlyList<DoxieSkillMemory> LoadMemories(string skillId)
    {
        if (string.IsNullOrWhiteSpace(skillId)) return Array.Empty<DoxieSkillMemory>();
        var skillRoot = EnumerateSkillRootsInPrecedenceOrder()
            .Reverse()
            .Select(r => Path.Combine(r.SkillsDirectory, skillId))
            .FirstOrDefault(Directory.Exists);
        if (skillRoot is null) return Array.Empty<DoxieSkillMemory>();

        var memoriesDir = Path.Combine(skillRoot, "memories");
        if (!Directory.Exists(memoriesDir)) return Array.Empty<DoxieSkillMemory>();

        var memories = new List<DoxieSkillMemory>();
        foreach (var file in Directory.EnumerateFiles(memoriesDir, "*.md").OrderBy(p => p, StringComparer.Ordinal))
        {
            try
            {
                var memory = ParseMemoryFile(file);
                if (memory is not null) memories.Add(memory);
            }
            catch (IOException)
            {
                // Locked file or bad permissions — skip and let the next
                // dispatch retry. Logging would surface the failure if
                // we wired this through ILogger; for now silent skip
                // matches the rest of the reader's posture.
            }
        }
        return memories;
    }

    private static DoxieSkillMemory? ParseMemoryFile(string path)
    {
        var fileName = Path.GetFileName(path);
        var content = File.ReadAllText(path, Encoding.UTF8);
        var (frontmatter, body) = SplitFrontmatter(content);
        var fields = frontmatter is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : ParseFrontmatter(frontmatter);

        var name = fields.GetValueOrDefault("name") ?? Path.GetFileNameWithoutExtension(path);
        var priorityRaw = fields.GetValueOrDefault("priority");
        var priority = ParsePriority(priorityRaw);
        var condition = fields.GetValueOrDefault("condition");

        return new DoxieSkillMemory(
            FileName: fileName,
            Name: name,
            Priority: priority,
            Condition: string.IsNullOrWhiteSpace(condition) ? null : condition.Trim(),
            Body: body.TrimEnd());
    }

    private static DoxieSkillMemoryPriority ParsePriority(string? raw) =>
        raw?.Trim().ToLowerInvariant() switch
        {
            "high" => DoxieSkillMemoryPriority.High,
            "low" => DoxieSkillMemoryPriority.Low,
            // Anything else (including null, "medium", "med", typos)
            // defaults to Medium so the common case needs no annotation.
            _ => DoxieSkillMemoryPriority.Medium,
        };

    private static (string? Frontmatter, string Body) SplitFrontmatter(string content)
    {
        // Mirror DoxieAgentReader's frontmatter convention so the parsing
        // is uniform across the codebase: opens with --- on its own line,
        // closes with --- on its own line, body follows.
        if (!content.StartsWith("---", StringComparison.Ordinal)) return (null, content);
        var afterOpening = content.AsSpan(3);
        var closingIndex = afterOpening.IndexOf("\n---");
        if (closingIndex < 0) return (null, content);
        var fm = afterOpening.Slice(0, closingIndex).ToString().TrimStart('\n');
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
            // Strip matching surrounding quotes ("foo" or 'foo') — common
            // in YAML for values with special characters.
            if (value.Length >= 2 && (value[0] == '"' || value[0] == '\'') && value[^1] == value[0])
            {
                value = value[1..^1];
            }
            result[key] = value;
        }
        return result;
    }

    private IEnumerable<DoxieCatalogRoot> EnumerateSkillRoots()
    {
        if (_catalogRootsProvider is not null)
        {
            foreach (var root in _catalogRootsProvider())
            {
                yield return root;
            }
            yield break;
        }

        yield return new DoxieCatalogRoot(
            DoxieCatalogStore.DefaultCatalogId,
            "Default catalog",
            _doxieRoot,
            IsDefault: true);
    }

    private IReadOnlyList<DoxieCatalogRoot> EnumerateSkillRootsInPrecedenceOrder() =>
        EnumerateSkillRoots()
            .OrderBy(r => r.IsDefault ? 0 : 1)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static DoxieSkillCanon? TryLoadSkill(string skillDir, DoxieCatalogRoot catalog)
    {
        var manifestPath = Path.Combine(skillDir, "manifest.json");
        var bodyPath = Path.Combine(skillDir, "body.md");
        if (!File.Exists(manifestPath) || !File.Exists(bodyPath))
        {
            return null;
        }

        var manifest = JsonSerializer.Deserialize<DoxieSkillManifest>(
            File.ReadAllText(manifestPath, Encoding.UTF8),
            JsonOptions)
            ?? throw new InvalidDataException(
                $"manifest.json at '{manifestPath}' deserialised to null.");

        if (string.IsNullOrWhiteSpace(manifest.Id))
        {
            throw new InvalidDataException(
                $"manifest.json at '{manifestPath}' is missing required field 'id'.");
        }

        var folderName = Path.GetFileName(skillDir);
        if (!string.Equals(manifest.Id, folderName, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"manifest.json id '{manifest.Id}' does not match folder name '{folderName}' " +
                $"at '{skillDir}'. Folder name is the canonical id; rename one to match.");
        }

        if (string.IsNullOrWhiteSpace(manifest.Description))
        {
            throw new InvalidDataException(
                $"manifest.json at '{manifestPath}' is missing required field 'description'.");
        }

        var body = File.ReadAllText(bodyPath, Encoding.UTF8);
        var companions = LoadCompanions(skillDir);

        return new DoxieSkillCanon(
            manifest,
            body,
            companions,
            IsPrivate: false,
            CatalogId: catalog.Id,
            CatalogName: catalog.Name,
            CatalogRoot: catalog.RootPath);
    }

    private static IReadOnlyDictionary<string, byte[]> LoadCompanions(string skillDir)
    {
        var companions = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        // Companions = every file in the skill folder OTHER than manifest.json
        // and body.md. Walked recursively so subfolders (scripts/, modes/, refs/)
        // are preserved as-is for the emitter to mirror into the shim.
        foreach (var file in Directory.EnumerateFiles(skillDir, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(skillDir, file).Replace('\\', '/');
            if (string.Equals(rel, "manifest.json", StringComparison.Ordinal)) continue;
            if (string.Equals(rel, "body.md", StringComparison.Ordinal)) continue;
            companions[rel] = File.ReadAllBytes(file);
        }
        return companions;
    }
}
