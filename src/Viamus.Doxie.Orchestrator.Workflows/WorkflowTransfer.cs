using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Viamus.Doxie.Orchestrator.Agents;

namespace Viamus.Doxie.Orchestrator.Workflows;

/// <summary>
/// Export / import of a workflow plus the agents referenced by its graph.
/// Agent payloads are nested normal AgentTransfer zips, so workflow bundles
/// reuse the same validation and canonical import path as direct agent import.
/// </summary>
public static class WorkflowTransfer
{
    public const string BundleManifestName = "doxie-workflow-export.json";
    public const string Format = "doxie.workflow.export.v1";

    private static readonly Regex KebabCase = new("^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.Compiled);

    public static void ExportToZip(
        string workflowPath,
        string workflowId,
        IEnumerable<AgentBundleSource> agents,
        Stream output)
    {
        if (!IsValidId(workflowId))
        {
            throw new ArgumentException($"Invalid workflow id '{workflowId}' - must be kebab-case.", nameof(workflowId));
        }

        var workflowDir = Path.GetFullPath(workflowPath);
        if (!Directory.Exists(workflowDir))
        {
            throw new DirectoryNotFoundException($"Workflow '{workflowId}' not found at {workflowDir}");
        }

        var agentList = agents
            .GroupBy(a => a.Id, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(a => a.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();

        using var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        foreach (var file in Directory.EnumerateFiles(workflowDir, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(workflowDir, file).Replace('\\', '/');
            AddFile(archive, file, $"workflow/{workflowId}/{rel}");
        }

        var manifest = new BundleManifest
        {
            Format = Format,
            WorkflowId = workflowId,
            ExportedAt = DateTimeOffset.UtcNow,
            Agents = agentList
                .Select(a => new BundleAgentManifest { Id = a.Id, Path = $"agents/{a.Id}.zip" })
                .ToList(),
        };
        var manifestEntry = archive.CreateEntry(BundleManifestName, CompressionLevel.Optimal);
        using (var entryStream = manifestEntry.Open())
        {
            JsonSerializer.Serialize(entryStream, manifest, JsonOptions);
        }

        foreach (var agent in agentList)
        {
            if (!IsValidId(agent.Id))
            {
                throw new ArgumentException($"Invalid agent id '{agent.Id}' - must be kebab-case.", nameof(agents));
            }

            var entry = archive.CreateEntry($"agents/{agent.Id}.zip", CompressionLevel.Optimal);
            using var entryStream = entry.Open();
            AgentTransfer.ExportToZip(agent.SkillsRoot, agent.AgentsRoot, agent.Id, entryStream);
        }
    }

    public static ImportResult ImportFromZip(
        Stream input,
        ImportCatalog destinationCatalog,
        IReadOnlyList<ImportCatalog> allCatalogs,
        bool overwrite = false)
    {
        ZipArchive archive;
        try
        {
            archive = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: false);
        }
        catch (InvalidDataException ex)
        {
            return ImportResult.Fail(ImportError.NotAZip, $"Not a valid zip file: {ex.Message}");
        }

        using (archive)
        {
            var validation = ValidateBundle(archive);
            if (!validation.Ok)
            {
                return ImportResult.Fail(validation.Error!.Value, validation.Message!);
            }

            var workflowId = validation.WorkflowId!;
            Directory.CreateDirectory(destinationCatalog.WorkflowsRoot);
            Directory.CreateDirectory(destinationCatalog.SkillsRoot);
            Directory.CreateDirectory(destinationCatalog.AgentsRoot);

            var workflowRoot = Path.GetFullPath(destinationCatalog.WorkflowsRoot);
            var destinationWorkflowDir = ResolveUnderRoot(workflowRoot, workflowId);
            var stagingWorkflowDir = ResolveUnderRoot(workflowRoot, $"{workflowId}.import-{Guid.NewGuid():N}");
            var agentStageRoot = Path.Combine(workflowRoot, $".agent-import-{Guid.NewGuid():N}");
            var stagedSkillsRoot = Path.Combine(agentStageRoot, "skills");
            var stagedAgentsRoot = Path.Combine(agentStageRoot, "agents");
            Directory.CreateDirectory(stagingWorkflowDir);
            Directory.CreateDirectory(stagedSkillsRoot);
            Directory.CreateDirectory(stagedAgentsRoot);

            try
            {
                ExtractWorkflow(archive, workflowId, stagingWorkflowDir);
                NormalizeWorkflowManifest(stagingWorkflowDir, workflowId, destinationCatalog.CatalogId);

                var workflowAction = WorkflowImportAction.Imported;
                var existingWorkflow = FindExistingWorkflow(allCatalogs, workflowId).ToList();
                if (existingWorkflow.Count > 0)
                {
                    var identical = existingWorkflow.FirstOrDefault(e =>
                        DirectoriesEqual(stagingWorkflowDir, e.WorkflowDir));
                    if (identical is not null)
                    {
                        workflowAction = WorkflowImportAction.SkippedIdentical;
                    }
                    else if (!overwrite)
                    {
                        return ImportResult.Fail(
                            ImportError.Collision,
                            $"Workflow '{workflowId}' already exists but differs from the bundled version. Pass overwrite=true to replace it.");
                    }
                    else
                    {
                        workflowAction = WorkflowImportAction.Replaced;
                    }
                }

                var decisions = new List<AgentImportDecision>();
                foreach (var agentId in validation.AgentIds)
                {
                    var zipEntry = archive.GetEntry($"agents/{agentId}.zip");
                    if (zipEntry is null)
                    {
                        return ImportResult.Fail(ImportError.MissingFile, $"Bundle manifest references missing agents/{agentId}.zip.");
                    }

                    using var ms = new MemoryStream();
                    using (var entryStream = zipEntry.Open())
                    {
                        entryStream.CopyTo(ms);
                    }
                    ms.Position = 0;
                    var stagedResult = AgentTransfer.ImportFromZip(ms, stagedSkillsRoot, stagedAgentsRoot);
                    if (!stagedResult.Ok || string.IsNullOrWhiteSpace(stagedResult.AgentId))
                    {
                        return ImportResult.Fail(
                            ImportError.InvalidAgent,
                            $"Bundled agent '{agentId}' is invalid: {stagedResult.Message ?? stagedResult.Error?.ToString() ?? "unknown error"}");
                    }
                    if (!string.Equals(stagedResult.AgentId, agentId, StringComparison.Ordinal))
                    {
                        return ImportResult.Fail(
                            ImportError.InvalidAgent,
                            $"Bundled agent path '{agentId}.zip' contains agent '{stagedResult.AgentId}'.");
                    }

                    var existing = FindExistingAgent(allCatalogs, agentId).ToList();
                    if (existing.Count == 0)
                    {
                        decisions.Add(new AgentImportDecision(agentId, AgentImportAction.Imported));
                        continue;
                    }

                    var identical = existing.FirstOrDefault(e => AgentTreesEqual(stagedSkillsRoot, stagedAgentsRoot, e));
                    if (identical is not null)
                    {
                        decisions.Add(new AgentImportDecision(agentId, AgentImportAction.SkippedIdentical, identical.CatalogId));
                        continue;
                    }

                    if (!overwrite)
                    {
                        return ImportResult.Fail(
                            ImportError.AgentCollision,
                            $"Agent '{agentId}' already exists but differs from the bundled version. Pass overwrite=true to replace it.",
                            decisions);
                    }

                    decisions.Add(new AgentImportDecision(agentId, AgentImportAction.Replaced, existing[0].CatalogId));
                }

                CommitWorkflow(stagingWorkflowDir, destinationWorkflowDir, workflowId, workflowAction, allCatalogs);
                foreach (var decision in decisions)
                {
                    if (decision.Action is AgentImportAction.Imported or AgentImportAction.Replaced)
                    {
                        CommitAgent(decision.AgentId, stagedSkillsRoot, stagedAgentsRoot, destinationCatalog, allCatalogs);
                    }
                }

                return ImportResult.Success(workflowId, workflowAction, decisions);
            }
            finally
            {
                DeleteIfExists(stagingWorkflowDir);
                DeleteIfExists(agentStageRoot);
            }
        }
    }

    private static BundleValidation ValidateBundle(ZipArchive archive)
    {
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.FullName)) continue;
            var normalized = entry.FullName.Replace('\\', '/');
            if (IsUnsafePath(normalized))
            {
                return BundleValidation.Fail(ImportError.UnsafePath, $"Refusing entry with traversal-like path: '{entry.FullName}'");
            }
        }

        var manifestEntry = archive.GetEntry(BundleManifestName);
        if (manifestEntry is null)
        {
            return BundleValidation.Fail(ImportError.MissingFile, $"Bundle is missing {BundleManifestName}.");
        }

        BundleManifest? manifest;
        try
        {
            using var stream = manifestEntry.Open();
            manifest = JsonSerializer.Deserialize<BundleManifest>(stream, JsonOptions);
        }
        catch (JsonException ex)
        {
            return BundleValidation.Fail(ImportError.InvalidJson, $"{BundleManifestName} is not valid JSON: {ex.Message}");
        }

        if (manifest is null || !string.Equals(manifest.Format, Format, StringComparison.Ordinal))
        {
            return BundleValidation.Fail(ImportError.InvalidJson, $"{BundleManifestName} has an unsupported format.");
        }
        if (!IsValidId(manifest.WorkflowId))
        {
            return BundleValidation.Fail(ImportError.InvalidId, $"Workflow id '{manifest.WorkflowId}' is not kebab-case.");
        }

        string? discoveredWorkflow = null;
        foreach (var entry in archive.Entries)
        {
            var normalized = entry.FullName.Replace('\\', '/');
            if (!normalized.StartsWith("workflow/", StringComparison.Ordinal)) continue;
            var rest = normalized["workflow/".Length..];
            var slash = rest.IndexOf('/');
            if (slash < 0) continue;
            var id = rest[..slash];
            if (discoveredWorkflow is null)
            {
                discoveredWorkflow = id;
            }
            else if (!string.Equals(discoveredWorkflow, id, StringComparison.Ordinal))
            {
                return BundleValidation.Fail(ImportError.MissingTopFolder, "Bundle must contain exactly one workflow folder.");
            }
        }

        if (!string.Equals(discoveredWorkflow, manifest.WorkflowId, StringComparison.Ordinal))
        {
            return BundleValidation.Fail(ImportError.MissingTopFolder, $"Bundle workflow folder does not match manifest id '{manifest.WorkflowId}'.");
        }

        var workflowManifestEntry = archive.GetEntry($"workflow/{manifest.WorkflowId}/workflow.json");
        if (workflowManifestEntry is null)
        {
            return BundleValidation.Fail(ImportError.MissingFile, "Workflow bundle is missing workflow.json.");
        }

        try
        {
            using var stream = workflowManifestEntry.Open();
            var workflowManifest = JsonNode.Parse(stream)?.AsObject();
            if (workflowManifest is null)
            {
                return BundleValidation.Fail(ImportError.InvalidJson, "workflow.json root must be an object.");
            }

            var id = workflowManifest["id"]?.GetValue<string>();
            if (!string.Equals(id, manifest.WorkflowId, StringComparison.Ordinal))
            {
                return BundleValidation.Fail(ImportError.InvalidId, $"workflow.json id '{id}' does not match workflow folder '{manifest.WorkflowId}/'.");
            }
        }
        catch (JsonException ex)
        {
            return BundleValidation.Fail(ImportError.InvalidJson, $"workflow.json is not valid JSON: {ex.Message}");
        }

        var agentIds = new List<string>();
        foreach (var agent in manifest.Agents ?? new List<BundleAgentManifest>())
        {
            if (!IsValidId(agent.Id))
            {
                return BundleValidation.Fail(ImportError.InvalidId, $"Agent id '{agent.Id}' is not kebab-case.");
            }
            var expected = $"agents/{agent.Id}.zip";
            if (!string.Equals(agent.Path, expected, StringComparison.Ordinal))
            {
                return BundleValidation.Fail(ImportError.InvalidAgent, $"Agent '{agent.Id}' must point at '{expected}'.");
            }
            if (archive.GetEntry(expected) is null)
            {
                return BundleValidation.Fail(ImportError.MissingFile, $"Bundle is missing {expected}.");
            }
            if (!agentIds.Contains(agent.Id, StringComparer.OrdinalIgnoreCase))
            {
                agentIds.Add(agent.Id);
            }
        }

        foreach (var entry in archive.Entries)
        {
            var normalized = entry.FullName.Replace('\\', '/');
            if (!normalized.StartsWith("agents/", StringComparison.Ordinal) || normalized.EndsWith('/')) continue;
            var rest = normalized["agents/".Length..];
            if (rest.Contains('/'))
            {
                return BundleValidation.Fail(ImportError.InvalidAgent, $"Unexpected nested agent entry '{normalized}'.");
            }
            if (!rest.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                return BundleValidation.Fail(ImportError.InvalidAgent, $"Unexpected agent entry '{normalized}'.");
            }
            var id = rest[..^4];
            if (!agentIds.Contains(id, StringComparer.OrdinalIgnoreCase))
            {
                return BundleValidation.Fail(ImportError.InvalidAgent, $"Agent entry '{normalized}' is not listed in {BundleManifestName}.");
            }
        }

        return BundleValidation.Success(manifest.WorkflowId, agentIds);
    }

