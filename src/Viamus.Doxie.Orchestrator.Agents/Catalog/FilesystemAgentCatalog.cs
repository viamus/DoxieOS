using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Viamus.Doxie.Orchestrator.Agents;

/// <summary>
/// Discovers agents (Claude Code skills) by walking the filesystem under a
/// skills directory. Each subdirectory containing a <c>SKILL.md</c> is an
/// agent. Each sibling <c>&lt;subcommand&gt;.md</c> file whose H1 matches the
/// pattern <c># `/&lt;skill&gt; &lt;subcommand&gt;` — &lt;description&gt;</c>
/// is registered as a mode.
///
/// Optional sidecar file <c>orchestrator.json</c> at the skill root provides
/// metadata that cannot be reliably extracted from prose: agent category and
/// per-mode field definitions (label, placeholder).
/// </summary>
public sealed class FilesystemAgentCatalog : IAgentCatalog, IDisposable
{
    private static readonly TimeSpan WatchDebounce = TimeSpan.FromMilliseconds(300);

    private readonly string _skillsDirectory;
    private Lazy<IReadOnlyList<AgentDescriptor>> _agents;
    private readonly FileSystemWatcher? _watcher;
    private readonly System.Threading.Timer? _debounceTimer;

    public FilesystemAgentCatalog(string skillsDirectory)
        : this(skillsDirectory, watch: false) { }

