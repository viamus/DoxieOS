using System.Collections.Concurrent;

namespace Viamus.Doxie.Orchestrator.Context;

/// <summary>
/// In-process registry of live console sessions. Sessions are transient Ã¢â‚¬â€
/// they die with DoxieOS, like tmux sessions die with the host. A future
/// version could rehydrate from disk if persistence becomes desirable.
/// </summary>
public sealed class InMemoryConsoleSessionStore : IConsoleSessionStore, IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, ConsoleSession> _sessions = new();
    private readonly IWorkspaceStore _workspaceStore;
    private readonly Func<string> _executableProvider;
    private readonly Func<string> _interactiveArgsProvider;
    private readonly Func<string> _labelProvider;
    private readonly Func<string?, ConsoleLaunchProfile>? _launchProfileProvider;

    /// <summary>
    /// All three providers are consulted on every <c>CreateAsync</c> /
    /// <c>CreateForBuilderAsync</c> call, so a Settings-page change to
    /// the active agent CLI provider takes effect for the next console
    /// without an orchestrator restart. Production wires this from
    /// <c>IAgentProviderResolver</c> in <c>Program.cs</c>; tests pass
    /// literal <c>() =&gt; "claude"</c> (or fakes).
    ///
    /// <paramref name="interactiveArgsProvider"/> returns a single
    /// space-separated args string injected after the executable on
    /// the PTY command line Ã¢â‚¬â€ Claude needs
    /// <c>--dangerously-skip-permissions</c>, Codex needs none.
    ///
    /// <paramref name="labelProvider"/> returns the short friendly
    /// name shown in the consoles side list (e.g. "claude" or
    /// "codex") Ã¢â‚¬â€ the executable string the resolver produces is
    /// already wrapped (<c>cmd.exe /c "..."</c>) and unsuitable for
    /// display. Defaults to "claude" for back-compat.
    /// </summary>
    public InMemoryConsoleSessionStore(
        IWorkspaceStore workspaceStore,
        Func<string> executableProvider,
        Func<string>? interactiveArgsProvider = null,
        Func<string>? labelProvider = null,
        Func<string?, ConsoleLaunchProfile>? launchProfileProvider = null)
    {
        _workspaceStore = workspaceStore;
        _executableProvider = executableProvider;
        _interactiveArgsProvider = interactiveArgsProvider ?? (() => "--dangerously-skip-permissions");
        _labelProvider = labelProvider ?? (() => "claude");
        _launchProfileProvider = launchProfileProvider;
    }

    /// <summary>Convenience overload Ã¢â‚¬â€ pin a single executable for the
    /// store's lifetime. Used by tests that don't need the dynamic
    /// resolver behaviour. Args default to the Claude flag for
    /// backward compatibility.</summary>
    public InMemoryConsoleSessionStore(IWorkspaceStore workspaceStore, string executable = "claude")
        : this(
            workspaceStore,
            () => string.IsNullOrWhiteSpace(executable) ? "claude" : executable,
            () => "--dangerously-skip-permissions",
            () => string.IsNullOrWhiteSpace(executable) ? "claude" : executable)
    {
    }

    private ConsoleLaunchProfile ResolveLaunchProfile(string? providerId = null)
    {
        if (_launchProfileProvider is not null)
        {
            var profile = _launchProfileProvider(providerId);
            return profile with
            {
                Executable = string.IsNullOrWhiteSpace(profile.Executable) ? "claude" : profile.Executable,
                InteractiveArgs = profile.InteractiveArgs ?? string.Empty,
                Label = string.IsNullOrWhiteSpace(profile.Label) ? "claude" : profile.Label,
            };
        }

        return new ConsoleLaunchProfile(
            ResolveExecutable(),
            ResolveInteractiveArgs(),
            ResolveLabel(),
            providerId);
    }

    private string ResolveExecutable()
    {
        var resolved = _executableProvider();
        return string.IsNullOrWhiteSpace(resolved) ? "claude" : resolved;
    }

    private string ResolveInteractiveArgs() => _interactiveArgsProvider() ?? string.Empty;

    private string ResolveLabel()
    {
        var label = _labelProvider();
        return string.IsNullOrWhiteSpace(label) ? "claude" : label;
    }

    public event Action<ConsoleSession>? SessionCreated;
    public event Action<ConsoleSession>? SessionRemoved;
    public event Action<ConsoleSession>? SessionUpdated;

    public IReadOnlyList<ConsoleSession> ListAll() =>
        _sessions.Values
            .OrderByDescending(s => s.StartedAt)
            .ToList();

    public ConsoleSession? Get(string id) =>
        _sessions.TryGetValue(id, out var session) ? session : null;

    public Task<ConsoleSession> CreateAsync(string workspaceId, CancellationToken ct = default)
    {
        var workspace = _workspaceStore.GetById(workspaceId)
            ?? throw new InvalidOperationException($"Workspace '{workspaceId}' not found.");
        if (!Directory.Exists(workspace.Path))
        {
            throw new InvalidOperationException($"Workspace path does not exist: {workspace.Path}");
        }

        // Re-apply the existing mount set so the workspace's provider
        // context files are regenerated with the current library
        // memories before the CLI reads them. Idempotent for the
        // manifest (same set in, same set out) but ensures older
        // workspaces get context files on first console open.
        try
        {
            workspace = _workspaceStore.SetMountedLibraries(workspace.Id, workspace.MountedLibraryIds);
        }
        catch (Exception)
        {
            // Best-effort Ã¢â‚¬â€ if the regen fails (file lock, perms),
            // still create the session so the user isn't blocked.
        }

        // Create the session WITHOUT spawning the PTY. The first
        // browser Resize call (after xterm.fit settles) triggers the
        // actual CLI spawn at exactly the dimensions xterm computed
        // Ã¢â‚¬â€ eliminating the spawn-vs-fit width mismatch that was
        // wrapping the agent CLI's UI on first paint.
        var profile = ResolveLaunchProfile();
        var label = $"{workspace.Name} Ã‚· {profile.Label}";
        var id = Guid.NewGuid().ToString("N")[..12];
        var session = new ConsoleSession(
            id,
            workspace.Id,
            workspace.Path,
            label,
            executable: profile.Executable,
            interactiveArgs: profile.InteractiveArgs,
            providerId: profile.ProviderId);
        _sessions[id] = session;
        // Re-emit lifecycle events so the SignalR hub / page only need
        // to subscribe at store level instead of walking the dictionary.
        session.StatusChanged += s => SessionUpdated?.Invoke(s);
        SessionCreated?.Invoke(session);
        return Task.FromResult(session);
    }

    public Task<ConsoleSession> CreateForBuilderAsync(
        string syntheticWorkspaceId,
        string cwd,
        string label,
        string? initialInput = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(cwd))
        {
            throw new ArgumentException("cwd is required.", nameof(cwd));
        }
        if (!Directory.Exists(cwd))
        {
            throw new InvalidOperationException($"Builder cwd does not exist: {cwd}");
        }

        // Same deferred-spawn shape as CreateAsync: PTY isn't started
        // until the browser sends its first Resize, so claude spawns at
        // exactly the dimensions xterm computed. The bootstrap (when
        // supplied) flows down to ConsoleSession.Spawn which writes it
        // to the PTY immediately after wiring up Ã¢â‚¬â€ so the session arrives
        // already in the persona of the requesting builder.
        var profile = ResolveLaunchProfile();
        var id = Guid.NewGuid().ToString("N")[..12];
        var session = new ConsoleSession(
            id,
            workspaceId: syntheticWorkspaceId,
            workspacePath: cwd,
            label: label,
            kind: ConsoleSessionKind.Builder,
            executable: profile.Executable,
            interactiveArgs: profile.InteractiveArgs,
            providerId: profile.ProviderId,
            initialInput: initialInput);
        _sessions[id] = session;
        session.StatusChanged += s => SessionUpdated?.Invoke(s);
        SessionCreated?.Invoke(session);
        return Task.FromResult(session);
    }

    public Task<ConsoleSession> CreateForProviderAsync(
        string providerId,
        string cwd,
        string label,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(providerId))
        {
            throw new ArgumentException("providerId is required.", nameof(providerId));
        }
        if (string.IsNullOrWhiteSpace(cwd))
        {
            throw new ArgumentException("cwd is required.", nameof(cwd));
        }
        if (!Directory.Exists(cwd))
        {
            throw new InvalidOperationException($"Console cwd does not exist: {cwd}");
        }

        var profile = ResolveLaunchProfile(providerId);
        var id = Guid.NewGuid().ToString("N")[..12];
        var session = new ConsoleSession(
            id,
            workspaceId: $"auth:{providerId}",
            workspacePath: cwd,
            label: label,
            kind: ConsoleSessionKind.Auth,
            executable: profile.Executable,
            interactiveArgs: profile.InteractiveArgs,
            providerId: profile.ProviderId ?? providerId);
        _sessions[id] = session;
        session.StatusChanged += s => SessionUpdated?.Invoke(s);
        SessionCreated?.Invoke(session);
        return Task.FromResult(session);
    }

    public async Task RemoveAsync(string sessionId)
    {
        if (!_sessions.TryRemove(sessionId, out var session)) return;
        await session.DisposeAsync();
        SessionRemoved?.Invoke(session);
    }

    public async ValueTask DisposeAsync()
    {
        // Drain on shutdown so PTY child processes don't outlive the host.
        foreach (var session in _sessions.Values.ToList())
        {
            try { await session.DisposeAsync(); } catch { }
        }
        _sessions.Clear();
    }
}

public sealed record ConsoleLaunchProfile(
    string Executable,
    string InteractiveArgs,
    string Label,
    string? ProviderId = null);
