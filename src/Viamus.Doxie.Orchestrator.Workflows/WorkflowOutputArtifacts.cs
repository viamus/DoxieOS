using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Viamus.Doxie.Orchestrator.Workflows;

internal static class WorkflowOutputArtifacts
{
    private static readonly JsonSerializerOptions ManifestJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static async Task MaterializeStdoutAsync(
        string outputDir,
        IReadOnlyList<string> stdoutLines,
        CancellationToken cancellation)
    {
        if (HasIndexableFiles(outputDir)) return;

        var output = stdoutLines.Count == 0
            ? "The agent completed successfully but did not produce an indexable artifact file or stdout content."
            : string.Join(Environment.NewLine, stdoutLines).Trim();

        Directory.CreateDirectory(outputDir);
        await File.WriteAllTextAsync(Path.Combine(outputDir, "output.md"), output + Environment.NewLine, cancellation)
            .ConfigureAwait(false);
    }

    public static async Task<string> ComposeFromUpstreamAsync(
        IReadOnlyList<string> upstreamFiles,
        WorkflowRun run,
        CancellationToken cancellation)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Workflow output ({run.WorkflowId})");
        sb.AppendLine();
        sb.AppendLine($"Run `{run.Id}` delivered at {DateTime.UtcNow:O}.");
        sb.AppendLine($"Composed from {upstreamFiles.Count} upstream file(s).");
        sb.AppendLine();

        foreach (var file in upstreamFiles)
        {
            var rel = Path.GetFileName(file);
            var body = await File.ReadAllTextAsync(file, cancellation).ConfigureAwait(false);
            sb.AppendLine("---");
            sb.AppendLine();
            sb.AppendLine($"## from `{rel}`");
            sb.AppendLine();
            sb.AppendLine(body);
            sb.AppendLine();
        }

        return sb.ToString();
    }

    public static async Task EnsureManifestAsync(
        string outputDir,
        string producerId,
        string producerName,
        string kind,
        string title,
        IReadOnlyList<string> tags,
        CancellationToken cancellation)
    {
        var manifestPath = Path.Combine(outputDir, ".doxie-artifact.json");
        if (File.Exists(manifestPath)) return;

        var files = Directory.Exists(outputDir)
            ? Directory.EnumerateFiles(outputDir, "*.*", SearchOption.AllDirectories)
                .Where(file => !Path.GetFileName(file).Equals(".doxie-artifact.json", StringComparison.OrdinalIgnoreCase))
                .Select(file => Path.GetRelativePath(outputDir, file))
                .Where(IsIndexableFile)
                .Take(80)
                .ToArray()
            : Array.Empty<string>();

        if (files.Length == 0) return;

        await WriteManifestAsync(
            outputDir,
            new WorkflowOutputArtifactManifest(
                "doxie.output-artifact.v1",
                producerId,
                producerName,
                kind,
                title,
                tags,
                files),
            cancellation).ConfigureAwait(false);
    }

    public static async Task WriteManifestAsync(
        string outputDir,
        WorkflowOutputArtifactManifest manifest,
        CancellationToken cancellation)
    {
        Directory.CreateDirectory(outputDir);
        var path = Path.Combine(outputDir, ".doxie-artifact.json");
        var json = JsonSerializer.Serialize(manifest, ManifestJsonOptions);
        await File.WriteAllTextAsync(path, json, cancellation).ConfigureAwait(false);
    }

    private static bool HasIndexableFiles(string outputDir) =>
        Directory.Exists(outputDir)
        && Directory.EnumerateFiles(outputDir, "*.*", SearchOption.AllDirectories)
            .Select(file => Path.GetRelativePath(outputDir, file))
            .Any(IsIndexableFile);

    private static bool IsIndexableFile(string relativePath)
    {
        var file = Path.GetFileName(relativePath);
        if (file.StartsWith(".", StringComparison.Ordinal)) return false;
        var extension = Path.GetExtension(file);
        return extension is ".md" or ".json" or ".txt" or ".yaml" or ".yml" or ".csv" or ".html" or ".xml" or ".log";
    }
}

internal sealed record WorkflowOutputArtifactManifest(
    string Schema,
    string ProducerId,
    string ProducerName,
    string Kind,
    string Title,
    IReadOnlyList<string> Tags,
    IReadOnlyList<string> Files);
