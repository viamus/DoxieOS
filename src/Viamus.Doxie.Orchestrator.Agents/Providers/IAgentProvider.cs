using Viamus.Doxie.Orchestrator.Agents.Doxie;

namespace Viamus.Doxie.Orchestrator.Agents;

/// <summary>
/// Abstraction over the underlying agent CLI that the orchestrator drives.
/// Each implementation knows how to invoke a specific tool (Claude Code,
/// OpenAI Codex, ...) — its executable, command-line shape, prompt
/// formatting, session-mode handshake, and output line parsing.
///
/// The runner (<see cref="IAgentRunner"/>) handles everything provider-
/// independent: process spawning, stdout/stderr streaming, working-dir +
/// env layering, child-process containment, persistence, cancellation.
/// Anything that *differs* between CLIs lives behind this interface.
/// </summary>
public interface IAgentProvider
{
    /// <summary>
    /// Stable identifier — lower-kebab. Used to wire the provider in
    /// configuration and (eventually) to record which provider executed
    /// a given run.
    /// </summary>
    string Id { get; }

    /// <summary>Human-readable name shown in the UI.</summary>
    string DisplayName { get; }

    /// <summary>
    /// Resolved executable name or path. Either an entry on PATH
    /// (e.g. <c>"claude"</c>) or an absolute path. Used as
    /// <see cref="System.Diagnostics.ProcessStartInfo.FileName"/>.
    /// </summary>
    string Executable { get; }

    /// <summary>
    /// Translates the user-visible <paramref name="arguments"/> string
    /// (as captured by the Run dialog or workflow node) into the final
    /// prompt the CLI expects. Claude Code prepends <c>/&lt;skill&gt;</c>;
    /// other providers may pass the prompt verbatim.
    ///
    /// <paramref name="skillBody"/> is the contents of the agent's
    /// <c>SKILL.md</c> when the catalog can supply it. Providers that
    /// resolve skills natively (Claude Code via slash-commands) may
    /// ignore it; providers without a skills concept (Codex) inline it
    /// into the prompt so the underlying LLM has the same instructions
    /// regardless of which CLI is in front of it.
    ///
    /// <paramref name="memories"/> is the list of skill-scoped memory
    /// entries that the runner pre-filtered + sorted for THIS dispatch
    /// (only the ones whose <c>condition</c> matched the active mode /
    /// provider / workspace). Both providers append them as a delimited
    /// "## Auto-loaded context" block AFTER the user-facing arguments —
    /// for Claude that means after the slash command (so SKILL.md still
    /// auto-loads), for Codex it means after the user input section.
    /// Pass null or empty when there are no memories to inject.
    /// </summary>
    string FormatPrompt(
        AgentDescriptor agent,
        string arguments,
        string? skillBody = null,
        IReadOnlyList<DoxieSkillMemory>? memories = null);

    /// <summary>
    /// Builds the CLI argument vector for a one-shot (headless) run.
    /// The runner already formatted the prompt; the provider only needs
    /// to wrap it with whatever flags its CLI requires for streaming
    /// non-interactive execution.
    /// </summary>
    IEnumerable<string> BuildHeadlessArgs(AgentDescriptor agent, string formattedPrompt);

    /// <summary>
    /// Builds the CLI argument vector for a long-lived session run
    /// (keep-session-alive). The initial message is sent later via
    /// <see cref="WriteInitialMessage"/>; only flags go here.
    /// Throw <see cref="System.NotSupportedException"/> if the provider
    /// does not support session mode.
    /// </summary>
    IEnumerable<string> BuildSessionArgs(AgentDescriptor agent);

    /// <summary>
    /// Writes the first user message into a long-lived session's stdin.
    /// Each provider may use a different envelope format — Claude Code
    /// uses stream-json (<c>{"type":"user","message":{...}}</c>), Codex
    /// will likely use a different shape.
    /// Throw <see cref="System.NotSupportedException"/> if the provider
    /// does not support session mode.
    /// </summary>
    void WriteInitialMessage(System.IO.TextWriter stdin, AgentDescriptor agent, string formattedPrompt);

    /// <summary>
    /// Parses one line of stdout into zero or more user-visible strings.
    /// For Claude Code <c>--output-format stream-json</c> this turns each
    /// JSON event into readable text. Plain stdout (non-streaming CLIs,
    /// test fixtures) is pass-through.
    /// </summary>
    IReadOnlyList<string> ParseOutputLine(string line);

    /// <summary>
    /// Pulls token + cost accounting out of a single stdout line, when
    /// the provider's protocol carries one. Called on every stdout line
    /// in addition to <see cref="ParseOutputLine"/>; returning null is
    /// the common case (most lines aren't accounting-bearing). Providers
    /// without structured usage data (Codex today) return null
    /// unconditionally; callers persist the first non-null result onto
    /// the run.
    /// </summary>
    AgentRunUsage? TryParseUsage(string line);

    /// <summary>
    /// Args passed to the CLI when it's launched as an interactive
    /// terminal (the <c>/consoles</c> page). Distinct from
    /// <see cref="BuildHeadlessArgs"/> because:
    /// <list type="bullet">
    /// <item>There IS a TTY, so streaming flags aren't needed.</item>
    /// <item>The user is in front of it — auto-approve flags may or
    ///       may not be appropriate (Claude wants them, Codex usually
    ///       doesn't).</item>
    /// </list>
    /// Return an empty sequence to spawn the CLI with no arguments.
    /// </summary>
    IEnumerable<string> BuildInteractiveArgs();

    /// <summary>
    /// Returns the exact bytes (incl. trailing CR) DoxieOS should write
    /// into a freshly-spawned interactive PTY so the session "wakes up"
    /// already in the persona of <paramref name="skillId"/>. Used by the
    /// Builder pages (and any future console that opens "with a skill")
    /// to bridge the gap between providers that resolve skills natively
    /// (Claude Code parses <c>/&lt;skill&gt;</c> as a slash-command and
    /// loads <c>SKILL.md</c>) and providers that do not (Codex sees
    /// <c>"/agent-builder"</c> as literal text).
    ///
    /// <paramref name="skillBody"/> is the skill's natural-language body
    /// (canonical <c>.doxie/skills/&lt;id&gt;/body.md</c>). Providers
    /// that resolve skills natively may ignore it; providers that need
    /// to inline the persona (Codex) inline it ahead of the user-facing
    /// kick-off message. May be null when the body cannot be loaded —
    /// implementations should degrade gracefully (Claude still sends the
    /// slash command; Codex falls back to a minimal "you are skill X"
    /// preface).
    ///
    /// Always include a trailing <c>"\r"</c> so the line is committed to
    /// the CLI prompt without the user pressing Enter.
    /// </summary>
    string FormatBuilderBootstrap(string skillId, string? skillBody);
}
