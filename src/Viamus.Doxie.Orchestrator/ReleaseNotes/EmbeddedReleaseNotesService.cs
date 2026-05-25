using System.Reflection;
using System.Text.RegularExpressions;

namespace Viamus.Doxie.Orchestrator.ReleaseNotes;

/// <summary>
/// Loads release notes from markdown files embedded in the assembly under
/// <c>docs/release-notes/v*.md</c>. Each file has YAML frontmatter with
/// <c>title</c>, <c>date</c>, <c>highlights</c>, and optional <c>lottie</c>
/// (path to a companion JSON served from <c>wwwroot/release-notes/</c>).
///
/// Loaded once at construction and cached — release notes are deployment
/// artifacts, never change at runtime.
/// </summary>
public sealed class EmbeddedReleaseNotesService : IReleaseNotesService
{
    private readonly IReadOnlyList<ReleaseNote> _notes;

    private static readonly Regex VersionFromName = new(
        // Accepts plain semver (1.2.3) and pre-release tags (1.0.0-alpha,
        // 1.0.0-beta.2, 1.0.0-rc.1). Build metadata (+sha…) is rejected on
        // purpose — it doesn't change the user-facing version label.
        @"v(?<v>\d+\.\d+\.\d+(?:-[a-zA-Z0-9.]+)?)\.md$",
        RegexOptions.Compiled);

    public EmbeddedReleaseNotesService()
    {
        _notes = LoadAll().OrderByDescending(n => Parse(n.Version), VersionComparer).ToList();
    }

    public IReadOnlyList<ReleaseNote> ListAll() => _notes;

    public ReleaseNote? Latest() => _notes.FirstOrDefault();

    public IReadOnlyList<ReleaseNote> Unseen(string? lastSeenVersion)
    {
        if (string.IsNullOrWhiteSpace(lastSeenVersion))
        {
            // First-ever boot: surface only the latest, not the entire backlog —
            // a fresh user shouldn't be hit with N pages of historical changes.
            return _notes.Take(1).ToList();
        }
        var seen = Parse(lastSeenVersion);
        return _notes.Where(n => VersionComparer.Compare(Parse(n.Version), seen) > 0).ToList();
    }

    private static IEnumerable<ReleaseNote> LoadAll()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var prefix = assembly.GetName().Name + ".docs.release_notes.";
        foreach (var resource in assembly.GetManifestResourceNames())
        {
            // Embedded resources flatten path separators into '.' — accept both
            // "release-notes" (renames to release_notes via MSBuild rule below)
            // and the literal form so this works regardless of how the project
            // configures resource naming.
            if (!resource.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) continue;
            if (!resource.Contains("release", StringComparison.OrdinalIgnoreCase)) continue;

            var match = VersionFromName.Match(resource);
            if (!match.Success)
            {
                // Filename doesn't match the v<semver>.md convention — skip.
                continue;
            }

            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream);
            var content = reader.ReadToEnd();
            yield return Parse(match.Groups["v"].Value, content);
        }
    }

    private static ReleaseNote Parse(string version, string content)
    {
        var (frontmatter, body) = SplitFrontmatter(content);
        var fields = ParseFrontmatter(frontmatter ?? string.Empty);

        var title = fields.GetValueOrDefault("title") ?? $"v{version}";
        var dateStr = fields.GetValueOrDefault("date");
        var date = DateOnly.TryParse(dateStr, out var parsed) ? parsed : DateOnly.FromDateTime(DateTime.UtcNow);
        var highlights = fields.TryGetValue("highlights", out var hl)
            ? hl.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList()
            : new List<string>();
        var lottie = fields.GetValueOrDefault("lottie");

        return new ReleaseNote(version, title, date, highlights, body, lottie);
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

    private static (int Major, int Minor, int Patch, string PreRelease) Parse(string version)
    {
        // Split off pre-release tag (everything after first '-'). SemVer rule:
        // a pre-release version is LOWER than the same triple without a tag,
        // so "1.0.0-alpha" < "1.0.0". Empty pre-release ranks higher.
        var dashIdx = version.IndexOf('-');
        var core = dashIdx < 0 ? version : version[..dashIdx];
        var pre = dashIdx < 0 ? string.Empty : version[(dashIdx + 1)..];

        var parts = core.Split('.');
        int.TryParse(parts.ElementAtOrDefault(0), out var major);
        int.TryParse(parts.ElementAtOrDefault(1), out var minor);
        int.TryParse(parts.ElementAtOrDefault(2), out var patch);
        return (major, minor, patch, pre);
    }

    private static readonly Comparer<(int Major, int Minor, int Patch, string PreRelease)> VersionComparer =
        Comparer<(int, int, int, string)>.Create((a, b) =>
        {
            var c = a.Item1.CompareTo(b.Item1); if (c != 0) return c;
            c = a.Item2.CompareTo(b.Item2); if (c != 0) return c;
            c = a.Item3.CompareTo(b.Item3); if (c != 0) return c;
            // Pre-release present is LOWER than absent. Otherwise lexical.
            if (string.IsNullOrEmpty(a.Item4) && !string.IsNullOrEmpty(b.Item4)) return 1;
            if (!string.IsNullOrEmpty(a.Item4) && string.IsNullOrEmpty(b.Item4)) return -1;
            return string.CompareOrdinal(a.Item4, b.Item4);
        });
}