    /// <param name="watch">
    /// When true, the catalog installs a <see cref="FileSystemWatcher"/>
    /// on <paramref name="skillsDirectory"/> and auto-invokes
    /// <see cref="Refresh"/> (debounced ~300ms) on any add/change/rename
    /// of a <c>SKILL.md</c>, mode <c>*.md</c>, or <c>orchestrator.json</c>
    /// — so manual edits or external scripts no longer require an app
    /// restart to surface in the UI. Tests pass <c>false</c> to keep
    /// runs deterministic.
    /// </param>
    public FilesystemAgentCatalog(string skillsDirectory, bool watch)
    {
        _skillsDirectory = skillsDirectory;
        _agents = new Lazy<IReadOnlyList<AgentDescriptor>>(LoadAgents);

        if (watch && Directory.Exists(skillsDirectory))
        {
            _debounceTimer = new System.Threading.Timer(
                _ => Refresh(),
                state: null,
                dueTime: Timeout.Infinite,
                period: Timeout.Infinite);

            _watcher = new FileSystemWatcher(skillsDirectory)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName
                             | NotifyFilters.LastWrite
                             | NotifyFilters.Size
                             | NotifyFilters.DirectoryName,
            };
            // Skill catalog only cares about markdown (SKILL.md, mode
            // files) and the orchestrator sidecar. Any other file under
            // the skill folder is noise. Filters is logical-OR.
            _watcher.Filters.Add("*.md");
            _watcher.Filters.Add("*.json");

            _watcher.Created += OnFsEvent;
            _watcher.Changed += OnFsEvent;
            _watcher.Deleted += OnFsEvent;
            _watcher.Renamed += OnFsEvent;
            _watcher.EnableRaisingEvents = true;
        }
    }

    public IReadOnlyList<AgentDescriptor> GetAll() => _agents.Value;

    public AgentDescriptor? FindById(string id) =>
        _agents.Value.FirstOrDefault(a => a.Id == id);

    public void Refresh()
    {
        // Replace the Lazy wholesale — cheaper than a lock + clear, and
        // any in-flight call still sees its old snapshot rather than an
        // empty list.
        _agents = new Lazy<IReadOnlyList<AgentDescriptor>>(LoadAgents);
    }

    public string? ReadSkillBody(string skillName, string? subcommand = null)
    {
        if (string.IsNullOrWhiteSpace(skillName)) return null;

        var path = Path.Combine(_skillsDirectory, skillName, "SKILL.md");
        if (!File.Exists(path)) return null;

        try { return File.ReadAllText(path); }
        catch { return null; /* unreadable file == treat as absent */ }
    }

    private void OnFsEvent(object? sender, FileSystemEventArgs e)
    {
        // Editors typically save in bursts (tmp write + rename + truncate
        // + final flush) producing 3â€“5 events. Reset the debounce so we
        // re-scan exactly once after the dust settles.
        _debounceTimer?.Change(WatchDebounce, Timeout.InfiniteTimeSpan);
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _debounceTimer?.Dispose();
    }

    private IReadOnlyList<AgentDescriptor> LoadAgents()
    {
        if (!Directory.Exists(_skillsDirectory))
        {
            return Array.Empty<AgentDescriptor>();
        }

        var agents = new List<AgentDescriptor>();
        foreach (var dir in Directory.EnumerateDirectories(_skillsDirectory))
        {
            var skillFile = Path.Combine(dir, "SKILL.md");
            if (!File.Exists(skillFile)) continue;

            // Opt-in policy: only skills that ship an orchestrator.json sidecar
            // are exposed in the UI. Skills without one stay invisible to the
            // orchestrator (avoids surfacing SDD, personal, or other internal
            // skills the user never wants to run from this control plane).
            var sidecarFile = Path.Combine(dir, "orchestrator.json");
            if (!File.Exists(sidecarFile)) continue;

            var agent = TryLoadAgent(dir, skillFile);
            if (agent is not null)
            {
                agents.Add(agent);
            }
        }
        return agents
            .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static AgentDescriptor? TryLoadAgent(string skillDir, string skillFile)
    {
        var content = File.ReadAllText(skillFile);
        var frontmatter = ParseFrontmatter(content);
        var skillName = frontmatter.GetValueOrDefault("name") ?? Path.GetFileName(skillDir);
        var description = frontmatter.GetValueOrDefault("description") ?? string.Empty;

        if (string.IsNullOrWhiteSpace(skillName))
        {
            return null;
        }

        var sidecar = LoadSidecar(skillDir);
        var modes = DiscoverModes(skillDir, skillName, sidecar);

        var displayName = !string.IsNullOrWhiteSpace(sidecar?.DisplayName)
            ? sidecar!.DisplayName!
            : ToDisplayName(skillName);

        // Preserve the empty-vs-unset distinction: an explicit `"requirements": []`
        // in the sidecar means "self-contained, deliberately nothing" and the UI
        // shows a reassuring note. A missing key leaves Requirements null.
        var requirements = sidecar?.Requirements?
            .Where(r => !string.IsNullOrWhiteSpace(r.Name))
            .Select(r => new AgentRequirement(
                Kind: r.Kind,
                Name: r.Name!.Trim(),
                Required: r.Required,
                Purpose: r.Purpose?.Trim() ?? string.Empty,
                SearchPaths: r.SearchPaths is { Count: > 0 } sp
                    ? sp.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim()).ToList()
                    : null))
            .ToList();

        AgentCategoryPalette.TryParse(sidecar?.Category, out var category, out var customLabel);

        return new AgentDescriptor(
            Id: skillName,
            Name: displayName,
            Description: description,
            SkillName: skillName,
            Category: category,
            Modes: modes.Count == 0 ? null : modes,
            Requirements: requirements,
            CustomCategory: customLabel,
            Icon: string.IsNullOrWhiteSpace(sidecar?.Icon) ? null : sidecar.Icon.Trim());
    }

    private static IReadOnlyList<AgentMode> DiscoverModes(string skillDir, string skillName, OrchestratorSidecar? sidecar)
    {
        var modes = new List<AgentMode>();
        var coveredSubcommands = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in Directory.EnumerateFiles(skillDir, "*.md"))
        {
            var fileName = Path.GetFileName(file);
            if (string.Equals(fileName, "SKILL.md", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var content = File.ReadAllText(file);
            var (subcommand, modeDescription) = TryParseSubcommandHeader(content, skillName);
            if (subcommand is null)
            {
                continue;
            }

            coveredSubcommands.Add(subcommand);

            ModeOverride? modeOverride = null;
            sidecar?.Modes?.TryGetValue(subcommand, out modeOverride);

            // Sidecar can explicitly hide a discovered subcommand from the
            // orchestrator UI (e.g. read-only listings, redundant one-shots).
            if (modeOverride?.Hidden == true) continue;

            var fields = modeOverride?.Fields;
            var template = !string.IsNullOrWhiteSpace(modeOverride?.ArgumentsTemplate)
                ? modeOverride!.ArgumentsTemplate!
                : BuildTemplate(subcommand, fields);

            var description = !string.IsNullOrWhiteSpace(modeOverride?.Description)
                ? modeOverride!.Description!
                : modeDescription ?? string.Empty;

            modes.Add(new AgentMode(
                Id: subcommand,
                Name: ToDisplayName(subcommand),
                Description: description,
                ArgumentsTemplate: template,
                Fields: fields,
                KeepSessionAlive: modeOverride?.KeepSessionAlive ?? false,
                RequiresWorkspace: modeOverride?.RequiresWorkspace ?? false));
        }

        // Synthetic modes — declared only in the sidecar, no matching .md
        // file. Used for orchestrator-level operations that compose existing
        // skills (e.g. "/loop 5m /sample-watch check" as a "Run" mode that
        // boots the watcher loop without registering a new change).
        if (sidecar?.Modes is { } sidecarModes)
        {
            foreach (var (modeId, modeOverride) in sidecarModes)
            {
                if (modeOverride.Hidden) continue;
                if (coveredSubcommands.Contains(modeId)) continue;
                if (string.IsNullOrWhiteSpace(modeOverride.ArgumentsTemplate)) continue;

                modes.Add(new AgentMode(
                    Id: modeId,
                    Name: ToDisplayName(modeId),
                    Description: modeOverride.Description ?? string.Empty,
                    ArgumentsTemplate: modeOverride.ArgumentsTemplate!,
                    Fields: modeOverride.Fields,
                    KeepSessionAlive: modeOverride.KeepSessionAlive,
                    RequiresWorkspace: modeOverride.RequiresWorkspace));
            }
        }

        return modes
            .OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string BuildTemplate(string subcommand, IReadOnlyList<AgentModeField>? fields)
    {
        if (fields is null || fields.Count == 0)
        {
            return subcommand;
        }
        var placeholders = string.Join(" ", fields.Select(f => "{" + f.Id + "}"));
        return $"{subcommand} {placeholders}";
    }

    private static readonly Regex SubcommandHeaderRegex = new(
        @"^#\s+`/(?<skill>[\w][-\w]*)\s+(?<sub>[\w][-\w]*)`\s*(?:[—\-]\s*(?<desc>.+))?$",
        RegexOptions.Multiline | RegexOptions.Compiled);

    private static (string? Subcommand, string? Description) TryParseSubcommandHeader(string content, string skillName)
    {
        foreach (Match match in SubcommandHeaderRegex.Matches(content))
        {
            if (!string.Equals(match.Groups["skill"].Value, skillName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var subcommand = match.Groups["sub"].Value;
            var description = match.Groups["desc"].Success ? match.Groups["desc"].Value.Trim() : null;
            return (subcommand, description);
        }
        return (null, null);
    }

    private static IReadOnlyDictionary<string, string> ParseFrontmatter(string content)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Frontmatter is the leading "---\n...\n---" block.
        if (!content.StartsWith("---", StringComparison.Ordinal))
        {
            return result;
        }

        var afterOpening = content.AsSpan(3);
        var closingIndex = afterOpening.IndexOf("\n---");
        if (closingIndex < 0)
        {
            return result;
        }

        var body = afterOpening.Slice(0, closingIndex).ToString();
        foreach (var raw in body.Split('\n'))
        {
            var line = raw.Trim();
            if (string.IsNullOrEmpty(line) || line.StartsWith('#'))
            {
                continue;
            }
            var colon = line.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }
            var key = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            // Strip surrounding quotes if any.
            if (value.Length >= 2 && (value[0] == '"' || value[0] == '\'') && value[^1] == value[0])
            {
                value = value[1..^1];
            }
            result[key] = value;
        }
        return result;
    }

    private static OrchestratorSidecar? LoadSidecar(string skillDir)
    {
        var path = Path.Combine(skillDir, "orchestrator.json");
        if (!File.Exists(path))
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize<OrchestratorSidecar>(
                File.ReadAllText(path),
                new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
                });
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string ToDisplayName(string identifier)
    {
        // Default rendering of a kebab-case skill id: title-case each word.
        // Acronyms (change, ID, API, etc.) should be supplied via the sidecar's
        // "displayName" field rather than guessed by the algorithm.
        return string.Join(" ", identifier.Split('-', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Length == 0
                ? string.Empty
                : char.ToUpperInvariant(part[0]) + part[1..]));
    }

    public sealed class OrchestratorSidecar
    {
        public string? DisplayName { get; set; }
        public string? Icon { get; set; }

        /// <summary>
        /// Free-form category label. Built-in names (matching
        /// <see cref="AgentCategory"/> case-insensitively) parse to the
        /// typed enum value; anything else (e.g. <c>"Marketing"</c>) is
        /// surfaced as a custom category on the descriptor. Stored as
        /// string for round-trip preservation across enum-name drift.
        /// </summary>
        public string? Category { get; set; }
        public Dictionary<string, ModeOverride>? Modes { get; set; }

        /// <summary>
        /// Prerequisites the orchestrator surfaces on the agent detail page —
        /// MCP servers, env vars, external tools the user needs before Run is
        /// useful. Empty/absent means the agent stands alone (e.g. library).
        /// </summary>
        public List<RequirementEntry>? Requirements { get; set; }
    }

    public sealed class RequirementEntry
    {
        public AgentRequirementKind Kind { get; set; }
        public string? Name { get; set; }
        public bool Required { get; set; } = true;
        public string? Purpose { get; set; }

        /// <summary>
        /// For env-var requirements: dotenv files to fall back to when the
        /// process environment doesn't already define <see cref="Name"/>.
        /// Mirrors the resolution helper scripts already do (env first,
        /// then a skill-specific .env), so the UI stays consistent.
        /// </summary>
        public List<string>? SearchPaths { get; set; }
    }

    public sealed class ModeOverride
    {
        public List<AgentModeField>? Fields { get; set; }

        /// <summary>
        /// Optional override for the mode's arguments template. When set,
        /// replaces the auto-built template entirely. Templates that start
        /// with <c>/</c> are treated as full slash commands (the runner
        /// will not prefix them with <c>/&lt;skill&gt;</c>) — useful for
        /// synthetic modes that invoke a different skill, e.g.
        /// <c>"/loop 5m /sample-watch check"</c>.
        /// </summary>
        public string? ArgumentsTemplate { get; set; }

        /// <summary>
        /// When true, the runner spawns Claude in stream-json input/output
        /// mode and keeps stdin open after sending the initial command, so
        /// session-scoped crons (created via <c>/loop</c>) continue firing
        /// inside the long-running subprocess. The session terminates only
        /// when the user cancels the run or DoxieOS exits.
        /// </summary>
        public bool KeepSessionAlive { get; set; }

        /// <summary>
        /// When true, the mode is omitted from the catalog. Use to suppress
        /// subcommand modes that don't make sense in the orchestrator UI
        /// (e.g. read-only or one-off polls already covered by another mode).
        /// </summary>
        public bool Hidden { get; set; }

        /// <summary>
        /// Description shown in the UI. Required for synthetic modes (those
        /// declared only in the sidecar with no matching subcommand .md
        /// file); optional for modes whose description comes from the .md H1.
        /// </summary>
        public string? Description { get; set; }

        /// <summary>
        /// When true, this mode must be dispatched at a user-owned
        /// Workspace — the AgentDetail UI marks the Workspace dropdown
        /// required and blocks Run until one is picked. Used by agents
        /// that read the workspace's CLAUDE.md / mounted libraries
        /// and/or write their output back into the workspace.
        /// </summary>
        public bool RequiresWorkspace { get; set; }
    }
}
