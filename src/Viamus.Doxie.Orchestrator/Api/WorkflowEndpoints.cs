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

internal static class WorkflowEndpoints
{
    public static IEndpointRouteBuilder MapWorkflowEndpoints(this IEndpointRouteBuilder app)
    {
        // === Workflows ===
        // Read API mirrors the agent endpoints: list / fetch / mutate via POST.
        // The simulator runner exposes its progress via in-process events
        // (subscribed by the Blazor pages) so no SignalR hub is needed for
        // the prototype.
        app.MapGet("/api/workflows", (IWorkflowStore workflowStore) =>
            Results.Ok(workflowStore.ListAll()));

        app.MapGet("/api/workflows/{workflowId}", (string workflowId, IWorkflowStore workflowStore) =>
        {
            var wf = workflowStore.GetById(workflowId);
            return wf is null ? Results.NotFound() : Results.Ok(wf);
        });

        // Export a workflow as a Doxie bundle. The workflow folder is
        // always included; referenced agents are nested as normal agent zips
        // by default so another machine can import the runnable graph.
        app.MapGet("/api/workflows/{workflowId}/export", (
                string workflowId,
                bool? includeAgents,
                IWorkflowStore workflowStore,
                IDoxieCatalogStore catalogStore,
                StorageOptions storageOptions,
                IAgentCatalog catalog) =>
        {
            if (string.IsNullOrEmpty(workflowId)
                || !System.Text.RegularExpressions.Regex.IsMatch(workflowId, "^[a-z0-9]+(-[a-z0-9]+)*$"))
            {
                return Results.BadRequest("Invalid workflow id (must be kebab-case)");
            }

            var wf = workflowStore.GetById(workflowId);
            if (wf is null) return Results.NotFound($"Workflow '{workflowId}' not found");

            var workflowCatalog = catalogStore.FindCatalog(wf.CatalogId) ?? catalogStore.FindCatalog(null);
            if (workflowCatalog is null)
            {
                return Results.Problem("No workflow catalog is configured.", statusCode: 500);
            }

            var workflowPath = Path.Combine(workflowCatalog.WorkflowsDirectory, wf.Id);
            var agentSources = new List<WorkflowTransfer.AgentBundleSource>();
            if (includeAgents != false)
            {
                foreach (var agentId in wf.Nodes
                    .Select(n => n.AgentId)
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    var agent = catalog.FindById(agentId!);
                    if (agent is null) continue;
                    agentSources.Add(new WorkflowTransfer.AgentBundleSource(
                        agent.Id,
                        AgentApiPaths.ResolveSkillsRoot(storageOptions, agent),
                        AgentApiPaths.ResolveAgentsRoot(storageOptions, agent)));
                }
            }

            try
            {
                var ms = new MemoryStream();
                WorkflowTransfer.ExportToZip(workflowPath, wf.Id, agentSources, ms);
                ms.Position = 0;
                return Results.File(ms, "application/zip", $"{wf.Id}-workflow.zip");
            }
            catch (DirectoryNotFoundException)
            {
                return Results.NotFound($"Workflow '{workflowId}' not found");
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(ex.Message);
            }
            catch (IOException ex)
            {
                return Results.Problem($"Could not export workflow: {ex.Message}", statusCode: 500);
            }
        });

        // Import a workflow bundle. multipart/form-data with a file part,
        // optional overwrite=true, and optional catalogId for the workflow
        // plus any missing/replaced agents. Identical existing artifacts are
        // skipped; differing workflows or agents return 409 unless overwrite=true.
        app.MapPost("/api/workflows/import", async (
                Microsoft.AspNetCore.Http.HttpRequest request,
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

            var selectedCatalog = catalogStore.FindCatalog(form["catalogId"].ToString());
            if (selectedCatalog is null)
            {
                return Results.BadRequest(new { error = $"Catalog '{form["catalogId"]}' was not found." });
            }

            var overwrite = string.Equals(form["overwrite"].ToString(), "true", StringComparison.OrdinalIgnoreCase);
            var destination = ToWorkflowImportCatalog(selectedCatalog);
            var allCatalogs = catalogStore.ListCatalogs()
                .Select(ToWorkflowImportCatalog)
                .ToList();

            WorkflowTransfer.ImportResult result;
            try
            {
                await using var stream = file.OpenReadStream();
                result = WorkflowTransfer.ImportFromZip(stream, destination, allCatalogs, overwrite);
            }
            catch (IOException ex)
            {
                return Results.Problem($"Could not import workflow: {ex.Message}", statusCode: 500);
            }
            catch (UnauthorizedAccessException ex)
            {
                return Results.Problem($"Permission denied importing: {ex.Message}", statusCode: 500);
            }
            catch (InvalidDataException ex)
            {
                return Results.Json(new { error = ex.Message, code = WorkflowTransfer.ImportError.InvalidJson.ToString() }, statusCode: 400);
            }

            if (!result.Ok)
            {
                var status = result.Error is WorkflowTransfer.ImportError.Collision or WorkflowTransfer.ImportError.AgentCollision
                    ? 409
                    : 400;
                return Results.Json(new
                {
                    error = result.Message,
                    code = result.Error?.ToString(),
                    agents = result.Agents.Select(a => new
                    {
                        agentId = a.AgentId,
                        action = a.Action.ToString(),
                        existingCatalogId = a.ExistingCatalogId,
                    }),
                }, statusCode: status);
            }

            regenerator.Regenerate();
            catalog.Refresh();
            return Results.Ok(new
            {
                ok = true,
                workflowId = result.WorkflowId,
                href = $"/workflows/{result.WorkflowId}",
                workflowAction = result.WorkflowAction.ToString(),
                agents = result.Agents.Select(a => new
                {
                    agentId = a.AgentId,
                    action = a.Action.ToString(),
                    existingCatalogId = a.ExistingCatalogId,
                }),
            });
        })
           .DisableAntiforgery();

        // Create a workflow. Body is a fully-formed WorkflowDefinition (with
        // nodes + edges already attached). The store layers a kebab-case
        // regex check on the id, so we re-validate here for a friendly
        // 400 error before touching disk.
        app.MapPost("/api/workflows", (WorkflowDefinition payload, IWorkflowStore workflowStore, DoxieRegenerator regenerator) =>
        {
            if (payload is null || string.IsNullOrEmpty(payload.Id))
            {
                return Results.BadRequest("id is required");
            }
            if (!System.Text.RegularExpressions.Regex.IsMatch(payload.Id, "^[a-z0-9]+(-[a-z0-9]+)*$"))
            {
                return Results.BadRequest($"Invalid workflow id '{payload.Id}' (must be kebab-case)");
            }
            if (workflowStore.GetById(payload.Id) is not null)
            {
                return Results.Conflict($"Workflow '{payload.Id}' already exists");
            }
            if (payload.Nodes is null || payload.Nodes.Count == 0)
            {
                return Results.BadRequest("workflow must have at least one node");
            }
            if (payload.Trigger.Kind == WorkflowTriggerKind.Cron && !CronExpression.IsValid(payload.Trigger.CronExpression))
            {
                return Results.BadRequest($"Invalid cron expression '{payload.Trigger.CronExpression}' (expected 5 fields: minute hour day-of-month month day-of-week)");
            }
            try
            {
                // Auto-position before saving so newly authored workflows render
                // cleanly on first open even if the client supplied X/Y = 0.
                var positioned = WorkflowLayout.AutoPosition(payload.Nodes, payload.Edges ?? Array.Empty<WorkflowEdge>());
                var ts = DateTime.UtcNow;
                var saved = (payload with
                {
                    Nodes = positioned,
                    CreatedAt = ts,
                    UpdatedAt = ts,
                }).WithCreateTimeEnabledPolicy();
                workflowStore.Save(saved);
                // Re-shim â€” adds the new workflow to the .codex/AGENTS.md catalog.
                regenerator.Regenerate();
                return Results.Created($"/api/workflows/{saved.Id}", saved);
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(ex.Message);
            }
        })
           .DisableAntiforgery();

        // Update an existing workflow. The id in the URL wins over any id in
        // the body â€” renaming a workflow would orphan its run history and the
        // on-disk folder, so we treat id as immutable post-creation. CreatedAt
        // is preserved from the existing record; UpdatedAt is re-stamped here.
        app.MapPut("/api/workflows/{workflowId}", (string workflowId, WorkflowDefinition payload, IWorkflowStore workflowStore, DoxieRegenerator regenerator) =>
        {
            if (string.IsNullOrEmpty(workflowId)
                || !System.Text.RegularExpressions.Regex.IsMatch(workflowId, "^[a-z0-9]+(-[a-z0-9]+)*$"))
            {
                return Results.BadRequest("Invalid workflow id (must be kebab-case)");
            }
            var existing = workflowStore.GetById(workflowId);
            if (existing is null)
            {
                return Results.NotFound($"Workflow '{workflowId}' not found");
            }
            if (payload is null || payload.Nodes is null || payload.Nodes.Count == 0)
            {
                return Results.BadRequest("workflow must have at least one node");
            }
            if (payload.Trigger.Kind == WorkflowTriggerKind.Cron && !CronExpression.IsValid(payload.Trigger.CronExpression))
            {
                return Results.BadRequest($"Invalid cron expression '{payload.Trigger.CronExpression}' (expected 5 fields: minute hour day-of-month month day-of-week)");
            }

            try
            {
                var positioned = WorkflowLayout.AutoPosition(payload.Nodes, payload.Edges ?? Array.Empty<WorkflowEdge>());
                var saved = payload with
                {
                    Id = workflowId,
                    Nodes = positioned,
                    CreatedAt = existing.CreatedAt,
                    UpdatedAt = DateTime.UtcNow,
                };
                workflowStore.Save(saved);
                // Re-shim â€” picks up renamed/edited workflow metadata in the codex catalog.
                regenerator.Regenerate();
                return Results.Ok(saved);
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(ex.Message);
            }
        })
           .DisableAntiforgery();

        app.MapDelete("/api/workflows/{workflowId}", (string workflowId, IWorkflowStore workflowStore, DoxieRegenerator regenerator) =>
        {
            if (string.IsNullOrEmpty(workflowId)
                || !System.Text.RegularExpressions.Regex.IsMatch(workflowId, "^[a-z0-9]+(-[a-z0-9]+)*$"))
            {
                return Results.BadRequest("Invalid workflow id (must be kebab-case)");
            }
            try
            {
                workflowStore.Delete(workflowId);
                // Re-shim â€” drops the deleted workflow from the codex catalog.
                regenerator.Regenerate();
                return Results.NoContent();
            }
            catch (InvalidOperationException ex)
            {
                return Results.NotFound(ex.Message);
            }
        });

        // Manual trigger for an existing workflow. The runner is async â€” we
        // return immediately with the run id; the UI follows progress via the
        // IWorkflowRunner.RunUpdated event (Blazor server) or by polling
        // /api/workflows/{id}/runs. Disabled workflows return 409 to make the
        // "why did nothing happen?" answer obvious in the network panel.
        //
        // Optional body { triggerInputs: { id: value, ... } } supplies values
        // for the workflow's Trigger.Inputs declarations. Empty body is fine
        // for workflows that declare no inputs. Missing required inputs come
        // back as 400 with a list of which keys are missing.
        app.MapPost("/api/workflows/{workflowId}/run", async (
                string workflowId,
                IWorkflowStore workflowStore,
                IWorkflowRunner runner,
                Microsoft.AspNetCore.Http.HttpRequest request) =>
        {
            var wf = workflowStore.GetById(workflowId);
            if (wf is null) return Results.NotFound($"Workflow '{workflowId}' not found");
            if (!wf.Enabled) return Results.Conflict($"Workflow '{workflowId}' is disabled. Enable it first.");

            Dictionary<string, string>? triggerInputs = null;
            if (request.ContentLength > 0)
            {
                try
                {
                    var body = await request.ReadFromJsonAsync<RunWorkflowBody>();
                    if (body?.TriggerInputs is { Count: > 0 })
                    {
                        triggerInputs = new Dictionary<string, string>(body.TriggerInputs, StringComparer.OrdinalIgnoreCase);
                    }
                }
                catch (System.Text.Json.JsonException ex)
                {
                    return Results.BadRequest(new { error = $"Invalid JSON body: {ex.Message}" });
                }
            }

            // Validate required trigger inputs are present. Missing â†’ 400 with
            // explicit list so the UI can highlight the offending fields.
            if (wf.Trigger.Inputs is { Count: > 0 } declared)
            {
                triggerInputs = ApplyTriggerInputDefaults(triggerInputs, declared);

                var missing = declared
                    .Where(f => f.Required &&
                                (triggerInputs is null
                                 || !triggerInputs.TryGetValue(f.Id, out var v)
                                 || string.IsNullOrWhiteSpace(v)))
                    .Select(f => f.Id)
                    .ToList();
                if (missing.Count > 0)
                {
                    return Results.BadRequest(new
                    {
                        error = $"Missing required trigger input(s): {string.Join(", ", missing)}",
                        missing,
                    });
                }

                var invalid = declared
                    .Where(f => f.Options is { Count: > 0 }
                                && triggerInputs is not null
                                && triggerInputs.TryGetValue(f.Id, out var v)
                                && !string.IsNullOrWhiteSpace(v)
                                && !f.Options.Contains(v, StringComparer.OrdinalIgnoreCase))
                    .Select(f => f.Id)
                    .ToList();
                if (invalid.Count > 0)
                {
                    return Results.BadRequest(new
                    {
                        error = $"Invalid trigger input option(s): {string.Join(", ", invalid)}",
                        invalid,
                    });
                }
            }

            var run = runner.Start(wf, triggeredBy: "manual:api", triggerInputs: triggerInputs);
            return Results.Accepted($"/api/workflows/{workflowId}/runs/{run.Id}", new
            {
                id = run.Id,
                workflowId = run.WorkflowId,
                status = run.Status.ToString(),
                startedAt = run.StartedAt,
            });
        })
           .DisableAntiforgery();

        // Toggle the enabled flag on a workflow. Dedicated endpoint so the
        // UI's switch doesn't have to re-send the entire definition every
        // time the user flips it. UpdatedAt is bumped so any view sorted by
        // "most recently changed" reflects the toggle.
        app.MapPatch("/api/workflows/{workflowId}/enabled", (
                string workflowId,
                WorkflowEnabledRequest payload,
                IWorkflowStore workflowStore) =>
        {
            if (string.IsNullOrEmpty(workflowId)
                || !System.Text.RegularExpressions.Regex.IsMatch(workflowId, "^[a-z0-9]+(-[a-z0-9]+)*$"))
            {
                return Results.BadRequest("Invalid workflow id (must be kebab-case)");
            }
            if (payload is null) return Results.BadRequest("body is required");

            var existing = workflowStore.GetById(workflowId);
            if (existing is null) return Results.NotFound($"Workflow '{workflowId}' not found");

            var updated = existing with { Enabled = payload.Enabled, UpdatedAt = DateTime.UtcNow };
            workflowStore.Save(updated);
            return Results.Ok(new { id = updated.Id, enabled = updated.Enabled });
        })
           .DisableAntiforgery();

        app.MapGet("/api/workflows/{workflowId}/runs", (string workflowId, IWorkflowRunStore runStore) =>
            Results.Ok(runStore.ListByWorkflow(workflowId).Select(WorkflowApiDto.SerializeRun)));

        app.MapGet("/api/workflows/{workflowId}/runs/{runId}", (
                string workflowId,
                string runId,
                IWorkflowRunStore runStore) =>
        {
            var run = runStore.Get(runId);
            if (run is null || !string.Equals(run.WorkflowId, workflowId, StringComparison.OrdinalIgnoreCase))
            {
                return Results.NotFound();
            }
            return Results.Ok(WorkflowApiDto.SerializeRun(run));
        });

        app.MapPost("/api/workflows/{workflowId}/runs/{runId}/cancel", (
                string workflowId,
                string runId,
                IWorkflowRunner runner,
                IWorkflowRunStore runStore) =>
        {
            var run = runStore.Get(runId);
            if (run is null || !string.Equals(run.WorkflowId, workflowId, StringComparison.OrdinalIgnoreCase))
            {
                return Results.NotFound();
            }
            runner.Cancel(runId);
            return Results.Accepted();
        })
           .DisableAntiforgery();

        app.MapPost("/api/workflows/{workflowId}/runs/{runId}/rerun-from", async (
                string workflowId,
                string runId,
                IWorkflowStore workflowStore,
                IWorkflowRunner runner,
                IWorkflowRunStore runStore,
                Microsoft.AspNetCore.Http.HttpRequest request) =>
        {
            var wf = workflowStore.GetById(workflowId);
            if (wf is null) return Results.NotFound($"Workflow '{workflowId}' not found");
            if (!wf.Enabled) return Results.Conflict($"Workflow '{workflowId}' is disabled. Enable it first.");

            var sourceRun = runStore.Get(runId);
            if (sourceRun is null || !string.Equals(sourceRun.WorkflowId, workflowId, StringComparison.OrdinalIgnoreCase))
            {
                return Results.NotFound();
            }

            RerunWorkflowFromBody? body;
            try
            {
                body = request.ContentLength > 0
                    ? await request.ReadFromJsonAsync<RerunWorkflowFromBody>()
                    : null;
            }
            catch (System.Text.Json.JsonException ex)
            {
                return Results.BadRequest(new { error = $"Invalid JSON body: {ex.Message}" });
            }

            if (body is null || string.IsNullOrWhiteSpace(body.NodeId))
            {
                return Results.BadRequest(new { error = "nodeId is required" });
            }

            try
            {
                var run = runner.StartFrom(wf, runId, body.NodeId, body.Guidance);
                return Results.Accepted($"/api/workflows/{workflowId}/runs/{run.Id}", new
                {
                    id = run.Id,
                    workflowId = run.WorkflowId,
                    status = run.Status.ToString(),
                    startedAt = run.StartedAt,
                });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        })
           .DisableAntiforgery();

        // Approve / Reject an approval-gate node that's currently parked.
        // 404 = run not found; 409 = node not parked at a gate (already
        // resolved, never an approval-gate, etc.); 200 = resolved.
        app.MapPost("/api/workflows/{workflowId}/runs/{runId}/nodes/{nodeId}/approve", async (
                string workflowId,
                string runId,
                string nodeId,
                IWorkflowRunner runner,
                IWorkflowRunStore runStore,
                Microsoft.AspNetCore.Http.HttpRequest request) =>
        {
            var run = runStore.Get(runId);
            if (run is null || !string.Equals(run.WorkflowId, workflowId, StringComparison.OrdinalIgnoreCase))
            {
                return Results.NotFound();
            }

            string? comment = null;
            try
            {
                if (request.ContentLength > 0)
                {
                    var body = await request.ReadFromJsonAsync<ApproveBody>();
                    comment = body?.Comment;
                }
            }
            catch (System.Text.Json.JsonException) { /* tolerate empty / malformed body */ }

            var result = runner.ResumeApprovalGate(runId, nodeId, approve: true, comment);
            return result switch
            {
                ApprovalGateResolveResult.Resolved => Results.Ok(new { ok = true }),
                ApprovalGateResolveResult.RunNotFound => Results.NotFound(),
                ApprovalGateResolveResult.NotAwaitingApproval => Results.Conflict(new
                {
                    error = "Node is not currently waiting for approval (already resolved, run finished, or this node is not an approval-gate).",
                }),
                _ => Results.Problem("Unknown resolve result", statusCode: 500),
            };
        })
           .DisableAntiforgery();

        app.MapPost("/api/workflows/{workflowId}/runs/{runId}/nodes/{nodeId}/reject", async (
                string workflowId,
                string runId,
                string nodeId,
                IWorkflowRunner runner,
                IWorkflowRunStore runStore,
                Microsoft.AspNetCore.Http.HttpRequest request) =>
        {
            var run = runStore.Get(runId);
            if (run is null || !string.Equals(run.WorkflowId, workflowId, StringComparison.OrdinalIgnoreCase))
            {
                return Results.NotFound();
            }

            string? comment = null;
            try
            {
                if (request.ContentLength > 0)
                {
                    var body = await request.ReadFromJsonAsync<ApproveBody>();
                    comment = body?.Comment;
                }
            }
            catch (System.Text.Json.JsonException) { /* tolerate empty / malformed body */ }

            var result = runner.ResumeApprovalGate(runId, nodeId, approve: false, comment);
            return result switch
            {
                ApprovalGateResolveResult.Resolved => Results.Ok(new { ok = true }),
                ApprovalGateResolveResult.RunNotFound => Results.NotFound(),
                ApprovalGateResolveResult.NotAwaitingApproval => Results.Conflict(new
                {
                    error = "Node is not currently waiting for approval.",
                }),
                _ => Results.Problem("Unknown resolve result", statusCode: 500),
            };
        })
           .DisableAntiforgery();

        // ---- Run-artefact browser endpoints (workflow runs + sandboxes) ----
        //
        // Read-only navigation of the per-run artefacts left on disk by the
        // workflow runner (`.runs/<runId>/`) and standalone agent runs
        // (`.sandbox/<tag>/`). Path validation is delegated entirely to
        // RunFileBrowser â€” these endpoints only translate the HTTP shape.

        app.MapGet("/api/workflow-runs/{runId}/files", (string runId, string? path, RunFileBrowser browser) =>
        {
            try
            {
                var listing = browser.ListWorkflowRun(runId, path ?? string.Empty);
                return Results.Ok(listing);
            }
            catch (DirectoryNotFoundException ex)
            {
                return Results.NotFound(new { error = ex.Message });
            }
            catch (Exception ex) when (ex is ArgumentException or UnauthorizedAccessException)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        })
           .DisableAntiforgery();

        app.MapGet("/api/workflow-runs/{runId}/file", (string runId, string path, RunFileBrowser browser) =>
        {
            try
            {
                var content = browser.ReadWorkflowRunFile(runId, path);
                return content is null ? Results.NotFound() : Results.Ok(content);
            }
            catch (DirectoryNotFoundException ex)
            {
                return Results.NotFound(new { error = ex.Message });
            }
            catch (Exception ex) when (ex is ArgumentException or UnauthorizedAccessException)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        })
           .DisableAntiforgery();

        app.MapGet("/api/sandboxes", (RunFileBrowser browser) => Results.Ok(browser.ListSandboxes()))
           .DisableAntiforgery();

        app.MapGet("/api/sandboxes/{tag}/files", (string tag, string? path, RunFileBrowser browser) =>
        {
            try
            {
                var listing = browser.ListSandbox(tag, path ?? string.Empty);
                return Results.Ok(listing);
            }
            catch (DirectoryNotFoundException ex)
            {
                return Results.NotFound(new { error = ex.Message });
            }
            catch (Exception ex) when (ex is ArgumentException or UnauthorizedAccessException)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        })
           .DisableAntiforgery();

        app.MapGet("/api/sandboxes/{tag}/file", (string tag, string path, RunFileBrowser browser) =>
        {
            try
            {
                var content = browser.ReadSandboxFile(tag, path);
                return content is null ? Results.NotFound() : Results.Ok(content);
            }
            catch (DirectoryNotFoundException ex)
            {
                return Results.NotFound(new { error = ex.Message });
            }
            catch (Exception ex) when (ex is ArgumentException or UnauthorizedAccessException)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        })
           .DisableAntiforgery();

        return app;
    }

    private static Dictionary<string, string>? ApplyTriggerInputDefaults(
        Dictionary<string, string>? supplied,
        IReadOnlyList<WorkflowTriggerInputField> declared)
    {
        foreach (var field in declared)
        {
            if (string.IsNullOrWhiteSpace(field.DefaultValue)) continue;
            if (supplied is not null
                && supplied.TryGetValue(field.Id, out var existing)
                && !string.IsNullOrWhiteSpace(existing))
            {
                continue;
            }

            supplied ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            supplied[field.Id] = field.DefaultValue!;
        }

        return supplied;
    }

    private static WorkflowTransfer.ImportCatalog ToWorkflowImportCatalog(DoxieCatalogRoot catalog) =>
        new(catalog.Id, catalog.SkillsDirectory, catalog.AgentsDirectory, catalog.WorkflowsDirectory);
}
