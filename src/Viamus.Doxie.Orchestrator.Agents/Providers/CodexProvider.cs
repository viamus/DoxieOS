using System.Text.Json;
using System.Text.RegularExpressions;
using Viamus.Doxie.Orchestrator.Agents.Doxie;

namespace Viamus.Doxie.Orchestrator.Agents;

/// <summary>
/// Provider for the OpenAI Codex CLI (<c>github.com/openai/codex</c>).
/// Spawns <c>codex exec -</c> in non-interactive mode via the runner's
/// stdin path so user prompts are not interpreted by the host shell.
///
/// Caveats vs <see cref="ClaudeCodeProvider"/>:
/// <list type="bullet">
/// <item>No slash-command convention — Codex does not consume
///       <c>.claude/skills/&lt;name&gt;/SKILL.md</c>. The orchestrator's
///       agent dispatch still produces a <c>/skill arguments</c> prompt
///       (preserving the universal contract); Codex receives that
///       string verbatim and treats it as a free-form instruction.
///       Skills that rely on Claude-side slash-prefix semantics will
///       degrade — a follow-up may translate them per-provider.</item>
/// <item>No session-mode handshake — keep-session-alive runs throw
///       <see cref="System.NotSupportedException"/>. Cron-driven
///       <c>/loop</c> workflows won't work under Codex until the CLI
///       grows an analogous long-lived stdin protocol.</item>
/// <item>Output parser is pass-through — Codex stdout flows to the UI
///       unchanged. Switch to JSON event parsing if/when Codex
///       stabilises a streaming format equivalent to Claude's
///       stream-json.</item>
/// </list>
/// </summary>
public sealed class CodexProvider : IAgentProvider, IAgentUsageEstimator, IHeadlessStdinAgentProvider
{
    // Matches /skill-name tokens that Codex CLI would intercept as native
    // skill invocations. Excludes paths (/dev/null, /path/to/file) by
    // requiring no preceding or following slash or word character.
    private static readonly Regex SlashCommandPattern =
        new(@"(?<![/\w])/([a-z][a-z0-9-]+)(?![/\w])", RegexOptions.Compiled);

    private readonly string _executable;
    private readonly bool _autoApprove;
    private readonly string _model;

    public CodexProvider(CodexProviderOptions options)
    {
        _executable = string.IsNullOrWhiteSpace(options.Executable)
            ? "codex"
            : options.Executable;
        _autoApprove = options.AutoApprove;
        _model = string.IsNullOrWhiteSpace(options.Model)
            ? "gpt-5.5"
            : options.Model.Trim();
    }

    public string Id => "codex";
    public string DisplayName => "OpenAI Codex";
    public string Executable => _executable;

    /// <summary>
    /// Codex doesn't resolve skills natively — it has no equivalent of
    /// <c>.claude/skills/&lt;name&gt;/SKILL.md</c> on disk. So the
    /// orchestrator does the resolution: the runner passes
    /// <paramref name="skillBody"/> (the SKILL.md content), and we
    /// inline it as the first half of the prompt with the user's
    /// arguments after a separator. This way an agent invoked via
    /// Codex behaves the same as via Claude Code at the prompt level —
    /// the model sees the same instructions either way.
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

        var memoryBlock = ClaudeCodeProvider.FormatMemoryBlock(memories);

        if (string.IsNullOrWhiteSpace(skillBody))
        {
            return memoryBlock is null
                ? slashCommand
                : $"{slashCommand}\n\n{memoryBlock}";
        }

