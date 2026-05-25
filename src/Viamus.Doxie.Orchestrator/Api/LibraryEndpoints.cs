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

internal static class LibraryEndpoints
{
    public static IEndpointRouteBuilder MapLibraryEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/libraries/import", async (
                Microsoft.AspNetCore.Http.HttpRequest request,
                StorageOptions storageOptions,
                IDoxieCatalogStore catalogStore,
                DoxieRegenerator regenerator,
                bool? force,
                string? catalogId) =>
        {
            if (!request.HasFormContentType)
            {
                return Results.BadRequest("Expected multipart/form-data with a 'file' field");
            }
            var form = await request.ReadFormAsync();
            var file = form.Files.FirstOrDefault();
            if (file is null || file.Length == 0)
            {
                return Results.BadRequest("No file uploaded");
            }
            if (!file.FileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                return Results.BadRequest("Expected a .zip archive");
            }

            using var memoryStream = new MemoryStream();
            await file.CopyToAsync(memoryStream);
            memoryStream.Position = 0;

            System.IO.Compression.ZipArchive archive;
            try
            {
                archive = new System.IO.Compression.ZipArchive(memoryStream, System.IO.Compression.ZipArchiveMode.Read);
            }
            catch (InvalidDataException)
            {
                return Results.BadRequest("Uploaded file is not a valid zip archive");
            }

            using (archive)
            {
                // Discover the library id from the archive layout.
                var rootFolders = archive.Entries
                    .Select(e => e.FullName.Replace('\\', '/'))
                    .Where(name => name.Contains('/'))
                    .Select(name => name.Split('/', 2)[0])
                    .Where(folder => !string.IsNullOrEmpty(folder))
                    .Distinct()
                    .ToList();

                string libraryId;
                bool flat;
                if (rootFolders.Count == 1)
                {
                    libraryId = rootFolders[0];
                    flat = false;
                }
                else if (rootFolders.Count == 0)
                {
                    libraryId = Path.GetFileNameWithoutExtension(file.FileName);
                    flat = true;
                }
                else
                {
                    return Results.BadRequest($"Archive must contain exactly one root folder (the library id); found: {string.Join(", ", rootFolders)}");
                }

                if (!System.Text.RegularExpressions.Regex.IsMatch(libraryId, "^[a-z0-9]+(-[a-z0-9]+)*$"))
                {
                    return Results.BadRequest($"Library id '{libraryId}' must be kebab-case");
                }

                var selectedCatalog = catalogStore.FindCatalog(catalogId);
                if (selectedCatalog is null)
                {
                    return Results.BadRequest($"Catalog '{catalogId}' was not found");
                }

                var librariesRoot = selectedCatalog.LibrariesDirectory;
                var targetDir = Path.Combine(librariesRoot, libraryId);
                var existingDir = FindLibraryDirectory(catalogStore, storageOptions, libraryId, catalogId: null);
                if (existingDir is not null && force != true)
                {
                    return Results.Conflict($"Library '{libraryId}' already exists; pass ?force=true to overwrite");
                }
                if (existingDir is not null && !PathsEqual(existingDir, targetDir))
                {
                    RobustDirectoryDelete.Delete(existingDir);
                }
                Directory.CreateDirectory(targetDir);

                var written = 0;
                foreach (var entry in archive.Entries)
                {
                    if (string.IsNullOrEmpty(entry.Name)) continue; // pure directory entry

                    var fullName = entry.FullName.Replace('\\', '/');
                    var relative = flat ? fullName : fullName.Substring(libraryId.Length).TrimStart('/');
                    if (string.IsNullOrEmpty(relative)) continue;
                    if (relative.Contains("..") || Path.IsPathRooted(relative)) continue;

                    // Allowed entries:
                    // - .md memory files at the root
                    // - library.json manifest at the root
                    // - any file under examples/<...>/<file>.<ext> â€” code snippets
                    //   carried alongside memories so consumers can reference real
                    //   patterns when generating new code.
                    var isMd = relative.EndsWith(".md", StringComparison.OrdinalIgnoreCase);
                    var isManifest = relative.Equals("library.json", StringComparison.OrdinalIgnoreCase);
                    var isExample = relative.StartsWith("examples/", StringComparison.OrdinalIgnoreCase);
                    if (!isMd && !isManifest && !isExample) continue;

                    var targetPath = Path.Combine(targetDir, relative);
                    var targetFolder = Path.GetDirectoryName(targetPath);
                    if (!string.IsNullOrEmpty(targetFolder)) Directory.CreateDirectory(targetFolder);

                    await using var entryStream = entry.Open();
                    await using var fileStream = File.Create(targetPath);
                    await entryStream.CopyToAsync(fileStream);
                    written++;
                }

                // Re-shim â€” emits .claude/libraries/<id>/ from the freshly imported canonical.
                regenerator.Regenerate();
                return Results.Created($"/libraries/{libraryId}", new { libraryId, catalogId = selectedCatalog.Id, files = written });
            }
        })
           .DisableAntiforgery();

        // Stream a library as a .zip download. Files are placed inside a
        // "<library-id>/" folder inside the archive so the user can drop the
        // extracted folder directly into another DoxieOS install's
        // .doxie/libraries/ tree.
        app.MapGet("/api/libraries/{libraryId}/export", async (
                string libraryId,
                StorageOptions storageOptions,
                IDoxieCatalogStore catalogStore,
                string? catalogId,
                HttpContext ctx) =>
        {
            if (string.IsNullOrEmpty(libraryId)
                || !System.Text.RegularExpressions.Regex.IsMatch(libraryId, "^[a-z0-9]+(-[a-z0-9]+)*$"))
            {
                return Results.BadRequest("Invalid library id (must be kebab-case)");
            }

            var libraryDir = FindLibraryDirectory(catalogStore, storageOptions, libraryId, catalogId);
            if (!Directory.Exists(libraryDir))
            {
                return Results.NotFound($"Library '{libraryId}' not found");
            }

            using var memoryStream = new MemoryStream();
            using (var archive = new System.IO.Compression.ZipArchive(memoryStream, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
            {
                // Recurse so examples/<repo>/<file>.<ext> trees are carried along.
                // Top-level lives at <library-id>/, subdirectories preserve their
                // path under it.
                foreach (var file in Directory.EnumerateFiles(libraryDir, "*", SearchOption.AllDirectories))
                {
                    var relative = Path.GetRelativePath(libraryDir, file).Replace(Path.DirectorySeparatorChar, '/');
                    var entry = archive.CreateEntry($"{libraryId}/{relative}", System.IO.Compression.CompressionLevel.Optimal);
                    await using var entryStream = entry.Open();
                    await using var fileStream = File.OpenRead(file);
                    await fileStream.CopyToAsync(entryStream);
                }
            }

            return Results.File(memoryStream.ToArray(), "application/zip", $"{libraryId}.zip");
        });

        // Delete a library by id. Recursively removes the entire
        // .doxie/libraries/<id>/ folder. Path-traversal guarded by both the
        // kebab-case regex on the id AND a final root-prefix check on the
        // resolved absolute path.
        app.MapDelete("/api/libraries/{libraryId}", (string libraryId, StorageOptions storageOptions, IDoxieCatalogStore catalogStore, DoxieRegenerator regenerator, string? catalogId) =>
        {
            if (string.IsNullOrEmpty(libraryId)
                || !System.Text.RegularExpressions.Regex.IsMatch(libraryId, "^[a-z0-9]+(-[a-z0-9]+)*$"))
            {
                return Results.BadRequest("Invalid library id (must be kebab-case)");
            }

            var libraryDir = FindLibraryDirectory(catalogStore, storageOptions, libraryId, catalogId);
            if (libraryDir is null)
            {
                return Results.NotFound($"Library '{libraryId}' not found");
            }
            libraryDir = Path.GetFullPath(libraryDir);
            var librariesRoot = Path.GetFullPath(Path.GetDirectoryName(libraryDir)!);

            // Defence-in-depth: even after the regex check above, refuse to
            // delete anything outside the libraries root, or the root itself.
            var isUnderRoot = libraryDir.StartsWith(librariesRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            if (!isUnderRoot)
            {
                return Results.BadRequest("Refusing to delete outside libraries root");
            }

            try
            {
                RobustDirectoryDelete.Delete(libraryDir);
            }
            catch (IOException ex)
            {
                return Results.Problem($"Could not delete library '{libraryId}': {ex.Message}", statusCode: 500);
            }
            catch (UnauthorizedAccessException ex)
            {
                return Results.Problem($"Permission denied deleting '{libraryId}': {ex.Message}", statusCode: 500);
            }

            // Re-shim â€” emits .claude/libraries/ minus the deleted folder.
            regenerator.Regenerate();
            return Results.NoContent();
        });

        // Create a new user-owned Workspace. Body: { id, name?, description?, libraries? }.
        // `id` must be kebab-case; uniqueness enforced by the store. `libraries`
        // (optional) seeds the manifest's mounted-library list. The resulting
        // folder lives under <workspaces-root>/<id>/ and ships the conventional
        // scaffold (workspace.json, WORKSPACE.md, memory/MEMORY.md).

        return app;
    }

    private static string? FindLibraryDirectory(IDoxieCatalogStore catalogStore, StorageOptions storageOptions, string libraryId, string? catalogId)
    {
        if (!string.IsNullOrWhiteSpace(catalogId))
        {
            var selected = catalogStore.FindCatalog(catalogId);
            if (selected is null) return null;
            var selectedDir = Path.GetFullPath(Path.Combine(selected.LibrariesDirectory, libraryId));
            return Directory.Exists(selectedDir) ? selectedDir : null;
        }

        foreach (var catalog in catalogStore.ListCatalogs().Reverse())
        {
            var dir = Path.GetFullPath(Path.Combine(catalog.LibrariesDirectory, libraryId));
            if (Directory.Exists(dir)) return dir;
        }

        var fallback = Path.GetFullPath(Path.Combine(StorageOptions.ResolvePath(storageOptions.LibrariesDirectory), libraryId));
        return Directory.Exists(fallback) ? fallback : null;
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
}
