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

internal static class ConsoleEndpoints
{
    public static IEndpointRouteBuilder MapConsoleEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/consoles", async (
                ConsoleCreateRequest payload,
                IWorkspaceStore workspaceStore,
                IConsoleSessionStore consoleStore) =>
        {
            if (payload is null || string.IsNullOrEmpty(payload.WorkspaceId))
            {
                return Results.BadRequest("workspaceId is required");
            }
            var ws = workspaceStore.GetById(payload.WorkspaceId);
            if (ws is null)
            {
                return Results.NotFound($"Workspace '{payload.WorkspaceId}' not found");
            }

            try
            {
                var session = await consoleStore.CreateAsync(ws.Id);
                return Results.Created($"/api/consoles/{session.Id}", new
                {
                    id = session.Id,
                    workspaceId = session.WorkspaceId,
                    label = session.Label,
                    startedAt = session.StartedAt,
                    status = session.Status.ToString(),
                });
            }
            catch (InvalidOperationException ex)
            {
                return Results.Problem($"Could not start console: {ex.Message}", statusCode: 500);
            }
        })
           .DisableAntiforgery();

        app.MapPost("/api/consoles/provider-auth", async (
                ProviderConsoleCreateRequest payload,
                IAgentProviderResolver resolver,
                IConsoleSessionStore consoleStore) =>
        {
            if (payload is null || string.IsNullOrWhiteSpace(payload.ProviderId))
            {
                return Results.BadRequest("providerId is required");
            }

            var provider = resolver.AllProviders.FirstOrDefault(p =>
                string.Equals(p.Id, payload.ProviderId, StringComparison.OrdinalIgnoreCase));
            if (provider is null)
            {
                return Results.NotFound($"Provider '{payload.ProviderId}' not found");
            }

            try
            {
                var cwd = StorageOptions.ResolvePath(".");
                var session = await consoleStore.CreateForProviderAsync(
                    provider.Id,
                    cwd,
                    $"Auth · {provider.DisplayName}");
                return Results.Created($"/api/consoles/{session.Id}", new
                {
                    id = session.Id,
                    providerId = provider.Id,
                    label = session.Label,
                    startedAt = session.StartedAt,
                    status = session.Status.ToString(),
                });
            }
            catch (InvalidOperationException ex)
            {
                return Results.Problem($"Could not start provider console: {ex.Message}", statusCode: 500);
            }
        })
           .DisableAntiforgery();

        // List all live console sessions. Used by the consoles page sidebar
        // and the NavMenu badge.
        app.MapGet("/api/consoles", (IConsoleSessionStore consoleStore) =>
            Results.Ok(consoleStore.ListAll().Select(s => new
            {
                id = s.Id,
                workspaceId = s.WorkspaceId,
                label = s.Label,
                providerId = s.ProviderId,
                kind = s.Kind.ToString(),
                startedAt = s.StartedAt,
                finishedAt = s.FinishedAt,
                status = s.Status.ToString(),
            })));

        // Terminate (or evict an already-exited) console session. Idempotent.
        app.MapDelete("/api/consoles/{sessionId}", async (
                string sessionId,
                IConsoleSessionStore consoleStore) =>
        {
            await consoleStore.RemoveAsync(sessionId);
            return Results.NoContent();
        });

        return app;
    }
}