        // Skill body first (the natural-language instructions Claude
        // would read on slash-invocation), then a delimiter, then the
        // user's effective input, then the memory block (when present).
        // The delimiter is verbose so the model doesn't accidentally mix
        // the sections.
        //
        // IMPORTANT: strip the leading "/<skillName>" prefix from the
        // user input. Codex has its own /command dispatcher and will try
        // to find a native "library" (or whatever) tool when it sees a
        // slash — and fail. The skill instructions are already in the
        // section above; only the bare arguments belong here.
        var skillPrefix = $"/{agent.SkillName}";
        var bareArgs = slashCommand.StartsWith(skillPrefix + " ", StringComparison.OrdinalIgnoreCase)
            ? slashCommand[(skillPrefix.Length + 1)..].Trim()
            : slashCommand == skillPrefix
                ? string.Empty
                : slashCommand; // synthetic /other-skill invocation — keep verbatim

        var userBlock = string.IsNullOrEmpty(bareArgs)
            ? memoryBlock ?? string.Empty
            : memoryBlock is null
                ? bareArgs
                : $"{bareArgs}\n\n{memoryBlock}";

        // Codex CLI intercepts /word patterns (e.g. /library, /loop) as
        // native skill invocations even when they appear in the instruction
        // body. Strip the leading slash from all command-like tokens in the
        // skill body so the model reads them as plain names, not dispatches.
        // Pattern: /word-with-hyphens not preceded or followed by another
        // slash or word char — matches /library but not /dev/null or URLs.
        var sanitizedBody = SlashCommandPattern.Replace(skillBody.TrimEnd(), "$1");

        // Lead with the imperative so the model treats this as a command
        // to execute now, not as documentation to acknowledge before asking
        // for a follow-up. Models like GPT-4o read heading-heavy prompts as
        // "context" and then reply "ready, what should I do?" — starting
        // with the direct instruction avoids that pattern.
        var intro = string.IsNullOrEmpty(userBlock)
            ? $"Execute the `{agent.SkillName}` skill as described below."
            : $"Execute `{agent.SkillName}` with the following arguments: {userBlock}";

