using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Viamus.Doxie.Orchestrator.Agents;

/// <summary>
/// Export / import of a workspace plus its explicitly mounted agents.
/// The archive shape is intentionally simple:
///
/// <code>
/// doxie-workspace-export.json
/// workspace/&lt;workspace-id&gt;/...
/// agents/&lt;agent-id&gt;.zip
/// </code>
///
/// Agent payloads are normal AgentTransfer zips nested inside the
/// workspace bundle. Import extracts those into staging first, compares
/// them with every configured local catalog, skips identical agents, and
/// refuses differing agents unless overwrite is explicit.
/// </summary>
public static class WorkspaceTransfer
{
    public const string BundleManifestName = "doxie-workspace-export.json";
    public const string Format = "doxie.workspace.export.v1";

    private static readonly Regex KebabCase = new("^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.Compiled);

    public static void ExportToZip(
        string workspacePath,
        string workspaceId,
        IEnumerable<AgentBundleSource> agents,
        Stream output)
    {
        if (!IsValidId(workspaceId))
        {
            throw new ArgumentException($"Invalid workspace id '{workspaceId}' - must be kebab-case.", nameof(workspaceId));
        }

        var workspaceDir = Path.GetFullPath(workspacePath);
        if (!Directory.Exists(workspaceDir))
        {
            throw new DirectoryNotFoundException($"Workspace '{workspaceId}' not found at {workspaceDir}");
        }

        var agentList = agents
            .GroupBy(a => a.Id, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(a => a.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();

        using var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        foreach (var file in Directory.EnumerateFiles(workspaceDir, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(workspaceDir, file).Replace('\\', '/');
            AddFile(archive, file, $"workspace/{workspaceId}/{rel}");
        }

        var manifest = new BundleManifest
        {
            Format = Format,
            WorkspaceId = workspaceId,
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
        string workspacesRoot,
        AgentImportCatalog destinationCatalog,
        IReadOnlyList<AgentImportCatalog> allCatalogs,
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

            var workspaceId = validation.WorkspaceId!;
            Directory.CreateDirectory(workspacesRoot);
            Directory.CreateDirectory(destinationCatalog.SkillsRoot);
            Directory.CreateDirectory(destinationCatalog.AgentsRoot);

            var rootFull = Path.GetFullPath(workspacesRoot);
            var destinationWorkspaceDir = ResolveUnderRoot(rootFull, workspaceId);
            var stagingWorkspaceDir = ResolveUnderRoot(rootFull, $"{workspaceId}.import-{Guid.NewGuid():N}");
            var agentStageRoot = Path.Combine(rootFull, $".agent-import-{Guid.NewGuid():N}");
            var stagedSkillsRoot = Path.Combine(agentStageRoot, "skills");
            var stagedAgentsRoot = Path.Combine(agentStageRoot, "agents");
            Directory.CreateDirectory(stagingWorkspaceDir);
            Directory.CreateDirectory(stagedSkillsRoot);
            Directory.CreateDirectory(stagedAgentsRoot);

            try
            {
                ExtractWorkspace(archive, workspaceId, stagingWorkspaceDir);
                EnsureWorkspaceManifestMentionsBundledAgents(stagingWorkspaceDir, validation.AgentIds);

                var workspaceAction = WorkspaceImportAction.Imported;
                if (Directory.Exists(destinationWorkspaceDir))
                {
                    if (DirectoriesEqual(stagingWorkspaceDir, destinationWorkspaceDir))
                    {
                        workspaceAction = WorkspaceImportAction.SkippedIdentical;
                    }
                    else if (!overwrite)
                    {
                        return ImportResult.Fail(
                            ImportError.Collision,
                            $"An existing workspace '{workspaceId}' would be replaced. Pass overwrite=true to proceed.");
                    }
                    else
                    {
                        workspaceAction = WorkspaceImportAction.Replaced;
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

                CommitWorkspace(stagingWorkspaceDir, destinationWorkspaceDir, workspaceAction);
                foreach (var decision in decisions)
                {
                    if (decision.Action is AgentImportAction.Imported or AgentImportAction.Replaced)
                    {
                        CommitAgent(decision.AgentId, stagedSkillsRoot, stagedAgentsRoot, destinationCatalog, allCatalogs);
                    }
                }

                return ImportResult.Success(workspaceId, workspaceAction, decisions);
            }
            finally
            {
                DeleteIfExists(stagingWorkspaceDir);
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
        if (!IsValidId(manifest.WorkspaceId))
        {
            return BundleValidation.Fail(ImportError.InvalidId, $"Workspace id '{manifest.WorkspaceId}' is not kebab-case.");
        }

        string? discoveredWorkspace = null;
        foreach (var entry in archive.Entries)
        {
            var normalized = entry.FullName.Replace('\\', '/');
            if (!normalized.StartsWith("workspace/", StringComparison.Ordinal)) continue;
            var rest = normalized["workspace/".Length..];
            var slash = rest.IndexOf('/');
            if (slash < 0) continue;
            var id = rest[..slash];
            if (discoveredWorkspace is null)
            {
                discoveredWorkspace = id;
            }
            else if (!string.Equals(discoveredWorkspace, id, StringComparison.Ordinal))
            {
                return BundleValidation.Fail(ImportError.MissingTopFolder, "Bundle must contain exactly one workspace folder.");
            }
        }

        if (!string.Equals(discoveredWorkspace, manifest.WorkspaceId, StringComparison.Ordinal))
        {
            return BundleValidation.Fail(ImportError.MissingTopFolder, $"Bundle workspace folder does not match manifest id '{manifest.WorkspaceId}'.");
        }
        var workspaceManifestEntry = archive.GetEntry($"workspace/{manifest.WorkspaceId}/workspace.json");
        if (workspaceManifestEntry is null)
        {
            return BundleValidation.Fail(ImportError.MissingFile, "Workspace bundle is missing workspace.json.");
        }
        try
        {
            using var stream = workspaceManifestEntry.Open();
            var workspaceManifest = JsonNode.Parse(stream)?.AsObject();
            if (workspaceManifest is null)
            {
                return BundleValidation.Fail(ImportError.InvalidJson, "workspace.json root must be an object.");
            }
        }
        catch (JsonException ex)
        {
            return BundleValidation.Fail(ImportError.InvalidJson, $"workspace.json is not valid JSON: {ex.Message}");
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

        return BundleValidation.Success(manifest.WorkspaceId, agentIds);
    }

    private static void ExtractWorkspace(ZipArchive archive, string workspaceId, string stagingWorkspaceDir)
    {
        var prefix = $"workspace/{workspaceId}/";
        foreach (var entry in archive.Entries)
        {
            var rel = entry.FullName.Replace('\\', '/');
            if (!rel.StartsWith(prefix, StringComparison.Ordinal)) continue;

            var inner = rel[prefix.Length..];
            if (string.IsNullOrEmpty(inner)) continue;

            var fullDest = Path.GetFullPath(Path.Combine(stagingWorkspaceDir, inner));
            if (!fullDest.StartsWith(stagingWorkspaceDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
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

    private static void EnsureWorkspaceManifestMentionsBundledAgents(string workspaceDir, IReadOnlyList<string> agentIds)
    {
        if (agentIds.Count == 0) return;

        var manifestPath = Path.Combine(workspaceDir, "workspace.json");
        var node = JsonNode.Parse(File.ReadAllText(manifestPath))?.AsObject()
            ?? throw new InvalidDataException("workspace.json root must be an object.");

        var existing = node["mountedAgents"] as JsonArray;
        if (existing is null)
        {
            existing = new JsonArray();
            node["mountedAgents"] = existing;
        }
        var seen = new HashSet<string>(
            existing.Select(TryGetString)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x!),
            StringComparer.OrdinalIgnoreCase);

        foreach (var id in agentIds)
        {
            if (seen.Add(id)) existing.Add(id);
        }
        File.WriteAllText(manifestPath, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
    }

    private static string? TryGetString(JsonNode? node)
    {
        try { return node?.GetValue<string>(); }
        catch (InvalidOperationException) { return null; }
        catch (FormatException) { return null; }
    }

    private static IEnumerable<ExistingAgentLocation> FindExistingAgent(IEnumerable<AgentImportCatalog> catalogs, string agentId)
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

    private static void CommitWorkspace(string stagingWorkspaceDir, string destinationWorkspaceDir, WorkspaceImportAction action)
    {
        if (action == WorkspaceImportAction.SkippedIdentical) return;

        if (Directory.Exists(destinationWorkspaceDir))
        {
            Directory.Delete(destinationWorkspaceDir, recursive: true);
        }
        Directory.Move(stagingWorkspaceDir, destinationWorkspaceDir);
    }

    private static void CommitAgent(
        string agentId,
        string stagedSkillsRoot,
        string stagedAgentsRoot,
        AgentImportCatalog destination,
        IReadOnlyList<AgentImportCatalog> allCatalogs)
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
        public string Format { get; set; } = WorkspaceTransfer.Format;
        public string WorkspaceId { get; set; } = string.Empty;
        public DateTimeOffset ExportedAt { get; set; }
        public List<BundleAgentManifest>? Agents { get; set; }
    }

    private sealed class BundleAgentManifest
    {
        public string Id { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
    }

    private sealed record BundleValidation(bool Ok, string? WorkspaceId, IReadOnlyList<string> AgentIds, ImportError? Error, string? Message)
    {
        public static BundleValidation Success(string workspaceId, IReadOnlyList<string> agentIds) =>
            new(true, workspaceId, agentIds, null, null);

        public static BundleValidation Fail(ImportError error, string message) =>
            new(false, null, Array.Empty<string>(), error, message);
    }

    private sealed record ExistingAgentLocation(string CatalogId, string SkillDir, string AgentPath);

    public sealed record AgentBundleSource(string Id, string SkillsRoot, string? AgentsRoot);
    public sealed record AgentImportCatalog(string CatalogId, string SkillsRoot, string AgentsRoot);
    public sealed record AgentImportDecision(string AgentId, AgentImportAction Action, string? ExistingCatalogId = null);

    public sealed record ImportResult(
        bool Ok,
        string? WorkspaceId,
        WorkspaceImportAction WorkspaceAction,
        IReadOnlyList<AgentImportDecision> Agents,
        ImportError? Error,
        string? Message)
    {
        public static ImportResult Success(string workspaceId, WorkspaceImportAction workspaceAction, IReadOnlyList<AgentImportDecision> agents) =>
            new(true, workspaceId, workspaceAction, agents, null, null);

        public static ImportResult Fail(ImportError code, string message, IReadOnlyList<AgentImportDecision>? agents = null) =>
            new(false, null, WorkspaceImportAction.None, agents ?? Array.Empty<AgentImportDecision>(), code, message);
    }

    public enum WorkspaceImportAction
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