    private static void ExtractWorkflow(ZipArchive archive, string workflowId, string stagingWorkflowDir)
    {
        var prefix = $"workflow/{workflowId}/";
        foreach (var entry in archive.Entries)
        {
            var rel = entry.FullName.Replace('\\', '/');
            if (!rel.StartsWith(prefix, StringComparison.Ordinal)) continue;

            var inner = rel[prefix.Length..];
            if (string.IsNullOrEmpty(inner)) continue;

            var fullDest = Path.GetFullPath(Path.Combine(stagingWorkflowDir, inner));
            if (!fullDest.StartsWith(stagingWorkflowDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException($"Refusing to extract outside staging dir: {entry.FullName}");
            }

            if (rel.EndsWith('/'))
            {
                Directory.CreateDirectory(fullDest);
                continue;
            }

            var parent = Path.GetDirectoryName(fullDest);
            if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
            entry.ExtractToFile(fullDest, overwrite: true);
        }
    }

    private static void NormalizeWorkflowManifest(string workflowDir, string workflowId, string catalogId)
    {
        var manifestPath = Path.Combine(workflowDir, "workflow.json");
        var node = JsonNode.Parse(File.ReadAllText(manifestPath))?.AsObject()
            ?? throw new InvalidDataException("workflow.json root must be an object.");

        node["id"] = workflowId;
        node["catalogId"] = catalogId;
        File.WriteAllText(manifestPath, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
    }

    private static IEnumerable<ExistingWorkflowLocation> FindExistingWorkflow(IEnumerable<ImportCatalog> catalogs, string workflowId)
    {
        foreach (var catalog in catalogs)
        {
            var workflowDir = Path.GetFullPath(Path.Combine(catalog.WorkflowsRoot, workflowId));
            if (Directory.Exists(workflowDir))
            {
                yield return new ExistingWorkflowLocation(catalog.CatalogId, workflowDir);
            }
        }
    }

    private static IEnumerable<ExistingAgentLocation> FindExistingAgent(IEnumerable<ImportCatalog> catalogs, string agentId)
    {
        foreach (var catalog in catalogs)
        {
            var skillDir = Path.GetFullPath(Path.Combine(catalog.SkillsRoot, agentId));
            var agentPath = Path.GetFullPath(Path.Combine(catalog.AgentsRoot, $"{agentId}.md"));
            if (Directory.Exists(skillDir) || File.Exists(agentPath))
            {
                yield return new ExistingAgentLocation(catalog.CatalogId, skillDir, agentPath);
            }
        }
    }

    private static bool AgentTreesEqual(string stagedSkillsRoot, string stagedAgentsRoot, ExistingAgentLocation existing)
    {
        var stagedSkillDir = Path.Combine(stagedSkillsRoot, Path.GetFileName(existing.SkillDir));
        var stagedAgentPath = Path.Combine(stagedAgentsRoot, Path.GetFileName(existing.AgentPath));
        if (!DirectoriesEqual(stagedSkillDir, existing.SkillDir)) return false;

        var stagedHasSubagent = File.Exists(stagedAgentPath);
        var existingHasSubagent = File.Exists(existing.AgentPath);
        if (stagedHasSubagent != existingHasSubagent) return false;
        return !stagedHasSubagent || FilesEqual(stagedAgentPath, existing.AgentPath);
    }

    private static void CommitWorkflow(
        string stagingWorkflowDir,
        string destinationWorkflowDir,
        string workflowId,
        WorkflowImportAction action,
        IReadOnlyList<ImportCatalog> allCatalogs)
    {
        if (action == WorkflowImportAction.SkippedIdentical) return;

        foreach (var existing in FindExistingWorkflow(allCatalogs, workflowId))
        {
            if (Directory.Exists(existing.WorkflowDir)) Directory.Delete(existing.WorkflowDir, recursive: true);
        }
        Directory.Move(stagingWorkflowDir, destinationWorkflowDir);
    }

    private static void CommitAgent(
        string agentId,
        string stagedSkillsRoot,
        string stagedAgentsRoot,
        ImportCatalog destination,
        IReadOnlyList<ImportCatalog> allCatalogs)
    {
        foreach (var existing in FindExistingAgent(allCatalogs, agentId))
        {
            if (Directory.Exists(existing.SkillDir)) Directory.Delete(existing.SkillDir, recursive: true);
            if (File.Exists(existing.AgentPath)) File.Delete(existing.AgentPath);
        }

        Directory.CreateDirectory(destination.SkillsRoot);
        Directory.CreateDirectory(destination.AgentsRoot);

        var stagedSkillDir = Path.Combine(stagedSkillsRoot, agentId);
        var destSkillDir = Path.Combine(destination.SkillsRoot, agentId);
        CopyDirectory(stagedSkillDir, destSkillDir);

        var stagedAgentPath = Path.Combine(stagedAgentsRoot, $"{agentId}.md");
        if (File.Exists(stagedAgentPath))
        {
            File.Copy(stagedAgentPath, Path.Combine(destination.AgentsRoot, $"{agentId}.md"), overwrite: true);
        }
    }

    private static void CopyDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (var dir in Directory.EnumerateDirectories(sourceDir, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(destDir, Path.GetRelativePath(sourceDir, dir)));
        }
        foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var dest = Path.Combine(destDir, Path.GetRelativePath(sourceDir, file));
            var parent = Path.GetDirectoryName(dest);
            if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
            File.Copy(file, dest, overwrite: true);
        }
    }

