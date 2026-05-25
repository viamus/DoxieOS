using FluentAssertions;

namespace Viamus.Doxie.Orchestrator.Agents.Tests;

public sealed class ClaudeCodeProviderTests
{
    private static readonly AgentDescriptor TestAgent = new(
        Id: "library",
        Name: "Library",
        Description: "fixture",
        SkillName: "library",
        Category: AgentCategory.Other);

    private static ClaudeCodeProvider Provider() => new(new ClaudeRunnerOptions());

    [Fact]
    public void Id_is_stable_and_lower_kebab()
    {
        Provider().Id.Should().Be("claude-code");
    }

    [Fact]
    public void Executable_falls_back_to_claude_when_options_blank()
    {
        new ClaudeCodeProvider(new ClaudeRunnerOptions { Executable = "" }).Executable
            .Should().Be("claude");
    }

    [Fact]
    public void Executable_honours_options_override()
    {
        new ClaudeCodeProvider(new ClaudeRunnerOptions { Executable = "/opt/claude/bin/claude" }).Executable
            .Should().Be("/opt/claude/bin/claude");
    }

    [Fact]
    public void FormatPrompt_with_empty_arguments_yields_bare_slash_command()
    {
        Provider().FormatPrompt(TestAgent, "").Should().Be("/library");
        Provider().FormatPrompt(TestAgent, "   ").Should().Be("/library");
    }

    [Fact]
    public void FormatPrompt_with_plain_arguments_prefixes_skill()
    {
        Provider().FormatPrompt(TestAgent, "from-files /tmp/foo")
            .Should().Be("/library from-files /tmp/foo");
    }

    [Fact]
    public void FormatPrompt_passes_explicit_slash_command_through_verbatim()
    {
        // Synthetic mode case: a Run mode whose template is a /loop-style
        // wrapper that drives a *different* skill than the agent's own.
        Provider().FormatPrompt(TestAgent, "/loop 5m /sample-watch check")
            .Should().Be("/loop 5m /sample-watch check");
    }

    [Fact]
    public void FormatPrompt_ignores_skillBody_because_Claude_reads_disk_natively()
    {
        // The provider drops the skill body — Claude resolves it via the
        // slash-command at the CLI level, so injecting it again would
        // bloat the prompt window with redundant content.
        const string skillBody = "# Library\n\nThis skill is enormous and should not be inlined.";
        Provider().FormatPrompt(TestAgent, "from-files /tmp/foo", skillBody)
            .Should().Be("/library from-files /tmp/foo")
            .And.NotContain("This skill is enormous");
    }

    [Fact]
    public void BuildHeadlessArgs_emits_the_expected_flag_sequence()
    {
        var args = Provider().BuildHeadlessArgs(TestAgent, "/library").ToList();

        args.Should().ContainInOrder(
            "-p", "/library",
            "--dangerously-skip-permissions",
            "--output-format", "stream-json",
            "--verbose");
    }

    [Fact]
    public void BuildSessionArgs_uses_stream_json_input_and_keeps_partial_messages()
    {
        var args = Provider().BuildSessionArgs(TestAgent).ToList();

        args.Should().Contain("--print");
        args.Should().ContainInOrder("--input-format", "stream-json");
        args.Should().ContainInOrder("--output-format", "stream-json");
        args.Should().Contain("--include-partial-messages");
        args.Should().Contain("--dangerously-skip-permissions");
        // Session mode never carries a positional prompt — the message is
        // delivered via WriteInitialMessage on stdin.
        args.Should().NotContain("-p");
    }

    [Fact]
    public void WriteInitialMessage_emits_a_user_envelope_as_one_json_line()
    {
        using var sw = new StringWriter();
        Provider().WriteInitialMessage(sw, TestAgent, "/library from-current");

        var written = sw.ToString().TrimEnd('\r', '\n');
        // Exactly one line.
        written.Should().NotContain("\n").And.NotContain("\r");

        using var doc = System.Text.Json.JsonDocument.Parse(written);
        var root = doc.RootElement;
        root.GetProperty("type").GetString().Should().Be("user");
        var content = root.GetProperty("message").GetProperty("content")[0];
        content.GetProperty("type").GetString().Should().Be("text");
        content.GetProperty("text").GetString().Should().Be("/library from-current");
    }

    [Fact]
    public void BuildInteractiveArgs_includes_dangerously_skip_permissions()
    {
        Provider().BuildInteractiveArgs().Should().Equal("--dangerously-skip-permissions");
    }

    [Fact]
    public void ParseOutputLine_passes_plain_text_through()
    {
        Provider().ParseOutputLine("hello world").Should().Equal("hello world");
    }

    [Fact]
    public void ParseOutputLine_renders_stream_json_events_to_readable_text()
    {
        // Smoke-test the integration with ClaudeStreamEventParser — exact
        // rendering is exercised in that parser's own tests; here we just
        // confirm the provider wires it in.
        var json = """{"type":"assistant","message":{"content":[{"type":"text","text":"ok"}]}}""";
        var emitted = Provider().ParseOutputLine(json);

        emitted.Should().NotBeEmpty();
        emitted.Should().NotEqual(new[] { json }); // not pass-through — got rendered
    }

    [Fact]
    public void ParseOutputLine_passes_empty_or_whitespace_through_as_zero_lines()
    {
        // Parser yields no lines for blanks; provider returns empty list.
        Provider().ParseOutputLine("").Should().BeEmpty();
        Provider().ParseOutputLine("   ").Should().BeEmpty();
    }

    [Fact]
    public void FormatBuilderBootstrap_emits_slash_command_with_trailing_cr()
    {
        // Claude Code resolves /<id> natively from .claude/skills/<id>/SKILL.md,
        // so the bootstrap is just the slash command + CR. The skill body
        // is ignored — auto-load by Claude is more efficient than inlining.
        Provider().FormatBuilderBootstrap("agent-builder", skillBody: "should be ignored")
            .Should().Be("/agent-builder\r");
    }

    [Fact]
    public void FormatBuilderBootstrap_ignores_null_skill_body()
    {
        Provider().FormatBuilderBootstrap("workflow-builder", skillBody: null)
            .Should().Be("/workflow-builder\r");
    }

    [Fact]
    public void FormatPrompt_appends_memories_after_slash_command()
    {
        var memories = new[]
        {
            new Viamus.Doxie.Orchestrator.Agents.Doxie.DoxieSkillMemory(
                "tip-1.md", "API token rotates",
                Viamus.Doxie.Orchestrator.Agents.Doxie.DoxieSkillMemoryPriority.High,
                null, "Refresh the PAT before calling the Example API."),
        };

        var result = Provider().FormatPrompt(TestAgent, "from-files /tmp/foo", skillBody: null, memories: memories);

        // The slash command MUST stay first so Claude resolves SKILL.md.
        result.Should().StartWith("/library from-files /tmp/foo");
        result.Should().Contain("# Auto-loaded context");
        result.Should().Contain("API token rotates");
        result.Should().Contain("Refresh the PAT");
    }

    [Fact]
    public void FormatPrompt_with_no_memories_unchanged_from_pre_memories_behaviour()
    {
        Provider().FormatPrompt(TestAgent, "from-files /tmp/foo", skillBody: null, memories: null)
            .Should().Be("/library from-files /tmp/foo");
        Provider().FormatPrompt(TestAgent, "from-files /tmp/foo", skillBody: null, memories: Array.Empty<Viamus.Doxie.Orchestrator.Agents.Doxie.DoxieSkillMemory>())
            .Should().Be("/library from-files /tmp/foo");
    }
}
