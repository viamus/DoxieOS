using FluentAssertions;
using Viamus.Doxie.Orchestrator.Workflows;

namespace Viamus.Doxie.Orchestrator.Workflows.Tests;

public sealed class WorkflowLayoutTests
{
    [Fact]
    public void AutoPosition_places_root_at_left_and_increments_columns_by_depth()
    {
        // Linear chain: a → b → c. Depths 0, 1, 2.
        var nodes = new List<WorkflowNode>
        {
            new("a", WorkflowNodeKind.Trigger, "A", 0, 0),
            new("b", WorkflowNodeKind.Agent,   "B", 0, 0),
            new("c", WorkflowNodeKind.Agent,   "C", 0, 0),
        };
        var edges = new List<WorkflowEdge>
        {
            new("a", "b"),
            new("b", "c"),
        };

        var positioned = WorkflowLayout.AutoPosition(nodes, edges);

        var byId = positioned.ToDictionary(n => n.Id);
        byId["a"].X.Should().Be(WorkflowLayout.MarginX);
        byId["b"].X.Should().Be(WorkflowLayout.MarginX + WorkflowLayout.ColumnSpacing);
        byId["c"].X.Should().Be(WorkflowLayout.MarginX + 2 * WorkflowLayout.ColumnSpacing);
    }

    [Fact]
    public void AutoPosition_stacks_siblings_in_same_column()
    {
        // Fan-out: a feeds both b and c, which converge at d.
        // b and c are both depth 1, so they share an X coordinate.
        var nodes = new List<WorkflowNode>
        {
            new("a", WorkflowNodeKind.Trigger, "A", 0, 0),
            new("b", WorkflowNodeKind.Agent,   "B", 0, 0),
            new("c", WorkflowNodeKind.Agent,   "C", 0, 0),
            new("d", WorkflowNodeKind.Agent,   "D", 0, 0),
        };
        var edges = new List<WorkflowEdge>
        {
            new("a", "b"),
            new("a", "c"),
            new("b", "d"),
            new("c", "d"),
        };

        var positioned = WorkflowLayout.AutoPosition(nodes, edges);

        var byId = positioned.ToDictionary(n => n.Id);
        byId["b"].X.Should().Be(byId["c"].X);
        byId["b"].Y.Should().NotBe(byId["c"].Y);
        byId["a"].X.Should().BeLessThan(byId["b"].X);
        byId["d"].X.Should().BeGreaterThan(byId["b"].X);
    }

    [Fact]
    public void AutoPosition_is_idempotent()
    {
        var nodes = new List<WorkflowNode>
        {
            new("a", WorkflowNodeKind.Trigger, "A", 0, 0),
            new("b", WorkflowNodeKind.Agent,   "B", 0, 0),
        };
        var edges = new List<WorkflowEdge> { new("a", "b") };

        var first = WorkflowLayout.AutoPosition(nodes, edges);
        var second = WorkflowLayout.AutoPosition(first, edges);

        for (var i = 0; i < first.Count; i++)
        {
            second[i].X.Should().Be(first[i].X);
            second[i].Y.Should().Be(first[i].Y);
        }
    }

    [Fact]
    public void AutoPosition_handles_empty_input()
    {
        var positioned = WorkflowLayout.AutoPosition(
            Array.Empty<WorkflowNode>(),
            Array.Empty<WorkflowEdge>());

        positioned.Should().BeEmpty();
    }
}
