using System.Text;

namespace Viamus.Doxie.Orchestrator.Context;

/// <summary>
/// Helpers used by the in-browser workspace file browser endpoints
/// (<c>/api/workspaces/{id}/tree</c>, <c>/file</c>, <c>/download</c>).
/// Lives in its own class so <c>Program.cs</c> stays focused on routing.
/// </summary>
public static class WorkspaceFileBrowser
{
    /// <summary>Folder names hidden by default in the tree view.</summary>
    public static readonly IReadOnlySet<string> DefaultHiddenFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".hg", ".svn", "node_modules", "bin", "obj", ".vs", ".idea", "__pycache__", ".pytest_cache",
        ".gradle", ".mvn", "target", "dist", ".next", ".nuxt", ".cache", ".venv", "venv", "env",
    };

    /// <summary>Hard cap on the number of children returned for a single folder listing.</summary>
    public const int MaxEntriesPerFolder = 5000;

    /// <summary>Hard refusal threshold — any file larger than this is never streamed as text.</summary>
    public const long HardSizeCapBytes = 10L * 1024 * 1024;

    /// <summary>Files between this and <see cref="HardSizeCapBytes"/> get the first 1000 lines only.</summary>
    public const long SoftTruncateThresholdBytes = 1L * 1024 * 1024;

    public const int TruncatedLineCount = 1000;

    /// <summary>
    /// Resolves <paramref name="relativePath"/> against the workspace root,
    /// guards against path-traversal escapes (<c>..</c>, absolute paths,
    /// symlinks pointing outside the root), and returns either the absolute
    /// canonical path or a structured error suitable for returning from a
    /// minimal-API endpoint.
    /// </summary>
    public static (string? Resolved, string? Error) ResolveSafePath(string workspaceRoot, string? relativePath)
    {
        if (string.IsNullOrEmpty(workspaceRoot))
        {
            return (null, "Workspace root is empty.");
        }

        var rootCanonical = Path.GetFullPath(workspaceRoot);
        // Normalize the root with a trailing separator so a substring check
        // can't match siblings of the root by accident — e.g. /home/u/ws
        // accidentally matching /home/u/ws-backup/secrets.
        var rootWithSep = rootCanonical.EndsWith(Path.DirectorySeparatorChar)
            ? rootCanonical
            : rootCanonical + Path.DirectorySeparatorChar;

        var rel = relativePath ?? string.Empty;
        // Reject anything that's syntactically an absolute path: on Windows
        // C:\foo or \\server\share, on POSIX /etc. The only safe inputs are
        // workspace-relative.
        if (Path.IsPathRooted(rel))
        {
            return (null, "Path must be relative to the workspace root.");
        }

        // Path.GetFullPath collapses ../ segments deterministically; combined
        // with the StartsWith guard below, dotdot escapes can't reach outside
        // the root.
        string combined;
        try
        {
            combined = Path.GetFullPath(Path.Combine(rootCanonical, rel));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return (null, "Invalid path.");
        }

        // Resolve symlinks: if the leaf or any directory along the path is
        // a symlink pointing outside the root, GetFullPath alone can't see
        // it. ResolveLinkTarget(returnFinalTarget: true) walks the chain.
        try
        {
            var info = File.Exists(combined) ? (FileSystemInfo)new FileInfo(combined)
                     : Directory.Exists(combined) ? new DirectoryInfo(combined)
                     : null;
            if (info is not null)
            {
                var target = info.ResolveLinkTarget(returnFinalTarget: true);
                if (target is not null)
                {
                    combined = Path.GetFullPath(target.FullName);
                }
            }
        }
        catch
        {
            // ResolveLinkTarget on broken or unsupported FS — fall through
            // to the StartsWith guard. Worst case we let a non-symlink path
            // through; the guard still rejects out-of-root.
        }

        var combinedWithSep = combined.EndsWith(Path.DirectorySeparatorChar)
            ? combined
            : combined + Path.DirectorySeparatorChar;

        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (!(combined.Equals(rootCanonical, comparison)
              || combinedWithSep.StartsWith(rootWithSep, comparison)))
        {
            return (null, "Path escapes the workspace root.");
        }

        return (combined, null);
    }

    /// <summary>
    /// Cheap binary-content sniff: read the first N bytes and look for a
    /// NUL byte. Catches almost all real-world binaries (executables,
    /// images, archives) without false-positives on UTF-8 text. Combined
    /// with an extension blocklist for explicit cases.
    /// </summary>
    public static bool IsBinary(string absolutePath, int sniffBytes = 8 * 1024)
    {
        var ext = Path.GetExtension(absolutePath).ToLowerInvariant();
        if (BinaryExtensions.Contains(ext)) return true;

        try
        {
            using var fs = File.OpenRead(absolutePath);
            Span<byte> buf = stackalloc byte[Math.Min(sniffBytes, 8 * 1024)];
            int read = fs.Read(buf);
            for (int i = 0; i < read; i++)
            {
                if (buf[i] == 0) return true;
            }
            return false;
        }
        catch
        {
            return false; // unreadable — let downstream surface the IO error
        }
    }

    private static readonly HashSet<string> BinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".dll", ".so", ".dylib", ".a", ".lib", ".o",
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".ico", ".tif", ".tiff",
        ".pdf", ".zip", ".tar", ".gz", ".bz2", ".7z", ".rar", ".xz",
        ".class", ".jar", ".war", ".wasm",
        ".mp3", ".mp4", ".mov", ".avi", ".mkv", ".flac", ".ogg", ".wav",
        ".bin", ".dat", ".db", ".sqlite", ".sqlite3",
        ".woff", ".woff2", ".ttf", ".otf", ".eot",
        ".psd", ".ai", ".sketch", ".fig",
    };

    /// <summary>
    /// Maps a file extension to a highlight.js language hint. Returning
    /// <c>null</c> means "let highlight.js auto-detect"; returning a
    /// specific language pins it (more accurate for ambiguous content
    /// like <c>.h</c> vs C/C++).
    /// </summary>
    public static string? LanguageForExtension(string absolutePath)
    {
        var ext = Path.GetExtension(absolutePath).ToLowerInvariant().TrimStart('.');
        return ext switch
        {
            "cs" => "csharp",
            "ts" or "tsx" => "typescript",
            "js" or "jsx" or "mjs" or "cjs" => "javascript",
            "py" => "python",
            "rs" => "rust",
            "go" => "go",
            "java" => "java",
            "kt" or "kts" => "kotlin",
            "rb" => "ruby",
            "php" => "php",
            "sh" or "bash" or "zsh" => "bash",
            "ps1" or "psm1" or "psd1" => "powershell",
            "yml" or "yaml" => "yaml",
            "json" or "jsonc" => "json",
            "xml" or "xaml" or "csproj" or "props" or "targets" or "config" or "axaml" => "xml",
            "html" or "htm" or "xhtml" => "html",
            "css" => "css",
            "scss" or "sass" => "scss",
            "razor" or "cshtml" => "cshtml",
            "sql" => "sql",
            "toml" => "toml",
            "ini" or "env" or "properties" => "ini",
            "dockerfile" => "dockerfile",
            "makefile" or "mk" => "makefile",
            "lua" => "lua",
            "swift" => "swift",
            "c" or "h" => "c",
            "cpp" or "cc" or "cxx" or "hpp" or "hxx" => "cpp",
            "" => Path.GetFileName(absolutePath).Equals("Dockerfile", StringComparison.OrdinalIgnoreCase)
                ? "dockerfile"
                : Path.GetFileName(absolutePath).Equals("Makefile", StringComparison.OrdinalIgnoreCase)
                    ? "makefile"
                    : null,
            _ => null,
        };
    }

    /// <summary>"markdown" | "code" | "text" — based on extension only.</summary>
    public static string KindForExtension(string absolutePath)
    {
        var ext = Path.GetExtension(absolutePath).ToLowerInvariant();
        if (ext is ".md" or ".markdown" or ".mdx") return "markdown";
        return LanguageForExtension(absolutePath) is not null ? "code" : "text";
    }

    /// <summary>Reads the first <paramref name="lineCount"/> lines of a file as UTF-8.</summary>
    public static string ReadFirstNLines(string path, int lineCount)
    {
        var sb = new StringBuilder();
        using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        for (int i = 0; i < lineCount; i++)
        {
            var line = reader.ReadLine();
            if (line is null) break;
            sb.AppendLine(line);
        }
        return sb.ToString();
    }

    public sealed record TreeEntry(string Name, bool IsDir, long? SizeBytes);
    public sealed record TreeListing(IReadOnlyList<TreeEntry> Entries, bool Truncated);
    public sealed record FileContent(string Name, string Kind, string? Language, long SizeBytes, string? Content, bool Truncated);

    /// <summary>
    /// Lists the immediate children of <paramref name="relativePath"/>
    /// inside <paramref name="workspaceRoot"/>. Returns <c>null</c> error
    /// on success, or a non-null error message on failure (invalid path,
    /// access denied, etc.).
    /// </summary>
    public static (TreeListing? Listing, string? Error) ListFolder(string workspaceRoot, string? relativePath, bool showHidden)
    {
        var (resolved, error) = ResolveSafePath(workspaceRoot, relativePath);
        if (error is not null || resolved is null) return (null, error ?? "Invalid path");
        if (!Directory.Exists(resolved)) return (null, "Folder not found");

        var hide = !showHidden;
        var entries = new List<TreeEntry>(capacity: 256);
        var truncated = false;
        try
        {
            foreach (var info in new DirectoryInfo(resolved).EnumerateFileSystemInfos()
                .OrderBy(i => (i.Attributes & FileAttributes.Directory) == 0)
                .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (hide && info.Name.StartsWith('.')) continue;
                var isDir = (info.Attributes & FileAttributes.Directory) != 0;
                if (hide && isDir && DefaultHiddenFolders.Contains(info.Name)) continue;

                if (entries.Count >= MaxEntriesPerFolder) { truncated = true; break; }
                entries.Add(new TreeEntry(info.Name, isDir, isDir ? null : ((FileInfo)info).Length));
            }
        }
        catch (UnauthorizedAccessException)
        {
            return (null, "Access denied");
        }
        return (new TreeListing(entries, truncated), null);
    }

    /// <summary>
    /// Reads a single file's content for display, applying the binary
    /// detection + 1MB soft truncate + 10MB hard cap policies.
    /// </summary>
    public static (FileContent? Content, string? Error) ReadFile(string workspaceRoot, string? relativePath)
    {
        var (resolved, error) = ResolveSafePath(workspaceRoot, relativePath);
        if (error is not null || resolved is null) return (null, error ?? "Invalid path");
        if (!File.Exists(resolved)) return (null, "File not found");

        var fi = new FileInfo(resolved);
        var name = fi.Name;
        var size = fi.Length;
        var language = LanguageForExtension(resolved);

        if (IsBinary(resolved))
        {
            return (new FileContent(name, "binary", null, size, null, false), null);
        }

        if (size > HardSizeCapBytes)
        {
            // Beyond the cap we render the same "binary card" — user has
            // to download. Truncated flag tells the UI it's an over-size
            // text file rather than a true binary.
            return (new FileContent(name, "binary", null, size, null, true), null);
        }

        var kind = KindForExtension(resolved);
        if (size > SoftTruncateThresholdBytes)
        {
            var content = ReadFirstNLines(resolved, TruncatedLineCount);
            return (new FileContent(name, kind, language, size, content, true), null);
        }
        return (new FileContent(name, kind, language, size, File.ReadAllText(resolved), false), null);
    }
}
