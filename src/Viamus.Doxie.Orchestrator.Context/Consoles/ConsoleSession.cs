using System.Text;

namespace Viamus.Doxie.Orchestrator.Context;

/// <summary>
/// One live PTY-backed subprocess (typically <c>claude</c>) bound to
/// a user-owned Workspace.
///
/// **Spawn is deferred.** The session is constructed in
/// <see cref="ConsoleSessionStatus.Starting"/> with no PTY; the browser
/// must call <c>Resize(cols, rows)</c> at least once (which xterm does
/// automatically after <c>fit()</c> settles) and that first call
/// triggers <see cref="Spawn"/> with those exact dimensions. This
/// eliminates the spawn-vs-fit width mismatch that was making Claude's
/// Ink UI wrap at the wrong column on first paint.
///
/// The actual PTY plumbing is delegated to <see cref="IPtyHandle"/> —
/// ConPTY/WinPTY on Windows (via Pty.Net), forkpty over libutil on
/// POSIX. Both backends surface UTF-8 strings (already decoded) and
/// fire a single <c>Disconnected</c> event when the child exits.
/// </summary>
public sealed class ConsoleSession : IAsyncDisposable
{
    // Empirical delay matching what the previous client-side path used.
    // Long enough for Claude's Ink TUI to finish booting and wire its
    // input loop, short enough not to be perceptibly slow.
    private static readonly TimeSpan BootstrapWriteDelay = TimeSpan.FromMilliseconds(800);

    // Gap between writing the bootstrap text and the trailing Enter.
    // Some TUIs (Codex on ratatui/crossterm) treat a single PTY write
    // containing both the body and the CR as a paste and never raise
    // the Enter key event — splitting the writes makes the CR look
    // like a deliberate keystroke. 150ms is enough on every CLI we've
    // tested without being noticeable to the user.
    private static readonly TimeSpan BootstrapEnterDelay = TimeSpan.FromMilliseconds(2000);
    private static readonly TimeSpan BootstrapEnterRetryDelay = TimeSpan.FromMilliseconds(4000);
    private static readonly TimeSpan PtyBroadcastDelay = TimeSpan.FromMilliseconds(33);
    private const int ImmediateBroadcastChars = 16 * 1024;

    private readonly object _bufferLock = new();
    private readonly object _broadcastLock = new();
    // Keep the last N chars of stdout for replay to a re-attaching
    // browser tab. Sized to comfortably fit a full xterm scrollback
    // without growing forever for a long-lived session.
    private readonly char[] _scrollback;
    private readonly StringBuilder _pendingBroadcast = new();
    private int _scrollbackLength;
    private int _scrollbackHead;
    private int _broadcastScheduled;
    private int _disposed;
    private int _spawnAttempted;

    private IPtyHandle? _pty;

    private readonly string _executable;
    private readonly string _interactiveArgs;
    private readonly string? _initialInput;

    public ConsoleSession(
        string id,
        string workspaceId,
        string workspacePath,
        string label,
        int scrollbackChars = 64 * 1024,
        string executable = "claude",
        string interactiveArgs = "--dangerously-skip-permissions",
        string? providerId = null,
        string? initialInput = null)
        : this(id, workspaceId, workspacePath, label, ConsoleSessionKind.Workspace, scrollbackChars, executable, interactiveArgs, providerId, initialInput)
    {
    }

