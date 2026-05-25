using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Viamus.Doxie.Orchestrator.Agents;

/// <summary>
/// Export / import of an agent as a single zip file. An "agent" on
/// disk has up to two artifacts that ship together so a recipient
/// gets a working install:
///
/// <list type="bullet">
///   <item><c>&lt;skillsRoot&gt;/&lt;id&gt;/</c> — folder with
///     <c>SKILL.md</c>, <c>orchestrator.json</c>, mode <c>*.md</c>
///     files, and any sibling assets (scripts, etc).</item>
///   <item><c>&lt;agentsRoot&gt;/&lt;id&gt;.md</c> — single Claude
///     Code subagent definition file (frontmatter + body). Often
///     paired one-to-one with the skill so the orchestrator can
///     dispatch the subagent via the Task tool. Optional — agents
///     that exist as skill-only stay supported.</item>
/// </list>
///
/// Zip layout mirrors that on-disk shape: a single top-level folder
/// <c>&lt;id&gt;/</c> (required) plus, optionally, a top-level file
/// <c>&lt;id&gt;.md</c> next to it (the subagent definition). Anything
/// else at the zip root is rejected so the import surface stays
/// unambiguous.
/// </summary>
public static class AgentTransfer
{
    private static readonly Regex KebabCase = new("^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.Compiled);

    /// <summary>
    /// Streams the agent's on-disk artifacts (skill folder + optional
    /// subagent <c>.md</c>) as a zip into <paramref name="output"/>.
    /// Throws <see cref="ArgumentException"/> for malformed ids,
    /// <see cref="DirectoryNotFoundException"/> when the skill folder
    /// doesn't exist. The caller owns the stream — this method does
    /// not flush, dispose, or seek it.
    /// </summary>
    /// <param name="agentsRoot">
    /// Sibling directory holding subagent <c>.md</c> files. May be
    /// <c>null</c> when callers don't want to ship the subagent half;
    /// when set and <c>&lt;agentsRoot&gt;/&lt;agentId&gt;.md</c>
    /// exists, that file is added to the zip root.
    /// </param>
    public static void ExportToZip(string skillsRoot, string? agentsRoot, string agentId, Stream output)
    {
        if (!IsValidAgentId(agentId))
        {
            throw new ArgumentException($"Invalid agent id '{agentId}' — must be kebab-case.", nameof(agentId));
        }

        var agentDir = ResolveUnderRoot(skillsRoot, agentId);
        if (!Directory.Exists(agentDir))
        {
            throw new DirectoryNotFoundException($"Agent '{agentId}' not found at {agentDir}");
        }

        // leaveOpen=true: ZipArchive flushes on Dispose, but we don't
        // want to close the response stream out from under ASP.NET.
        using var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);

        foreach (var file in Directory.EnumerateFiles(agentDir, "*", SearchOption.AllDirectories))
        {
            // Entry path: "<agentId>/<relative>". Forward slashes per
            // the zip spec (Windows backslashes confuse unzip on macOS
            // and Linux).
            var rel = Path.GetRelativePath(agentDir, file).Replace('\\', '/');
            var entryPath = $"{agentId}/{rel}";
            var entry = archive.CreateEntry(entryPath, CompressionLevel.Optimal);

            using var entryStream = entry.Open();
            using var fs = File.OpenRead(file);
            fs.CopyTo(entryStream);
        }

        // Optional subagent half. Skipped silently when the agents dir
        // isn't configured or the file doesn't exist — many agents are
        // skill-only.
        if (!string.IsNullOrEmpty(agentsRoot))
        {
            var subagentPath = ResolveUnderRoot(agentsRoot, $"{agentId}.md");
            if (File.Exists(subagentPath))
            {
                var entry = archive.CreateEntry($"{agentId}.md", CompressionLevel.Optimal);
                using var entryStream = entry.Open();
                using var fs = File.OpenRead(subagentPath);
                fs.CopyTo(entryStream);
            }
        }
    }

