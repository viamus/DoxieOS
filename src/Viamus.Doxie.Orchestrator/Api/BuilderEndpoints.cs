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

internal static class BuilderEndpoints
{
    public static IEndpointRouteBuilder MapBuilderEndpoints(this IEndpointRouteBuilder app)
    {
        var agentIconKeys = AgentIconPalette.Keys;

        // === Agent / Workflow Builder ===
        // Interactive PTY-backed authoring. The browser opens an Agent Builder
        // page, hits POST /api/builder/agent â†’ an empty sandbox is allocated,
        // claude is spawned inside it, and the session id is returned. The
        // page then attaches xterm to that session via the existing
        // /hubs/console SignalR bridge and polls /api/builder/{id}/manifest
        // to render the right-pane preview. On Save, /api/builder/{id}/save
        // promotes the manifest into the canonical skills directory.

        // Start a new Agent Builder session. Body (optional):
        //   { workspaceId?: string, editAgentId?: string }
        // When workspaceId is supplied, the chosen workspace's mounted libraries
        // are inlined into the sandbox provider context. When editAgentId is
        // supplied, the manifest is pre-seeded from the existing agent and the
        // sandbox context tells the active CLI to
        // enrich it from the original SKILL.md before iterating with the user;
        // Save then promotes with overwrite.
        app.MapPost("/api/builder/agent", async (
                StartAgentBuilderBody? body,
                BuilderSessionFactory factory) =>
        {
            try
            {
                var info = await factory.StartAgentBuilderAsync(body?.WorkspaceId, body?.EditAgentId);
                return Results.Created($"/api/builder/{info.SessionId}", new
                {
                    sessionId = info.SessionId,
                    kind = info.Kind.ToString().ToLowerInvariant(),
                    sandboxTag = info.SandboxTag,
                    sandboxPath = info.SandboxPath,
                    label = info.Label,
                    attachedWorkspaceId = info.AttachedWorkspaceId,
                    editAgentId = info.EditAgentId,
                    startedAt = info.StartedAt,
                });
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                return Results.Problem($"Could not start builder session: {ex.Message}", statusCode: 500);
            }
        })
           .DisableAntiforgery();

        // List live builder sessions of a given kind. Used by the Agent / Workflow
        // Builder pages to populate the "resume an open session" sidebar.
        app.MapGet("/api/builder/sessions", (string? kind, BuilderSessionFactory factory) =>
        {
            var parsed = string.Equals(kind, "workflow", StringComparison.OrdinalIgnoreCase)
                ? BuilderKind.Workflow
                : BuilderKind.Agent;
            var sessions = factory.List(parsed)
                .OrderByDescending(s => s.StartedAt)
                .Select(s => new
                {
                    sessionId = s.SessionId,
                    kind = s.Kind.ToString().ToLowerInvariant(),
                    sandboxTag = s.SandboxTag,
                    label = s.Label,
                    attachedWorkspaceId = s.AttachedWorkspaceId,
                    editAgentId = s.EditAgentId,
                    startedAt = s.StartedAt,
                })
                .ToList();
            return Results.Ok(sessions);
        });

        // Read the current manifest draft for a builder session. Returns the
        // raw parsed JSON if the manifest exists, an empty object if the file
        // hasn't been written yet, or a 404 if the session is unknown.
        app.MapGet("/api/builder/{sessionId}/manifest", (string sessionId, BuilderSessionFactory factory) =>
        {
            var info = factory.Find(sessionId);
            if (info is null) return Results.NotFound(new { error = "Builder session not found" });
            if (!File.Exists(info.ManifestPath))
            {
                return Results.Ok(new
                {
                    present = false,
                    valid = true,
                    manifest = (object?)null,
                    error = (string?)null,
                    errors = Array.Empty<string>(),
                    updatedAt = (DateTime?)null,
                });
            }
            try
            {
                using var fs = new FileStream(info.ManifestPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var manifest = System.Text.Json.JsonSerializer.Deserialize<AgentManifest>(fs, AgentManifestPromoter.ParseOptionsLenient);
                // Always run the validator so the right pane can show field-
                // level problems before Save. Lenient parsing means typos are
                // silently dropped here; Save runs the strict parser to catch
                // those. valid=false is reserved for JSON parse failures â€”
                // semantic problems flow through errors[] with valid=true so
                // the preview can still render the (partial) manifest.
                var errors = AgentManifestPromoter.Validate(manifest);
                return Results.Ok(new
                {
                    present = true,
                    valid = manifest is not null,
                    manifest,
                    error = errors.Count == 0 ? null : string.Join("; ", errors),
                    errors,
                    updatedAt = File.GetLastWriteTimeUtc(info.ManifestPath),
                });
            }
            catch (System.Text.Json.JsonException ex)
            {
                var msg = $"Manifest is not valid JSON: {ex.Message}";
                return Results.Ok(new
                {
                    present = true,
                    valid = false,
                    manifest = (object?)null,
                    error = msg,
                    errors = new[] { msg },
                    updatedAt = File.GetLastWriteTimeUtc(info.ManifestPath),
                });
            }
        });

        // Update only the icon on an in-progress agent-builder draft. The
        // builder console remains the author of the rest of the manifest; this
        // endpoint is the UI's narrow escape hatch for a visual preference.
        app.MapPatch("/api/builder/{sessionId}/manifest/icon", (
            string sessionId,
            AgentDraftIconUpdate payload,
            BuilderSessionFactory factory) =>
        {
            var info = factory.Find(sessionId);
            if (info is null) return Results.NotFound(new { error = "Builder session not found" });
            if (info.Kind != BuilderKind.Agent)
            {
                return Results.BadRequest(new { error = "This endpoint is only valid for agent builder sessions." });
            }
            if (!File.Exists(info.ManifestPath))
            {
                return Results.BadRequest(new { error = "No manifest yet Ã¢â‚¬â€ wait for the chat to produce one." });
            }

            var icon = string.IsNullOrWhiteSpace(payload.Icon) ? null : payload.Icon.Trim();
            if (icon is not null && !agentIconKeys.Contains(icon))
            {
                return Results.BadRequest(new { error = $"Invalid icon '{payload.Icon}'." });
            }

            try
            {
                var raw = File.ReadAllText(info.ManifestPath, System.Text.Encoding.UTF8);
                var node = System.Text.Json.Nodes.JsonNode.Parse(raw)?.AsObject()
                    ?? throw new InvalidDataException("manifest.json root is not an object");
                if (icon is null)
                {
                    node.Remove("icon");
                }
                else
                {
                    node["icon"] = icon;
                }
                var rewritten = node.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) + "\n";
                File.WriteAllText(info.ManifestPath, rewritten, System.Text.Encoding.UTF8);
                return Results.Ok(new { ok = true, icon, updatedAt = File.GetLastWriteTimeUtc(info.ManifestPath) });
            }
            catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or InvalidDataException)
            {
                return Results.Problem($"Could not update manifest: {ex.Message}", statusCode: 500);
            }
        })
           .DisableAntiforgery();