    public ConsoleSession(
        string id,
        string workspaceId,
        string workspacePath,
        string label,
        ConsoleSessionKind kind,
        int scrollbackChars = 64 * 1024,
        string executable = "claude",
        string interactiveArgs = "--dangerously-skip-permissions",
        string? providerId = null,
        string? initialInput = null)
    {
        Id = id;
        WorkspaceId = workspaceId;
        WorkspacePath = workspacePath;
        Label = label;
        ProviderId = providerId;
        Kind = kind;
        StartedAt = DateTimeOffset.UtcNow;
        Status = ConsoleSessionStatus.Starting;
        _scrollback = new char[scrollbackChars];
        _executable = string.IsNullOrWhiteSpace(executable) ? "claude" : executable;
        // The args string is space-separated and inserted verbatim into the
        // PtyHandleFactory.Spawn command line — Claude wants
        // --dangerously-skip-permissions, Codex wants nothing. Empty means
        // "spawn the executable with no extra args" (just the bare CLI).
        _interactiveArgs = interactiveArgs ?? string.Empty;
        // Optional bootstrap bytes (typically a slash command + CR for
        // Claude, or an inlined skill body + CR for Codex) — written into
        // the freshly-spawned PTY so the session arrives "in character"
        // without the user having to type anything. Null/empty = no
        // bootstrap (regular workspace consoles).
        _initialInput = string.IsNullOrEmpty(initialInput) ? null : initialInput;
    }

    public string Id { get; }
    public string WorkspaceId { get; }
    public string WorkspacePath { get; }
    public string Label { get; }
    public string? ProviderId { get; }

    /// <summary>
    /// Discriminator separating regular Workspace consoles (shown on
    /// <c>/consoles</c>) from internal Builder sessions (shown only on
    /// the Agent / Workflow Builder pages).
    /// </summary>
    public ConsoleSessionKind Kind { get; }
    public DateTimeOffset StartedAt { get; }
    public DateTimeOffset? FinishedAt { get; private set; }
    public ConsoleSessionStatus Status { get; private set; }

    /// <summary>Fires for every chunk of stdout the PTY emits (UTF-8 string).</summary>
    public event Action<string>? DataReceived;

    /// <summary>Fires whenever <see cref="Status"/> transitions.</summary>
    public event Action<ConsoleSession>? StatusChanged;

    /// <summary>
    /// Snapshot of recent stdout characters (up to the scrollback cap).
    /// Sent to a new subscriber so they re-attach with terminal state.
    /// </summary>
    public string GetScrollback()
    {
        lock (_bufferLock)
        {
            if (_scrollbackLength == 0) return string.Empty;
            var sb = new StringBuilder(_scrollbackLength);
            if (_scrollbackLength == _scrollback.Length)
            {
                sb.Append(_scrollback, _scrollbackHead, _scrollback.Length - _scrollbackHead);
                sb.Append(_scrollback, 0, _scrollbackHead);
            }
            else
            {
                sb.Append(_scrollback, 0, _scrollbackLength);
            }
            return sb.ToString();
        }
    }

