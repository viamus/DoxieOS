namespace Viamus.Doxie.Orchestrator.Agents;

/// <summary>
/// Configuration for the Claude subprocess runner. Exposed so the executable
/// path and working directory can be overridden in <c>appsettings.json</c>
/// without recompiling — useful when a machine doesn't ship the user's
/// <c>claudex</c> wrapper or wants to point at a different project root.
/// </summary>
public sealed class ClaudeRunnerOptions
{
    /// <summary>
    /// Executable spawned for each agent run. Default is <c>claude</c>; the
    /// runner already passes <c>--dangerously-skip-permissions</c>, so a
    /// stock Claude CLI install works out of the box without any wrapper.
    /// Override this value if a custom shim or alternate executable name
    /// is needed on a particular machine.
    /// </summary>
    public string Executable { get; set; } = "claude";

    /// <summary>
    /// Working directory used to launch the subprocess. The Claude CLI
    /// restricts file access to the cwd at startup, so this needs to point
    /// at the project root containing <c>.doxie/</c> (canonical) plus the
    /// auto-generated <c>.claude/</c> shim Claude Code reads natively,
    /// alongside any repos / state files the agents need to read or write.
    ///
    /// Default <c>"."</c> means "use whatever directory the orchestrator
    /// itself was launched from". This makes the project-root folder name
    /// irrelevant — rename the folder and DoxieOS keeps working as long
    /// as it's started from inside the new location.
    /// </summary>
    public string WorkingDirectory { get; set; } = ".";
}
