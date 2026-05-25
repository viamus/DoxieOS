namespace Viamus.Doxie.Orchestrator.Agents.Doxie;

/// <summary>
/// Emits libraries under <c>.claude/libraries/&lt;id&gt;/</c> as byte-for-byte
/// copies of <c>.doxie/libraries/&lt;id&gt;/</c>. Libraries are not
/// provider-specific — DoxieOS reads <c>.doxie/libraries/</c> directly,
/// and this shim exists for Claude Code compatibility plus legacy tooling
/// that still expects the old folder.
///
/// Managed-set tracking: a <c>.doxie-managed</c> marker file is dropped at
/// the libraries root listing every id we own; libraries no longer in the
/// canon get their folders deleted. Hand-authored library folders not in
/// the manifest are left untouched.
/// </summary>
public sealed class ClaudeLibraryEmitter : IShimEmitter
{
    private const string ManagedManifestFile = ".doxie-managed";

    public string ShimRoot => ".claude/libraries";

    public void Emit(DoxieRegistry registry, string workspaceRoot)
    {
        var librariesRoot = Path.Combine(workspaceRoot, ".claude", "libraries");
        Directory.CreateDirectory(librariesRoot);

        var managedNow = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var library in registry.Libraries)
        {
            EmitOne(library, librariesRoot);
            managedNow.Add(library.Id);
        }

        // Orphan cleanup keyed off the managed manifest.
        var managedPath = Path.Combine(librariesRoot, ManagedManifestFile);
        if (File.Exists(managedPath))
        {
            foreach (var prevId in File.ReadAllLines(managedPath))
            {
                var trimmed = prevId.Trim();
                if (string.IsNullOrEmpty(trimmed)) continue;
                if (managedNow.Contains(trimmed)) continue;
                var orphanDir = Path.Combine(librariesRoot, trimmed);
                if (Directory.Exists(orphanDir)) Directory.Delete(orphanDir, recursive: true);
            }
        }

        var manifestContent = string.Join('\n', managedNow.OrderBy(id => id, StringComparer.Ordinal)) + "\n";
        WriteIfChanged(managedPath, System.Text.Encoding.UTF8.GetBytes(manifestContent));
    }

    private static void EmitOne(DoxieLibraryCanon library, string librariesRoot)
    {
        var dir = Path.Combine(librariesRoot, library.Id);
        Directory.CreateDirectory(dir);

        var expectedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (relPath, content) in library.Files)
        {
            var fullPath = Path.Combine(dir, relPath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            WriteIfChanged(fullPath, content);
            expectedFiles.Add(NormalizeRel(relPath));
        }

        // Sweep files that vanished from the canon.
        foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            var rel = NormalizeRel(Path.GetRelativePath(dir, file));
            if (expectedFiles.Contains(rel)) continue;
            File.Delete(file);
        }
        foreach (var subDir in Directory.EnumerateDirectories(dir, "*", SearchOption.AllDirectories)
            .OrderByDescending(d => d.Length))
        {
            if (!Directory.EnumerateFileSystemEntries(subDir).Any())
            {
                Directory.Delete(subDir);
            }
        }
    }

    private static string NormalizeRel(string rel) => rel.Replace('\\', '/');

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