    private static bool DirectoriesEqual(string left, string right)
    {
        if (!Directory.Exists(left) || !Directory.Exists(right)) return false;

        var leftFiles = Directory.EnumerateFiles(left, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(left, f).Replace('\\', '/'))
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();
        var rightFiles = Directory.EnumerateFiles(right, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(right, f).Replace('\\', '/'))
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        if (!leftFiles.SequenceEqual(rightFiles, StringComparer.Ordinal)) return false;
        foreach (var rel in leftFiles)
        {
            if (!FilesEqual(Path.Combine(left, rel.Replace('/', Path.DirectorySeparatorChar)),
                    Path.Combine(right, rel.Replace('/', Path.DirectorySeparatorChar))))
            {
                return false;
            }
        }
        return true;
    }

    private static bool FilesEqual(string left, string right)
    {
        var leftInfo = new FileInfo(left);
        var rightInfo = new FileInfo(right);
        if (!leftInfo.Exists || !rightInfo.Exists) return false;
        if (leftInfo.Length != rightInfo.Length) return false;

        using var leftStream = File.OpenRead(left);
        using var rightStream = File.OpenRead(right);
        Span<byte> leftBuffer = stackalloc byte[8192];
        Span<byte> rightBuffer = stackalloc byte[8192];
        while (true)
        {
            var leftRead = leftStream.Read(leftBuffer);
            var rightRead = rightStream.Read(rightBuffer);
            if (leftRead != rightRead) return false;
            if (leftRead == 0) return true;
            if (!leftBuffer[..leftRead].SequenceEqual(rightBuffer[..rightRead])) return false;
        }
    }

