using System.Text.RegularExpressions;
using Viamus.Doxie.Orchestrator.Common;

namespace Viamus.Doxie.Orchestrator.Agents.Doxie;

/// <summary>
/// Reads the orchestrator's skill catalog directly from the canonical
/// <c>.doxie/skills/</c> layer, bypassing the <c>.claude/skills/</c> shim
/// entirely. This makes <c>.doxie/</c> the single source of truth at runtime,
/// not just on disk: the regen pipeline emits the shim for Claude Code's
/// consumption, but DoxieOS itself never reads it.
///
/// No fallback to the shim path. Skills authored under <c>.claude/skills/</c>
/// without a canonical equivalent stay invisible until promoted into
/// <c>.doxie/</c>. This is deliberate: a fallback would split the source of
/// truth in two.
/// </summary>
public sealed class DoxieAgentCatalog : IAgentCatalog, IDisposable
{
    private static readonly TimeSpan WatchDebounce = TimeSpan.FromMilliseconds(300);

    private readonly string _doxieRoot;
    private readonly DoxieSkillReader _reader;
    private readonly Func<IReadOnlyList<DoxieCatalogRoot>>? _catalogRootsProvider;
    private Lazy<IReadOnlyList<AgentDescriptor>> _agents;
    private readonly FileSystemWatcher? _watcher;
    private readonly System.Threading.Timer? _debounceTimer;

    public DoxieAgentCatalog(string workspaceRoot)
        : this(workspaceRoot, watch: false) { }

    public DoxieAgentCatalog(string workspaceRoot, bool watch, Func<IReadOnlyList<DoxieCatalogRoot>>? catalogRootsProvider = null)
    {
        _doxieRoot = Path.Combine(workspaceRoot, ".doxie");
        _catalogRootsProvider = catalogRootsProvider;
        _reader = new DoxieSkillReader(_doxieRoot, catalogRootsProvider);
        _agents = new Lazy<IReadOnlyList<AgentDescriptor>>(LoadAgents);

        if (watch && Directory.Exists(_doxieRoot))
        {
            _debounceTimer = new System.Threading.Timer(
                _ => Refresh(),
                state: null,
                dueTime: Timeout.Infinite,
                period: Timeout.Infinite);

            _watcher = new FileSystemWatcher(_doxieRoot)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName
                             | NotifyFilters.LastWrite
                             | NotifyFilters.Size
                             | NotifyFilters.DirectoryName,
            };
            _watcher.Filters.Add("manifest.json");
            _watcher.Filters.Add("body.md");
            _watcher.Filters.Add("*.md"); // companion subcommand files

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
        _agents = new Lazy<IReadOnlyList<AgentDescriptor>>(LoadAgents);
    }

    /// <summary>
    /// Returns the canonical <c>body.md</c> content for the named skill.
    /// Used by <see cref="CodexProvider"/> to inline the skill's instructions
    /// into Codex's prompt — Claude Code reads from its own SKILL.md (the
    /// shim) and ignores this method's return value. The body is emitted
    /// verbatim so what Codex sees matches what authors write.
    /// </summary>
    public string? ReadSkillBody(string skillName, string? subcommand = null)
    {
        if (string.IsNullOrWhiteSpace(skillName)) return null;

        // Try the subcommand-specific file first (e.g. from-files.md).
        // It contains concrete step-by-step instructions vs. the routing
        // table in body.md, which is more useful for providers like Codex
        // that need to execute the steps directly rather than re-dispatch.
        if (!string.IsNullOrWhiteSpace(subcommand))
        {
            foreach (var root in SkillRoots())
            {
                var subPath = Path.Combine(root, skillName, $"{subcommand}.md");
                if (File.Exists(subPath))
                    try { return File.ReadAllText(subPath); } catch { /* fall through */ }
            }
        }

        foreach (var root in SkillRoots())
        {
            var path = Path.Combine(root, skillName, "body.md");
            if (!File.Exists(path)) continue;
            try { return File.ReadAllText(path); }
            catch { return null; }
        }
        return null;
    }

