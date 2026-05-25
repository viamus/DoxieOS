using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Viamus.Doxie.Orchestrator.Agents.Doxie;

/// <summary>
/// One-shot migration helper for users upgrading from a v0.1 install where
/// skills lived directly under <c>.claude/skills/&lt;id&gt;/</c>. Scans the
/// shim root for unmanaged folders (those WITHOUT the <c>doxie/generated</c>
/// marker), converts each into the canonical <c>.doxie/skills/&lt;id&gt;/</c>
/// shape, and lets the regen pipeline produce the round-trip <c>.claude/</c>
/// shim back out — so Claude Code keeps reading the same skill it always did,
/// just now sourced from <c>.doxie/</c>.
///
/// Idempotent: re-running with the same input is a no-op (every detected
/// folder gets a marker on first migration; subsequent passes skip them).
/// </summary>
public sealed class LegacySkillsMigrator
{
    private const string GeneratedMarker = "doxie/generated";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
    };

    private static readonly JsonSerializerOptions WriteJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
    };

    private readonly string _workspaceRoot;

    public LegacySkillsMigrator(string workspaceRoot)
    {
        _workspaceRoot = workspaceRoot ?? throw new ArgumentNullException(nameof(workspaceRoot));
    }

    /// <summary>
    /// Returns every <c>.workflows/&lt;id&gt;/</c> folder at the project root
    /// that doesn't have a canonical equivalent under <c>.doxie/workflows/&lt;id&gt;/</c>.
    /// Workflows aren't provider-shaped — folder is copied byte-for-byte.
    /// </summary>
    public IReadOnlyList<LegacyLibrary> DetectWorkflows()
    {
        var legacyRoot = Path.Combine(_workspaceRoot, ".workflows");
        if (!Directory.Exists(legacyRoot)) return Array.Empty<LegacyLibrary>();

        var found = new List<LegacyLibrary>();
        foreach (var dir in Directory.EnumerateDirectories(legacyRoot).OrderBy(p => p, StringComparer.Ordinal))
        {
            var id = Path.GetFileName(dir);
            if (string.IsNullOrEmpty(id)) continue;
            var canonicalDir = Path.Combine(_workspaceRoot, ".doxie", "workflows", id);
            if (Directory.Exists(canonicalDir)) continue;
            found.Add(new LegacyLibrary(id, dir));
        }
        return found;
    }

    /// <summary>Same shape as <see cref="MigrateLibraries"/> but writes under <c>.doxie/workflows/</c>.</summary>
    public IReadOnlyList<MigrationResult> MigrateWorkflows(IEnumerable<LegacyLibrary> workflows)
    {
        var results = new List<MigrationResult>();
        foreach (var wf in workflows)
        {
            try
            {
                var dest = Path.Combine(_workspaceRoot, ".doxie", "workflows", wf.Id);
                if (Directory.Exists(dest))
                {
                    results.Add(new MigrationResult(wf.Id, Migrated: false, Reason: "canonical already exists"));
                    continue;
                }
                Directory.CreateDirectory(dest);
                foreach (var file in Directory.EnumerateFiles(wf.ShimDirectory, "*", SearchOption.AllDirectories))
                {
                    var rel = Path.GetRelativePath(wf.ShimDirectory, file);
                    var destPath = Path.Combine(dest, rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                    File.Copy(file, destPath, overwrite: true);
                }
                results.Add(new MigrationResult(wf.Id, Migrated: true, Reason: null));
            }
            catch (Exception ex)
            {
                results.Add(new MigrationResult(wf.Id, Migrated: false, Reason: ex.Message));
            }
        }
        return results;
    }

    /// <summary>
    /// Returns every <c>.claude/libraries/&lt;id&gt;/</c> folder that doesn't
    /// have a canonical equivalent under <c>.doxie/libraries/&lt;id&gt;/</c>.
    /// Libraries don't carry a marker file the way skills do — the canon
    /// presence is itself the "managed" signal.
    /// </summary>
    public IReadOnlyList<LegacyLibrary> DetectLibraries()
    {
        var librariesRoot = Path.Combine(_workspaceRoot, ".claude", "libraries");
        if (!Directory.Exists(librariesRoot)) return Array.Empty<LegacyLibrary>();

        var found = new List<LegacyLibrary>();
        foreach (var dir in Directory.EnumerateDirectories(librariesRoot).OrderBy(p => p, StringComparer.Ordinal))
        {
            var id = Path.GetFileName(dir);
            if (string.IsNullOrEmpty(id)) continue;
            var canonicalDir = Path.Combine(_workspaceRoot, ".doxie", "libraries", id);
            if (Directory.Exists(canonicalDir)) continue;     // already canonical
            found.Add(new LegacyLibrary(id, dir));
        }
        return found;
    }

    /// <summary>
    /// Copies the supplied legacy libraries verbatim into <c>.doxie/libraries/</c>.
    /// No schema transformation — libraries aren't provider-shaped, just a
    /// folder of files. The <see cref="ClaudeLibraryEmitter"/> will then
    /// emit them back to <c>.claude/libraries/</c> on the next regen.
    /// </summary>
    public IReadOnlyList<MigrationResult> MigrateLibraries(IEnumerable<LegacyLibrary> libraries)
    {
        var results = new List<MigrationResult>();
        foreach (var lib in libraries)
        {
            try
            {
                var dest = Path.Combine(_workspaceRoot, ".doxie", "libraries", lib.Id);
                if (Directory.Exists(dest))
                {
                    results.Add(new MigrationResult(lib.Id, Migrated: false, Reason: "canonical already exists"));
                    continue;
                }
                Directory.CreateDirectory(dest);
                foreach (var file in Directory.EnumerateFiles(lib.ShimDirectory, "*", SearchOption.AllDirectories))
                {
                    var rel = Path.GetRelativePath(lib.ShimDirectory, file);
                    var destPath = Path.Combine(dest, rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                    File.Copy(file, destPath, overwrite: true);
                }
                results.Add(new MigrationResult(lib.Id, Migrated: true, Reason: null));
            }
            catch (Exception ex)
            {
                results.Add(new MigrationResult(lib.Id, Migrated: false, Reason: ex.Message));
            }
        }
        return results;
    }

    /// <summary>
    /// Returns every <c>.claude/skills/&lt;id&gt;/</c> folder that has a SKILL.md
    /// but is NOT yet managed by the doxie pipeline (no marker file). These are
    /// the migration candidates.
    /// </summary>
    public IReadOnlyList<LegacySkill> Detect()
    {
        var skillsRoot = Path.Combine(_workspaceRoot, ".claude", "skills");
        if (!Directory.Exists(skillsRoot)) return Array.Empty<LegacySkill>();

        var found = new List<LegacySkill>();
        foreach (var dir in Directory.EnumerateDirectories(skillsRoot).OrderBy(p => p, StringComparer.Ordinal))
        {
            var id = Path.GetFileName(dir);
            if (string.IsNullOrEmpty(id)) continue;
            if (File.Exists(Path.Combine(dir, GeneratedMarker))) continue;     // already managed
            if (!File.Exists(Path.Combine(dir, "SKILL.md"))) continue;          // not a real skill folder

            // Skip if the canonical already exists — partial-state safety net,
            // shouldn't happen in normal flow but protects against a half-done
            // hand migration.
            var canonicalDir = Path.Combine(_workspaceRoot, ".doxie", "skills", id);
            var alreadyCanonical = File.Exists(Path.Combine(canonicalDir, "manifest.json"));

            found.Add(new LegacySkill(
                Id: id,
                ShimDirectory: dir,
                HasSidecar: File.Exists(Path.Combine(dir, "orchestrator.json")),
                AlreadyCanonical: alreadyCanonical));
        }
        return found;
    }

    /// <summary>
    /// Migrates the supplied legacy skills into the canonical <c>.doxie/skills/</c>
    /// layout. Returns one result per skill describing what happened.
    /// </summary>
    public IReadOnlyList<MigrationResult> Migrate(IEnumerable<LegacySkill> skills)
    {
        var results = new List<MigrationResult>();
        foreach (var skill in skills)
        {
            try
            {
                if (skill.AlreadyCanonical)
                {
                    results.Add(new MigrationResult(skill.Id, Migrated: false, Reason: "canonical already exists"));
                    continue;
                }
                MigrateOne(skill);
                results.Add(new MigrationResult(skill.Id, Migrated: true, Reason: null));
            }
            catch (Exception ex)
            {
                results.Add(new MigrationResult(skill.Id, Migrated: false, Reason: ex.Message));
            }
        }
        return results;
    }

    private void MigrateOne(LegacySkill skill)
    {
        var skillMdPath = Path.Combine(skill.ShimDirectory, "SKILL.md");
        var content = File.ReadAllText(skillMdPath, Encoding.UTF8);
        var (frontmatter, body) = SplitFrontmatter(content);
        var fmFields = ParseFrontmatter(frontmatter ?? string.Empty);

        var name = fmFields.GetValueOrDefault("name") ?? skill.Id;
        var description = fmFields.GetValueOrDefault("description") ?? string.Empty;

        if (!string.Equals(name, skill.Id, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"SKILL.md frontmatter name '{name}' does not match folder '{skill.Id}'. " +
                $"Rename one to match before migrating.");
        }

        // Sidecar (orchestrator.json) — fold every field into the canonical manifest.
        Sidecar? sidecar = null;
        if (skill.HasSidecar)
        {
            var sidecarPath = Path.Combine(skill.ShimDirectory, "orchestrator.json");
            sidecar = JsonSerializer.Deserialize<Sidecar>(File.ReadAllText(sidecarPath), JsonOptions);
        }

        var manifest = new
        {
            id = skill.Id,
            description,
            displayName = sidecar?.DisplayName,
            category = sidecar?.Category,
            requirements = sidecar?.Requirements,
            modes = sidecar?.Modes,
        };

        var canonicalDir = Path.Combine(_workspaceRoot, ".doxie", "skills", skill.Id);
        Directory.CreateDirectory(canonicalDir);

        File.WriteAllText(
            Path.Combine(canonicalDir, "manifest.json"),
            JsonSerializer.Serialize(manifest, WriteJsonOptions) + "\n",
            Encoding.UTF8);

        File.WriteAllText(
            Path.Combine(canonicalDir, "body.md"),
            body.TrimStart('\n'),
            Encoding.UTF8);

        // Companion files — every .md / scripts/ / refs etc., copied verbatim.
        // Excludes SKILL.md, orchestrator.json, and the marker file itself.
        foreach (var file in Directory.EnumerateFiles(skill.ShimDirectory, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(skill.ShimDirectory, file).Replace('\\', '/');
            if (rel.Equals("SKILL.md", StringComparison.OrdinalIgnoreCase)) continue;
            if (rel.Equals("orchestrator.json", StringComparison.OrdinalIgnoreCase)) continue;
            if (rel.Equals(GeneratedMarker, StringComparison.OrdinalIgnoreCase)) continue;

            var destPath = Path.Combine(canonicalDir, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            File.Copy(file, destPath, overwrite: true);
        }
    }

    private static (string? Frontmatter, string Body) SplitFrontmatter(string content)
    {
        if (!content.StartsWith("---", StringComparison.Ordinal)) return (null, content);
        var afterOpen = content.AsSpan(3);
        var closeIndex = afterOpen.IndexOf("\n---");
        if (closeIndex < 0) return (null, content);
        var fm = afterOpen.Slice(0, closeIndex).ToString().TrimStart('\n');
        var body = afterOpen.Slice(closeIndex + 4).ToString().TrimStart('\n');
        return (fm, body);
    }

    private static IReadOnlyDictionary<string, string> ParseFrontmatter(string fm)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
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
            dict[key] = value;
        }
        return dict;
    }

    private sealed class Sidecar
    {
        public string? DisplayName { get; set; }
        public AgentCategory? Category { get; set; }
        public List<DoxieRequirement>? Requirements { get; set; }
        public Dictionary<string, DoxieMode>? Modes { get; set; }
    }
}

public sealed record LegacySkill(
    string Id,
    string ShimDirectory,
    bool HasSidecar,
    bool AlreadyCanonical);

public sealed record LegacyLibrary(
    string Id,
    string ShimDirectory);

public sealed record MigrationResult(
    string Id,
    bool Migrated,
    string? Reason);