    private static void AddFile(ZipArchive archive, string file, string entryPath)
    {
        var entry = archive.CreateEntry(entryPath, CompressionLevel.Optimal);
        using var entryStream = entry.Open();
        using var fs = File.OpenRead(file);
        fs.CopyTo(entryStream);
    }

    private static bool IsUnsafePath(string normalized) =>
        normalized.Contains("..", StringComparison.Ordinal)
        || normalized.StartsWith("/", StringComparison.Ordinal)
        || Path.IsPathRooted(normalized);

    private static bool IsValidId(string value) => !string.IsNullOrEmpty(value) && KebabCase.IsMatch(value);

    private static string ResolveUnderRoot(string root, string segment)
    {
        var rootFull = Path.GetFullPath(root);
        var combined = Path.GetFullPath(Path.Combine(rootFull, segment));
        if (!combined.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Resolved path '{combined}' is outside root '{rootFull}'.");
        }
        return combined;
    }

    private static void DeleteIfExists(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch { /* best effort cleanup */ }
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private sealed class BundleManifest
    {
        public string Format { get; set; } = WorkflowTransfer.Format;
        public string WorkflowId { get; set; } = string.Empty;
        public DateTimeOffset ExportedAt { get; set; }
        public List<BundleAgentManifest>? Agents { get; set; }
    }

    private sealed class BundleAgentManifest
    {
        public string Id { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
    }

    private sealed record BundleValidation(bool Ok, string? WorkflowId, IReadOnlyList<string> AgentIds, ImportError? Error, string? Message)
    {
        public static BundleValidation Success(string workflowId, IReadOnlyList<string> agentIds) =>
            new(true, workflowId, agentIds, null, null);

        public static BundleValidation Fail(ImportError error, string message) =>
            new(false, null, Array.Empty<string>(), error, message);
    }

    private sealed record ExistingWorkflowLocation(string CatalogId, string WorkflowDir);
    private sealed record ExistingAgentLocation(string CatalogId, string SkillDir, string AgentPath);

    public sealed record AgentBundleSource(string Id, string SkillsRoot, string? AgentsRoot);
    public sealed record ImportCatalog(string CatalogId, string SkillsRoot, string AgentsRoot, string WorkflowsRoot);
    public sealed record AgentImportDecision(string AgentId, AgentImportAction Action, string? ExistingCatalogId = null);

    public sealed record ImportResult(
        bool Ok,
        string? WorkflowId,
        WorkflowImportAction WorkflowAction,
        IReadOnlyList<AgentImportDecision> Agents,
        ImportError? Error,
        string? Message)
    {
        public static ImportResult Success(string workflowId, WorkflowImportAction workflowAction, IReadOnlyList<AgentImportDecision> agents) =>
            new(true, workflowId, workflowAction, agents, null, null);

        public static ImportResult Fail(ImportError code, string message, IReadOnlyList<AgentImportDecision>? agents = null) =>
            new(false, null, WorkflowImportAction.None, agents ?? Array.Empty<AgentImportDecision>(), code, message);
    }

    public enum WorkflowImportAction
    {
        None,
        Imported,
        Replaced,
        SkippedIdentical,
    }

    public enum AgentImportAction
    {
        Imported,
        Replaced,
        SkippedIdentical,
    }

    public enum ImportError
    {
        NotAZip,
        MissingFile,
        MissingTopFolder,
        InvalidId,
        InvalidJson,
        UnsafePath,
        Collision,
        AgentCollision,
        InvalidAgent,
    }
}