        return $"""
            {intro}

            DoxieOS resolution rules:
            - You are already inside the DoxieOS agent/skill `{agent.SkillName}`. Do not search for `{agent.SkillName}` as a shell command, executable, or repository-root filename.
            - Kebab-case names such as `backend-platform-architect`, `qa-test-strategist`, or `code-review-surgeon` are Doxie skill/agent identifiers first. Resolve them from `$DOXIE_AGENT_SKILL_ROOT` when set; otherwise prefer `.doxie/skills/<id>/manifest.json` + `body.md` before considering shell commands or files.
            - In workflow handoffs, an `agentId` is a Doxie skill id. If `$DOXIE_WORKFLOW_ROOT` is set, treat that folder as the workflow definition source.

            ---

            {sanitizedBody}

            ---

            Use your file-access tools to complete the task end-to-end. Do not dispatch subagents. Do not ask for clarification. Return your output when done.
            """;
    }

    public IEnumerable<string> BuildHeadlessArgs(AgentDescriptor agent, string formattedPrompt)
    {
        // `codex exec` runs Codex non-interactively. The flag set below
        // is the bare minimum that lets the orchestrator (no TTY, may
        // be outside a git repo) drive Codex without it stalling on
        // approval prompts or refusing to start:
        //
        //   --dangerously-bypass-approvals-and-sandbox
        //       analogue of Claude's --dangerously-skip-permissions —
        //       the orchestrator IS the trust boundary that approved
        //       this run, and there's no human at the subprocess to
        //       answer y/n prompts.
        //   --skip-git-repo-check
        //       Codex defaults to refusing to operate outside a git
        //       repo; workspace dirs (.workspace/<id>/) are not git
        //       repos, so we have to opt out.
        //   --color never
        //       no ANSI escapes in the captured output — the UI does
        //       not currently render xterm colour codes for piped
        //       (non-PTY) output.
        //
        // This positional-prompt path is kept for the provider contract.
        // The production runner prefers BuildHeadlessStdinArgs for Codex
        // so shell metacharacters in user input never touch Windows cmd.exe.
        yield return "exec";
        foreach (var arg in CommonExecArgs())
        {
            yield return arg;
        }
        yield return formattedPrompt;
    }

    public IEnumerable<string> BuildHeadlessStdinArgs(AgentDescriptor agent)
    {
        yield return "exec";
        foreach (var arg in CommonExecArgs())
        {
            yield return arg;
        }
        yield return "-";
    }

    public void WriteHeadlessPrompt(System.IO.TextWriter stdin, AgentDescriptor agent, string formattedPrompt)
    {
        stdin.Write(formattedPrompt);
        stdin.Close();
    }

    private IEnumerable<string> CommonExecArgs()
    {
        yield return "--dangerously-bypass-approvals-and-sandbox";
        yield return "--skip-git-repo-check";
        yield return "--json";
        if (!string.IsNullOrWhiteSpace(_model))
        {
            yield return "--model";
            yield return _model;
        }
        yield return "--color";
        yield return "never";
    }

    public IEnumerable<string> BuildSessionArgs(AgentDescriptor agent) =>
        throw new NotSupportedException(
            "Codex provider does not yet support keep-session-alive (cron) runs. " +
            "Use Claude Code as the provider for /loop-driven workflows.");

    public void WriteInitialMessage(System.IO.TextWriter stdin, AgentDescriptor agent, string formattedPrompt) =>
        throw new NotSupportedException(
            "Codex provider does not yet support keep-session-alive (cron) runs.");

    public IEnumerable<string> BuildInteractiveArgs()
    {
        // Workspace consoles are Doxie-owned PTYs, same trust boundary
        // as headless agent runs. Keep Codex interactive, but start it in
        // YOLO mode so console sessions behave like agent runs.
        yield return "--dangerously-bypass-approvals-and-sandbox";
    }

    public IReadOnlyList<string> ParseOutputLine(string line)
    {
        if (string.IsNullOrEmpty(line)) return System.Array.Empty<string>();

        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (!root.TryGetProperty("type", out var typeElement)) return System.Array.Empty<string>();

            var type = typeElement.GetString();
            if (string.Equals(type, "item.completed", StringComparison.Ordinal)
                && root.TryGetProperty("item", out var item)
                && item.TryGetProperty("type", out var itemType)
                && string.Equals(itemType.GetString(), "agent_message", StringComparison.Ordinal)
                && item.TryGetProperty("text", out var text)
                && !string.IsNullOrWhiteSpace(text.GetString()))
            {
                return new[] { text.GetString()! };
            }

            return System.Array.Empty<string>();
        }
        catch (JsonException)
        {
            return new[] { line };
        }
    }

    /// <summary>
    /// Codex doesn't currently emit a structured per-run usage block on
    /// stdout. Returning null leaves <see cref="AgentRun.Usage"/> at its
    /// default null state — the dashboard simply skips Codex runs in
    /// the cost aggregation, and the per-run page hides the cost panel.
    /// Wire this up once Codex exposes an equivalent of Claude's
    /// <c>{"type":"result", ...}</c> event.
    /// </summary>
    public AgentRunUsage? TryParseUsage(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;

        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (!root.TryGetProperty("type", out var type)
                || !string.Equals(type.GetString(), "turn.completed", StringComparison.Ordinal)
                || !root.TryGetProperty("usage", out var usage))
            {
                return null;
            }

            var input = GetInt64(usage, "input_tokens");
            var cached = GetInt64(usage, "cached_input_tokens");
            var output = GetInt64(usage, "output_tokens") + GetInt64(usage, "reasoning_output_tokens");

            return input == 0 && cached == 0 && output == 0
                ? null
                : new AgentRunUsage(
                    InputTokens: Math.Max(0, input - cached),
                    OutputTokens: output,
                    CacheReadTokens: cached,
                    CacheCreationTokens: 0,
                    TotalCostUsd: EstimateCostUsd(_model, input, cached, output));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public AgentRunUsage? EstimateUsage(string formattedPrompt, IReadOnlyList<AgentRunOutputLine> output)
    {
        var inputTokens = EstimateTokenCount(formattedPrompt);
        var outputText = string.Join(
            "\n",
            output
                .Where(line => line.Source == AgentRunOutputSource.Stdout)
                .Select(line => line.Text)
                .Where(text => !string.IsNullOrWhiteSpace(text)));
        var outputTokens = EstimateTokenCount(outputText);

        if (inputTokens == 0 && outputTokens == 0) return null;

        return new AgentRunUsage(
            InputTokens: inputTokens,
            OutputTokens: outputTokens,
            CacheReadTokens: 0,
            CacheCreationTokens: 0,
            TotalCostUsd: EstimateCostUsd(_model, inputTokens, cachedInputTokens: 0, outputTokens));
    }

    private static long EstimateTokenCount(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;

        var chars = 0;
        foreach (var ch in text)
        {
            if (!char.IsWhiteSpace(ch)) chars++;
        }

        // Conservative GPT-family approximation: one token is about four
        // non-whitespace characters in mixed English/Portuguese/code prompts.
        return Math.Max(1, (long)Math.Ceiling(chars / 4.0));
    }

    private static long GetInt64(JsonElement obj, string propertyName)
    {
        if (!obj.TryGetProperty(propertyName, out var value)) return 0;
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var parsed)
            ? parsed
            : 0;
    }

    private static decimal? EstimateCostUsd(string model, long inputTokens, long cachedInputTokens, long outputTokens)
    {
        var prices = GetPrices(model);
        if (prices is null) return null;

        var freshInputTokens = Math.Max(0, inputTokens - cachedInputTokens);
        var inputMultiplier = inputTokens > 272_000 ? 2m : 1m;
        var outputMultiplier = inputTokens > 272_000 ? 1.5m : 1m;

        return (freshInputTokens * prices.Value.InputPerMillion * inputMultiplier
            + cachedInputTokens * prices.Value.CachedInputPerMillion * inputMultiplier
            + outputTokens * prices.Value.OutputPerMillion * outputMultiplier) / 1_000_000m;
    }

    private static ModelPrices? GetPrices(string model)
    {
        var normalized = model.Trim().ToLowerInvariant();
        return normalized switch
        {
            "gpt-5.5" or "gpt-5.5-2026-04-23" => new ModelPrices(5.00m, 0.50m, 30.00m),
            _ => null,
        };
    }

    private readonly record struct ModelPrices(
        decimal InputPerMillion,
        decimal CachedInputPerMillion,
        decimal OutputPerMillion);

    /// <summary>
    /// Codex has no slash-command surface, so a literal <c>"/agent-builder"</c>
    /// would just be free text. The persona/body is already inlined into
    /// the sandbox-local <c>AGENTS.md</c> by <c>BuilderSandboxAllocator</c>
    /// — Codex reads that on TUI start. So this bootstrap is just a
    /// **single-line** kick-off that fires one Submit (the trailing CR
    /// becomes Enter); embedding multi-line content here would be a bug
    /// because every <c>\n</c> in the payload makes Codex's TUI fire
    /// Submit early, sending only the first line and leaking the rest
    /// as a follow-up prompt.
    ///
    /// <paramref name="skillBody"/> is intentionally ignored — it lives
    /// in AGENTS.md, not in the wire. Kept on the signature so the
    /// interface stays uniform with providers that DO inline it.
    /// </summary>
    public string FormatBuilderBootstrap(string skillId, string? skillBody) =>
        $"Antes de responder, leia e siga o AGENTS.md deste diretÃ³rio como guia obrigatÃ³rio da conversa para o skill `{skillId}`. Use cada mensagem do usuÃ¡rio como instruÃ§Ã£o de ediÃ§Ã£o, atualize .draft/manifest.json quando houver informaÃ§Ã£o suficiente, pergunte sÃ³ o essencial e comece em PT-BR.\r";
}