    public Task WriteAsync(string data, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(data)) return Task.CompletedTask;
        if (_disposed != 0) return Task.CompletedTask;
        var pty = _pty;
        if (pty is null) return Task.CompletedTask; // not yet spawned — keystrokes are lost (browser shouldn't be sending them yet)
        try { return pty.WriteAsync(data); }
        catch (ObjectDisposedException) { return Task.CompletedTask; }
        catch (IOException) { return Task.CompletedTask; }
    }

    /// <summary>
    /// Resize OR first-spawn. The first call to Resize triggers the
    /// PTY spawn at the exact dimensions reported by xterm — no
    /// initial-default â†’ resize race. Subsequent calls forward to the
    /// PTY's own resize.
    /// </summary>
    public void Resize(int cols, int rows)
    {
        if (cols <= 0 || rows <= 0) return;
        if (_disposed != 0) return;

        if (_pty is null)
        {
            Spawn(cols, rows);
            return;
        }
        try { _pty.Resize(cols, rows); }
        catch (Exception) { /* terminal size is best-effort */ }
    }

    /// <summary>
    /// Lazily spawns the underlying PTY child process at the requested
    /// dimensions. Idempotent — a second call is a no-op. Sets
    /// <see cref="Status"/> to <see cref="ConsoleSessionStatus.Running"/>
    /// on success or <see cref="ConsoleSessionStatus.Failed"/> on error.
    /// </summary>
    private void Spawn(int cols, int rows)
    {
        if (System.Threading.Interlocked.Exchange(ref _spawnAttempted, 1) != 0) return;

        try
        {
            // PtyHandleFactory picks ConPTY/WinPTY (Pty.Net) on Windows
            // or forkpty (libutil) on Linux/macOS. The single command
            // string is parsed by the backend — `--dangerously-skip-permissions`
            // mirrors the agent runner; without it claude pauses on
            // every tool call waiting for an approval prompt that never
            // comes. The child inherits DoxieOS's environment.
            // Quote the executable path so spaces (e.g. on Windows
            // "C:\Program Files\..."  or a Linux user with a space in
            // $HOME) survive the backend's command-line tokeniser.
            // Build the command from exec + (optional) provider-supplied
            // args. _interactiveArgs is "--dangerously-skip-permissions"
            // for Claude (auto-approve since the orchestrator is the trust
            // boundary) and empty for Codex (the user is in front of the
            // TUI and confirms tool calls themselves).
            //
            // _executable is expected to be ALREADY quoted/wrapped by the
            // upstream resolver (ExecutableResolver.ResolveCommand handles
            // .cmd â†’ cmd.exe /c "path" wrapping on Windows, where ConPTY
            // would otherwise fail with "Could not create process"). So we
            // do NOT add quotes here — that would double-quote a wrapped
            // command and CreateProcessW would treat it as one giant token.
            var commandLine = string.IsNullOrWhiteSpace(_interactiveArgs)
                ? _executable
                : $"{_executable} {_interactiveArgs}";
            var pty = PtyHandleFactory.Spawn(
                command: commandLine,
                cols: cols,
                rows: rows,
                workingDirectory: WorkspacePath);
            _pty = pty;
            pty.Data += OnPtyData;
            pty.Disconnected += OnPtyDisconnected;
            Status = ConsoleSessionStatus.Running;

            // Bootstrap injection (builder sessions, console-with-skill).
            // Two-phase write deferred ~800ms after spawn:
            //   1. Wait for the CLI's TUI to finish booting (without this
            //      delay the bytes land before the child wires its
            //      readline-equivalent and disappear).
            //   2. Write everything UP TO (but excluding) the trailing
            //      CR — the body that should appear in the prompt.
            //   3. Brief pause so the TUI processes the body as input
            //      rather than as part of a paste burst.
            //   4. Write the trailing CR by itself — this fires the
            //      Enter key event and submits the prompt.
            //
            // The split-write matters because some TUIs (Codex, ratatui-
            // based) coalesce a single PTY write into a paste and never
            // raise the Enter key event for the embedded CR. Claude's
            // Ink TUI tolerates the combined write but also works with
            // the split, so we always split for consistency.
            //
            // Fire-and-forget so Resize returns immediately; failures
            // are best-effort (the user can always type the bootstrap
            // themselves if it's lost).
            if (!string.IsNullOrEmpty(_initialInput))
            {
                var input = _initialInput;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(BootstrapWriteDelay);

                        var crIndex = input.IndexOf('\r');
                        if (crIndex < 0)
                        {
                            // No CR at all — nothing to split. Just write.
                            await pty.WriteAsync(input);
                        }
                        else
                        {
                            var typed = input[..crIndex];
                            if (typed.Length > 0)
                            {
                                await pty.WriteAsync(typed);
                                await Task.Delay(BootstrapEnterDelay);
                            }
                            // Write only the CR (not "\r" + remainder).
                            // Retry with an empty Enter because some TUIs accept
                            // pasted text before startup is ready to submit it.
                            await pty.WriteAsync("\r");
                            await Task.Delay(BootstrapEnterRetryDelay);
                            await pty.WriteAsync("\r");
                        }
                    }
                    catch (Exception) { /* keep the session alive */ }
                });
            }
        }
        catch (Exception ex)
        {
            FinishedAt = DateTimeOffset.UtcNow;
            Status = ConsoleSessionStatus.Failed;
            // Surface the failure in the terminal so the user sees what
            // happened instead of a blank pane.
            try
            {
                DataReceived?.Invoke($"\r\n\x1b[31mCould not start {_executable}: {ex.Message}\x1b[0m\r\n");
            }
            catch { }
        }
        try { StatusChanged?.Invoke(this); } catch { }
    }

    /// <summary>
    /// Kills the underlying PTY session. There is no <c>Kill()</c> on
    /// Pty.Net's IPtyConnection — disposing the connection tears down
    /// the winpty session, which terminates the child process.
    /// </summary>
    public Task KillAsync() => DisposeAsync().AsTask();

    private void OnPtyData(string data)
    {
        if (string.IsNullOrEmpty(data)) return;
        AppendToScrollback(data);
        QueuePtyBroadcast(data);
    }

    private void QueuePtyBroadcast(string data)
    {
        bool flushNow;
        lock (_broadcastLock)
        {
            _pendingBroadcast.Append(data);
            flushNow = _pendingBroadcast.Length >= ImmediateBroadcastChars;
            if (!flushNow && _broadcastScheduled == 0)
            {
                _broadcastScheduled = 1;
                _ = Task.Run(FlushPtyBroadcastAfterDelay);
            }
        }

        if (flushNow)
        {
            FlushPtyBroadcast();
        }
    }

    private async Task FlushPtyBroadcastAfterDelay()
    {
        try { await Task.Delay(PtyBroadcastDelay); }
        catch { /* timer cancellation is non-critical */ }
        FlushPtyBroadcast();
    }

    private void FlushPtyBroadcast()
    {
        string payload;
        lock (_broadcastLock)
        {
            if (_pendingBroadcast.Length == 0)
            {
                _broadcastScheduled = 0;
                return;
            }
            payload = _pendingBroadcast.ToString();
            _pendingBroadcast.Clear();
            _broadcastScheduled = 0;
        }

        try
        {
            DataReceived?.Invoke(payload);
        }
        catch (Exception) { /* a bad subscriber shouldn't kill the read loop */ }
    }

    private void OnPtyDisconnected()
    {
        FlushPtyBroadcast();
        if (Status == ConsoleSessionStatus.Exited) return;
        FinishedAt = DateTimeOffset.UtcNow;
        Status = ConsoleSessionStatus.Exited;
        try { StatusChanged?.Invoke(this); } catch { }
    }

    private void AppendToScrollback(string chunk)
    {
        lock (_bufferLock)
        {
            var span = chunk.AsSpan();
            if (span.Length >= _scrollback.Length)
            {
                span[^_scrollback.Length..].CopyTo(_scrollback);
                _scrollbackHead = 0;
                _scrollbackLength = _scrollback.Length;
                return;
            }
            var insertAt = (_scrollbackHead + _scrollbackLength) % _scrollback.Length;
            var tail = _scrollback.Length - insertAt;
            if (span.Length <= tail)
            {
                span.CopyTo(_scrollback.AsSpan(insertAt));
            }
            else
            {
                span[..tail].CopyTo(_scrollback.AsSpan(insertAt));
                span[tail..].CopyTo(_scrollback.AsSpan(0));
            }
            _scrollbackLength += span.Length;
            if (_scrollbackLength > _scrollback.Length)
            {
                var overflow = _scrollbackLength - _scrollback.Length;
                _scrollbackHead = (_scrollbackHead + overflow) % _scrollback.Length;
                _scrollbackLength = _scrollback.Length;
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        if (System.Threading.Interlocked.Exchange(ref _disposed, 1) != 0) return ValueTask.CompletedTask;
        FlushPtyBroadcast();
        var pty = _pty;
        if (pty is not null)
        {
            try { pty.Data -= OnPtyData; } catch { }
            try { pty.Disconnected -= OnPtyDisconnected; } catch { }
            try { pty.Dispose(); } catch { }
        }
        if (Status != ConsoleSessionStatus.Exited && Status != ConsoleSessionStatus.Failed)
        {
            FinishedAt = DateTimeOffset.UtcNow;
            Status = ConsoleSessionStatus.Exited;
            try { StatusChanged?.Invoke(this); } catch { }
        }
        return ValueTask.CompletedTask;
    }
}
