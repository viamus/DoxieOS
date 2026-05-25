namespace Viamus.Doxie.Orchestrator.Notifications;

/// <summary>
/// Payload an agent posts to DoxieOS via the local HTTP endpoint to
/// surface a real-time toast in every connected browser tab.
/// </summary>
/// <param name="Href">
/// Optional click-through target. When set, both the Snackbar toast
/// and the row on <c>/notifications</c> become clickable and navigate
/// to this URL. Accepts an external URL (e.g. a review artifact link
/// <c>https://example.invalid/review/123</c>) or an internal
/// route (<c>/agents/&lt;id&gt;?run=&lt;run-id&gt;</c>,
/// <c>/workflows/&lt;id&gt;?run=&lt;run-id&gt;</c>). Lets agents
/// point the user straight at the artefact / history that justifies
/// the notification.
/// </param>
public sealed record Notification(
    string Title,
    string Body,
    string Severity = "info",
    string? AgentId = null,
    DateTimeOffset? Timestamp = null,
    string? Href = null,
    string? Content = null,
    string ContentFormat = "text",
    string? SourcePath = null,
    IReadOnlyList<NotificationAction>? Actions = null);

public sealed record NotificationAction(
    string Label,
    string Href,
    string Icon = "open_in_new");
