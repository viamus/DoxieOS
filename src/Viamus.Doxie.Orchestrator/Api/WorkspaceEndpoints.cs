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

internal static class WorkspaceEndpoints
{
    public static IEndpointRouteBuilder MapWorkspaceEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/workspaces", (WorkspaceCreateRequest payload, IWorkspaceStore store) =>
        {
            if (payload is null || string.IsNullOrEmpty(payload.Id))
            {
                return Results.BadRequest("id is required");
            }
            if (!System.Text.RegularExpressions.Regex.IsMatch(payload.Id, "^[a-z0-9]+(-[a-z0-9]+)*$"))
            {
                return Results.BadRequest($"Invalid workspace id '{payload.Id}' (must be kebab-case)");
            }
            if (payload.Libraries is { Count: > 0 } libraries)
            {
                foreach (var libId in libraries)
                {
                    if (!System.Text.RegularExpressions.Regex.IsMatch(libId, "^[a-z0-9]+(-[a-z0-9]+)*$"))
                    {
                        return Results.BadRequest($"Invalid library id '{libId}' (must be kebab-case)");
                    }
                }
            }

            try
            {
                var workspace = store.Create(payload.Id, payload.Name, payload.Description, payload.Libraries);
                return Results.Created($"/api/workspaces/{workspace.Id}", new
                {
                    id = workspace.Id,
                    name = workspace.Name,
                    description = workspace.Description,
                    path = workspace.Path,
                    createdAt = workspace.CreatedAt,
                    mountedLibraries = workspace.MountedLibraryIds,
                });
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(ex.Message);
            }
            catch (IOException ex)
            {
                return Results.Problem($"Could not create workspace: {ex.Message}", statusCode: 500);
            }
        })
           .DisableAntiforgery();

        // Replace the mounted-library set on a workspace. Body: { libraries: [...] }.
        // Validates each library id against the kebab-case regex; the store
        // dedupes and writes the workspace.json manifest atomically. Idempotent
        // â€” sending the same set twice is a no-op, sending an empty list
        // unmounts everything.
        app.MapPut("/api/workspaces/{workspaceId}/libraries", (
                string workspaceId,
                WorkspaceLibrariesRequest payload,
                IWorkspaceStore store) =>
        {
            if (string.IsNullOrEmpty(workspaceId)
                || !System.Text.RegularExpressions.Regex.IsMatch(workspaceId, "^[a-z0-9]+(-[a-z0-9]+)*$"))
            {
                return Results.BadRequest("Invalid workspace id (must be kebab-case)");
            }
            var libraries = payload?.Libraries ?? new List<string>();
            foreach (var libId in libraries)
            {
                if (!System.Text.RegularExpressions.Regex.IsMatch(libId, "^[a-z0-9]+(-[a-z0-9]+)*$"))
                {
                    return Results.BadRequest($"Invalid library id '{libId}' (must be kebab-case)");
                }
            }

            try
            {
                var workspace = store.SetMountedLibraries(workspaceId, libraries);
                return Results.Ok(new
                {
                    id = workspace.Id,
                    mountedLibraries = workspace.MountedLibraryIds,
                });
            }
            catch (InvalidOperationException ex)
            {
                return Results.NotFound(ex.Message);
            }
            catch (IOException ex)
            {
                return Results.Problem($"Could not update workspace: {ex.Message}", statusCode: 500);
            }
        })
           .DisableAntiforgery();

        // Open a workspace folder in Explorer, or spin up a new terminal that
        // immediately starts an interactive Claude session at the workspace
        // path. `target` decides which:
        //   - "folder" â†’ explorer.exe <path>
        //   - "claude" â†’ wt.exe / pwsh.exe / powershell.exe with cwd set and
        //     `claude` invoked as the first command. The shell stays open after
        //     the session ends (NoExit) so the user can re-run, inspect, or cd.
        //
        // Local-only convenience â€” DoxieOS itself is bound to localhost so we
        // trust the caller. No interactive prompt is shown to the user; the
        // new window appears with cwd set to the workspace folder.
        app.MapPost("/api/workspaces/{workspaceId}/open", (
                string workspaceId,
                string? target,
                IWorkspaceStore store) =>
        {
            if (string.IsNullOrEmpty(workspaceId)
                || !System.Text.RegularExpressions.Regex.IsMatch(workspaceId, "^[a-z0-9]+(-[a-z0-9]+)*$"))
            {
                return Results.BadRequest("Invalid workspace id (must be kebab-case)");
            }
            var ws = store.GetById(workspaceId);
            if (ws is null)
            {
                return Results.NotFound($"Workspace '{workspaceId}' not found");
            }

            var resolvedTarget = string.IsNullOrEmpty(target) ? "folder" : target.ToLowerInvariant();
            try
            {
                switch (resolvedTarget)
                {
                    case "folder":
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = "explorer.exe",
                            Arguments = $"\"{ws.Path}\"",
                            UseShellExecute = true,
                        });
                        return Results.NoContent();

                    case "claude":
                        // Spawn a terminal that boots straight into an interactive
                        // claude session at the workspace path. NoExit keeps the
                        // shell open after Claude exits so the user can rerun.
                        // Fallback chain: Windows Terminal â†’ pwsh â†’ powershell.
                        foreach (var attempt in new[]
                        {
                            new { Exe = "wt.exe", Args = $"-d \"{ws.Path}\" pwsh.exe -NoExit -Command claude" },
                            new { Exe = "pwsh.exe", Args = $"-NoExit -WorkingDirectory \"{ws.Path}\" -Command claude" },
                            new { Exe = "powershell.exe", Args = $"-NoExit -Command \"Set-Location -LiteralPath '{ws.Path}'; claude\"" },
                        })
                        {
                            try
                            {
                                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                                {
                                    FileName = attempt.Exe,
                                    Arguments = attempt.Args,
                                    UseShellExecute = true,
                                });
                                return Results.NoContent();
                            }
                            catch (System.ComponentModel.Win32Exception)
                            {
                                // Not installed â€” try the next fallback.
                            }
                        }
                        return Results.Problem(
                            "No terminal launcher found. Install Windows Terminal (wt.exe) or PowerShell.",
                            statusCode: 500);

                    default:
                        return Results.BadRequest($"Unknown target '{resolvedTarget}' (expected: folder, claude)");
                }
            }
            catch (Exception ex)
            {
                return Results.Problem($"Could not open: {ex.Message}", statusCode: 500);
            }
        });

        // Workspace file browser endpoints â€” used by /workspaces/{id}/files (the
        // in-browser fallback for "Open folder in Explorer" when DoxieOS is being
        // reached over the network). All three guard against path-traversal via
        // WorkspaceFileBrowser.ResolveSafePath which canonicalizes, follows
        // symlinks, and rejects anything that escapes the workspace root.

        // List immediate children of <workspace>/<path>. Lazy-loaded by the tree
        // view in WorkspaceFiles.razor â€” one call per folder expansion.
        app.MapGet("/api/workspaces/{workspaceId}/tree", (
                string workspaceId,
                string? path,
                bool? showHidden,
                IWorkspaceStore store) =>
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(workspaceId, "^[a-z0-9]+(-[a-z0-9]+)*$"))
            {
                return Results.BadRequest("Invalid workspace id (must be kebab-case)");
            }
            var ws = store.GetById(workspaceId);
            if (ws is null) return Results.NotFound($"Workspace '{workspaceId}' not found");

            var (listing, error) = WorkspaceFileBrowser.ListFolder(ws.Path, path, showHidden ?? false);
            return error is not null
                ? Results.BadRequest(error)
                : Results.Ok(new { entries = listing!.Entries, truncated = listing.Truncated });
        });

        // Read a single file's content for the right pane. Refuses huge files
        // (10 MB+); 1-10 MB returns truncated to first 1000 lines (logs/dumps).
        // Binary detection is conservative â€” extension blocklist + null-byte
        // sniff in the first 8 KB.
        app.MapGet("/api/workspaces/{workspaceId}/file", (
                string workspaceId,
                string? path,
                IWorkspaceStore store) =>
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(workspaceId, "^[a-z0-9]+(-[a-z0-9]+)*$"))
            {
                return Results.BadRequest("Invalid workspace id (must be kebab-case)");
            }
            var ws = store.GetById(workspaceId);
            if (ws is null) return Results.NotFound($"Workspace '{workspaceId}' not found");

            var (content, error) = WorkspaceFileBrowser.ReadFile(ws.Path, path);
            return error is not null ? Results.BadRequest(error) : Results.Ok(content);
        });

        // Stream the raw bytes of a workspace file as an attachment download.
        // Used by the right-pane download button and by the "binary file" card.
        app.MapGet("/api/workspaces/{workspaceId}/download", (
                string workspaceId,
                string? path,
                IWorkspaceStore store) =>
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(workspaceId, "^[a-z0-9]+(-[a-z0-9]+)*$"))
            {
                return Results.BadRequest("Invalid workspace id (must be kebab-case)");
            }
            var ws = store.GetById(workspaceId);
            if (ws is null) return Results.NotFound($"Workspace '{workspaceId}' not found");

            var (resolved, error) = WorkspaceFileBrowser.ResolveSafePath(ws.Path, path);
            if (error is not null || resolved is null) return Results.BadRequest(error ?? "Invalid path");
            if (!File.Exists(resolved)) return Results.NotFound("File not found");

            var name = Path.GetFileName(resolved);
            return Results.File(File.OpenRead(resolved), "application/octet-stream", fileDownloadName: name);
        });

        // Delete a workspace by id. Recursively removes the entire
        // <workspaces-root>/<id>/ folder. Path-traversal guarded by both the
        // kebab-case regex on the id AND a final root-prefix check on the
        // resolved absolute path (mirrors the library delete endpoint).
        app.MapDelete("/api/workspaces/{workspaceId}", (string workspaceId, StorageOptions storageOptions) =>
        {
            if (string.IsNullOrEmpty(workspaceId)
                || !System.Text.RegularExpressions.Regex.IsMatch(workspaceId, "^[a-z0-9]+(-[a-z0-9]+)*$"))
            {
                return Results.BadRequest("Invalid workspace id (must be kebab-case)");
            }

            var workspacesRoot = StorageOptions.ResolvePath(storageOptions.WorkspacesDirectory);
            var workspaceDir = Path.GetFullPath(Path.Combine(workspacesRoot, workspaceId));

            var isUnderRoot = workspaceDir.StartsWith(workspacesRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            if (!isUnderRoot)
            {
                return Results.BadRequest("Refusing to delete outside workspaces root");
            }

            if (!Directory.Exists(workspaceDir))
            {
                return Results.NotFound($"Workspace '{workspaceId}' not found");
            }

            try
            {
                RobustDirectoryDelete.Delete(workspaceDir);
            }
            catch (IOException ex)
            {
                return Results.Problem($"Could not delete workspace '{workspaceId}': {ex.Message}", statusCode: 500);
            }
            catch (UnauthorizedAccessException ex)
            {
                return Results.Problem($"Permission denied deleting '{workspaceId}': {ex.Message}", statusCode: 500);
            }

            return Results.NoContent();
        });

        // Spawn a new interactive console session inside a workspace. Body:
        // { workspaceId }. The store actually launches the PTY subprocess
        // (currently `claude` at the workspace path); the response carries

        return app;
    }
}
