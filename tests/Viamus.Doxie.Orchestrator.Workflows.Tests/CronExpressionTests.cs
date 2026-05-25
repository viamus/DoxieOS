using FluentAssertions;
using Viamus.Doxie.Orchestrator.Workflows;

namespace Viamus.Doxie.Orchestrator.Workflows.Tests;

public sealed class CronExpressionTests
{
    [Theory]
    [InlineData("* * * * *")]            // every minute
    [InlineData("*/5 * * * *")]          // every 5 minutes
    [InlineData("0 8 * * *")]            // every day at 08:00
    [InlineData("0 9 * * 1-5")]          // weekdays at 09:00
    [InlineData("0 0 1 * *")]            // first of every month
    public void IsValid_returns_true_for_well_formed_cron(string expression)
    {
        CronExpression.IsValid(expression).Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-cron")]
    [InlineData("60 * * * *")]           // minute > 59
    [InlineData("* 25 * * *")]           // hour > 23
    [InlineData("* * * *")]              // 4 fields, not 5
    public void IsValid_returns_false_for_garbage(string? expression)
    {
        CronExpression.IsValid(expression).Should().BeFalse();
    }

    [Fact]
    public void NextOccurrenceAfter_returns_strict_next_for_every_minute()
    {
        var anchor = new DateTime(2026, 5, 2, 12, 30, 15, DateTimeKind.Utc);

        var next = CronExpression.NextOccurrenceAfter("* * * * *", anchor);

        // "Every minute" past 12:30:15 â†’ 12:31:00.
        next.Should().Be(new DateTime(2026, 5, 2, 12, 31, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void NextOccurrenceAfter_skips_to_next_matching_slot()
    {
        var anchor = new DateTime(2026, 5, 2, 12, 30, 0, DateTimeKind.Utc);

        var next = CronExpression.NextOccurrenceAfter("*/15 * * * *", anchor);

        // Multiples of 15: …:00, :15, :30, :45. After 12:30:00 â†’ 12:45.
        next.Should().Be(new DateTime(2026, 5, 2, 12, 45, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void NextOccurrenceAfter_returns_null_for_invalid_expression()
    {
        CronExpression.NextOccurrenceAfter("garbage", DateTime.UtcNow).Should().BeNull();
        CronExpression.NextOccurrenceAfter(null, DateTime.UtcNow).Should().BeNull();
    }
}
