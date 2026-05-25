using FluentAssertions;

namespace Viamus.Doxie.Orchestrator.Agents.Tests;

public sealed class CodexProviderTests
{
    private static readonly AgentDescriptor TestAgent = new(
        Id: "library",
        Name: "Library",
        Description: "fixture",
        SkillName: "library",
        Category: AgentCategory.Other);

    private static CodexProvider Provider() => new(new CodexProviderOptions());

    [Fact]
    public void Id_is_stable_and_lower_kebab()
    {
        Provider().Id.Should().Be("codex");
    }

    [Fact]
    public void Executable_falls_back_to_codex_when_options_blank()
    {
        new CodexProvider(new CodexProviderOptions { Executable = "" }).Executable
            .Should().Be("codex");
    }

    [Fact]
    public void Executable_honours_options_override()
    {
        new CodexProvider(new CodexProviderOptions { Executable = "/usr/local/bin/codex" }).Executable
            .Should().Be("/usr/local/bin/codex");
    }

    [Fact]
    public void BuildHeadlessArgs_defaults_to_gpt_5_5_model()
    {
        Provider().BuildHeadlessArgs(TestAgent, "/library")
            .Should().ContainInOrder("--model", "gpt-5.5");
    }

    [Fact]
    public void FormatPrompt_with_empty_arguments_yields_bare_slash_command()
    {
        Provider().FormatPrompt(TestAgent, "").Should().Be("/library");
    }

    [Fact]
    public void FormatPrompt_with_plain_arguments_prefixes_skill()
    {
        Provider().FormatPrompt(TestAgent, "from-files /tmp/foo")
            .Should().Be("/library from-files /tmp/foo");
    }

    [Fact]
    public void FormatPrompt_inlines_skill_body_when_supplied()
    {
        const string skillBody = "# Library\n\nThis skill builds memory packs.";
        var result = Provider().FormatPrompt(TestAgent, "from-files /tmp/foo", skillBody);

        // Starts with an imperative so GPT-style models treat it as a command.
        result.Should().StartWith("Execute `library` with the following arguments: from-files /tmp/foo");
        // Skill body is embedded after the opening line.
        result.Should().Contain("This skill builds memory packs.");
        result.Should().Contain("DoxieOS resolution rules");
        result.Should().Contain("Do not search for `library` as a shell command");
        result.Should().Contain("$DOXIE_AGENT_SKILL_ROOT");
        result.Should().Contain(".doxie/skills/<id>");
        result.Should().Contain("$DOXIE_WORKFLOW_ROOT");
        result.Should().Contain("workflow handoffs");
        // No slash-command prefix — Codex would intercept it.
        result.Should().NotContain("/library");
        // Imperative intro comes BEFORE the skill body.
        result.IndexOf("Execute").Should()
            .BeLessThan(result.IndexOf("This skill builds memory packs."));
    }

    [Fact]
    public void FormatPrompt_without_skill_body_returns_just_the_slash_command()
    {
        var result = Provider().FormatPrompt(TestAgent, "from-files /tmp/foo", skillBody: null);

        result.Should().Be("/library from-files /tmp/foo");
        result.Should().NotContain("# Skill:");
    }

    [Fact]
    public void FormatPrompt_treats_whitespace_skill_body_as_absent()
    {
        var result = Provider().FormatPrompt(TestAgent, "go", "   \n  \n");

        result.Should().Be("/library go");
    }

    [Fact]
    public void BuildHeadlessArgs_emits_exec_with_defensive_flags_and_trailing_prompt()
    {
        var args = Provider().BuildHeadlessArgs(TestAgent, "/library").ToList();

        // exec subcommand first
        args[0].Should().Be("exec");
        // bypass-approvals (no human to confirm prompts in headless)
        args.Should().Contain("--dangerously-bypass-approvals-and-sandbox");
        // run outside git repos (workspaces are not git-init'd by default)
        args.Should().Contain("--skip-git-repo-check");
        // structured events carry final usage telemetry
        args.Should().Contain("--json");
        // model is explicit so cost estimates can use the matching tariff
        args.Should().ContainInOrder("--model", "gpt-5.5");
        // strip ANSI from the captured stdout
        args.Should().ContainInOrder("--color", "never");
        // prompt is the LAST argument — Codex parses it as positional
        args.Last().Should().Be("/library");
    }