    private void OnFsEvent(object? sender, FileSystemEventArgs e)
    {
        _debounceTimer?.Change(WatchDebounce, Timeout.InfiniteTimeSpan);
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _debounceTimer?.Dispose();
    }

    private IReadOnlyList<AgentDescriptor> LoadAgents()
    {
        var canon = _reader.LoadAll();
        return canon
            .Select(MapToDescriptor)
            .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static AgentDescriptor MapToDescriptor(DoxieSkillCanon canon)
    {
        var manifest = canon.Manifest;
        var displayName = !string.IsNullOrWhiteSpace(manifest.DisplayName)
            ? manifest.DisplayName!
            : ToDisplayName(manifest.Id);

        var modes = manifest.Modes is { Count: > 0 }
            ? manifest.Modes
                .Where(kv => !kv.Value.Hidden)
                .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => MapMode(kv.Key, kv.Value))
                .ToList()
            : null;

        var requirements = manifest.Requirements?
            .Where(r => !string.IsNullOrWhiteSpace(r.Name))
            .Select(r => new AgentRequirement(
                Kind: r.Kind,
                Name: r.Name.Trim(),
                Required: r.Required,
                Purpose: r.Purpose?.Trim() ?? string.Empty,
                SearchPaths: r.SearchPaths is { Count: > 0 } sp
                    ? sp.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim()).ToList()
                    : null))
            .ToList();

        // Resolve the raw category string into the typed (enum, custom)
        // pair the descriptor exposes. A null/blank manifest field maps
        // to AgentCategory.Other with no custom label; an unrecognised
        // but well-formed string becomes Other + the literal label.
        AgentCategoryPalette.TryParse(manifest.Category, out var category, out var customLabel);

        return new AgentDescriptor(
            Id: manifest.Id,
            Name: displayName,
            Description: manifest.Description,
            SkillName: manifest.Id,
            Category: category,
            Modes: modes is { Count: > 0 } ? modes : null,
            Requirements: requirements,
            CustomCategory: customLabel,
            Icon: string.IsNullOrWhiteSpace(manifest.Icon) ? null : manifest.Icon.Trim(),
            IsPrivate: false,
            CatalogId: canon.CatalogId,
            CatalogName: canon.CatalogName,
            CatalogRoot: canon.CatalogRoot);
    }

    private IEnumerable<string> SkillRoots()
    {
        if (_catalogRootsProvider is not null)
        {
            foreach (var root in _catalogRootsProvider())
            {
                yield return root.SkillsDirectory;
            }
            yield break;
        }

        yield return Path.Combine(_doxieRoot, "skills");
    }

    private static AgentMode MapMode(string id, DoxieMode mode)
    {
        var fields = mode.Fields?.Select(f => new AgentModeField(
            Id: f.Id,
            Label: f.Label ?? f.Id,
            Placeholder: f.Placeholder ?? string.Empty,
            Required: f.Required)).ToList();

        var template = !string.IsNullOrWhiteSpace(mode.ArgumentsTemplate)
            ? mode.ArgumentsTemplate!
            : BuildTemplate(id, fields);

        return new AgentMode(
            Id: id,
            Name: ToDisplayName(id),
            Description: mode.Description ?? string.Empty,
            ArgumentsTemplate: template,
            Fields: fields,
            KeepSessionAlive: mode.KeepSessionAlive,
            RequiresWorkspace: mode.RequiresWorkspace);
    }

    private static string BuildTemplate(string subcommand, IReadOnlyList<AgentModeField>? fields)
    {
        if (fields is null || fields.Count == 0) return subcommand;
        var placeholders = string.Join(" ", fields.Select(f => "{" + f.Id + "}"));
        return $"{subcommand} {placeholders}";
    }

    private static string ToDisplayName(string identifier) =>
        string.Join(" ", identifier.Split('-', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Length == 0
                ? string.Empty
                : char.ToUpperInvariant(part[0]) + part[1..]));
}
