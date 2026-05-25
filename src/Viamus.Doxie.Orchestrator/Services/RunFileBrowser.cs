using System.Text;
using Viamus.Doxie.Orchestrator.Agents;

namespace Viamus.Doxie.Orchestrator.Services;

/// <summary>
/// Read-only browser for run artefacts on disk. Two roots: workflow
/// per-run folders under <c>.runs/&lt;runId&gt;/</c> and standalone-run
/// sandboxes under <c>.sandbox/&lt;tag&gt;/</c>. Path validation is
/// load-bearing — every method anchors against its root and refuses
/// any segment containing <c>..</c> or anything that resolves outside.
/// </summary>
public sealed class RunFileBrowser
{
    private const long MaxInlinePreviewBytes = 1L * 1024 * 1024; // 1 MB
    private const int BinarySniffBytes = 8 * 1024;

    private readonly StorageOptions _storage;

    public RunFileBrowser(StorageOptions storage)
    {
        _storage = storage;
    }

    private string RunsRoot => StorageOptions.ResolvePath(_storage.WorkflowRunsDirectory);

    private string SandboxesRoot => StorageOptions.ResolvePath(_storage.SandboxesDirectory);

    // --- Sandboxes -----------------------------------------------------

    public IReadOnlyList<SandboxSummary> ListSandboxes()
    {
        var root = SandboxesRoot;
        if (!Directory.Exists(root))
        {
            return Array.Empty<SandboxSummary>();
        }

        var summaries = new List<SandboxSummary>();
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            var info = new DirectoryInfo(dir);
            (long bytes, int files) = MeasureRecursive(dir);
            summaries.Add(new SandboxSummary(
                Tag: info.Name,
                LastModified: info.LastWriteTimeUtc,
                TotalBytes: bytes,
                FileCount: files));
        }

