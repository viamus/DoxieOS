using FluentAssertions;

namespace Viamus.Doxie.Orchestrator.Agents.Tests;

public sealed class AgentModeTests
{
    [Fact]
    public void BuildArguments_returns_template_when_no_fields()
    {
        var mode = new AgentMode("check", "Check", "", "check");

        mode.BuildArguments().Should().Be("check");
    }

    [Fact]
    public void BuildArguments_substitutes_field_placeholders()
    {
        var mode = new AgentMode(
            Id: "add",
            Name: "Add Change",
            Description: "",
            ArgumentsTemplate: "add {url}",
            Fields: new[] { new AgentModeField("url", "Change URL") });

        var args = mode.BuildArguments(new Dictionary<string, string>
        {
            { "url", "https://example.com/change/1" },
        });

        args.Should().Be("add https://example.com/change/1");
    }

    [Fact]
    public void BuildArguments_collapses_to_command_when_field_value_is_missing()
    {
        var mode = new AgentMode(
            Id: "add",
            Name: "Add Change",
            Description: "",
            ArgumentsTemplate: "add {url}",
            Fields: new[] { new AgentModeField("url", "Change URL") });

        // Empty values become empty strings; the template's surrounding
        // whitespace collapses, so the result is a clean "add" without a
        // dangling space.
        mode.BuildArguments(new Dictionary<string, string>())
            .Should().Be("add");
    }

    [Fact]
    public void BuildArguments_trims_field_values()
    {
        var mode = new AgentMode(
            Id: "add",
            Name: "Add Change",
            Description: "",
            ArgumentsTemplate: "add {url}",
            Fields: new[] { new AgentModeField("url", "Change URL") });

        var args = mode.BuildArguments(new Dictionary<string, string>
        {
            { "url", "  https://example.com/change/1  " },
        });

        args.Should().Be("add https://example.com/change/1");
    }

    [Fact]
    public void BuildArguments_substitutes_multiple_fields()
    {
        var mode = new AgentMode(
            Id: "deploy",
            Name: "Deploy",
            Description: "",
            ArgumentsTemplate: "deploy --env {env} --tag {tag}",
            Fields: new[]
            {
                new AgentModeField("env", "Environment"),
                new AgentModeField("tag", "Tag"),
            });

        var args = mode.BuildArguments(new Dictionary<string, string>
        {
            { "env", "prod" },
            { "tag", "v1.2.3" },
        });

        args.Should().Be("deploy --env prod --tag v1.2.3");
    }
}
