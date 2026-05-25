using Viamus.Doxie.Orchestrator.Common;

namespace Viamus.Doxie.Orchestrator.Agents.Doxie;

/// <summary>
/// Reads library canons from <c>.doxie/libraries/&lt;id&gt;/</c>. Each
/// subdirectory is one library; every file under it is carried through as
/// opaque bytes for the shim emitter to mirror 1:1. Library contents
/// (<c>library.json</c>, memory <c>.md</c> files) aren't parsed here —
/// <see cref="FilesystemLibraryStore"/> still owns interpretation.
/// </summary>
public sealed class DoxieLibraryReader
{
    private readonly string _doxieRoot;
    private readonly Func<IReadOnlyList<DoxieCatalogRoot>>? _catalogRootsProvider;

    public DoxieLibraryReader(string doxieRoot, Func<IReadOnlyList<DoxieCatalogRoot>>? catalogRootsProvider = null)
    {
        _doxieRoot = doxieRoot ?? throw new ArgumentNullException(nameof(doxieRoot));
        _catalogRootsProvider = catalogRootsProvider;
    }

    public IReadOnlyList<DoxieLibraryCanon> LoadAll()
    {
        var libraries = new Dictionary<string, DoxieLibraryCanon>(StringComparer.Ordinal);
        foreach (var librariesDir in LibraryRoots())
        {
            if (!Directory.Exists(librariesDir)) continue;

            foreach (var dir in Directory.EnumerateDirectories(librariesDir).OrderBy(p => p, StringComparer.Ordinal))
            {
                var id = Path.GetFileName(dir);
                var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
                foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                {
                    var rel = Path.GetRelativePath(dir, file).Replace('\\', '/');
                    files[rel] = File.ReadAllBytes(file);
                }
                libraries[id] = new DoxieLibraryCanon(id, files);
            }
        }
        return libraries.Values.OrderBy(l => l.Id, StringComparer.Ordinal).ToList();
    }

    private IEnumerable<string> LibraryRoots()
    {
        if (_catalogRootsProvider is not null)
        {
            foreach (var root in _catalogRootsProvider())
            {
                yield return root.LibrariesDirectory;
            }
            yield break;
        }

        yield return Path.Combine(_doxieRoot, "libraries");
    }
}
