using System.Text;
using System.Text.Json;
using Viamus.Doxie.Orchestrator.Agents.Doxie;

namespace Viamus.Doxie.Orchestrator.Agents;

/// <summary>
/// First-party provider — drives the Anthropic Claude Code CLI
/// (<c>claude</c>). This is the historical default and captures every
/// Claude-specific behaviour that used to live inline in the runner:
/// slash-command prompt format, headless flags
/// (<c>-p &lt;prompt&gt; --dangerously-skip-permissions --output-format
/// stream-json --verbose</c>), session-mode handshake, and stream-json
/// output parsing.
/// </summary>
public sealed class ClaudeCodeProvider : IAgentProvider, IHeadlessStdinAgentProvider
{
    private readonly string _executable;

    public ClaudeCodeProvider(ClaudeRunnerOptions options)
    {
        _executable = string.IsNullOrWhiteSpace(options.Executable)
            ? "claude"
            : options.Executable;
    }

    public string Id => "claude-code";
    public string DisplayName => "Claude Code";
    public string Executable => _executable;

    /// <summary>
    /// If <paramref name="arguments"/> already starts with <c>/</c> it is a
    /// full slash-command and is used verbatim — this lets synthetic modes
    /// invoke a different skill (e.g. a "Run" mode whose template is
    /// <c>"/loop 5m /sample-watch check"</c>). Otherwise the agent's primary
    /// skill is used as the prefix.
    ///
    /// <paramref name="skillBody"/> is ignored — Claude Code reads the
    /// SKILL.md natively when the slash-command is invoked, so injecting
    /// it would be redundant and bloat the prompt window.
    ///
    /// <paramref name="memories"/> are appended AFTER the slash command
    /// + args under a clear delimiter. The slash stays as the first
    /// token so Claude still resolves and auto-loads the SKILL.md; the
    /// memories arrive as additional context the model reads before
    /// taking action. Empty / null collections are skipped silently.
    /// </summary>
    public string FormatPrompt(
        AgentDescriptor agent,
        string arguments,
        string? skillBody = null,
        IReadOnlyList<DoxieSkillMemory>? memories = null)
    {
        var trimmed = arguments?.Trim() ?? string.Empty;
        var slashCommand = string.IsNullOrEmpty(trimmed)
            ? $"/{agent.SkillName}"
            : trimmed.StartsWith('/') ? trimmed : $"/{agent.SkillName} {trimmed}";

        var memoryBlock = FormatMemoryBlock(memories);
        return memoryBlock is null ? slashCommand : $"{slashCommand}\n\n{memoryBlock}";
    }

    /// <summary>
    /// Renders the auto-loaded memory block — shared with Codex's
    /// equivalent so both providers produce the same delimiter shape
    /// (the model sees identical context regardless of CLI). Returns
    /// null when there's nothing to render.
    /// </summary>
    internal static string? FormatMemoryBlock(IReadOnlyList<DoxieSkillMemory>? memories)
    {
        if (memories is null || memories.Count == 0) return null;

        var sb = new StringBuilder();
        sb.Append("---\n\n");
        sb.Append("# Auto-loaded context\n\n");
        sb.Append("The following memories were attached to this skill and matched the active dispatch ");
        sb.Append("(mode / provider / workspace). Treat them as binding guidance — they encode lessons ");
        sb.Append("learned from past runs and they apply to this invocation.\n\n");
        foreach (var m in memories)
        {
            sb.Append("## ").Append(m.Name);
            sb.Append("  *(").Append(m.Priority.ToString().ToLowerInvariant()).Append(")*\n\n");
            sb.Append(m.Body.TrimEnd()).Append("\n\n");
        }
        return sb.ToString().TrimEnd();
    }

    public IEnumerable<string> BuildHeadlessArgs(AgentDescriptor agent, string formattedPrompt)
    {
        // Headless Claude CLI invocation.
        yield return "-p";
        yield return formattedPrompt;
        // No TTY is attached to the subprocess, so any tool that requires
        // permission approval would hang the run. Skip the prompts — the
        // orchestrator is the trust boundary that approved this run.
        yield return "--dangerously-skip-permissions";
        // stream-json forces line-buffered NDJSON output (one event per line)
        // even when stdout is a pipe; without it Claude block-buffers stdout
        // and nothing reaches the UI until process exit.
        yield return "--output-format";
        yield return "stream-json";
        yield return "--verbose";
    }

    public IEnumerable<string> BuildSessionArgs(AgentDescriptor agent)
    {
        // No prompt as argument — the message comes via stdin as a single
        // stream-json line. The subprocess stays alive as long as stdin is
        // open, so any /loop-registered cron keeps firing inside the same
        // session.
        yield return "--print";
        yield return "--input-format";
        yield return "stream-json";
        yield return "--output-format";
        yield return "stream-json";
        yield return "--verbose";
        yield return "--include-partial-messages";
        yield return "--dangerously-skip-permissions";
    }

    public IEnumerable<string> BuildHeadlessStdinArgs(AgentDescriptor agent) =>
        BuildSessionArgs(agent);

    public void WriteHeadlessPrompt(System.IO.TextWriter stdin, AgentDescriptor agent, string formattedPrompt)
    {
        WriteInitialMessage(stdin, agent, formattedPrompt);
        stdin.Close();
    }

    public void WriteInitialMessage(System.IO.TextWriter stdin, AgentDescriptor agent, string formattedPrompt)
    {
        var envelope = new
        {
            type = "user",
            message = new
            {
                role = "user",
                content = new object[]
                {
                    new { type = "text", text = formattedPrompt },
                },
            },
        };

        var json = JsonSerializer.Serialize(envelope);
        stdin.WriteLine(json);
        stdin.Flush();
    }

    public IEnumerable<string> BuildInteractiveArgs()
    {
        // The Console (TUI) is wrapped by the orchestrator, which is the
        // trust boundary that authorised this session — same logic as
        // the headless path. Without this flag claude pauses on every
        // tool call waiting for an approval prompt that never comes.
        yield return "--dangerously-skip-permissions";
    }

    /// <summary>
    /// Claude Code resolves slash-commands natively from
    /// <c>.claude/skills/&lt;id&gt;/SKILL.md</c>, so the bootstrap is
    /// just the slash command followed by CR — Claude reads SKILL.md
    /// itself. <paramref name="skillBody"/> is intentionally ignored:
    /// inlining it would duplicate what's about to be auto-loaded and
    /// waste tokens.
    /// </summary>
    public string FormatBuilderBootstrap(string skillId, string? skillBody) =>
        $"/{skillId}\r";

    public IReadOnlyList<string> ParseOutputLine(string line)
    {
        // Existing parser already pass-through non-JSON, so plain stdout
        // (cmd.exe in tests, or any non-Claude executable) flows through
        // unchanged; Claude stream-json events render to readable lines.
        return ClaudeStreamEventParser.Parse(line).ToList();
    }

    public AgentRunUsage? TryParseUsage(string line) =>
        ClaudeStreamEventParser.TryParseUsage(line);
}
