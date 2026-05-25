namespace Viamus.Doxie.Orchestrator.Context;

public interface IConsoleSessionStore
{
    IReadOnlyList<ConsoleSession> ListAll();
    ConsoleSession? Get(string id);

    /// <summary>
    /// Spawns a PTY-backed <c>claude</c> subprocess at the supplied
    /// workspace path and tracks the resulting session. Refreshes the
    /// workspace's auto-generated <c>CLAUDE.md</c> and <c>AGENTS.md</c>
    /// first so mounted libraries are available to both Claude and Codex
    /// for workspaces created before the auto-mount feature shipped.
    /// Throws if the workspace doesn't exist or the spawn fails.
    /// </summary>
    Task<ConsoleSession> CreateAsync(
        string workspaceId,
        CancellationToken ct = default);

    /// <summary>
    /// Spawns a PTY-backed <c>claude</c> subprocess at an arbitrary cwd
    /// (typically a builder sandbox under <c>./.sandbox/</c>) without
    /// requiring a registered Workspace. Used by the Agent / Workflow
    /// Builder pages — they own the lifecycle and the cwd. The returned
    /// session is tagged <see cref="ConsoleSessionKind.Builder"/> so the
    /// regular <c>/consoles</c> page filters it out.
    /// </summary>
    /// <param name="syntheticWorkspaceId">
    /// A pseudo-workspace id used for grouping / display only — not
    /// resolved against <c>IWorkspaceStore</c>. Typically the sandbox tag
    /// (e.g. <c>"builder-agent-3"</c>).
    /// </param>
    /// <param name="cwd">Absolute path the PTY's child process inherits as its working directory.</param>
    /// <param name="label">Human-readable label shown in any UI that lists builder sessions.</param>
    /// <param name="initialInput">
    /// Optional bytes (typically a slash command + CR for Claude, or an
    /// inlined skill body + CR for Codex) DoxieOS writes into the
    /// freshly-spawned PTY so the session arrives in-character. Pass
    /// null/empty for a bare console.
    /// </param>
    Task<ConsoleSession> CreateForBuilderAsync(
        string syntheticWorkspaceId,
        string cwd,
        string label,
        string? initialInput = null,
        CancellationToken ct = default);

    Task<ConsoleSession> CreateForProviderAsync(
        string providerId,
        string cwd,
        string label,
        CancellationToken ct = default);

    /// <summary>
    /// Terminates the subprocess and removes the session from the
    /// in-memory list. No-op if the id is unknown.
    /// </summary>
    Task RemoveAsync(string sessionId);

    event Action<ConsoleSession>? SessionCreated;
    event Action<ConsoleSession>? SessionRemoved;
    event Action<ConsoleSession>? SessionUpdated;
}
