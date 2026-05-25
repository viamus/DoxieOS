using Microsoft.AspNetCore.SignalR;
using Viamus.Doxie.Orchestrator.Context;

namespace Viamus.Doxie.Orchestrator.Hubs;

/// <summary>
/// SignalR bridge between the browser xterm and a server-side
/// <see cref="ConsoleSession"/>. The browser calls
/// <c>Subscribe(sessionId)</c> to attach (joins a per-session group and
/// receives the scrollback as the first <c>Output</c> message), then
/// <c>Send(sessionId, data)</c> for keystrokes and
/// <c>Resize(sessionId, cols, rows)</c> on terminal resize.
///
/// Stdout fan-out is driven by the session's <see cref="ConsoleSession.DataReceived"/>
/// event — wired in <see cref="OutboundPump"/>, which subscribes once
/// per session and broadcasts to every client in that group.
/// </summary>
public sealed class ConsoleHub : Hub
{
    private readonly IConsoleSessionStore _store;

    public ConsoleHub(IConsoleSessionStore store)
    {
        _store = store;
    }

    public async Task Subscribe(string sessionId)
    {
        var session = _store.Get(sessionId);
        if (session is null) return;
        await Groups.AddToGroupAsync(Context.ConnectionId, GroupName(sessionId));
        // Replay scrollback so a re-attaching browser sees existing
        // terminal state instead of a blank pane.
        var scroll = session.GetScrollback();
        if (scroll.Length > 0)
        {
            await Clients.Caller.SendAsync("Output", scroll);
        }
        await Clients.Caller.SendAsync("Status", session.Status.ToString());
    }

    public Task Unsubscribe(string sessionId) =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, GroupName(sessionId));

    public async Task Send(string sessionId, string data)
    {
        var session = _store.Get(sessionId);
        if (session is null) return;
        await session.WriteAsync(data, Context.ConnectionAborted);
    }

    public void Resize(string sessionId, int cols, int rows)
    {
        _store.Get(sessionId)?.Resize(cols, rows);
    }

    internal static string GroupName(string sessionId) => $"console:{sessionId}";
}

/// <summary>
/// Subscribes to every <see cref="ConsoleSession"/>'s data + status events
/// and broadcasts them through the <see cref="ConsoleHub"/> to all
/// browsers attached to that session's group. Lives as a singleton so
/// subscriptions persist for the session's lifetime, not per-connection.
/// </summary>
public sealed class ConsoleHubBroadcaster : IDisposable
{
    private readonly IConsoleSessionStore _store;
    private readonly IHubContext<ConsoleHub> _hub;
    private readonly Dictionary<string, (Action<string> data, Action<ConsoleSession> status)> _subscriptions = new();
    private readonly object _lock = new();

    public ConsoleHubBroadcaster(IConsoleSessionStore store, IHubContext<ConsoleHub> hub)
    {
        _store = store;
        _hub = hub;
        _store.SessionCreated += OnSessionCreated;
        _store.SessionRemoved += OnSessionRemoved;
        // Hook any sessions that already exist (defensive — store is
        // singleton + this is registered as hosted, so in practice the
        // store is empty at startup).
        foreach (var session in _store.ListAll())
        {
            HookSession(session);
        }
    }

    private void OnSessionCreated(ConsoleSession session) => HookSession(session);

    private void OnSessionRemoved(ConsoleSession session)
    {
        lock (_lock)
        {
            if (_subscriptions.TryGetValue(session.Id, out var handlers))
            {
                session.DataReceived -= handlers.data;
                session.StatusChanged -= handlers.status;
                _subscriptions.Remove(session.Id);
            }
        }
        // Notify anyone still attached that the session is gone.
        _ = _hub.Clients.Group(ConsoleHub.GroupName(session.Id))
            .SendAsync("Status", "Removed");
    }

    private void HookSession(ConsoleSession session)
    {
        Action<string> dataHandler = data =>
        {
            _ = _hub.Clients.Group(ConsoleHub.GroupName(session.Id))
                .SendAsync("Output", data);
        };
        Action<ConsoleSession> statusHandler = s =>
        {
            _ = _hub.Clients.Group(ConsoleHub.GroupName(s.Id))
                .SendAsync("Status", s.Status.ToString());
        };
        session.DataReceived += dataHandler;
        session.StatusChanged += statusHandler;
        lock (_lock) { _subscriptions[session.Id] = (dataHandler, statusHandler); }
    }

    public void Dispose()
    {
        _store.SessionCreated -= OnSessionCreated;
        _store.SessionRemoved -= OnSessionRemoved;
        lock (_lock)
        {
            foreach (var (id, (data, status)) in _subscriptions)
            {
                var session = _store.Get(id);
                if (session is not null)
                {
                    session.DataReceived -= data;
                    session.StatusChanged -= status;
                }
            }
            _subscriptions.Clear();
        }
    }
}
