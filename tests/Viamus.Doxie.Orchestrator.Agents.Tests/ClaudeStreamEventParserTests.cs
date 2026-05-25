using FluentAssertions;

namespace Viamus.Doxie.Orchestrator.Agents.Tests;

public sealed class ClaudeStreamEventParserTests
{
    [Fact]
    public void Empty_input_produces_no_lines()
    {
        ClaudeStreamEventParser.Parse("").Should().BeEmpty();
        ClaudeStreamEventParser.Parse("   ").Should().BeEmpty();
    }

    [Fact]
    public void Non_json_passes_through_unchanged()
    {
        ClaudeStreamEventParser.Parse("hello world").Should().Equal("hello world");
    }

    [Fact]
    public void Malformed_json_passes_through_as_raw_text()
    {
        ClaudeStreamEventParser.Parse("{not actually json").Should().Equal("{not actually json");
    }

    [Fact]
    public void Assistant_text_block_yields_text_content()
    {
        var json = """{"type":"assistant","message":{"content":[{"type":"text","text":"hello"}]}}""";

        ClaudeStreamEventParser.Parse(json).Should().Equal("hello");
    }

    [Fact]
    public void Assistant_text_with_embedded_newlines_yields_one_line_each()
    {
        var json = """{"type":"assistant","message":{"content":[{"type":"text","text":"line1\nline2\nline3"}]}}""";

        ClaudeStreamEventParser.Parse(json).Should().Equal("line1", "line2", "line3");
    }

    [Fact]
    public void Assistant_tool_use_yields_arrow_with_tool_name()
    {
        var json = """{"type":"assistant","message":{"content":[{"type":"tool_use","name":"Read","input":{}}]}}""";

        ClaudeStreamEventParser.Parse(json).Should().Equal("▶ Read");
    }

    [Fact]
    public void Assistant_with_text_and_tool_use_emits_both_in_order()
    {
        var json = """{"type":"assistant","message":{"content":[{"type":"text","text":"reading"},{"type":"tool_use","name":"Read"}]}}""";

        ClaudeStreamEventParser.Parse(json).Should().Equal("reading", "▶ Read");
    }

    [Fact]
    public void System_init_event_is_suppressed()
    {
        var json = """{"type":"system","subtype":"init","cwd":"."}""";

        ClaudeStreamEventParser.Parse(json).Should().BeEmpty();
    }

    [Fact]
    public void User_tool_result_event_is_suppressed()
    {
        var json = """{"type":"user","message":{"content":[{"type":"tool_result","content":"x"}]}}""";

        ClaudeStreamEventParser.Parse(json).Should().BeEmpty();
    }

    [Fact]
    public void Result_success_event_yields_done_marker_with_duration()
    {
        var json = """{"type":"result","subtype":"success","duration_ms":1234}""";

        var lines = ClaudeStreamEventParser.Parse(json).ToList();

        lines.Should().ContainSingle();
        lines[0].Should().StartWith("✓ success").And.Contain("1234ms");
    }

    [Fact]
    public void Result_error_event_yields_failure_marker()
    {
        var json = """{"type":"result","subtype":"error","duration_ms":42}""";

        var lines = ClaudeStreamEventParser.Parse(json).ToList();

        lines.Should().ContainSingle();
        lines[0].Should().StartWith("✗");
    }

    [Fact]
    public void Read_tool_use_emits_file_path_with_arrow()
    {
        var json = """{"type":"assistant","message":{"content":[{"type":"tool_use","name":"Read","input":{"file_path":".claude/skills/sample-watch/add.md"}}]}}""";

        ClaudeStreamEventParser.Parse(json).Should().ContainSingle()
            .Which.Should().Be("▶ Read  .claude/skills/sample-watch/add.md");
    }

    [Fact]
    public void Bash_tool_use_prefers_description_over_command()
    {
        var json = """{"type":"assistant","message":{"content":[{"type":"tool_use","name":"Bash","input":{"command":"git status","description":"Show working tree status"}}]}}""";

        ClaudeStreamEventParser.Parse(json).Should().ContainSingle()
            .Which.Should().Be("▶ Bash  Show working tree status");
    }

    [Fact]
    public void Bash_tool_use_falls_back_to_command_when_no_description()
    {
        var json = """{"type":"assistant","message":{"content":[{"type":"tool_use","name":"Bash","input":{"command":"git status"}}]}}""";

        ClaudeStreamEventParser.Parse(json).Should().ContainSingle()
            .Which.Should().Be("▶ Bash  git status");
    }

