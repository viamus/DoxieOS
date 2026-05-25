using FluentAssertions;
using Microsoft.Data.Sqlite;
using Viamus.Doxie.Orchestrator.Agents.Doxie;
using Viamus.Doxie.Orchestrator.Context;

namespace Viamus.Doxie.Orchestrator.Agents.Tests;

public sealed class ClaudeProcessAgentRunnerTests : IDisposable
{
    private static readonly AgentDescriptor TestAgent = new(
        Id: "test-agent",
        Name: "Test Agent",
        Description: "fixture agent for runner tests",
        SkillName: "test",
        Category: AgentCategory.Other);

    private readonly string _dbPath;
    private readonly SqliteAgentRunStore _store;
    private readonly FakeAgentCatalog _catalog;
    private readonly ClaudeProcessAgentRunner _runner;

    public ClaudeProcessAgentRunnerTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"orchestrator-test-{Guid.NewGuid():N}.db");
        _store = new SqliteAgentRunStore($"Data Source={_dbPath}");
        _catalog = new FakeAgentCatalog(TestAgent);
        // cmd.exe stands in for the Claude CLI through a fake provider —
        // we don't depend on Claude being installed and exercise the runner
        // independently of the real provider implementation. The cmd.exe
        // scripts are Windows-specific so every [WindowsFact] in this file
        // skips on Linux/macOS. A cross-platform refactor (sh on POSIX) is
        // a future follow-up.
        _runner = new ClaudeProcessAgentRunner(
            _catalog,
            _store,
            AgentProviderResolver.ForSingle(new CmdFakeProvider()));
    }

    public void Dispose()
    {
        _runner.Dispose();
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath))
        {
            try { File.Delete(_dbPath); } catch { /* best effort */ }
        }
    }

    [WindowsFact]
    public async Task Run_with_zero_exit_code_completes_successfully()
    {
        var terminal = WaitForTerminal();

        var run = _runner.Start("test-agent", "exit /b 0");
        var finished = await terminal.WaitAsync(TimeSpan.FromSeconds(10));

        finished.Status.Should().Be(AgentRunStatus.Completed);
        finished.ExitCode.Should().Be(0);
        finished.FinishedAt.Should().NotBeNull();
        finished.Id.Should().Be(run.Id);
    }

    [WindowsFact]
    public async Task Run_with_non_zero_exit_code_is_marked_Failed()
    {
        var terminal = WaitForTerminal();

        _runner.Start("test-agent", "exit /b 5");
        var finished = await terminal.WaitAsync(TimeSpan.FromSeconds(10));

        finished.Status.Should().Be(AgentRunStatus.Failed);
        finished.ExitCode.Should().Be(5);
    }

    [WindowsFact]
    public async Task Stdout_lines_are_captured_and_persisted()
    {
        var terminal = WaitForTerminal();

        _runner.Start("test-agent", "echo first&echo second");
        var finished = await terminal.WaitAsync(TimeSpan.FromSeconds(10));

        finished.Status.Should().Be(AgentRunStatus.Completed);
        finished.Output.Should().Contain(o => o.Source == AgentRunOutputSource.Stdout && o.Text.Contains("first"));
        finished.Output.Should().Contain(o => o.Source == AgentRunOutputSource.Stdout && o.Text.Contains("second"));

        // Persisted in the store, not just in the in-memory run.
        var fromStore = _store.Get(finished.Id)!;
        fromStore.Output.Should().HaveCountGreaterOrEqualTo(2);
    }

    [WindowsFact]
    public async Task Cancel_terminates_running_process_and_marks_Cancelled()
    {
        var terminal = WaitForTerminal();

        // Pure-cmd infinite busy loop; Cancel owns termination here. It doesn't
        // depend on any external command being on PATH (the test
        // previously used `ping` which fails in stripped containers
        // / dev sandboxes where System32 isn't on PATH and cmd can't
        // resolve ping, making the run exit Failed before Cancel ran).
        var run = _runner.Start("test-agent", "for /l %i in (1,0,1) do @rem");
        await Task.Delay(300); // give the process time to actually be running

        _runner.Cancel(run.Id);
        var finished = await terminal.WaitAsync(TimeSpan.FromSeconds(10));

        finished.Status.Should().Be(AgentRunStatus.Cancelled);
        finished.FinishedAt.Should().NotBeNull();
        finished.Output.Should().Contain(o =>
            o.Source == AgentRunOutputSource.Stderr && o.Text.Contains("cancelled"));
    }

    [WindowsFact]
    public async Task Get_returns_active_run_during_execution_and_falls_back_to_store_after_terminal()
    {
        var terminal = WaitForTerminal();

        var run = _runner.Start("test-agent", "echo hi");
        // While the process is still booting we can hit the active map.
        var active = _runner.Get(run.Id);
        active.Should().NotBeNull();
        active!.Id.Should().Be(run.Id);

        await terminal.WaitAsync(TimeSpan.FromSeconds(10));

        // After completion the active map is cleared; Get falls through to
        // the store and we still get the run back (now hydrated from disk).
        var afterTerminal = _runner.Get(run.Id);
        afterTerminal.Should().NotBeNull();
        afterTerminal!.Id.Should().Be(run.Id);
        afterTerminal.Status.Should().Be(AgentRunStatus.Completed);
    }

    [Fact]
    public void Start_with_unknown_agent_throws()
    {
        var act = () => _runner.Start("does-not-exist", "");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*does-not-exist*");
    }

    [WindowsFact]
    public async Task Process_inherits_configured_working_directory()
    {
        var customDir = Path.Combine(Path.GetTempPath(), $"orchestrator-cwd-{Guid.NewGuid():N}");
        Directory.CreateDirectory(customDir);
        try
        {
            using var runnerWithCwd = new ClaudeProcessAgentRunner(
                _catalog,
                _store,
                AgentProviderResolver.ForSingle(new CmdFakeProvider()),
                workingDirectory: customDir);

            var terminal = new TaskCompletionSource<AgentRun>(TaskCreationOptions.RunContinuationsAsynchronously);
            runnerWithCwd.RunUpdated += run =>
            {
                if (IsTerminal(run.Status)) terminal.TrySetResult(run);
            };

            runnerWithCwd.Start("test-agent", "cd");
            var finished = await terminal.Task.WaitAsync(TimeSpan.FromSeconds(10));

            finished.Status.Should().Be(AgentRunStatus.Completed);
            finished.Output.Should().Contain(o =>
                o.Source == AgentRunOutputSource.Stdout
                && o.Text.Contains(customDir, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            try { Directory.Delete(customDir, recursive: true); } catch { /* best effort */ }
        }
    }

    [WindowsFact]
    public async Task Start_creates_operator_hints_file_and_instructs_agent_to_read_it()
    {
        var customDir = Path.Combine(Path.GetTempPath(), $"orchestrator-hints-{Guid.NewGuid():N}");
        Directory.CreateDirectory(customDir);
        var provider = new RecordingExitProvider();
        try
        {
            using var runnerWithCwd = new ClaudeProcessAgentRunner(
                _catalog,
                _store,
                AgentProviderResolver.ForSingle(provider),
                workingDirectory: customDir);

            var terminal = new TaskCompletionSource<AgentRun>(TaskCreationOptions.RunContinuationsAsynchronously);
            runnerWithCwd.RunUpdated += run =>
            {
                if (IsTerminal(run.Status)) terminal.TrySetResult(run);
            };

            runnerWithCwd.Start("test-agent", "do the thing");
            var finished = await terminal.Task.WaitAsync(TimeSpan.FromSeconds(10));

            finished.Status.Should().Be(AgentRunStatus.Completed);
            var hintsDir = Path.Combine(customDir, ".doxie", "run-inputs");
            var hintFiles = Directory.GetFiles(hintsDir, "operator-hints-*.md");
            hintFiles.Should().ContainSingle();
            var hintFile = hintFiles[0];
            File.ReadAllText(hintFile).Should().Contain("Re-read this file");
            provider.LastFormattedPrompt.Should().Contain("Live operator hints and conversation guidance");
            provider.LastFormattedPrompt.Should().Contain("DOXIE_OPERATOR_HINTS_FILE");
            provider.LastFormattedPrompt.Should().Contain("every new work cycle or instruction batch");
            provider.LastFormattedPrompt.Should().Contain("before the final response");
        }
        finally
        {
            try { Directory.Delete(customDir, recursive: true); } catch { /* best effort */ }
        }
    }

    [WindowsFact]
    public async Task RunUpdated_fires_status_transitions_in_order()
    {
        var statuses = new List<AgentRunStatus>();
        var terminal = new TaskCompletionSource<AgentRun>();
        _runner.RunUpdated += run =>
        {
            lock (statuses) statuses.Add(run.Status);
            if (IsTerminal(run.Status)) terminal.TrySetResult(run);
        };

        _runner.Start("test-agent", "exit /b 0");
        await terminal.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // First non-Queued transition must be Running, last must be Completed.
        statuses.Should().NotBeEmpty();
        statuses.First().Should().Be(AgentRunStatus.Running);
        statuses.Last().Should().Be(AgentRunStatus.Completed);
    }

    [WindowsFact]
    public async Task Provider_usage_estimate_is_persisted_when_no_usage_event_is_emitted()
    {
        using var runnerWithEstimator = new ClaudeProcessAgentRunner(
            _catalog,
            _store,
            AgentProviderResolver.ForSingle(new EstimatingCmdFakeProvider()));
        var terminal = new TaskCompletionSource<AgentRun>(TaskCreationOptions.RunContinuationsAsynchronously);
        runnerWithEstimator.RunUpdated += run =>
        {
            if (IsTerminal(run.Status)) terminal.TrySetResult(run);
        };

        var run = runnerWithEstimator.Start("test-agent", "echo estimated");
        var finished = await terminal.Task.WaitAsync(TimeSpan.FromSeconds(10));

        finished.Usage.Should().NotBeNull();
        finished.Usage!.InputTokens.Should().Be(11);
        finished.Usage.OutputTokens.Should().Be(22);
        finished.Usage.TotalCostUsd.Should().Be(0.123m);

        var persisted = _store.Get(run.Id)!;
        persisted.Usage.Should().BeEquivalentTo(finished.Usage);
    }

    [WindowsFact]
    public async Task Large_arguments_are_materialized_to_file_before_provider_dispatch()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"doxie-large-args-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var provider = new RecordingExitProvider();
        using var runnerWithRecordingProvider = new ClaudeProcessAgentRunner(
            _catalog,
            _store,
            AgentProviderResolver.ForSingle(provider),
            workingDirectory: tempDir);
        var terminal = new TaskCompletionSource<AgentRun>(TaskCreationOptions.RunContinuationsAsynchronously);
        runnerWithRecordingProvider.RunUpdated += run =>
        {
            if (IsTerminal(run.Status)) terminal.TrySetResult(run);
        };

        var large = new string('x', 30_000);
        var run = runnerWithRecordingProvider.Start("test-agent", large);
        var finished = await terminal.Task.WaitAsync(TimeSpan.FromSeconds(10));

        finished.Status.Should().Be(AgentRunStatus.Completed);
        run.Arguments.Should().StartWith("input-file:.doxie/run-inputs/");
        provider.LastFormattedPrompt.Should().Contain("Read the full invocation from `.doxie/run-inputs/");
        provider.LastFormattedPrompt.Should().NotContain(large);
        Directory.GetFiles(Path.Combine(tempDir, ".doxie", "run-inputs"), "*.md")
            .Where(path => !Path.GetFileName(path).StartsWith("operator-hints-", StringComparison.OrdinalIgnoreCase))
            .Should().ContainSingle()
            .Which.Should().Match(path => File.ReadAllText(path).Length == large.Length);
    }

    [WindowsFact]
    public async Task Arguments_near_windows_cmd_limit_are_materialized_before_provider_dispatch()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"doxie-cmd-limit-args-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var provider = new RecordingExitProvider();
        using var runnerWithRecordingProvider = new ClaudeProcessAgentRunner(
            _catalog,
            _store,
            AgentProviderResolver.ForSingle(provider),
            workingDirectory: tempDir);
        var terminal = new TaskCompletionSource<AgentRun>(TaskCreationOptions.RunContinuationsAsynchronously);
        runnerWithRecordingProvider.RunUpdated += run =>
        {
            if (IsTerminal(run.Status)) terminal.TrySetResult(run);
        };

        var nearCmdLimit = new string('x', 7_000);
        var run = runnerWithRecordingProvider.Start("test-agent", nearCmdLimit);
        var finished = await terminal.Task.WaitAsync(TimeSpan.FromSeconds(10));

        finished.Status.Should().Be(AgentRunStatus.Completed);
        run.Arguments.Should().StartWith("input-file:.doxie/run-inputs/");
        provider.LastFormattedPrompt.Should().Contain("Read the full invocation from `.doxie/run-inputs/");
        provider.LastFormattedPrompt.Should().NotContain(nearCmdLimit);
        Directory.GetFiles(Path.Combine(tempDir, ".doxie", "run-inputs"), "*.md")
            .Where(path => !Path.GetFileName(path).StartsWith("operator-hints-", StringComparison.OrdinalIgnoreCase))
            .Should().ContainSingle()
            .Which.Should().Match(path => File.ReadAllText(path).Length == nearCmdLimit.Length);
    }

    [WindowsFact]
    public async Task Large_formatted_prompt_is_materialized_after_provider_context_injection()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"doxie-large-prompt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var provider = new BloatedPromptProvider();
        using var runnerWithBloatedProvider = new ClaudeProcessAgentRunner(
            _catalog,
            _store,
            AgentProviderResolver.ForSingle(provider),
            workingDirectory: tempDir);
        var terminal = new TaskCompletionSource<AgentRun>(TaskCreationOptions.RunContinuationsAsynchronously);
        runnerWithBloatedProvider.RunUpdated += run =>
        {
            if (IsTerminal(run.Status)) terminal.TrySetResult(run);
        };

        runnerWithBloatedProvider.Start("test-agent", "go");
        var finished = await terminal.Task.WaitAsync(TimeSpan.FromSeconds(10));

        finished.Status.Should().Be(AgentRunStatus.Completed);
        provider.LastHeadlessPrompt.Should().NotBeNull();
        provider.LastHeadlessPrompt!.Should().Contain("Read the full invocation from `.doxie/run-inputs/");
        provider.LastHeadlessPrompt.Should().NotContain(BloatedPromptProvider.BloatMarker);
        Directory.GetFiles(Path.Combine(tempDir, ".doxie", "run-inputs"), "*-prompt-*.md")
            .Should().ContainSingle()
            .Which.Should().Match(path => File.ReadAllText(path).Contains(BloatedPromptProvider.BloatMarker));
    }

    [WindowsFact]
    public async Task Large_claude_formatted_prompt_keeps_slash_command_when_materialized()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"doxie-large-claude-prompt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var provider = new BloatedClaudePromptProvider();
        using var runnerWithBloatedProvider = new ClaudeProcessAgentRunner(
            _catalog,
            _store,
            AgentProviderResolver.ForSingle(provider),
            workingDirectory: tempDir);
        var terminal = new TaskCompletionSource<AgentRun>(TaskCreationOptions.RunContinuationsAsynchronously);
        runnerWithBloatedProvider.RunUpdated += run =>
        {
            if (IsTerminal(run.Status)) terminal.TrySetResult(run);
        };

        runnerWithBloatedProvider.Start("test-agent", "go");
        var finished = await terminal.Task.WaitAsync(TimeSpan.FromSeconds(10));

        finished.Status.Should().Be(AgentRunStatus.Completed);
        provider.LastHeadlessPrompt.Should().StartWith("/test ");
        provider.LastHeadlessPrompt.Should().Contain("Read the full invocation from `.doxie/run-inputs/");
    }

    [WindowsFact]
    public async Task Large_formatted_prompt_uses_stdin_when_provider_supports_it()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"doxie-large-prompt-stdin-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var provider = new StdinBloatedPromptProvider();
        using var runnerWithStdinProvider = new ClaudeProcessAgentRunner(
            _catalog,
            _store,
            AgentProviderResolver.ForSingle(provider),
            workingDirectory: tempDir);
        var terminal = new TaskCompletionSource<AgentRun>(TaskCreationOptions.RunContinuationsAsynchronously);
        runnerWithStdinProvider.RunUpdated += run =>
        {
            if (IsTerminal(run.Status)) terminal.TrySetResult(run);
        };

        runnerWithStdinProvider.Start("test-agent", "go");
        var finished = await terminal.Task.WaitAsync(TimeSpan.FromSeconds(10));

        finished.Status.Should().Be(AgentRunStatus.Completed);
        provider.LastHeadlessPrompt.Should().BeNull();
        provider.LastStdinPrompt.Should().Contain(BloatedPromptProvider.BloatMarker);
        provider.LastStdinPrompt.Should().Contain(new string('z', 100));
        Directory.GetFiles(Path.Combine(tempDir, ".doxie", "run-inputs"), "*.md")
            .Should().OnlyContain(path => Path.GetFileName(path).StartsWith("operator-hints-", StringComparison.OrdinalIgnoreCase));
    }

    [WindowsFact]
    public async Task Large_arguments_stay_in_prompt_when_provider_streams_prompt_to_stdin()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"doxie-large-args-stdin-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var provider = new StdinBloatedPromptProvider();
        using var runnerWithStdinProvider = new ClaudeProcessAgentRunner(
            _catalog,
            _store,
            AgentProviderResolver.ForSingle(provider),
            workingDirectory: tempDir);
        var terminal = new TaskCompletionSource<AgentRun>(TaskCreationOptions.RunContinuationsAsynchronously);
        runnerWithStdinProvider.RunUpdated += run =>
        {
            if (IsTerminal(run.Status)) terminal.TrySetResult(run);
        };

        var large = new string('x', 30_000);
        var run = runnerWithStdinProvider.Start("test-agent", large);
        var finished = await terminal.Task.WaitAsync(TimeSpan.FromSeconds(10));

        finished.Status.Should().Be(AgentRunStatus.Completed);
        run.Arguments.Should().Be($"stdin-payload:{large.Length} chars");
        provider.LastStdinPrompt.Should().Contain(large);
        provider.LastStdinPrompt.Should().NotContain("Read the full invocation from");
        Directory.GetFiles(Path.Combine(tempDir, ".doxie", "run-inputs"), "*.md")
            .Should().OnlyContain(path => Path.GetFileName(path).StartsWith("operator-hints-", StringComparison.OrdinalIgnoreCase));
    }

    [WindowsFact]
    public async Task Codex_provider_streams_small_prompts_to_stdin_to_preserve_shell_metacharacters()
    {
        var provider = new SmallCodexStdinProvider();
        using var runnerWithCodexProvider = new ClaudeProcessAgentRunner(
            _catalog,
            _store,
            AgentProviderResolver.ForSingle(provider));
        var terminal = new TaskCompletionSource<AgentRun>(TaskCreationOptions.RunContinuationsAsynchronously);
        runnerWithCodexProvider.RunUpdated += run =>
        {
            if (IsTerminal(run.Status)) terminal.TrySetResult(run);
        };

        const string url = "https://quality.example.invalid/summary/overall?id=PROJECT&branch=dev";
        runnerWithCodexProvider.Start("test-agent", $"advise --context \"{url}\"");
        var finished = await terminal.Task.WaitAsync(TimeSpan.FromSeconds(10));

        finished.Status.Should().Be(AgentRunStatus.Completed);
        provider.LastHeadlessPrompt.Should().BeNull("Codex prompts should not be sent as argv on Windows");
        provider.LastStdinPrompt.Should().Contain("&branch=dev");
    }

    [WindowsFact]
    public async Task Workspace_memories_are_wrapped_by_runner_without_agent_specific_changes()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"doxie-workspace-ram-{Guid.NewGuid():N}");
        var workspaceDir = Path.Combine(tempDir, "workspace");
        Directory.CreateDirectory(Path.Combine(workspaceDir, "memory"));
        File.WriteAllText(Path.Combine(workspaceDir, "WORKSPACE.md"), "Workspace mission context.");
        File.WriteAllText(Path.Combine(workspaceDir, "memory", "decisions.md"), "Prefer SQLite-backed persistence.");

        var workspace = new Workspace(
            Id: "release-room",
            Name: "Release Room",
            Description: "fixture workspace",
            CreatedAt: DateTime.UtcNow,
            MountedLibraryIds: new[] { "platform-playbook" },
            Path: workspaceDir);
        var library = new Library(
            Id: "platform-playbook",
            Name: "Platform Playbook",
            Description: "fixture library",
            Memories: new[]
            {
                new MemoryEntry(
                    FileName: "handoff.md",
                    Name: "Handoff Contract",
                    Description: "fixture memory",
                    Type: MemoryType.Project,
                    Content: "Every handoff must carry evidence and next action."),
            },
            Files: Array.Empty<LibraryFileEntry>());

        var provider = new RecordingExitProvider();
        using var runnerWithWorkspaceRam = new ClaudeProcessAgentRunner(
            _catalog,
            _store,
            AgentProviderResolver.ForSingle(provider),
            workingDirectory: tempDir,
            workspaceStore: new SingleWorkspaceStore(workspace),
            libraryStore: new SingleLibraryStore(library));
        var terminal = new TaskCompletionSource<AgentRun>(TaskCreationOptions.RunContinuationsAsynchronously);
        runnerWithWorkspaceRam.RunUpdated += run =>
        {
            if (IsTerminal(run.Status)) terminal.TrySetResult(run);
        };

        runnerWithWorkspaceRam.Start("test-agent", "go", workspaceId: workspace.Id);
        var finished = await terminal.Task.WaitAsync(TimeSpan.FromSeconds(10));

        finished.Status.Should().Be(AgentRunStatus.Completed);
        provider.LastMemories.Should().NotBeNull();
        provider.LastMemories!.Select(m => m.Name).Should().Contain(new[]
        {
            "Workspace: WORKSPACE.md",
            "Workspace: memory/decisions.md",
            "Workspace library: Platform Playbook / Handoff Contract",
        });
        provider.LastMemories!.Select(m => m.Body).Should().Contain(body => body.Contains("Workspace mission context."));
        provider.LastMemories!.Select(m => m.Body).Should().Contain(body => body.Contains("Prefer SQLite-backed persistence."));
        provider.LastMemories!.Select(m => m.Body).Should().Contain(body => body.Contains("Every handoff must carry evidence"));
    }

    [Fact]
    public async Task Custom_catalog_agent_memories_are_precreated_under_custom_catalog()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"doxie-custom-agent-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var customCatalogRoot = Path.Combine(tempDir, "shared-team-catalog");
        var customAgent = TestAgent with
        {
            Id = "custom-agent",
            SkillName = "custom-agent",
            CatalogId = "shared-team",
            CatalogName = "Shared team",
            CatalogRoot = customCatalogRoot,
        };

        using var runnerWithCustomAgent = new ClaudeProcessAgentRunner(
            new FakeAgentCatalog(customAgent),
            _store,
            AgentProviderResolver.ForSingle(new DotnetVersionProvider()),
            workingDirectory: tempDir);
        var terminal = new TaskCompletionSource<AgentRun>(TaskCreationOptions.RunContinuationsAsynchronously);
        runnerWithCustomAgent.RunUpdated += run =>
        {
            if (IsTerminal(run.Status)) terminal.TrySetResult(run);
        };

        runnerWithCustomAgent.Start("custom-agent", "go");

        Directory.Exists(Path.Combine(customCatalogRoot, "skills", "custom-agent", "memories"))
            .Should().BeTrue("custom catalog agents must learn under their selected catalog");
        Directory.Exists(Path.Combine(tempDir, ".doxie", "skills", "custom-agent", "memories"))
            .Should().BeFalse("custom catalog runs must not create default fallback memory folders");

        var finished = await terminal.Task.WaitAsync(TimeSpan.FromSeconds(10));
        finished.Status.Should().Be(AgentRunStatus.Completed);

        try { Directory.Delete(tempDir, recursive: true); } catch { /* best effort */ }
    }

    private TaskCompletionSource<AgentRun> _terminalTcs = null!;

    private TaskCompletionSource<AgentRun> WaitForTerminal()
    {
        _terminalTcs = new TaskCompletionSource<AgentRun>(TaskCreationOptions.RunContinuationsAsynchronously);
        _runner.RunUpdated += run =>
        {
            if (IsTerminal(run.Status))
            {
                _terminalTcs.TrySetResult(run);
            }
        };
        return _terminalTcs;
    }

    private static bool IsTerminal(AgentRunStatus status) =>
        status is AgentRunStatus.Completed
            or AgentRunStatus.Failed
            or AgentRunStatus.Cancelled
            or AgentRunStatus.Interrupted;

    /// <summary>
    /// Fake <see cref="IAgentProvider"/> that spawns <c>cmd.exe</c> with the
    /// caller's argument string as a one-shot batch ("cmd /d /c &lt;args&gt;").
    /// Pass-through output parser; no session-mode support — every test that
    /// uses this is [WindowsFact] and runs in headless mode only.
    /// </summary>
    private sealed class CmdFakeProvider : IAgentProvider
    {
        public string Id => "fake-cmd";
        public string DisplayName => "Test (cmd.exe)";
        public string Executable => "cmd.exe";

        // No slash-prefix dance — the test arguments are raw cmd scripts
        // (e.g. "exit /b 0", "echo first&echo second"). Pass through.
        public string FormatPrompt(
            AgentDescriptor agent,
            string arguments,
            string? skillBody = null,
            IReadOnlyList<Viamus.Doxie.Orchestrator.Agents.Doxie.DoxieSkillMemory>? memories = null) =>
            arguments ?? string.Empty;

        public IEnumerable<string> BuildHeadlessArgs(AgentDescriptor agent, string formattedPrompt) =>
            new[] { "/d", "/c", formattedPrompt };

        public IEnumerable<string> BuildSessionArgs(AgentDescriptor agent) =>
            throw new NotSupportedException("Test fake does not support session mode.");

        public void WriteInitialMessage(System.IO.TextWriter stdin, AgentDescriptor agent, string formattedPrompt) =>
            throw new NotSupportedException("Test fake does not support session mode.");

        public IReadOnlyList<string> ParseOutputLine(string line) => new[] { line };

        public AgentRunUsage? TryParseUsage(string line) => null;

        public IEnumerable<string> BuildInteractiveArgs() => Array.Empty<string>();

        public string FormatBuilderBootstrap(string skillId, string? skillBody) => string.Empty;
    }

    private sealed class EstimatingCmdFakeProvider : IAgentProvider, IAgentUsageEstimator
    {
        public string Id => "fake-estimating-cmd";
        public string DisplayName => "Test (estimating cmd.exe)";
        public string Executable => "cmd.exe";

        public string FormatPrompt(
            AgentDescriptor agent,
            string arguments,
            string? skillBody = null,
            IReadOnlyList<Viamus.Doxie.Orchestrator.Agents.Doxie.DoxieSkillMemory>? memories = null) =>
            arguments ?? string.Empty;

        public IEnumerable<string> BuildHeadlessArgs(AgentDescriptor agent, string formattedPrompt) =>
            new[] { "/d", "/c", formattedPrompt };

        public IEnumerable<string> BuildSessionArgs(AgentDescriptor agent) =>
            throw new NotSupportedException("Test fake does not support session mode.");

        public void WriteInitialMessage(System.IO.TextWriter stdin, AgentDescriptor agent, string formattedPrompt) =>
            throw new NotSupportedException("Test fake does not support session mode.");

        public IReadOnlyList<string> ParseOutputLine(string line) => new[] { line };

        public AgentRunUsage? TryParseUsage(string line) => null;

        public AgentRunUsage? EstimateUsage(string formattedPrompt, IReadOnlyList<AgentRunOutputLine> output) =>
            new(11, 22, 0, 0, 0.123m);

        public IEnumerable<string> BuildInteractiveArgs() => Array.Empty<string>();

        public string FormatBuilderBootstrap(string skillId, string? skillBody) => string.Empty;
    }

    private sealed class RecordingExitProvider : IAgentProvider
    {
        public string? LastFormattedPrompt { get; private set; }
        public IReadOnlyList<DoxieSkillMemory>? LastMemories { get; private set; }

        public string Id => "recording-exit";
        public string DisplayName => "Recording Exit";
        public string Executable => "cmd.exe";

        public string FormatPrompt(
            AgentDescriptor agent,
            string arguments,
            string? skillBody = null,
            IReadOnlyList<DoxieSkillMemory>? memories = null)
        {
            LastMemories = memories;
            return arguments ?? string.Empty;
        }

        public IEnumerable<string> BuildHeadlessArgs(AgentDescriptor agent, string formattedPrompt)
        {
            LastFormattedPrompt = formattedPrompt;
            return new[] { "/d", "/c", "exit /b 0" };
        }

        public IEnumerable<string> BuildSessionArgs(AgentDescriptor agent) =>
            throw new NotSupportedException("Test fake does not support session mode.");

        public void WriteInitialMessage(System.IO.TextWriter stdin, AgentDescriptor agent, string formattedPrompt) =>
            throw new NotSupportedException("Test fake does not support session mode.");

        public IReadOnlyList<string> ParseOutputLine(string line) => new[] { line };

        public AgentRunUsage? TryParseUsage(string line) => null;

        public IEnumerable<string> BuildInteractiveArgs() => Array.Empty<string>();

        public string FormatBuilderBootstrap(string skillId, string? skillBody) => string.Empty;
    }

    private sealed class DotnetVersionProvider : IAgentProvider
    {
        public string Id => "dotnet-version";
        public string DisplayName => ".NET Version";
        public string Executable => "dotnet";

        public string FormatPrompt(
            AgentDescriptor agent,
            string arguments,
            string? skillBody = null,
            IReadOnlyList<DoxieSkillMemory>? memories = null) =>
            arguments ?? string.Empty;

        public IEnumerable<string> BuildHeadlessArgs(AgentDescriptor agent, string formattedPrompt) =>
            new[] { "--version" };

        public IEnumerable<string> BuildSessionArgs(AgentDescriptor agent) =>
            throw new NotSupportedException("Test fake does not support session mode.");

        public void WriteInitialMessage(System.IO.TextWriter stdin, AgentDescriptor agent, string formattedPrompt) =>
            throw new NotSupportedException("Test fake does not support session mode.");

        public IReadOnlyList<string> ParseOutputLine(string line) => new[] { line };

        public AgentRunUsage? TryParseUsage(string line) => null;

        public IEnumerable<string> BuildInteractiveArgs() => Array.Empty<string>();

        public string FormatBuilderBootstrap(string skillId, string? skillBody) => string.Empty;
    }

    private class BloatedPromptProvider : IAgentProvider
    {
        public const string BloatMarker = "BLOATED_PROVIDER_CONTEXT";

        public string? LastHeadlessPrompt { get; private set; }

        public virtual string Id => "bloated";
        public string DisplayName => "Bloated";
        public string Executable => "cmd.exe";

        public virtual string FormatPrompt(
            AgentDescriptor agent,
            string arguments,
            string? skillBody = null,
            IReadOnlyList<DoxieSkillMemory>? memories = null) =>
            $"{arguments}\n{BloatMarker}\n{new string('z', 30_000)}";

        public IEnumerable<string> BuildHeadlessArgs(AgentDescriptor agent, string formattedPrompt)
        {
            LastHeadlessPrompt = formattedPrompt;
            return new[] { "/d", "/c", "exit /b 0" };
        }

        public IEnumerable<string> BuildSessionArgs(AgentDescriptor agent) =>
            throw new NotSupportedException("Test fake does not support session mode.");

        public void WriteInitialMessage(System.IO.TextWriter stdin, AgentDescriptor agent, string formattedPrompt) =>
            throw new NotSupportedException("Test fake does not support session mode.");

        public IReadOnlyList<string> ParseOutputLine(string line) => new[] { line };

        public AgentRunUsage? TryParseUsage(string line) => null;

        public IEnumerable<string> BuildInteractiveArgs() => Array.Empty<string>();

        public string FormatBuilderBootstrap(string skillId, string? skillBody) => string.Empty;
    }

    private sealed class BloatedClaudePromptProvider : BloatedPromptProvider
    {
        public override string Id => "claude-code";

        public override string FormatPrompt(
            AgentDescriptor agent,
            string arguments,
            string? skillBody = null,
            IReadOnlyList<DoxieSkillMemory>? memories = null) =>
            $"/{agent.SkillName} {arguments}\n{BloatMarker}\n{new string('z', 30_000)}";
    }

    private sealed class StdinBloatedPromptProvider : BloatedPromptProvider, IHeadlessStdinAgentProvider
    {
        public string? LastStdinPrompt { get; private set; }

        public IEnumerable<string> BuildHeadlessStdinArgs(AgentDescriptor agent) =>
            new[] { "/d", "/c", "more > nul" };

        public void WriteHeadlessPrompt(System.IO.TextWriter stdin, AgentDescriptor agent, string formattedPrompt)
        {
            LastStdinPrompt = formattedPrompt;
            stdin.Write(formattedPrompt);
            stdin.Close();
        }
    }

    private sealed class SmallCodexStdinProvider : IAgentProvider, IHeadlessStdinAgentProvider
    {
        public string? LastHeadlessPrompt { get; private set; }
        public string? LastStdinPrompt { get; private set; }

        public string Id => "codex";
        public string DisplayName => "Codex Test";
        public string Executable => "cmd.exe";

        public string FormatPrompt(
            AgentDescriptor agent,
            string arguments,
            string? skillBody = null,
            IReadOnlyList<DoxieSkillMemory>? memories = null) =>
            arguments ?? string.Empty;

        public IEnumerable<string> BuildHeadlessArgs(AgentDescriptor agent, string formattedPrompt)
        {
            LastHeadlessPrompt = formattedPrompt;
            return new[] { "/d", "/c", "exit /b 9" };
        }

        public IEnumerable<string> BuildHeadlessStdinArgs(AgentDescriptor agent) =>
            new[] { "/d", "/c", "more > nul" };

        public void WriteHeadlessPrompt(System.IO.TextWriter stdin, AgentDescriptor agent, string formattedPrompt)
        {
            LastStdinPrompt = formattedPrompt;
            stdin.Write(formattedPrompt);
            stdin.Close();
        }

        public IEnumerable<string> BuildSessionArgs(AgentDescriptor agent) =>
            throw new NotSupportedException("Test fake does not support session mode.");

        public void WriteInitialMessage(System.IO.TextWriter stdin, AgentDescriptor agent, string formattedPrompt) =>
            throw new NotSupportedException("Test fake does not support session mode.");

        public IReadOnlyList<string> ParseOutputLine(string line) => new[] { line };

        public AgentRunUsage? TryParseUsage(string line) => null;

        public IEnumerable<string> BuildInteractiveArgs() => Array.Empty<string>();

        public string FormatBuilderBootstrap(string skillId, string? skillBody) => string.Empty;
    }

    private sealed class FakeAgentCatalog : IAgentCatalog
    {
        private readonly IReadOnlyList<AgentDescriptor> _agents;

        public FakeAgentCatalog(params AgentDescriptor[] agents)
        {
            _agents = agents;
        }

        public IReadOnlyList<AgentDescriptor> GetAll() => _agents;

        public AgentDescriptor? FindById(string id) =>
            _agents.FirstOrDefault(a => a.Id == id);

        public void Refresh() { /* fixture is static — nothing to refresh */ }

        // No SKILL.md backing the test fixture — runner gets null and
        // providers fall back to "no skill body" branch.
        public string? ReadSkillBody(string skillName, string? subcommand = null) => null;
    }

    private sealed class SingleWorkspaceStore : IWorkspaceStore
    {
        private readonly Workspace _workspace;

        public SingleWorkspaceStore(Workspace workspace)
        {
            _workspace = workspace;
        }

        public IReadOnlyList<Workspace> ListAll() => new[] { _workspace };

        public Workspace? GetById(string id) =>
            string.Equals(id, _workspace.Id, StringComparison.OrdinalIgnoreCase) ? _workspace : null;

        public Workspace Create(string id, string? displayName, string? description, IReadOnlyList<string>? mountedLibraryIds = null) =>
            throw new NotSupportedException();

        public void Delete(string id) =>
            throw new NotSupportedException();

        public Workspace SetMountedLibraries(string id, IReadOnlyList<string> libraryIds) =>
            throw new NotSupportedException();
    }

    private sealed class SingleLibraryStore : ILibraryStore
    {
        private readonly Library _library;

        public SingleLibraryStore(Library library)
        {
            _library = library;
        }

        public IReadOnlyList<Library> ListAll() => new[] { _library };

        public Library? GetById(string id) =>
            string.Equals(id, _library.Id, StringComparison.OrdinalIgnoreCase) ? _library : null;
    }
}

internal static class TaskExtensions
{
    public static async Task<T> WaitAsync<T>(this TaskCompletionSource<T> tcs, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        return await tcs.Task.WaitAsync(cts.Token);
    }
}
