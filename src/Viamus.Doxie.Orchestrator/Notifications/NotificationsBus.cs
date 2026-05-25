namespace Viamus.Doxie.Orchestrator.Notifications;

/// <summary>
/// In-process pub/sub for notifications + the "what just happened"
/// history backing the bell icon and <c>/notifications</c> page.
/// The HTTP endpoint that receives agent posts publishes here; every
/// Blazor circuit subscribes during MainLayout init for the live toast,
/// and the NavMenu / notifications page subscribe to <see cref="Changed"/>
/// so the badge counter refreshes without polling.
///
/// <para>Storage is in-process — last <see cref="MaxHistory"/> entries
/// in a ring buffer; survives across browser-tab refreshes for as long
/// as DoxieOS is running, dies when the process exits. Single-user
/// dev tool; no per-user queue.</para>
/// </summary>
public sealed class NotificationsBus
{
    /// <summary>Cap on the in-memory history. Older entries are dropped.</summary>
    public const int MaxHistory = 200;

    private readonly object _lock = new();
    private readonly LinkedList<Notification> _recent = new();
    private readonly ILogger<NotificationsBus> _logger;
    private DateTimeOffset _lastReadAt = DateTimeOffset.MinValue;

    public NotificationsBus(ILogger<NotificationsBus> logger)
    {
        _logger = logger;
    }

    /// <summary>Fires for every published notification — drives the live toast.</summary>
    public event Action<Notification>? Received;

    /// <summary>
    /// Fires whenever the recent-list or unread count changes (after a
    /// Publish or a MarkAllRead). Subscribers are the NavMenu badge and
    /// the notifications page; both call <see cref="ListRecent"/> on tick.
    /// </summary>
    public event Action? Changed;

    public void Publish(Notification notification)
    {
        var stamped = notification.Timestamp.HasValue
            ? notification
            : notification with { Timestamp = DateTimeOffset.UtcNow };

        lock (_lock)
        {
            _recent.AddFirst(stamped);
            // Trim — drop oldest entries past the cap.
            while (_recent.Count > MaxHistory) _recent.RemoveLast();
        }

        _logger.LogInformation(
            "Notification published: severity={Severity} title={Title} agentId={AgentId} href={Href} contentFormat={ContentFormat} hasContent={HasContent}",
            stamped.Severity,
            stamped.Title,
            stamped.AgentId,
            stamped.Href,
            stamped.ContentFormat,
            !string.IsNullOrWhiteSpace(stamped.Content));

        Received?.Invoke(stamped);
        Changed?.Invoke();
    }

    /// <summary>Most-recent-first snapshot of the history (up to <see cref="MaxHistory"/>).</summary>
    public IReadOnlyList<Notification> ListRecent()
    {
        lock (_lock) return _recent.ToList();
    }

    /// <summary>
    /// Count of notifications received after the last
    /// <see cref="MarkAllRead"/> call. Drives the "+N" badge on the
    /// NavMenu's bell.
    /// </summary>
    public int UnreadCount
    {
        get
        {
            lock (_lock)
            {
                var read = _lastReadAt;
                return _recent.Count(n => n.Timestamp is { } t && t > read);
            }
        }
    }

    /// <summary>
    /// Resets the unread cursor to "now" — typically called when the
    /// user opens the notifications page, so the badge zeroes out.
    /// </summary>
    public void MarkAllRead()
    {
        lock (_lock) _lastReadAt = DateTimeOffset.UtcNow;
        _logger.LogDebug("Notifications marked as read at {LastReadAt}", _lastReadAt);
        Changed?.Invoke();
    }
}