    [Fact]
    public void Grep_tool_use_shows_pattern_with_path()
    {
        var json = """{"type":"assistant","message":{"content":[{"type":"tool_use","name":"Grep","input":{"pattern":"foo","path":"src/"}}]}}""";

        ClaudeStreamEventParser.Parse(json).Should().ContainSingle()
            .Which.Should().Be("▶ Grep  foo  in src/");
    }

    [Fact]
    public void ToolSearch_shows_query()
    {
        var json = """{"type":"assistant","message":{"content":[{"type":"tool_use","name":"ToolSearch","input":{"query":"select:Read"}}]}}""";

        ClaudeStreamEventParser.Parse(json).Should().ContainSingle()
            .Which.Should().Be("▶ ToolSearch  select:Read");
    }

    [Fact]
    public void Unknown_tool_falls_back_to_first_short_string_input()
    {
        var json = """{"type":"assistant","message":{"content":[{"type":"tool_use","name":"mcp__custom","input":{"target":"some-value","other":"x"}}]}}""";

        ClaudeStreamEventParser.Parse(json).Should().ContainSingle()
            .Which.Should().Be("▶ mcp__custom  target=some-value");
    }

    [Fact]
    public void Long_command_is_truncated_with_ellipsis()
    {
        var longCommand = new string('x', 200);
        var json = "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"tool_use\",\"name\":\"Bash\",\"input\":{\"command\":\""
            + longCommand
            + "\"}}]}}";

        var line = ClaudeStreamEventParser.Parse(json).Single();

        line.Should().StartWith("▶ Bash  ");
        line.Should().EndWith("…");
        line.Length.Should().BeLessThan(longCommand.Length + 10);
    }

    [Fact]
    public void Unknown_event_type_is_suppressed()
    {
        var json = """{"type":"something_weird","data":42}""";

        ClaudeStreamEventParser.Parse(json).Should().BeEmpty();
    }

    [Fact]
    public void TryParseUsage_extracts_tokens_and_cost_from_result_event()
    {
        var json = """
            {"type":"result","subtype":"success","duration_ms":1234,
             "total_cost_usd":0.0234,
             "usage":{
               "input_tokens":100,
               "output_tokens":200,
               "cache_read_input_tokens":300,
               "cache_creation_input_tokens":400
             }}
            """;

        var usage = ClaudeStreamEventParser.TryParseUsage(json);

        usage.Should().NotBeNull();
        usage!.InputTokens.Should().Be(100);
        usage.OutputTokens.Should().Be(200);
        usage.CacheReadTokens.Should().Be(300);
        usage.CacheCreationTokens.Should().Be(400);
        usage.TotalCostUsd.Should().BeApproximately(0.0234m, 0.0001m);
        usage.TotalTokens.Should().Be(1000);
    }

    [Fact]
    public void TryParseUsage_returns_null_for_non_result_events()
    {
        var assistantJson = """{"type":"assistant","message":{"content":[]}}""";
        var systemJson = """{"type":"system","subtype":"init"}""";

        ClaudeStreamEventParser.TryParseUsage(assistantJson).Should().BeNull();
        ClaudeStreamEventParser.TryParseUsage(systemJson).Should().BeNull();
    }

    [Fact]
    public void TryParseUsage_returns_null_for_non_json_lines()
    {
        ClaudeStreamEventParser.TryParseUsage("hello world").Should().BeNull();
        ClaudeStreamEventParser.TryParseUsage("").Should().BeNull();
        ClaudeStreamEventParser.TryParseUsage("   ").Should().BeNull();
        ClaudeStreamEventParser.TryParseUsage("{not json").Should().BeNull();
    }

    [Fact]
    public void TryParseUsage_returns_null_when_result_has_no_usage_or_cost()
    {
        // Result events that carry no accounting payload (subtype only)
        // shouldn't overwrite any prior usage with zeros.
        var json = """{"type":"result","subtype":"success","duration_ms":42}""";

        ClaudeStreamEventParser.TryParseUsage(json).Should().BeNull();
    }

    [Fact]
    public void TryParseUsage_handles_partial_usage_with_only_input_output()
    {
        // Older CLI versions may not emit the cache_* fields. Missing
        // numeric fields default to 0 — the record stays valid.
        var json = """
            {"type":"result","subtype":"success",
             "usage":{"input_tokens":50,"output_tokens":75}}
            """;

        var usage = ClaudeStreamEventParser.TryParseUsage(json);

        usage.Should().NotBeNull();
        usage!.InputTokens.Should().Be(50);
        usage.OutputTokens.Should().Be(75);
        usage.CacheReadTokens.Should().Be(0);
        usage.CacheCreationTokens.Should().Be(0);
        usage.TotalCostUsd.Should().BeNull();
    }
}
