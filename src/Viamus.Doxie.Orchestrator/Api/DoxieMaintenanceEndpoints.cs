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

internal static class DoxieMaintenanceEndpoints
{
    public static IEndpointRouteBuilder MapDoxieMaintenanceEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/doxie/migrate-legacy/detect", (LegacySkillsMigrator migrator) =>
        {
            var detected = migrator.Detect();
            return Results.Ok(detected.Select(d => new
            {
                id = d.Id,
                hasSidecar = d.HasSidecar,
                alreadyCanonical = d.AlreadyCanonical,
            }));
        })
           .DisableAntiforgery();

        // Open a folder under the workspace root in the OS file explorer.
        // DoxieOS runs as a local-desktop tool, so the orchestrator IS on the
        // user's machine â€” Process.Start lands at the right place. The path
        // argument is resolved against the workspace root via StorageOptions
        // then validated to stay inside it (no parent-path traversal); a
        // hand-crafted POST cannot pop open arbitrary system folders.
        app.MapPost("/api/files/open", (string? path) =>
        {
            if (string.IsNullOrWhiteSpace(path)) return Results.BadRequest("path is required");
            var resolved = StorageOptions.ResolvePath(path);
            var workspaceRootPath = StorageOptions.ResolvePath(".");
            if (!resolved.StartsWith(workspaceRootPath, StringComparison.OrdinalIgnoreCase))
            {
                return Results.BadRequest("Refusing to open paths outside the workspace root");
            }
            if (!Directory.Exists(resolved) && !File.Exists(resolved))
            {
                return Results.NotFound($"Path does not exist: {resolved}");
            }
            try
            {
                // explorer.exe on Windows; fallback to xdg-open / open on POSIX.
                if (OperatingSystem.IsWindows())
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments = $"\"{resolved}\"",
                        UseShellExecute = true,
                    });
                }
                else
                {
                    var opener = OperatingSystem.IsMacOS() ? "open" : "xdg-open";
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = opener,
                        Arguments = $"\"{resolved}\"",
                        UseShellExecute = true,
                    });
                }
                return Results.Ok(new { path = resolved });
            }
            catch (Exception ex)
            {
                return Results.Problem($"Could not open folder: {ex.Message}", statusCode: 500);
            }
        }).DisableAntiforgery();

        app.MapPost("/api/run-files/open", (RunFileOpenRequest payload, RunFileBrowser browser) =>
        {
            if (payload is null) return Results.BadRequest("payload is required");
            if (string.IsNullOrWhiteSpace(payload.Identifier)) return Results.BadRequest("identifier is required");

            string resolved;
            try
            {
                resolved = payload.RootKind switch
                {
                    "workflow-run" => browser.ResolveWorkflowRunPath(payload.Identifier, payload.RelativePath ?? string.Empty),
                    "sandbox" => browser.ResolveSandboxPath(payload.Identifier, payload.RelativePath ?? string.Empty),
                    _ => throw new ArgumentException("rootKind must be 'workflow-run' or 'sandbox'."),
                };
            }
            catch (Exception ex) when (ex is ArgumentException or DirectoryNotFoundException or UnauthorizedAccessException)
            {
                return Results.BadRequest(ex.Message);
            }

            if (!Directory.Exists(resolved) && !File.Exists(resolved))
            {
                return Results.NotFound($"Path does not exist: {resolved}");
            }

            try
            {
                OpenLocalPath(resolved);
                return Results.Ok(new { path = resolved });
            }
            catch (Exception ex)
            {
                return Results.Problem($"Could not open path: {ex.Message}", statusCode: 500);
            }
        }).DisableAntiforgery();

        app.MapPost("/api/doxie/migrate-legacy", (LegacySkillsMigrator migrator, DoxieRegenerator regenerator) =>
        {
            try
            {
                var detected = migrator.Detect();
                var results = migrator.Migrate(detected);
                // Run the regen pipeline so the migrated skills get the
                // doxie/generated marker stamped on their .claude/ folders â€”
                // without that, a subsequent Detect() would re-flag them.
                var regen = regenerator.Regenerate();
                return Results.Ok(new
                {
                    detected = detected.Count,
                    migrated = results.Count(r => r.Migrated),
                    skipped = results.Count(r => !r.Migrated),
                    failures = results.Where(r => !r.Migrated).Select(r => new { r.Id, r.Reason }).ToList(),
                    regen = new { regen.SkillCount, regen.AgentCount, regen.LibraryCount, regen.WorkflowCount, regen.EmitterCount },
                });
            }
            catch (Exception ex)
            {
                return Results.Problem(detail: ex.Message, statusCode: 500, title: "Legacy migration failed");
            }
        })
           .DisableAntiforgery();

        app.MapPost("/api/doxie/regen", (DoxieRegenerator regenerator) =>
        {
            try
            {
                var result = regenerator.Regenerate();
                return Results.Ok(new
                {
                    skills = result.SkillCount,
                    agents = result.AgentCount,
                    libraries = result.LibraryCount,
                    workflows = result.WorkflowCount,
                    emitters = result.EmitterCount,
                });
            }
            catch (Exception ex)
            {
                return Results.Problem(detail: ex.Message, statusCode: 500, title: "Doxie regen failed");
            }
        })
           .DisableAntiforgery();

        // Import an entire library from a .zip upload. The archive is expected
        // to contain exactly one root folder (the library id) with .md memory
        // files and an optional library.json manifest inside. A flat archive
        // (no root folder) is also accepted and uses the zip's filename minus

        return app;
    }

    private static void OpenLocalPath(string resolved)
    {
        if (OperatingSystem.IsWindows())
        {
            if (Directory.Exists(resolved))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"\"{resolved}\"",
                    UseShellExecute = true,
                });
                return;
            }

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = resolved,
                UseShellExecute = true,
            });
            return;
        }

        var opener = OperatingSystem.IsMacOS() ? "open" : "xdg-open";
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = opener,
            Arguments = $"\"{resolved}\"",
            UseShellExecute = true,
        });
    }
}
