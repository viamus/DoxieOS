using FluentAssertions;
using Viamus.Doxie.Orchestrator.Workflows;

namespace Viamus.Doxie.Orchestrator.Workflows.Tests;

public sealed class WorkflowDefinitionEnabledPolicyTests
{
    [Fact]
    public void Cron_with_enabled_true_is_forced_to_false()
    {
        var wf = NewWorkflow(
            new WorkflowTrigger(WorkflowTriggerKind.Cron, CronExpression: "*/5 * * * *"),
            enabled: true);

        var result = wf.WithCreateTimeEnabledPolicy();

        result.Enabled.Should().BeFalse(
            "cron-triggered workflows must nascer disabled until env vars are filled");
    }

    [Fact]
    public void Cron_with_enabled_false_stays_false()
    {
        var wf = NewWorkflow(
            new WorkflowTrigger(WorkflowTriggerKind.Cron, CronExpression: "0 * * * *"),
            enabled: false);

        var result = wf.WithCreateTimeEnabledPolicy();

        result.Enabled.Should().BeFalse();
    }

    [Fact]
    public void Manual_passes_enabled_true_through()
    {
        var wf = NewWorkflow(
            new WorkflowTrigger(WorkflowTriggerKind.Manual),
            enabled: true);

        var result = wf.WithCreateTimeEnabledPolicy();

        result.Enabled.Should().BeTrue(
            "manual workflows don't fire on a schedule, so the policy must not touch them");
    }

    [Fact]
    public void Event_passes_enabled_true_through()
    {
        var wf = NewWorkflow(
            new WorkflowTrigger(WorkflowTriggerKind.Event, EventName: "something-happened"),
            enabled: true);

        var result = wf.WithCreateTimeEnabledPolicy();

        result.Enabled.Should().BeTrue();
    }

    [Fact]
    public void Webhook_passes_enabled_true_through()
    {
        var wf = NewWorkflow(
            new WorkflowTrigger(WorkflowTriggerKind.Webhook, WebhookPath: "/hooks/x"),
            enabled: true);

        var result = wf.WithCreateTimeEnabledPolicy();

        result.Enabled.Should().BeTrue();
    }

    [Fact]
    public void Policy_returns_same_instance_when_no_change_needed()
    {
        var wf = NewWorkflow(new WorkflowTrigger(WorkflowTriggerKind.Manual), enabled: true);

        var result = wf.WithCreateTimeEnabledPolicy();

        result.Should().BeSameAs(wf,
            "no-op path should avoid the record copy to keep call sites cheap");
    }

    [Fact]
    public void Policy_preserves_unrelated_fields_when_disabling_cron()
    {
        var wf = NewWorkflow(
            new WorkflowTrigger(WorkflowTriggerKind.Cron, CronExpression: "*/5 * * * *"),
            enabled: true) with
        {
            WorkspaceId = "my-workspace",
            Env = new Dictionary<string, string> { ["FOO"] = "bar" },
        };

        var result = wf.WithCreateTimeEnabledPolicy();

        result.Enabled.Should().BeFalse();
        result.Id.Should().Be(wf.Id);
        result.Name.Should().Be(wf.Name);
        result.Trigger.Should().BeSameAs(wf.Trigger);
        result.Nodes.Should().BeSameAs(wf.Nodes);
        result.WorkspaceId.Should().Be("my-workspace");
        result.Env.Should().ContainKey("FOO").WhoseValue.Should().Be("bar");
    }

    private static WorkflowDefinition NewWorkflow(WorkflowTrigger trigger, bool enabled)
    {
        var ts = new DateTime(2026, 5, 10, 12, 0, 0, DateTimeKind.Utc);
        return new WorkflowDefinition(
            Id: "test-wf",
            Name: "Test",
            Description: "",
            Trigger: trigger,
            Nodes: new List<WorkflowNode>
            {
                new(
                    Id: "n1",
                    Kind: WorkflowNodeKind.Agent,
                    Label: "n1",
                    X: 0, Y: 0,
                    AgentId: "fake-agent"),
            },
            Edges: Array.Empty<WorkflowEdge>(),
            CreatedAt: ts,
            UpdatedAt: ts,
            Enabled: enabled);
    }
}