        // Promote the in-progress manifest into the canonical agents catalog.
        // Body: { overwrite?: boolean }. Refreshes IAgentCatalog on success
        // so the new agent appears on /agents without an app restart.
        app.MapPost("/api/builder/{sessionId}/save", async (
                string sessionId,
                BuilderSessionFactory factory,
                AgentManifestPromoter promoter,
                IConsoleSessionStore consoleStore,
                Microsoft.AspNetCore.Http.HttpRequest request) =>
        {
            var info = factory.Find(sessionId);
            if (info is null) return Results.NotFound(new { error = "Builder session not found" });
            if (!File.Exists(info.ManifestPath))
            {
                return Results.BadRequest(new { error = "No manifest yet â€” wait for the chat to produce one." });
            }

            bool overwrite = false;
            string? catalogId = null;
            try
            {
                if (request.ContentLength > 0)
                {
                    var body = await request.ReadFromJsonAsync<SaveAgentBody>();
                    overwrite = body?.Overwrite ?? false;
                    catalogId = body?.CatalogId;
                }
            }
            catch (System.Text.Json.JsonException) { /* tolerate empty / malformed body */ }

            AgentManifest? manifest;
            try
            {
                await using var fs = new FileStream(info.ManifestPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                // Strict parsing: unknown / typo'd properties cause an explicit
                // failure here instead of being silently dropped and surfacing
                // later as a vague "field is required" downstream.
                manifest = await System.Text.Json.JsonSerializer.DeserializeAsync<AgentManifest>(fs, AgentManifestPromoter.ParseOptionsStrict);
            }
            catch (System.Text.Json.JsonException ex)
            {
                var msg = $"Manifest is not valid JSON or contains an unknown field: {ex.Message}";
                return Results.BadRequest(new { error = msg, errors = new[] { msg } });
            }

            if (manifest is null)
            {
                var empty = "Manifest is empty";
                return Results.BadRequest(new { error = empty, errors = new[] { empty } });
            }

            var result = promoter.Promote(manifest, overwrite, catalogId);
            if (!result.Ok)
            {
                return Results.BadRequest(new { error = result.Error, errors = result.Errors });
            }

            // Save consumes the session â€” kill the PTY so it doesn't leak.
            // The sandbox folder stays on disk for the housekeeping pass to
            // collect alongside .runs/, matching the workspace-wide policy.
            try { await consoleStore.RemoveAsync(sessionId); }
            catch (Exception) { /* best-effort cleanup, the agent is already promoted */ }

            return Results.Ok(new
            {
                ok = true,
                agentId = result.AgentId,
                catalogId = string.IsNullOrWhiteSpace(catalogId) ? DoxieCatalogStore.DefaultCatalogId : catalogId,
                href = $"/agents/{result.AgentId}",
            });
        })
           .DisableAntiforgery();

        // === Workflow Builder ===
        // Counterpart to /api/builder/agent â€” same lifecycle (start / read /
        // save), different schema. Cross-creation: the workflow promoter walks
        // .draft/new-agents/ first and promotes each via AgentManifestPromoter
        // before persisting the workflow itself.

        app.MapPost("/api/builder/workflow", async (
                StartWorkflowBuilderBody? body,
                BuilderSessionFactory factory) =>
        {
            try
            {
                var info = await factory.StartWorkflowBuilderAsync(body?.WorkspaceId, body?.EditWorkflowId);
                return Results.Created($"/api/builder/{info.SessionId}", new
                {
                    sessionId = info.SessionId,
                    kind = info.Kind.ToString().ToLowerInvariant(),
                    sandboxTag = info.SandboxTag,
                    sandboxPath = info.SandboxPath,
                    label = info.Label,
                    attachedWorkspaceId = info.AttachedWorkspaceId,
                    editWorkflowId = info.EditWorkflowId,
                    startedAt = info.StartedAt,
                });
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                return Results.Problem($"Could not start workflow builder session: {ex.Message}", statusCode: 500);
            }
        })
           .DisableAntiforgery();

        // Workflow-flavoured manifest read. Returns the parsed WorkflowManifest
        // AND a separate list of pending new-agent drafts the session has
        // proposed â€” the right pane renders both side by side.
        app.MapGet("/api/builder/{sessionId}/workflow-manifest", (
                string sessionId,
                BuilderSessionFactory factory,
                WorkflowManifestPromoter wfPromoter) =>
        {
            var info = factory.Find(sessionId);
            if (info is null) return Results.NotFound(new { error = "Builder session not found" });
            if (info.Kind != BuilderKind.Workflow)
            {
                return Results.BadRequest(new { error = "This endpoint is only valid for workflow builder sessions." });
            }

            WorkflowManifest? manifest = null;
            bool present = File.Exists(info.ManifestPath);
            bool valid = true;
            string? error = null;
            DateTime? updatedAt = present ? File.GetLastWriteTimeUtc(info.ManifestPath) : null;

            if (present)
            {
                try
                {
                    using var fs = new FileStream(info.ManifestPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    manifest = System.Text.Json.JsonSerializer.Deserialize<WorkflowManifest>(fs, WorkflowManifestPromoter.ParseOptionsLenient);
                }
                catch (System.Text.Json.JsonException ex)
                {
                    valid = false;
                    error = $"Manifest is not valid JSON: {ex.Message}";
                }
            }

            // Run the validator on whatever parsed (lenient mode means typo'd
            // properties are silently dropped here â€” Save catches those with
            // strict parsing). Semantic errors flow through errors[] with
            // valid=true so the right pane still renders the partial manifest.
            var errors = valid && present
                ? wfPromoter.Validate(manifest)
                : (error is null ? Array.Empty<string>() : new[] { error });

            // Sibling new-agent proposals â€” anything under .draft/new-agents/*.json.
            // Returned as parsed AgentManifest objects for the right-pane preview.
            var newAgentsDir = Path.Combine(info.SandboxPath, ".draft", "new-agents");
            var newAgents = new List<object>();
            if (Directory.Exists(newAgentsDir))
            {
                foreach (var file in Directory.EnumerateFiles(newAgentsDir, "*.json"))
                {
                    try
                    {
                        using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        var na = System.Text.Json.JsonSerializer.Deserialize<AgentManifest>(fs, AgentManifestPromoter.ParseOptionsLenient);
                        var naErrors = AgentManifestPromoter.Validate(na);
                        newAgents.Add(new
                        {
                            fileName = Path.GetFileName(file),
                            valid = na is not null,
                            manifest = na,
                            error = naErrors.Count == 0 ? null : string.Join("; ", naErrors),
                            errors = naErrors,
                        });
                    }
                    catch (System.Text.Json.JsonException ex)
                    {
                        var msg = $"invalid JSON: {ex.Message}";
                        newAgents.Add(new
                        {
                            fileName = Path.GetFileName(file),
                            valid = false,
                            manifest = (AgentManifest?)null,
                            error = (string?)msg,
                            errors = new[] { msg },
                        });
                    }
                }
            }

            return Results.Ok(new
            {
                present,
                valid,
                manifest,
                error = error ?? (errors.Count == 0 ? null : string.Join("; ", errors)),
                errors,
                updatedAt,
                newAgents,
            });
        });

        // Promote a workflow draft. Body: { overwrite?: boolean }. Cross-creates
        // any pending new-agent drafts (under .draft/new-agents/) first via
        // AgentManifestPromoter, then promotes the workflow itself. Response
        // carries the per-agent results so the UI can render a "what landed"
        // summary if there were multiple sub-promotions.
        app.MapPost("/api/builder/{sessionId}/save-workflow", async (
                string sessionId,
                BuilderSessionFactory factory,
                WorkflowManifestPromoter promoter,
                IConsoleSessionStore consoleStore,
                Microsoft.AspNetCore.Http.HttpRequest request) =>
        {
            var info = factory.Find(sessionId);
            if (info is null) return Results.NotFound(new { error = "Builder session not found" });
            if (info.Kind != BuilderKind.Workflow)
            {
                return Results.BadRequest(new { error = "This endpoint is only valid for workflow builder sessions." });
            }
            if (!File.Exists(info.ManifestPath))
            {
                return Results.BadRequest(new { error = "No manifest yet â€” wait for the chat to produce one." });
            }

            bool overwrite = false;
            string? catalogId = null;
            try
            {
                if (request.ContentLength > 0)
                {
                    var body = await request.ReadFromJsonAsync<SaveWorkflowBody>();
                    overwrite = body?.Overwrite ?? false;
                    catalogId = body?.CatalogId;
                }
            }
            catch (System.Text.Json.JsonException) { /* tolerate empty / malformed body */ }

            var result = promoter.Promote(info.ManifestPath, overwrite, catalogId);
            if (!result.Ok)
            {
                // Even on failure, surface what got cross-created so the user
                // sees that some agents are now in the catalog (no rollback).
                return Results.BadRequest(new
                {
                    error = result.Error,
                    errors = result.Errors,
                    agentResults = result.AgentSubResults,
                });
            }

            try { await consoleStore.RemoveAsync(sessionId); }
            catch (Exception) { /* best-effort, workflow is already promoted */ }

            return Results.Ok(new
            {
                ok = true,
                workflowId = result.WorkflowId,
                catalogId = string.IsNullOrWhiteSpace(catalogId) ? DoxieCatalogStore.DefaultCatalogId : catalogId,
                href = $"/workflows/{result.WorkflowId}",
                agentResults = result.AgentSubResults,
            });
        })
           .DisableAntiforgery();

        return app;
    }
}