        // Most-recent first — matches every other history view in DoxieOS.
        summaries.Sort((a, b) => b.LastModified.CompareTo(a.LastModified));
        return summaries;
    }

    public DirectoryListing ListSandbox(string tag, string relativePath)
    {
        var sandboxRoot = ResolveSandboxRoot(tag);
        return ListInternal(sandboxRoot, relativePath);
    }

    public FileContent? ReadSandboxFile(string tag, string relativePath)
    {
        var sandboxRoot = ResolveSandboxRoot(tag);
        return ReadInternal(sandboxRoot, relativePath);
    }

    // --- Workflow runs -------------------------------------------------

    public DirectoryListing ListWorkflowRun(string runId, string relativePath)
    {
        var runRoot = ResolveWorkflowRunRoot(runId);
        return ListInternal(runRoot, relativePath);
    }

    public FileContent? ReadWorkflowRunFile(string runId, string relativePath)
    {
        var runRoot = ResolveWorkflowRunRoot(runId);
        return ReadInternal(runRoot, relativePath);
    }

    // --- Internals -----------------------------------------------------

    private string ResolveSandboxRoot(string tag)
    {
        ValidateSegment(tag, nameof(tag));
        var root = Path.Combine(SandboxesRoot, tag);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"Sandbox '{tag}' not found at {root}.");
        }
        return Path.GetFullPath(root);
    }

    private string ResolveWorkflowRunRoot(string runId)
    {
        ValidateSegment(runId, nameof(runId));
        var root = Path.Combine(RunsRoot, runId);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"Workflow run '{runId}' not found at {root}.");
        }
        return Path.GetFullPath(root);
    }

    private static DirectoryListing ListInternal(string root, string relativePath)
    {
        var target = ResolveRelativePath(root, relativePath);
        if (!Directory.Exists(target))
        {
            throw new DirectoryNotFoundException($"Directory not found: {relativePath}");
        }

        var entries = new List<FileEntry>();
        foreach (var dir in Directory.EnumerateDirectories(target))
        {
            var info = new DirectoryInfo(dir);
            entries.Add(new FileEntry(
                Name: info.Name,
                RelativePath: ToRelative(root, dir),
                IsDirectory: true,
                Size: null,
                Modified: info.LastWriteTimeUtc));
        }
        foreach (var file in Directory.EnumerateFiles(target))
        {
            var info = new FileInfo(file);
            entries.Add(new FileEntry(
                Name: info.Name,
                RelativePath: ToRelative(root, file),
                IsDirectory: false,
                Size: info.Length,
                Modified: info.LastWriteTimeUtc));
        }

        // Folders first, alphabetical within group — predictable browsing.
        entries.Sort((a, b) =>
        {
            if (a.IsDirectory != b.IsDirectory) return a.IsDirectory ? -1 : 1;
            return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });

        var rel = ToRelative(root, target);
        return new DirectoryListing(rel, entries);
    }

    private static FileContent? ReadInternal(string root, string relativePath)
    {
        var target = ResolveRelativePath(root, relativePath);
        if (!File.Exists(target))
        {
            return null;
        }

        var info = new FileInfo(target);
        var contentType = GuessContentType(info.Name);

        // Above the inline cap â†’ metadata only, no body. The UI knows
        // to offer a download instead of trying to render.
        if (info.Length > MaxInlinePreviewBytes)
        {
            return new FileContent(
                RelativePath: ToRelative(root, target),
                Name: info.Name,
                Size: info.Length,
                ContentType: contentType,
                Text: null);
        }

        // Sniff a small prefix to decide text vs binary. Binary files are
        // returned as metadata-only — the UI can offer a download link.
        using var stream = File.OpenRead(target);
        var sniffLen = (int)Math.Min(BinarySniffBytes, info.Length);
        var sniff = new byte[sniffLen];
        var read = stream.Read(sniff, 0, sniffLen);
        var looksBinary = ContainsNullByte(sniff, read);
        if (looksBinary)
        {
            return new FileContent(
                RelativePath: ToRelative(root, target),
                Name: info.Name,
                Size: info.Length,
                ContentType: contentType,
                Text: null);
        }

        // Reset and slurp as UTF-8 — small enough to fit in memory by the
        // 1 MB cap above.
        stream.Position = 0;
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var text = reader.ReadToEnd();
        return new FileContent(
            RelativePath: ToRelative(root, target),
            Name: info.Name,
            Size: info.Length,
            ContentType: contentType,
            Text: text);
    }

    /// <summary>
    /// Anchors <paramref name="relativePath"/> under <paramref name="root"/>
    /// and refuses anything that escapes via <c>..</c>, absolute paths,
    /// null bytes, or other tricks. Returns the canonical full path.
    /// </summary>
    private static string ResolveRelativePath(string root, string relativePath)
    {
        // Empty / null relative path means "the root itself".
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return root;
        }

        if (relativePath.Contains('\0'))
        {
            throw new ArgumentException("Path contains null byte.", nameof(relativePath));
        }

        // Reject absolute paths up front — caller is supposed to pass a
        // path *relative* to the root we already resolved.
        if (Path.IsPathRooted(relativePath))
        {
            throw new UnauthorizedAccessException("Absolute paths are not allowed.");
        }

        // Reject any segment that's literally "..". Path.GetFullPath
        // would silently fold them, leaving us no chance to detect the
        // attempt before it's too late.
        var segments = relativePath.Split('/', '\\');
        if (segments.Any(s => s == ".." || s == "..\\" || s == "../"))
        {
            throw new UnauthorizedAccessException("Parent directory traversal is not allowed.");
        }

        var combined = Path.GetFullPath(Path.Combine(root, relativePath));

        // Final containment check — the resolved absolute path must
        // still live under the root.
        var rootWithSep = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        if (!combined.Equals(root, StringComparison.OrdinalIgnoreCase)
            && !combined.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException("Path resolves outside the run root.");
        }

        return combined;
    }

    private static void ValidateSegment(string segment, string paramName)
    {
        if (string.IsNullOrWhiteSpace(segment))
        {
            throw new ArgumentException("Segment cannot be empty.", paramName);
        }
        if (segment.Contains('/') || segment.Contains('\\') || segment.Contains("..") || segment.Contains('\0'))
        {
            throw new ArgumentException("Segment cannot contain path separators or traversal tokens.", paramName);
        }
    }

    private static string ToRelative(string root, string fullPath)
    {
        var rel = Path.GetRelativePath(root, fullPath);
        // Use forward slashes in the wire format so URLs / browsers don't
        // have to deal with backslash quoting.
        return rel.Replace('\\', '/');
    }

    private static bool ContainsNullByte(byte[] buffer, int length)
    {
        for (var i = 0; i < length; i++)
        {
            if (buffer[i] == 0) return true;
        }
        return false;
    }

    private static (long bytes, int files) MeasureRecursive(string dir)
    {
        long total = 0;
        var count = 0;
        foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            try
            {
                var info = new FileInfo(file);
                total += info.Length;
                count++;
            }
            catch
            {
                // File could've been deleted mid-walk; just skip it.
            }
        }
        return (total, count);
    }

    private static string GuessContentType(string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ext switch
        {
            ".md" => "text/markdown",
            ".txt" => "text/plain",
            ".log" => "text/plain",
            ".json" => "application/json",
            ".yml" or ".yaml" => "application/yaml",
            ".csv" => "text/csv",
            ".html" or ".htm" => "text/html",
            ".xml" => "application/xml",
            ".js" => "application/javascript",
            ".ts" => "application/typescript",
            ".cs" => "text/x-csharp",
            ".sql" => "application/sql",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".svg" => "image/svg+xml",
            ".webp" => "image/webp",
            ".pdf" => "application/pdf",
            ".zip" => "application/zip",
            _ => "application/octet-stream",
        };
    }
}

public sealed record SandboxSummary(string Tag, DateTimeOffset LastModified, long TotalBytes, int FileCount);

public sealed record DirectoryListing(string RelativePath, IReadOnlyList<FileEntry> Entries);

public sealed record FileEntry(string Name, string RelativePath, bool IsDirectory, long? Size, DateTimeOffset Modified);

public sealed record FileContent(string RelativePath, string Name, long Size, string ContentType, string? Text);
