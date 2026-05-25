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

internal static class RuntimeNotificationEndpoints
{
    public static IEndpointRouteBuilder MapRuntimeNotificationEndpoints(this IEndpointRouteBuilder app)
    {
        // Tells callers whether the request looks local (same machine as DoxieOS)
        // or remote. Used by the Workspaces page to decide between the desktop
        // "Open in Explorer" launcher and the in-browser file browser.
        app.MapGet("/api/runtime/locality", (IRequestLocalityService locality) =>
            Results.Ok(new { isLocal = locality.IsLocalRequest() }));

        // Local-only notification ingest: any subprocess (typically a Claude
        // agent run) can POST a Notification JSON here and every connected
        // browser tab will surface a Snackbar toast. The orchestrator is
        // listening on localhost only, no auth â€” single-user dev tool.
        app.MapPost("/api/notifications", (Notification payload, NotificationsBus bus, ILoggerFactory loggerFactory) =>
        {
            loggerFactory.CreateLogger("DoxieOS.Notifications").LogInformation(
                "Notification ingest accepted: severity={Severity} title={Title} agentId={AgentId} href={Href} contentFormat={ContentFormat} hasContent={HasContent}",
                payload.Severity,
                payload.Title,
                payload.AgentId,
                payload.Href,
                payload.ContentFormat,
                !string.IsNullOrWhiteSpace(payload.Content));

            bus.Publish(payload);
            return Results.Accepted();
        })
           .DisableAntiforgery();

        // Read the notifications history (last N, most-recent first). Used by
        // the /notifications page; in-process subscribers (NavMenu badge,
        // page itself) get push updates via NotificationsBus.Changed too.
        app.MapGet("/api/notifications", (NotificationsBus bus, ILoggerFactory loggerFactory) =>
        {
            var items = bus.ListRecent();
            var unread = bus.UnreadCount;
            loggerFactory.CreateLogger("DoxieOS.Notifications").LogDebug(
                "Notifications history requested: unread={UnreadCount} count={Count}",
                unread,
                items.Count);

            return Results.Ok(new
            {
                unread,
                items,
            });
        });

        // Reset the unread cursor to "now". The notifications page calls this
        // on first render so the bell badge zeroes out.
        app.MapPost("/api/notifications/mark-read", (NotificationsBus bus, ILoggerFactory loggerFactory) =>
        {
            bus.MarkAllRead();
            loggerFactory.CreateLogger("DoxieOS.Notifications").LogDebug("Notifications mark-read requested");
            return Results.NoContent();
        })
           .DisableAntiforgery();

        // Manual escape hatch for the Doxie regen pipeline. The in-process
        // FileSystemWatcher catches every edit under .doxie/, but if the user
        // rewrote files while DoxieOS was offline (or wants to force-rebuild
        // after a checkout) this endpoint reruns the pass on demand. Returns
        // the regen counters so the Settings page can flash them in a toast.
        // Legacy skills migration â€” one-shot upgrade path for users coming
        // from a v0.1 install with .claude/skills/ authored by hand. Detect
        // returns candidates without performing any writes; Migrate folds
        // them into .doxie/skills/<id>/{manifest.json, body.md} + companions

        return app;
    }
}
