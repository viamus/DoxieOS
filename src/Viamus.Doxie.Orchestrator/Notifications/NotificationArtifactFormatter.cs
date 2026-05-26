using System.Text;
using System.Text.Json;
using Viamus.Doxie.Orchestrator.Agents;
using Viamus.Doxie.Orchestrator.Services;
using Viamus.Doxie.Orchestrator.Workflows;

namespace Viamus.Doxie.Orchestrator.Notifications;

internal static class NotificationArtifactFormatter
{
    private const int MaxContentChars = 300_000;
    private const int MaxBodyExcerptChars = 180;

    public static NotificationContentSnapshot ForAgentRun(AgentRun run, string body)
    {
        if (!string.IsNullOrWhiteSpace(run.OutputDir))
        {
            var fromDir = TryFromDirectory(run.OutputDir, body);
            if (fromDir is not null) return fromDir;
        }

        var consoleLines = run.Output
            .Select(FormatConsoleLine)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToArray();

        if (consoleLines.Length == 0) return NotificationContentSnapshot.Empty(body);

        var text = OutputArtifactText.Clean(string.Join(Environment.NewLine, consoleLines));
        var clipped = ClipContent(text);
        return string.IsNullOrWhiteSpace(text)
            ? NotificationContentSnapshot.Empty(body)
            : new NotificationContentSnapshot(
                WithExcerpt(body, text, "console"),
                clipped.Content,
                "text",
                null,
                clipped.Truncated,
                clipped.OriginalLength);
    }

    public static NotificationContentSnapshot ForWorkflowRun(WorkflowRun run, string runsRoot, string body)
    {
        var nodeIds = run.NodeRuns.Values
            .OrderByDescending(n => n.Status == WorkflowNodeRunStatus.Failed)
            .ThenByDescending(n => n.Status == WorkflowNodeRunStatus.Succeeded)
            .ThenByDescending(n => n.FinishedAt ?? n.StartedAt ?? DateTimeOffset.MinValue)
            .Select(n => n.NodeId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var nodeId in nodeIds)
        {
            var dir = WorkflowRunPaths.NodeDir(runsRoot, run.Id, nodeId);
            var fromDir = TryFromDirectory(dir, body);
            if (fromDir is not null) return fromDir;
        }

        var runDir = WorkflowRunPaths.RunDir(runsRoot, run.Id);
        return TryFromDirectory(runDir, body) ?? NotificationContentSnapshot.Empty(body);
    }

    private static NotificationContentSnapshot? TryFromDirectory(string directory, string body)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return null;

        var candidate = CandidateFiles(directory).FirstOrDefault();
        if (candidate is null) return null;

        string text;
        try
        {
            text = File.ReadAllText(candidate, Encoding.UTF8);
        }
        catch
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(text)) return null;

        var format = FormatFor(candidate);
        var previewText = format == "html"
            ? StripHtmlForExcerpt(text)
            : OutputArtifactText.Clean(text);

        var clipped = ClipContent(text);
        return new NotificationContentSnapshot(
            WithExcerpt(body, previewText, Path.GetFileName(candidate)),
            clipped.Content,
            format,
            candidate,
            clipped.Truncated,
            clipped.OriginalLength);
    }

    private static IEnumerable<string> CandidateFiles(string directory)
    {
        var manifestFiles = ReadManifestFiles(directory);
        var all = manifestFiles.Count > 0
            ? manifestFiles.Select(path => Path.Combine(directory, path))
            : Directory.EnumerateFiles(directory, "*.*", SearchOption.AllDirectories);

        return all
            .Where(File.Exists)
            .Where(IsPreviewable)
            .OrderBy(ScoreFile)
            .ThenBy(path => path, StringComparer.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<string> ReadManifestFiles(string directory)
    {
        var manifest = Path.Combine(directory, ".doxie-artifact.json");
        if (!File.Exists(manifest)) return Array.Empty<string>();

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(manifest, Encoding.UTF8));
            if (!document.RootElement.TryGetProperty("files", out var files) ||
                files.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<string>();
            }

            return files.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString())
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Cast<string>()
                .ToList();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static int ScoreFile(string path)
    {
        var file = Path.GetFileName(path).ToLowerInvariant();
        return file switch
        {
            "output.md" => 0,
            "report.md" => 1,
            "summary.md" => 2,
            "result.md" => 3,
            "index.md" => 4,
            "output.html" => 5,
            "report.html" => 6,
            "result.json" => 7,
            "output.json" => 8,
            _ when file.EndsWith(".md", StringComparison.Ordinal) => 20,
            _ when file.EndsWith(".html", StringComparison.Ordinal) => 30,
            _ when file.EndsWith(".json", StringComparison.Ordinal) => 40,
            _ => 80,
        };
    }

    private static bool IsPreviewable(string path)
    {
        var file = Path.GetFileName(path);
        if (file.StartsWith(".", StringComparison.Ordinal)) return false;
        return Path.GetExtension(file).ToLowerInvariant()
            is ".md" or ".markdown" or ".html" or ".htm" or ".json" or ".txt" or ".log" or ".yaml" or ".yml";
    }

    private static string FormatFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".md" or ".markdown" => "markdown",
        ".html" or ".htm" => "html",
        ".json" => "json",
        ".yaml" or ".yml" => "yaml",
        _ => "text",
    };

    private static string WithExcerpt(string body, string content, string source)
    {
        var excerpt = Compact(content, MaxBodyExcerptChars);
        if (string.IsNullOrWhiteSpace(excerpt)) return $"{body} - {source}";
        return $"{body} - {source}: {excerpt}";
    }

    private static string StripHtmlForExcerpt(string html)
    {
        var sb = new StringBuilder(html.Length);
        var inTag = false;
        foreach (var c in html)
        {
            if (c == '<')
            {
                inTag = true;
                sb.Append(' ');
                continue;
            }
            if (c == '>')
            {
                inTag = false;
                continue;
            }
            if (!inTag) sb.Append(c);
        }
        return sb.ToString();
    }

    private static string FormatConsoleLine(AgentRunOutputLine line)
    {
        var prefix = line.Source switch
        {
            AgentRunOutputSource.Stderr => "[stderr] ",
            _ => string.Empty,
        };

        return prefix + line.Text;
    }

    private static (string Content, bool Truncated, int OriginalLength) ClipContent(string content)
    {
        if (content.Length <= MaxContentChars) return (content, false, content.Length);
        return (
            content[..MaxContentChars].TrimEnd() + Environment.NewLine + "\n[output truncated in notification]",
            true,
            content.Length);
    }

    private static string Compact(string? value, int maxChars)
    {
        var clean = string.Join(' ', (value ?? string.Empty)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (clean.Length <= maxChars) return clean;
        return clean[..Math.Max(0, maxChars - 1)].TrimEnd() + "...";
    }
}

internal sealed record NotificationContentSnapshot(
    string Body,
    string? Content,
    string ContentFormat,
    string? SourcePath,
    bool ContentTruncated = false,
    int? ContentLength = null)
{
    public static NotificationContentSnapshot Empty(string body) => new(body, null, "text", null);
}