    [Fact]
    public void BuildHeadlessStdinArgs_emits_exec_with_dash_prompt()
    {
        var provider = Provider();
        var args = ((IHeadlessStdinAgentProvider)provider).BuildHeadlessStdinArgs(TestAgent).ToList();

        args[0].Should().Be("exec");
        args.Should().Contain("--dangerously-bypass-approvals-and-sandbox");
        args.Should().Contain("--skip-git-repo-check");
        args.Should().Contain("--json");
        args.Should().ContainInOrder("--model", "gpt-5.5");
        args.Should().ContainInOrder("--color", "never");
        args.Last().Should().Be("-");
    }

    [Fact]
    public void WriteHeadlessPrompt_writes_plain_prompt_to_stdin_and_closes()
    {
        var provider = Provider();
        using var sw = new StringWriter();

        ((IHeadlessStdinAgentProvider)provider).WriteHeadlessPrompt(sw, TestAgent, "hello from stdin");

        sw.ToString().Should().Be("hello from stdin");
    }

    [Fact]
    public void BuildSessionArgs_throws_NotSupportedException()
    {
        var act = () => Provider().BuildSessionArgs(TestAgent).ToList();

        act.Should().Throw<NotSupportedException>()
            .WithMessage("*does not yet support*");
    }

    [Fact]
    public void WriteInitialMessage_throws_NotSupportedException()
    {
        using var sw = new StringWriter();

        var act = () => Provider().WriteInitialMessage(sw, TestAgent, "/library");

        act.Should().Throw<NotSupportedException>();
    }

    [Fact]
    public void BuildInteractiveArgs_emits_yolo_flag_by_default()
    {
        Provider().BuildInteractiveArgs()
            .Should().ContainSingle()
            .Which.Should().Be("--dangerously-bypass-approvals-and-sandbox");
    }

    [Fact]
    public void ParseOutputLine_passes_non_json_text_through()
    {
        Provider().ParseOutputLine("ok hello").Should().Equal("ok hello");
    }

    [Fact]
    public void ParseOutputLine_extracts_codex_agent_message_json()
    {
        const string json = """{"type":"item.completed","item":{"id":"item_0","type":"agent_message","text":"OK"}}""";

        Provider().ParseOutputLine(json).Should().Equal("OK");
    }

    [Fact]
    public void ParseOutputLine_suppresses_codex_lifecycle_json()
    {
        Provider().ParseOutputLine("""{"type":"turn.started"}""").Should().BeEmpty();
    }

    [Fact]
    public void TryParseUsage_extracts_codex_turn_completed_usage()
    {
        const string json = """
            {"type":"turn.completed","usage":{"input_tokens":12682,"cached_input_tokens":11136,"output_tokens":5,"reasoning_output_tokens":7}}
            """;

        var usage = Provider().TryParseUsage(json);

        usage.Should().NotBeNull();
        usage!.InputTokens.Should().Be(1546);
        usage.CacheReadTokens.Should().Be(11136);
        usage.OutputTokens.Should().Be(12);
        usage.CacheCreationTokens.Should().Be(0);
        usage.TotalCostUsd.Should().BeApproximately(0.013658m, 0.000001m);
        usage.TotalTokens.Should().Be(12694);
    }

    [Fact]
    public void TryParseUsage_applies_gpt_5_5_long_context_multiplier()
    {
        var provider = new CodexProvider(new CodexProviderOptions { Model = "gpt-5.5" });
        const string json = """
            {"type":"turn.completed","usage":{"input_tokens":300000,"cached_input_tokens":100000,"output_tokens":10000,"reasoning_output_tokens":0}}
            """;

        var usage = provider.TryParseUsage(json);

        usage.Should().NotBeNull();
        usage!.TotalCostUsd.Should().BeApproximately(2.55m, 0.0001m);
    }

    [Fact]
    public void TryParseUsage_leaves_cost_null_for_unknown_model()
    {
        var provider = new CodexProvider(new CodexProviderOptions { Model = "future-model" });
        const string json = """
            {"type":"turn.completed","usage":{"input_tokens":100,"cached_input_tokens":0,"output_tokens":10}}
            """;

        provider.TryParseUsage(json)!.TotalCostUsd.Should().BeNull();
    }

    [Fact]
    public void TryParseUsage_ignores_codex_events_without_usage()
    {
        Provider().TryParseUsage("""{"type":"turn.started"}""").Should().BeNull();
        Provider().TryParseUsage("plain text").Should().BeNull();
    }

