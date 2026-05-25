using FluentAssertions;
using Viamus.Doxie.Orchestrator.Agents.Doxie;

namespace Viamus.Doxie.Orchestrator.Agents.Tests;

public sealed class MemoryConditionEvaluatorTests
{
    private static readonly MemoryConditionEvaluator.Context Ctx =
        new(Mode: "from-files", Provider: "claude-code", WorkspaceId: "sample-product-pss");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("always")]
    [InlineData("ALWAYS")]
    public void Empty_or_always_evaluates_true(string? condition)
    {
        MemoryConditionEvaluator.Eval(condition, Ctx).Should().BeTrue();
    }

    [Theory]
    [InlineData("never")]
    [InlineData("NEVER")]
    public void Never_evaluates_false(string condition)
    {
        MemoryConditionEvaluator.Eval(condition, Ctx).Should().BeFalse();
    }

    [Theory]
    [InlineData("mode = \"from-files\"")]
    [InlineData("mode=\"from-files\"")]                  // no spaces
    [InlineData("MODE = \"from-files\"")]                // case-insensitive key
    [InlineData("mode = 'from-files'")]                  // single quotes ok
    public void Equality_against_actual_mode_passes(string condition)
    {
        MemoryConditionEvaluator.Eval(condition, Ctx).Should().BeTrue();
    }

    [Fact]
    public void Equality_against_other_mode_fails()
    {
        MemoryConditionEvaluator.Eval("mode = \"interactive\"", Ctx).Should().BeFalse();
    }

    [Fact]
    public void Inequality_works_against_provider()
    {
        MemoryConditionEvaluator.Eval("provider != \"codex\"", Ctx).Should().BeTrue();
        MemoryConditionEvaluator.Eval("provider != \"claude-code\"", Ctx).Should().BeFalse();
    }

    [Fact]
    public void And_combines_two_clauses_both_must_be_true()
    {
        MemoryConditionEvaluator
            .Eval("mode = \"from-files\" AND provider = \"claude-code\"", Ctx)
            .Should().BeTrue();

        MemoryConditionEvaluator
            .Eval("mode = \"from-files\" AND provider = \"codex\"", Ctx)
            .Should().BeFalse();
    }

    [Fact]
    public void And_works_with_three_or_more_clauses()
    {
        var ok = "mode = \"from-files\" AND provider = \"claude-code\" AND workspace_id = \"sample-product-pss\"";
        MemoryConditionEvaluator.Eval(ok, Ctx).Should().BeTrue();

        var failsOnLast = "mode = \"from-files\" AND provider = \"claude-code\" AND workspace_id = \"other\"";
        MemoryConditionEvaluator.Eval(failsOnLast, Ctx).Should().BeFalse();
    }

    [Fact]
    public void Workspace_id_can_test_for_absence_with_empty_string()
    {
        var emptyWs = Ctx with { WorkspaceId = "" };
        MemoryConditionEvaluator.Eval("workspace_id = \"\"", emptyWs).Should().BeTrue();
        MemoryConditionEvaluator.Eval("workspace_id = \"\"", Ctx).Should().BeFalse();
    }

    [Theory]
    [InlineData("garbage soup")]
    [InlineData("mode == \"from-files\"")]              // double-equals not supported
    [InlineData("mode in (\"a\",\"b\")")]                // no IN operator
    [InlineData("not mode = \"from-files\"")]            // no top-level NOT
    [InlineData("mode = from-files")]                    // unquoted value
    [InlineData("badkey = \"x\"")]                       // unknown key
    public void Invalid_expressions_evaluate_false_and_report_reason(string condition)
    {
        string? lastReason = null;
        var result = MemoryConditionEvaluator.Eval(condition, Ctx, r => lastReason = r);

        result.Should().BeFalse();
        lastReason.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void Filter_orders_by_priority_desc_then_filename_asc()
    {
        var memories = new[]
        {
            new DoxieSkillMemory("z.md", "Z low",     DoxieSkillMemoryPriority.Low,    null, "z"),
            new DoxieSkillMemory("a.md", "A medium",  DoxieSkillMemoryPriority.Medium, null, "a"),
            new DoxieSkillMemory("m.md", "M high",    DoxieSkillMemoryPriority.High,   null, "m"),
            new DoxieSkillMemory("b.md", "B medium",  DoxieSkillMemoryPriority.Medium, null, "b"),
        };

        var filtered = MemoryConditionEvaluator.Filter(memories, Ctx);

        filtered.Select(m => m.FileName).Should().Equal("m.md", "a.md", "b.md", "z.md");
    }

    [Fact]
    public void Filter_drops_memories_whose_condition_is_false()
    {
        var memories = new[]
        {
            new DoxieSkillMemory("keep.md",  "Keep",  DoxieSkillMemoryPriority.High,   "always", "keep"),
            new DoxieSkillMemory("skip.md",  "Skip",  DoxieSkillMemoryPriority.High,   "never",  "skip"),
            new DoxieSkillMemory("match.md", "Match", DoxieSkillMemoryPriority.Medium, "mode = \"from-files\"", "match"),
            new DoxieSkillMemory("miss.md",  "Miss",  DoxieSkillMemoryPriority.Medium, "mode = \"interactive\"", "miss"),
        };

        var filtered = MemoryConditionEvaluator.Filter(memories, Ctx);

        filtered.Select(m => m.FileName).Should().Equal("keep.md", "match.md");
    }
}
