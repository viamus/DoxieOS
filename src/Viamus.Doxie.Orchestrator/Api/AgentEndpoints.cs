using Microsoft.AspNetCore.Http;
using Viamus.Doxie.Orchestrator.Agents;
using Viamus.Doxie.Orchestrator.Agents.Doxie;
using Viamus.Doxie.Orchestrator.Builder;
using Viamus.Doxie.Orchestrator.Common;
using Viamus.Doxie.Orchestrator.Context;
using Viamus.Doxie.Orchestrator.Components.Shared;
using Viamus.Doxie.Orchestrator.Notifications;
using Viamus.Doxie.Orchestrator.Runtime;
using Viamus.Doxie.Orchestrator.Services;
using Viamus.Doxie.Orchestrator.Workflows;

namespace Viamus.Doxie.Orchestrator.Api;

internal static class AgentEndpoints
{
    public static IEndpointRouteBuilder MapAgentEndpoints(this IEndpointRouteBuilder app)
    {
        var agentIconKeys = AgentIconPalette.Keys;

        // === Agents ===
        // Move an agent to a different category. Reads the canonical manifest
        // at .doxie/skills/<id>/manifest.json, swaps the `category` field, and
        // rewrites â€” the Doxie regen pipeline picks the change up via its
        // filesystem watcher (or the explicit Refresh below for the UI). The
        // sealed-category check on the in-memory descriptor (Doxie/Connector)
        // is enforced server-side so a hand-crafted PATCH can't bypass the UI.
        app.MapPatch("/api/agents/{agentId}/category", (
            string agentId,
            AgentCategoryUpdate payload,
            StorageOptions storageOptions,
            IAgentCatalog catalog,
            DoxieRegenerator regenerator) =>
        {
            if (string.IsNullOrEmpty(agentId)
                || !System.Text.RegularExpressions.Regex.IsMatch(agentId, "^[a-z0-9]+(-[a-z0-9]+)*$"))
            {
                return Results.BadRequest("Invalid agent id (must be kebab-case)");
            }
            if (payload is null || string.IsNullOrWhiteSpace(payload.Category))
            {
                return Results.BadRequest("category is required");
            }
            // Accept either a built-in name (case-insensitive) or a user-defined
            // custom label. The palette helper enforces the same shape used at
            // Save: alphanumeric + hyphens/spaces, 1-32 chars, leading letter.
            if (!AgentCategoryPalette.TryParse(payload.Category, out var newCategory, out var newCustomLabel))
            {
                return Results.BadRequest(
                    $"Invalid category '{payload.Category}' â€” pick a built-in (Builder, Inspector, " +
                    $"Fixer, Connector, Other) or a custom label (1-32 chars: letters, digits, " +
                    $"spaces or hyphens; must start with a letter).");
            }

            var existing = catalog.FindById(agentId);
            if (existing is null) return Results.NotFound($"Agent '{agentId}' not found");
            if (existing.CategorySealed)
            {
                return Results.Conflict(
                    $"Agent '{agentId}' is in the sealed category '{existing.DisplayCategory}' and cannot be re-categorised by the user.");
            }
            // Built-in sealed categories can't be assigned by users â€” they're
            // reserved for first-party authoring kit / workflow glue. Custom
            // labels and the user-editable built-ins flow through.
            if (newCustomLabel is null && newCategory is AgentCategory.Doxie or AgentCategory.Connector)
            {
                return Results.Conflict(
                    $"Cannot move user agents into the sealed category '{newCategory}'.");
            }
            var icon = string.IsNullOrWhiteSpace(payload.Icon) ? null : payload.Icon.Trim();
            if (icon is not null && !agentIconKeys.Contains(icon))
            {
                return Results.BadRequest($"Invalid icon '{payload.Icon}'.");
            }

            var skillsRoot = AgentApiPaths.ResolveSkillsRoot(storageOptions, existing);
            var manifestPath = Path.Combine(skillsRoot, agentId, "manifest.json");
            if (!File.Exists(manifestPath)) return Results.NotFound($"Manifest for '{agentId}' not found at {manifestPath}");

            try
            {
                // Round-trip via JsonNode so we don't have to mirror every field
                // of the canonical schema here â€” we touch one property and the
                // rest passes through verbatim, preserving comment-free formatting.
                var raw = File.ReadAllText(manifestPath, System.Text.Encoding.UTF8);
                var node = System.Text.Json.Nodes.JsonNode.Parse(raw)?.AsObject()
                    ?? throw new InvalidDataException("manifest.json root is not an object");
                // Custom label wins on the wire so it round-trips; built-in
                // resolves to canonical PascalCase enum name.
                var persisted = newCustomLabel ?? newCategory.ToString();
                node["category"] = persisted;
                if (icon is null)
                {
                    node.Remove("icon");
                }
                else
                {
                    node["icon"] = icon;
                }
                var rewritten = node.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) + "\n";
                File.WriteAllText(manifestPath, rewritten, System.Text.Encoding.UTF8);
            }
            catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or InvalidDataException)
            {
                return Results.Problem($"Could not update manifest: {ex.Message}", statusCode: 500);
            }

            regenerator.Regenerate();
            catalog.Refresh();
            return Results.Ok(new { id = agentId, category = newCustomLabel ?? newCategory.ToString(), icon });
        })
           .DisableAntiforgery();

        // Delete an agent by id. Removes the entire <skills-dir>/<id>/ folder
        // recursively. Path-traversal guarded by both the kebab-case regex on
        // the id AND a final root-prefix check on the resolved absolute path
        // (mirrors the libraries / workspaces delete endpoints). Refreshes the
        // in-memory agent catalog so /agents reflects the deletion immediately.
        app.MapDelete("/api/agents/{agentId}", (string agentId, StorageOptions storageOptions, IAgentCatalog catalog, DoxieRegenerator regenerator) =>
        {
            if (string.IsNullOrEmpty(agentId)
                || !System.Text.RegularExpressions.Regex.IsMatch(agentId, "^[a-z0-9]+(-[a-z0-9]+)*$"))
            {
                return Results.BadRequest("Invalid agent id (must be kebab-case)");
            }

            var existing = catalog.FindById(agentId);
            if (existing is null)
            {
                return Results.NotFound($"Agent '{agentId}' not found");
            }

            var skillsRoot = AgentApiPaths.ResolveSkillsRoot(storageOptions, existing);
            var agentDir = Path.GetFullPath(Path.Combine(skillsRoot, agentId));

            var isUnderRoot = agentDir.StartsWith(skillsRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            if (!isUnderRoot)
            {
                return Results.BadRequest("Refusing to delete outside skills root");
            }

            if (!Directory.Exists(agentDir))
            {
                return Results.NotFound($"Agent '{agentId}' not found");
            }

            try
            {
                RobustDirectoryDelete.Delete(agentDir);
            }
            catch (IOException ex)
            {
                return Results.Problem($"Could not delete agent '{agentId}': {ex.Message}", statusCode: 500);
            }
            catch (UnauthorizedAccessException ex)
            {
                return Results.Problem($"Permission denied deleting '{agentId}': {ex.Message}", statusCode: 500);
            }

            // Re-shim â€” emits .claude/skills/ minus the deleted folder. The
            // ClaudeShimEmitter's orphan-cleanup respects the doxie/generated
            // marker so hand-authored shim folders stay safe.
            regenerator.Regenerate();
            catalog.Refresh();
            return Results.NoContent();
        });

        // Export an agent as a single zip for sharing / backup. The zip
        // contains the skill folder <id>/ plus, when present, the paired
        // subagent file <id>.md at the zip root â€” mirror of the on-disk
        // layout under .claude/skills/ and .claude/agents/. Streamed
        // directly so large agents don't sit in memory longer than needed.
        app.MapGet("/api/agents/{agentId}/export", (string agentId, StorageOptions storageOptions, IAgentCatalog catalog) =>
        {
            if (string.IsNullOrEmpty(agentId)
                || !System.Text.RegularExpressions.Regex.IsMatch(agentId, "^[a-z0-9]+(-[a-z0-9]+)*$"))
            {
                return Results.BadRequest("Invalid agent id (must be kebab-case)");
            }

            var existing = catalog.FindById(agentId);
            if (existing is null)
            {
                return Results.NotFound($"Agent '{agentId}' not found");
            }

            var skillsRoot = AgentApiPaths.ResolveSkillsRoot(storageOptions, existing);
            var agentsRoot = AgentApiPaths.ResolveAgentsRoot(storageOptions, existing);
            try
            {
                var ms = new MemoryStream();
                AgentTransfer.ExportToZip(skillsRoot, agentsRoot, agentId, ms);
                ms.Position = 0;
                return Results.File(ms, "application/zip", $"{agentId}.zip");
            }
            catch (DirectoryNotFoundException)
            {
                return Results.NotFound($"Agent '{agentId}' not found");
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(ex.Message);
            }
        });

        // Import an agent from a zip. multipart/form-data with a single file
        // part named "file" and an optional "overwrite" form field. Validates
        // the zip layout, parses orchestrator.json, then extracts atomically
        // into <skills-dir>/<id>/ and refreshes the catalog so the new agent
        // shows up in the UI immediately. The same file-watcher we installed
        // would catch this on its own â€” the explicit Refresh just guarantees
        // the response sees fresh state for the redirect.
        app.MapPost("/api/agents/import", async (
                Microsoft.AspNetCore.Http.HttpRequest request,
                StorageOptions storageOptions,
                IDoxieCatalogStore catalogStore,
                IAgentCatalog catalog,
                DoxieRegenerator regenerator) =>
        {
            if (!request.HasFormContentType)
            {
                return Results.BadRequest(new { error = "Expected multipart/form-data with a 'file' part." });
            }

            var form = await request.ReadFormAsync();
            var file = form.Files["file"];
            if (file is null || file.Length == 0)
            {
                return Results.BadRequest(new { error = "Missing 'file' part." });
            }
            var overwrite = string.Equals(form["overwrite"].ToString(), "true", StringComparison.OrdinalIgnoreCase);
            var selectedCatalog = catalogStore.FindCatalog(form["catalogId"].ToString());
            if (selectedCatalog is null)
            {
                return Results.BadRequest(new { error = $"Catalog '{form["catalogId"]}' was not found." });
            }

            string? importId;
            try
            {
                await using var preview = file.OpenReadStream();
                var inspection = TryReadImportAgentId(preview);
                if (!inspection.Ok)
                {
                    return Results.Json(new { error = inspection.Message, code = inspection.Code }, statusCode: 400);
                }
                importId = inspection.AgentId;
            }
            catch (IOException ex)
            {
                return Results.Problem($"Could not inspect agent import: {ex.Message}", statusCode: 500);
            }

            if (!string.IsNullOrWhiteSpace(importId))
            {
                var existingDirs = FindAgentDirectories(catalogStore, importId).ToList();
                if (existingDirs.Count > 0 && !overwrite)
                {
                    return Results.Json(
                        new
                        {
                            error = $"An existing agent '{importId}' would be replaced. Pass overwrite=true to proceed.",
                            code = AgentTransfer.ImportError.Collision.ToString()
                        },
                        statusCode: 409);
                }
            }

            var skillsRoot = selectedCatalog.SkillsDirectory;
            var agentsRoot = selectedCatalog.AgentsDirectory;

            AgentTransfer.ImportResult result;
            try
            {
                await using var stream = file.OpenReadStream();
                result = AgentTransfer.ImportFromZip(stream, skillsRoot, agentsRoot, overwrite);
            }
            catch (IOException ex)
            {
                return Results.Problem($"Could not import agent: {ex.Message}", statusCode: 500);
            }
            catch (UnauthorizedAccessException ex)
            {
                return Results.Problem($"Permission denied importing: {ex.Message}", statusCode: 500);
            }

            if (!result.Ok)
            {
                // Collisions are a 409 Conflict so the UI can prompt for
                // overwrite without treating it as a generic 400.
                var status = result.Error == AgentTransfer.ImportError.Collision ? 409 : 400;
                return Results.Json(new { error = result.Message, code = result.Error?.ToString() }, statusCode: status);
            }

            if (!string.IsNullOrWhiteSpace(result.AgentId))
            {
                var selectedSkillDir = Path.GetFullPath(Path.Combine(selectedCatalog.SkillsDirectory, result.AgentId));
                var selectedAgentPath = Path.GetFullPath(Path.Combine(selectedCatalog.AgentsDirectory, $"{result.AgentId}.md"));
                foreach (var path in FindAgentDirectories(catalogStore, result.AgentId))
                {
                    var fullPath = Path.GetFullPath(path);
                    if (string.Equals(fullPath, selectedSkillDir, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(fullPath, selectedAgentPath, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (Directory.Exists(fullPath))
                    {
                        RobustDirectoryDelete.Delete(fullPath);
                    }
                    else if (File.Exists(fullPath))
                    {
                        File.Delete(fullPath);
                    }
                }
            }

            // Re-shim â€” emits .claude/skills/<id>/ from the freshly imported
            // canonical files (when the import wrote canonical) or refreshes
            // any existing shim if the import wrote .claude/-shaped legacy
            // (then a future migrate run folds it into .doxie/).
            regenerator.Regenerate();
            catalog.Refresh();
            return Results.Ok(new { ok = true, agentId = result.AgentId, catalogId = selectedCatalog.Id, href = $"/agents/{result.AgentId}" });
        })
           .DisableAntiforgery();

        return app;
    }

    private static IEnumerable<string> FindAgentDirectories(IDoxieCatalogStore catalogStore, string agentId)
    {
        foreach (var catalog in catalogStore.ListCatalogs())
        {
            var skillDir = Path.GetFullPath(Path.Combine(catalog.SkillsDirectory, agentId));
            if (Directory.Exists(skillDir)) yield return skillDir;

            var agentPath = Path.GetFullPath(Path.Combine(catalog.AgentsDirectory, $"{agentId}.md"));
            if (File.Exists(agentPath)) yield return agentPath;
        }
    }

    private static AgentImportInspection TryReadImportAgentId(Stream input)
    {
        System.IO.Compression.ZipArchive archive;
        try
        {
            archive = new System.IO.Compression.ZipArchive(input, System.IO.Compression.ZipArchiveMode.Read, leaveOpen: false);
        }
        catch (InvalidDataException ex)
        {
            return AgentImportInspection.Fail(AgentTransfer.ImportError.NotAZip, $"Not a valid zip file: {ex.Message}");
        }

        using (archive)
        {
            string? folderRoot = null;
            foreach (var entry in archive.Entries)
            {
                if (string.IsNullOrEmpty(entry.FullName)) continue;
                var normalized = entry.FullName.Replace('\\', '/');
                if (normalized.Contains("..") || normalized.StartsWith('/') || Path.IsPathRooted(normalized))
                {
                    return AgentImportInspection.Fail(AgentTransfer.ImportError.UnsafePath, $"Refusing entry with traversal-like path: '{entry.FullName}'");
                }

                var slash = normalized.IndexOf('/');
                if (slash < 0) continue;

                var top = normalized[..slash];
                if (folderRoot is null)
                {
                    folderRoot = top;
                }
                else if (!string.Equals(folderRoot, top, StringComparison.Ordinal))
                {
                    return AgentImportInspection.Fail(AgentTransfer.ImportError.MissingTopFolder, "Zip must contain exactly one top-level folder.");
                }
            }

            if (folderRoot is null)
            {
                return AgentImportInspection.Fail(AgentTransfer.ImportError.Empty, "Zip has no skill folder.");
            }
            if (!System.Text.RegularExpressions.Regex.IsMatch(folderRoot, "^[a-z0-9]+(-[a-z0-9]+)*$"))
            {
                return AgentImportInspection.Fail(AgentTransfer.ImportError.InvalidId, $"Top-level folder '{folderRoot}' is not a valid agent id (must be kebab-case).");
            }

            return AgentImportInspection.Success(folderRoot);
        }
    }

    private sealed record AgentImportInspection(bool Ok, string? AgentId, string? Code, string? Message)
    {
        public static AgentImportInspection Success(string agentId) => new(true, agentId, null, null);
        public static AgentImportInspection Fail(AgentTransfer.ImportError error, string message) => new(false, null, error.ToString(), message);
    }
}
