using System.Text.Json;
using Viamus.Doxie.Orchestrator.Common;

namespace Viamus.Doxie.Orchestrator.Context;

/// <summary>
/// Discovers libraries by walking the configured libraries directory.
/// Each subdirectory is one library; memory files can live either at the
/// library root or under a <c>memory/</c>/<c>memories/</c> subfolder. Other
/// files are still surfaced as library artifacts so richer packs can expose
/// metadata, indexes, examples, and nested folders in the UI.
/// </summary>
public sealed class FilesystemLibraryStore : ILibraryStore
{
    private const string DefaultCatalogId = "default";
    private static readonly string[] MemorySubdirectories = { "memory", "memories" };

    private readonly string _directory;
    private readonly Func<IReadOnlyList<DoxieCatalogRoot>>? _catalogRootsProvider;

    public FilesystemLibraryStore(string directory, Func<IReadOnlyList<DoxieCatalogRoot>>? catalogRootsProvider = null)
    {
        _directory = directory;
        _catalogRootsProvider = catalogRootsProvider;
    }

    public IReadOnlyList<Library> ListAll()
    {
        var libraries = new Dictionary<string, Library>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in LibraryRoots())
        {
            if (!Directory.Exists(root.LibrariesDirectory)) continue;

            foreach (var libDir in Directory.EnumerateDirectories(root.LibrariesDirectory))
            {
                var library = TryLoad(libDir, root);
                if (library is not null) libraries[library.Id] = library;
            }
        }
        return libraries.Values
            .OrderBy(l => l.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public Library? GetById(string id)
    {
        foreach (var root in LibraryRoots())
        {
            var libDir = Path.Combine(root.LibrariesDirectory, id);
            if (Directory.Exists(libDir)) return TryLoad(libDir, root);
        }

        return null;
    }

    private IEnumerable<DoxieCatalogRoot> LibraryRoots()
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
            DefaultCatalogId,
            "Default catalog",
            Path.GetFullPath(Path.Combine(_directory, "..")),
            IsDefault: true,
            LibrariesPath: _directory);
    }

    private static Library? TryLoad(string libDir, DoxieCatalogRoot catalog)
    {
        var id = Path.GetFileName(libDir);
        if (string.IsNullOrEmpty(id)) return null;

        var manifest = LoadManifest(libDir);
        var memories = LoadMemories(libDir);
        var files = LoadFiles(libDir);

        return new Library(
            Id: id,
            Name: !string.IsNullOrWhiteSpace(manifest?.Name) ? manifest!.Name! : ToDisplayName(id),
            Description: manifest?.Description ?? string.Empty,
            Memories: memories,
            Files: files,
            CatalogId: catalog.Id,
            CatalogName: catalog.Name,
            CatalogRoot: catalog.RootPath);
    }

    private static IReadOnlyList<MemoryEntry> LoadMemories(string libDir)
    {
        var memories = new List<MemoryEntry>();
        foreach (var file in EnumerateMemoryFiles(libDir))
        {
            var entry = TryParseEntry(file);
            if (entry is not null) memories.Add(entry);
        }
        return memories
            .OrderBy(m => m.Type)
            .ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IReadOnlyList<LibraryFileEntry> LoadFiles(string libDir)
    {
        var entries = new List<LibraryFileEntry>();
        WalkLibraryFolder(libDir, libDir, entries);
        return entries;
    }

    private static void WalkLibraryFolder(string root, string folder, List<LibraryFileEntry> entries)
    {
        DirectoryInfo directory;
        try { directory = new DirectoryInfo(folder); }
        catch { return; }

        foreach (var dir in directory.EnumerateDirectories()
            .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
        {
            var rel = Path.GetRelativePath(root, dir.FullName).Replace('\\', '/');
            entries.Add(new LibraryFileEntry(rel, dir.Name, true, null));
            WalkLibraryFolder(root, dir.FullName, entries);
        }

        foreach (var file in directory.EnumerateFiles()
            .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
        {
            var rel = Path.GetRelativePath(root, file.FullName).Replace('\\', '/');
            string? content = null;
            if (string.Equals(file.Extension, ".md", StringComparison.OrdinalIgnoreCase))
            {
                try { content = File.ReadAllText(file.FullName); }
                catch { /* surfaced as null content */ }
            }
            entries.Add(new LibraryFileEntry(rel, file.Name, false, file.Length, content));
        }
    }

    private static IReadOnlyList<string> EnumerateMemoryFiles(string libDir)
    {
        var files = new List<string>();

        files.AddRange(Directory.EnumerateFiles(libDir, "*.md", SearchOption.TopDirectoryOnly));

        foreach (var subdirectory in MemorySubdirectories)
        {
            var memoryDir = Path.Combine(libDir, subdirectory);
            if (Directory.Exists(memoryDir))
            {
                files.AddRange(Directory.EnumerateFiles(memoryDir, "*.md", SearchOption.AllDirectories));
            }
        }

        return files
            .Where(IsMemoryFile)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(f => Path.GetRelativePath(libDir, f).Replace('\\', '/'), StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool IsMemoryFile(string file)
    {
        var fileName = Path.GetFileName(file);
        return !string.Equals(fileName, "MEMORY.md", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(fileName, "README.md", StringComparison.OrdinalIgnoreCase);
    }

    private static MemoryEntry? TryParseEntry(string filePath)
    {
        string content;
        try { content = File.ReadAllText(filePath); }
        catch { return null; }

        var (frontmatter, body) = SplitFrontmatter(content);
        var fileName = Path.GetFileName(filePath);
        var name = frontmatter.GetValueOrDefault("name")
            ?? frontmatter.GetValueOrDefault("title")
            ?? fileName;
        var description = frontmatter.GetValueOrDefault("description")
            ?? frontmatter.GetValueOrDefault("summary")
            ?? string.Empty;
        var type = ParseType(frontmatter.GetValueOrDefault("type"));

        return new MemoryEntry(
            FileName: fileName,
            Name: name,
            Description: description,
            Type: type,
            Content: body.TrimStart('\n', '\r'));
    }

    private static LibraryManifest? LoadManifest(string libDir)
    {
        var path = Path.Combine(libDir, "library.json");
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<LibraryManifest>(
                File.ReadAllText(path),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static (IReadOnlyDictionary<string, string> Frontmatter, string Body) SplitFrontmatter(string content)
    {
        var empty = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!content.StartsWith("---", StringComparison.Ordinal))
        {
            return (empty, content);
        }

        var afterOpening = content.AsSpan(3);
        var closingIndex = afterOpening.IndexOf("\n---");
        if (closingIndex < 0)
        {
            return (empty, content);
        }

        var fmText = afterOpening.Slice(0, closingIndex).ToString();
        var bodyStart = 3 + closingIndex + "\n---".Length;
        var body = bodyStart < content.Length ? content[bodyStart..] : string.Empty;

        var fm = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in fmText.Split('\n'))
        {
            var line = raw.Trim();
            if (string.IsNullOrEmpty(line) || line.StartsWith('#')) continue;
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var key = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            if (value.Length >= 2 && (value[0] == '"' || value[0] == '\'') && value[^1] == value[0])
            {
                value = value[1..^1];
            }
            fm[key] = value;
        }
        return (fm, body);
    }

    private static MemoryType ParseType(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return MemoryType.Unknown;
        return Enum.TryParse<MemoryType>(raw, ignoreCase: true, out var parsed)
            ? parsed
            : MemoryType.Unknown;
    }

    private static string ToDisplayName(string identifier) =>
        string.Join(" ", identifier
            .Split(new[] { '-', '_' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Length == 0 ? string.Empty : char.ToUpperInvariant(p[0]) + p[1..]));

    private sealed class LibraryManifest
    {
        public string? Name { get; set; }
        public string? Description { get; set; }
    }
}