    [Fact]
    public void EstimateUsage_simulates_codex_tokens_and_cost_when_usage_event_is_absent()
    {
        var provider = Provider();
        var output = new[]
        {
            new AgentRunOutputLine(DateTimeOffset.UtcNow, AgentRunOutputSource.Stdout, "Done with a useful answer."),
            new AgentRunOutputLine(DateTimeOffset.UtcNow, AgentRunOutputSource.Stderr, "debug noise"),
        };

        var usage = provider.EstimateUsage(new string('a', 400), output);

        usage.Should().NotBeNull();
        usage!.InputTokens.Should().Be(100);
        usage.OutputTokens.Should().BeGreaterThan(0);
        usage.CacheReadTokens.Should().Be(0);
        usage.CacheCreationTokens.Should().Be(0);
        usage.TotalCostUsd.Should().NotBeNull();
        usage.TotalCostUsd.Should().BeGreaterThan(0);
    }

    [Fact]
    public void ParseOutputLine_filters_empty_lines()
    {
        Provider().ParseOutputLine("").Should().BeEmpty();
    }

    [Fact]
    public void FormatBuilderBootstrap_is_single_line_kickoff_pointing_at_AGENTS_md()
    {
        // Codex TUI fires Submit on every \n, so a multi-line bootstrap
        // would short-circuit after the first line and leak the rest as
        // follow-up prompts. The persona/body lives in AGENTS.md (read
        // by Codex on TUI start); the bootstrap is a single-line nudge
        // to load that file and start the skill flow. \r at the end is
        // the only newline-equivalent â†’ exactly one Submit fires.
        var result = Provider().FormatBuilderBootstrap("agent-builder", skillBody: "ignored — lives in AGENTS.md");

        result.Should().Contain("AGENTS.md");
        result.Should().Contain("agent-builder");
        result.Should().Contain("guia obrigatÃ³rio");
        result.Should().Contain(".draft/manifest.json");
        result.Should().EndWith("\r");
        // No interior newlines — that's the whole point.
        result.TrimEnd('\r').Should().NotContain("\n");
        result.TrimEnd('\r').Should().NotContain("\r");
    }

    [Fact]
    public void FormatBuilderBootstrap_ignores_skill_body_argument()
    {
        // Same shape regardless of body presence — the body is consumed
        // out-of-band via AGENTS.md.
        var withBody = Provider().FormatBuilderBootstrap("workflow-builder", "# something");
        var withoutBody = Provider().FormatBuilderBootstrap("workflow-builder", null);

        withBody.Should().Be(withoutBody);
    }

    [Fact]
    public void FormatPrompt_appends_memories_after_user_input_when_skill_body_present()
    {
        const string body = "# Library skill\n\nCreate libraries from files.";
        var memories = new[]
        {
            new Viamus.Doxie.Orchestrator.Agents.Doxie.DoxieSkillMemory(
                "tip-1.md", "Use absolute paths",
                Viamus.Doxie.Orchestrator.Agents.Doxie.DoxieSkillMemoryPriority.High,
                null, "Always pass absolute paths — relative paths break in workspace contexts."),
        };

        var result = Provider().FormatPrompt(TestAgent, "from-files C:\\src", body, memories);

        result.Should().StartWith("Execute `library` with the following arguments:");
        result.Should().Contain("from-files C:\\src");
        result.Should().Contain("Create libraries from files.");
        result.Should().NotContain("/library");
        result.Should().Contain("# Auto-loaded context");
        result.Should().Contain("Use absolute paths");
        // Memories appear after the arguments (inside the intro line).
        result.IndexOf("# Auto-loaded context").Should()
            .BeGreaterThan(result.IndexOf("from-files C:\\src"));
    }

    [Fact]
    public void FormatPrompt_appends_memories_with_no_skill_body()
    {
        var memories = new[]
        {
            new Viamus.Doxie.Orchestrator.Agents.Doxie.DoxieSkillMemory(
                "tip.md", "Tip",
                Viamus.Doxie.Orchestrator.Agents.Doxie.DoxieSkillMemoryPriority.Medium,
                null, "remember X"),
        };

        var result = Provider().FormatPrompt(TestAgent, "go", skillBody: null, memories: memories);

        // Without a body, the memory block sits right after the slash —
        // matches Claude's shape so the model behaves identically.
        result.Should().StartWith("/library go");
        result.Should().Contain("# Auto-loaded context");
        result.Should().Contain("remember X");
    }
}