    /// <summary>
    /// Validates and extracts a zip into the canonical agent
    /// locations: the skill folder under <paramref name="skillsRoot"/>
    /// and (when present in the zip) the subagent <c>.md</c> under
    /// <paramref name="agentsRoot"/>. Validation refuses zip-slip
    /// paths, missing required files, malformed JSON, bad ids, and
    /// stray entries at the zip root before touching either
    /// destination. Returns the imported id on success.
    /// </summary>
    /// <param name="agentsRoot">
    /// Sibling directory where the subagent <c>.md</c> (if shipped in
    /// the zip) lands. Created on demand. May be <c>null</c> only when
    /// the caller is certain the zip contains no subagent half — if a
    /// subagent file is present and <paramref name="agentsRoot"/> is
    /// null the import fails.
    /// </param>
    /// <param name="overwrite">
    /// When false (default), a collision with an existing skill
    /// folder OR an existing subagent file is reported as
    /// <see cref="ImportError.Collision"/>. When true, both are
    /// replaced.
    /// </param>
    public static ImportResult ImportFromZip(Stream input, string skillsRoot, string? agentsRoot, bool overwrite = false)
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
            // Pass one — classify every entry by its top-level shape and
            // run the cheap zip-slip / layout checks. We don't touch
            // the destination until pass two.
            string? folderRoot = null;     // <id>/
            string? subagentRoot = null;   // <id>.md
            foreach (var entry in archive.Entries)
            {
                if (string.IsNullOrEmpty(entry.FullName)) continue;
                var normalized = entry.FullName.Replace('\\', '/');

                // Zip-slip guard: refuse any entry whose path tries
                // to escape the destination via "..", absolute paths,
                // or backslashes that would land outside on Windows.
                if (normalized.Contains("..") || normalized.StartsWith('/') || Path.IsPathRooted(normalized))
                {
                    return ImportResult.Fail(ImportError.UnsafePath,
                        $"Refusing entry with traversal-like path: '{entry.FullName}'");
                }

                var slash = normalized.IndexOf('/');
                if (slash < 0)
                {
                    // Top-level file — only acceptable shape is
                    // "<folderRoot>.md" (the subagent half).
                    if (!normalized.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                    {
                        return ImportResult.Fail(ImportError.MissingTopFolder,
                            $"Unexpected top-level file '{normalized}' — only the agent's <id>.md subagent file is allowed at zip root.");
                    }
                    var idCandidate = normalized[..^3];
                    if (!IsValidAgentId(idCandidate))
                    {
                        return ImportResult.Fail(ImportError.InvalidId,
                            $"Top-level file '{normalized}' is not a valid '<id>.md' (id must be kebab-case).");
                    }
                    if (subagentRoot is not null && !string.Equals(subagentRoot, idCandidate, StringComparison.Ordinal))
                    {
                        return ImportResult.Fail(ImportError.MissingTopFolder,
                            "Multiple top-level <id>.md files in zip — expected at most one.");
                    }
                    subagentRoot = idCandidate;
                    continue;
                }

                var top = normalized[..slash];
                if (folderRoot is null)
                {
                    folderRoot = top;
                }
                else if (!string.Equals(folderRoot, top, StringComparison.Ordinal))
                {
                    return ImportResult.Fail(ImportError.MissingTopFolder,
                        "Zip must contain exactly one top-level folder.");
                }
            }

            if (folderRoot is null)
            {
                return ImportResult.Fail(ImportError.Empty,
                    subagentRoot is null
                        ? "Zip is empty."
                        : "Zip has a subagent <id>.md but no skill folder — cannot import without the skill half.");
            }
            if (!IsValidAgentId(folderRoot))
            {
                return ImportResult.Fail(ImportError.InvalidId,
                    $"Top-level folder '{folderRoot}' is not a valid agent id (must be kebab-case).");
            }
            if (subagentRoot is not null && !string.Equals(subagentRoot, folderRoot, StringComparison.Ordinal))
            {
                return ImportResult.Fail(ImportError.MissingTopFolder,
                    $"Subagent file '{subagentRoot}.md' does not match skill folder '{folderRoot}/' — they must share the same id.");
            }
            if (subagentRoot is not null && string.IsNullOrEmpty(agentsRoot))
            {
                return ImportResult.Fail(ImportError.MissingFile,
                    "Zip contains a subagent <id>.md but no agentsRoot was configured to receive it.");
            }

            // Required entries inside the skill folder — guarantee the
            // imported folder is a working agent the catalog will
            // surface (orchestrator.json is the opt-in marker for
            // FilesystemAgentCatalog).
            var prefix = folderRoot + "/";
            var hasCanonicalManifest = archive.GetEntry(prefix + "manifest.json") is not null;
            var hasCanonicalBody = archive.GetEntry(prefix + "body.md") is not null;
            var hasLegacySkillMd = archive.GetEntry(prefix + "SKILL.md") is not null;
            var hasLegacySidecar = archive.GetEntry(prefix + "orchestrator.json") is not null;
            var isCanonical = hasCanonicalManifest || hasCanonicalBody;
            var isLegacy = !isCanonical && (hasLegacySkillMd || hasLegacySidecar);

            if (isCanonical)
            {
                if (!hasCanonicalManifest) return ImportResult.Fail(ImportError.MissingFile, "Zip is missing manifest.json.");
                if (!hasCanonicalBody) return ImportResult.Fail(ImportError.MissingFile, "Zip is missing body.md.");

                var manifestEntry = archive.GetEntry(prefix + "manifest.json")!;
                try
                {
                    using var sr = new StreamReader(manifestEntry.Open());
                    var manifest = JsonNode.Parse(sr.ReadToEnd())?.AsObject();
                    if (manifest is null)
                    {
                        return ImportResult.Fail(ImportError.InvalidJson, "manifest.json root must be an object.");
                    }

                    var id = manifest["id"]?.GetValue<string>();
                    if (!string.Equals(id, folderRoot, StringComparison.Ordinal))
                    {
                        return ImportResult.Fail(ImportError.InvalidId,
                            $"manifest.json id '{id}' does not match skill folder '{folderRoot}/'.");
                    }
                }
                catch (JsonException ex)
                {
                    return ImportResult.Fail(ImportError.InvalidJson,
                        $"manifest.json is not valid JSON: {ex.Message}");
                }
            }
            else if (isLegacy)
            {
                if (!hasLegacySkillMd) return ImportResult.Fail(ImportError.MissingFile, "Zip is missing SKILL.md.");
                if (!hasLegacySidecar) return ImportResult.Fail(ImportError.MissingFile, "Zip is missing orchestrator.json.");

                var sidecarEntry = archive.GetEntry(prefix + "orchestrator.json")!;
                try
                {
                    using var sr = new StreamReader(sidecarEntry.Open());
                    _ = JsonDocument.Parse(sr.ReadToEnd());
                }
                catch (JsonException ex)
                {
                    return ImportResult.Fail(ImportError.InvalidJson,
                        $"orchestrator.json is not valid JSON: {ex.Message}");
                }
            }
            else
            {
                return ImportResult.Fail(ImportError.MissingFile, "Zip is missing manifest.json/body.md or SKILL.md/orchestrator.json.");
            }

            // Destination handling — both halves checked together so
            // the user gets one clear error instead of a half-imported
            // state if e.g. the skill folder collides but the subagent
            // file doesn't.
            Directory.CreateDirectory(skillsRoot);
            var destSkillDir = ResolveUnderRoot(skillsRoot, folderRoot);
            string? destSubagentPath = null;
            if (subagentRoot is not null)
            {
                Directory.CreateDirectory(agentsRoot!);
                destSubagentPath = ResolveUnderRoot(agentsRoot!, $"{subagentRoot}.md");
            }

            var skillCollides = Directory.Exists(destSkillDir);
            var subagentCollides = destSubagentPath is not null && File.Exists(destSubagentPath);
            if ((skillCollides || subagentCollides) && !overwrite)
            {
                var what = (skillCollides, subagentCollides) switch
                {
                    (true, true) => $"agent '{folderRoot}' (skill folder + subagent file)",
                    (true, false) => $"agent '{folderRoot}' (skill folder)",
                    (false, true) => $"subagent file '{subagentRoot}.md'",
                    _ => folderRoot,
                };
                return ImportResult.Fail(ImportError.Collision,
                    $"An existing {what} would be replaced. Pass overwrite=true to proceed.");
            }

            // Stage skill folder into a sibling temp dir; stage subagent
            // file into a sibling temp file. On success move both into
            // place. On any failure roll back both stages so the
            // destination never sees a half-import.
            var stagingSkillDir = ResolveUnderRoot(skillsRoot, $"{folderRoot}.import-{Guid.NewGuid():N}");
            string? stagingSubagentPath = destSubagentPath is null
                ? null
                : ResolveUnderRoot(agentsRoot!, $"{subagentRoot}.import-{Guid.NewGuid():N}.md");
            Directory.CreateDirectory(stagingSkillDir);
            try
            {
                foreach (var entry in archive.Entries)
                {
                    var rel = entry.FullName.Replace('\\', '/');
                    if (rel.StartsWith(prefix, StringComparison.Ordinal))
                    {
                        var inner = rel[prefix.Length..];
                        if (string.IsNullOrEmpty(inner)) continue; // the folder itself

                        var fullDest = Path.GetFullPath(Path.Combine(stagingSkillDir, inner));
                        if (!fullDest.StartsWith(stagingSkillDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                        {
                            // Belt-and-suspenders: even with the earlier
                            // path checks, refuse anything that resolved
                            // outside the staging dir.
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
                    else if (subagentRoot is not null && string.Equals(rel, $"{subagentRoot}.md", StringComparison.Ordinal))
                    {
                        entry.ExtractToFile(stagingSubagentPath!, overwrite: true);
                    }
                }

                // Commit phase. The two moves are not transactional —
                // a power loss between them leaves a stale half — but
                // the staging files are already validated, so the
                // worst case is the user re-runs import.
                if (isLegacy)
                {
                    NormalizeLegacySkillFolder(stagingSkillDir, folderRoot);
                }

                if (skillCollides) Directory.Delete(destSkillDir, recursive: true);
                Directory.Move(stagingSkillDir, destSkillDir);

                if (stagingSubagentPath is not null)
                {
                    if (subagentCollides) File.Delete(destSubagentPath!);
                    File.Move(stagingSubagentPath, destSubagentPath!);
                }
            }
            catch
            {
                if (Directory.Exists(stagingSkillDir))
                {
                    try { Directory.Delete(stagingSkillDir, recursive: true); } catch { /* swallow cleanup */ }
                }
                if (stagingSubagentPath is not null && File.Exists(stagingSubagentPath))
                {
                    try { File.Delete(stagingSubagentPath); } catch { /* swallow cleanup */ }
                }
                throw;
            }

            return ImportResult.Success(folderRoot, hasSubagent: subagentRoot is not null);
        }
    }

    private static bool IsValidAgentId(string id) => !string.IsNullOrEmpty(id) && KebabCase.IsMatch(id);

    private static void NormalizeLegacySkillFolder(string skillDir, string id)
    {
        var skillMdPath = Path.Combine(skillDir, "SKILL.md");
        var sidecarPath = Path.Combine(skillDir, "orchestrator.json");
        var skillMd = File.Exists(skillMdPath) ? File.ReadAllText(skillMdPath) : string.Empty;
        var (frontmatter, body) = SplitFrontmatter(skillMd);
        var fields = frontmatter is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : ParseFrontmatter(frontmatter);

        var manifest = File.Exists(sidecarPath)
            ? JsonNode.Parse(File.ReadAllText(sidecarPath))?.AsObject() ?? new JsonObject()
            : new JsonObject();

        manifest["id"] = id;
        if (manifest["description"] is null)
        {
            manifest["description"] = fields.GetValueOrDefault("description") ?? id;
        }
        if (manifest["displayName"] is null && fields.TryGetValue("displayName", out var displayName))
        {
            manifest["displayName"] = displayName;
        }
        if (manifest["displayName"] is null && fields.TryGetValue("name", out var name) && !string.Equals(name, id, StringComparison.Ordinal))
        {
            manifest["displayName"] = name;
        }

        File.WriteAllText(
            Path.Combine(skillDir, "manifest.json"),
            manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
        File.WriteAllText(Path.Combine(skillDir, "body.md"), body);

        if (File.Exists(skillMdPath)) File.Delete(skillMdPath);
        if (File.Exists(sidecarPath)) File.Delete(sidecarPath);
    }

    private static (string? Frontmatter, string Body) SplitFrontmatter(string content)
    {
        if (!content.StartsWith("---", StringComparison.Ordinal))
        {
            return (null, content);
        }

        var afterOpening = content.AsSpan(3);
        var closingIndex = afterOpening.IndexOf("\n---");
        if (closingIndex < 0) return (null, content);

        var fm = afterOpening.Slice(0, closingIndex).ToString().TrimStart('\n');
        var afterClose = afterOpening.Slice(closingIndex + 4);
        var body = afterClose.ToString().TrimStart('\n');
        return (fm, body);
    }

    private static IReadOnlyDictionary<string, string> ParseFrontmatter(string fm)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
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
            result[key] = value;
        }
        return result;
    }

    private static string ResolveUnderRoot(string root, string segment)
    {
        var rootFull = Path.GetFullPath(root);
        var combined = Path.GetFullPath(Path.Combine(rootFull, segment));
        // Final guardrail — even if KebabCase validation slipped, a
        // resolved path outside the root is refused.
        if (!combined.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Resolved path '{combined}' is outside root '{rootFull}'.");
        }
        return combined;
    }

    public sealed record ImportResult(bool Ok, string? AgentId, ImportError? Error, string? Message, bool HasSubagent)
    {
        public static ImportResult Success(string id, bool hasSubagent) => new(true, id, null, null, hasSubagent);
        public static ImportResult Fail(ImportError code, string message) => new(false, null, code, message, false);
    }

    public enum ImportError
    {
        NotAZip,
        Empty,
        MissingTopFolder,
        InvalidId,
        UnsafePath,
        MissingFile,
        InvalidJson,
        Collision,
    }
}
