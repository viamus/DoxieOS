using FluentAssertions;
using Viamus.Doxie.Orchestrator.Agents;
using Viamus.Doxie.Orchestrator.Notifications;

namespace Viamus.Doxie.Orchestrator.Workflows.Tests;

public sealed class NotificationArtifactFormatterTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "doxie-notification-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void AgentRunPreviewUsesManifestArtifactWhenAvailable()
    {
        Directory.CreateDirectory(_root);
        var output = Path.Combine(_root, "agent-output");
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, ".doxie-artifact.json"), """
            { "files": [ "output.md" ] }
            """);
        File.WriteAllText(Path.Combine(output, "output.md"), "# Delivery\n\nEverything shipped.");

        var run = AgentRun.Hydrate(
            "run-1",
            "developer",
            "",
            DateTimeOffset.UtcNow,
            AgentRunStatus.Completed,
            DateTimeOffset.UtcNow,
            0,
            Array.Empty<AgentRunOutputLine>(),
            outputDir: output);

        var preview = NotificationArtifactFormatter.ForAgentRun(run, "Run completed");

        preview.ContentFormat.Should().Be("markdown");
        preview.Content.Should().Contain("Everything shipped");
        preview.Body.Should().Contain("output.md");
        preview.SourcePath.Should().EndWith("output.md");
    }

    [Fact]
    public void AgentRunPreviewFallsBackToStdoutTail()
    {
        var lines = new[]
        {
            new AgentRunOutputLine(DateTimeOffset.UtcNow, AgentRunOutputSource.Stderr, "warning"),
            new AgentRunOutputLine(DateTimeOffset.UtcNow, AgentRunOutputSource.Stdout, "first line"),
            new AgentRunOutputLine(DateTimeOffset.UtcNow, AgentRunOutputSource.Stdout, "final summary"),
        };
        var run = AgentRun.Hydrate(
            "run-2",
            "developer",
            "",
            DateTimeOffset.UtcNow,
            AgentRunStatus.Completed,
            DateTimeOffset.UtcNow,
            0,
            lines);

        var preview = NotificationArtifactFormatter.ForAgentRun(run, "Run completed");

        preview.ContentFormat.Should().Be("text");
        preview.Content.Should().Contain("[stderr] warning");
        preview.Content.Should().Contain("final summary");
        preview.Body.Should().Contain("console");
        preview.SourcePath.Should().BeNull();
    }

    [Fact]
    public void AgentRunPreviewMarksVeryLargeConsoleOutputAsTruncated()
    {
        var longLine = new string('x', 310_000);
        var run = AgentRun.Hydrate(
            "run-large",
            "developer",
            "",
            DateTimeOffset.UtcNow,
            AgentRunStatus.Completed,
            DateTimeOffset.UtcNow,
            0,
            new[] { new AgentRunOutputLine(DateTimeOffset.UtcNow, AgentRunOutputSource.Stdout, longLine) });

        var preview = NotificationArtifactFormatter.ForAgentRun(run, "Run completed");

        preview.ContentTruncated.Should().BeTrue();
        preview.ContentLength.Should().Be(310_000);
        preview.Content.Should().EndWith("[output truncated in notification]");
    }

    [Fact]
    public void WorkflowRunPreviewPrefersFailedNodeArtifact()
    {
        var failedDir = Path.Combine(_root, "workflow-runs", "wf-run-1", "breaking");
        var succeededDir = Path.Combine(_root, "workflow-runs", "wf-run-1", "business");
        Directory.CreateDirectory(failedDir);
        Directory.CreateDirectory(succeededDir);
        File.WriteAllText(Path.Combine(succeededDir, "output.md"), "Business OK");
        File.WriteAllText(Path.Combine(failedDir, "report.md"), "Breaking gate failed");

        var run = WorkflowRun.Hydrate(
            "wf-run-1",
            "refinement",
            "manual",
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            WorkflowRunStatus.Failed,
            new Dictionary<string, WorkflowNodeRun>
            {
                ["business"] = WorkflowNodeRun.Hydrate("business", WorkflowNodeRunStatus.Succeeded, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, Array.Empty<string>()),
                ["breaking"] = WorkflowNodeRun.Hydrate("breaking", WorkflowNodeRunStatus.Failed, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, Array.Empty<string>()),
            });

        var preview = NotificationArtifactFormatter.ForWorkflowRun(run, Path.Combine(_root, "workflow-runs"), "Workflow failed");

        preview.ContentFormat.Should().Be("markdown");
        preview.Content.Should().Contain("Breaking gate failed");
        preview.Body.Should().Contain("report.md");
        preview.SourcePath.Should().EndWith("report.md");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
