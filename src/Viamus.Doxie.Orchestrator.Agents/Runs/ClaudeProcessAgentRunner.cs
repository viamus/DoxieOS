using System.Collections.Concurrent;
using System.Diagnostics;
using Viamus.Doxie.Orchestrator.Agents.Doxie;
using Viamus.Doxie.Orchestrator.Common;
using Viamus.Doxie.Orchestrator.Context;

namespace Viamus.Doxie.Orchestrator.Agents;

/// <summary>
/// Runs agents by spawning a configured CLI provider in headless or
/// session mode as child processes. Every spawned process is bound to an
/// <see cref="IProcessGuard"/> so the orchestrator does not leak orphan
/// children when it exits — Win32 Job Object on Windows, subreaper +
/// signal-handler teardown on POSIX.
///
/// The runner itself is provider-agnostic: which executable to spawn,
/// what flags to pass, how to format the prompt, the session-mode
/// handshake, and how to parse output lines all live behind
/// <see cref="IAgentProvider"/>. <see cref="ClaudeCodeProvider"/> is the
/// historical default; a <c>CodexProvider</c> can plug in here without
/// touching the runner.
/// </summary>
public sealed partial class ClaudeProcessAgentRunner : IAgentRunner, IDisposable
{
    // Windows npm shims resolve to .cmd/.bat and are launched through
    // cmd.exe, whose practical command-line limit is around 8k chars.
    // Keep Doxie well below that so workflow handoffs never hit
    // "The command line is too long" before stdin/file handoff can kick in.
    private const int MaxInlineArgumentsChars = 6_000;
    private const int MaxInlinePromptChars = 6_000;
    private const int MaxWorkspaceMemoryChars = 12_000;

    private readonly IAgentCatalog _catalog;
    private readonly IAgentRunStore _store;
    private readonly IAgentProviderResolver _resolver;
    private readonly IProcessGuard _processGuard;
    private readonly DoxieSkillReader? _skillReader;
    private readonly IWorkspaceStore? _workspaceStore;
    private readonly ILibraryStore? _libraryStore;
    private readonly StorageOptions? _storage;
    private readonly ConcurrentDictionary<string, ActiveRun> _activeRuns = new();
    private readonly string? _workingDirectory;

    public event Action<AgentRun>? RunUpdated;

    /// <summary>
    /// Production wires this with the real
    /// <see cref="IAgentProviderResolver"/> via the DI factory in
    /// <c>Program.cs</c>; tests inject
    /// <see cref="AgentProviderResolver.ForSingle"/> wrapping a fake
    /// <see cref="IAgentProvider"/>. The resolver is consulted *per
    /// run* so a Settings-page change to the default provider takes
    /// effect for subsequent dispatches without an orchestrator
    /// restart.
    ///
    /// <paramref name="skillReader"/> is used to load per-skill
    /// memories at dispatch time and pass them to
    /// <see cref="IAgentProvider.FormatPrompt"/>. Tests that don't
    /// care about memories can leave it null — the runner simply
    /// dispatches without injecting a memory block.
    /// </summary>
    public ClaudeProcessAgentRunner(
        IAgentCatalog catalog,
        IAgentRunStore store,
        IAgentProviderResolver resolver,
        string? workingDirectory = null,
        DoxieSkillReader? skillReader = null,
        IWorkspaceStore? workspaceStore = null,
        ILibraryStore? libraryStore = null,
        StorageOptions? storage = null)
    {
        _catalog = catalog;
        _store = store;
        _resolver = resolver;
        _workingDirectory = workingDirectory;
        _skillReader = skillReader;
        _workspaceStore = workspaceStore;
        _libraryStore = libraryStore;
        _storage = storage;
        _processGuard = ProcessGuard.Create();
    }

    public AgentRun Start(
        string agentId,
        string arguments,
        bool keepSessionAlive = false,
        string? workingDirectoryOverride = null,
        IReadOnlyDictionary<string, string>? envOverrides = null,
        string? modeId = null,
        string? workspaceId = null,
        string? displayArguments = null)
    {
        var requestedArguments = arguments ?? string.Empty;
        var agent = _catalog.FindById(agentId)
            ?? throw new InvalidOperationException($"Unknown agent '{agentId}'.");

        // Pre-create the per-skill memories folder so the agent can
        // append a learning without first having to mkdir. The runner's
        // CWD for the spawned child varies (project root, workspace,
        // sandbox), but the canonical .doxie/ always lives at the
        // configured project root — that's the only directory the
        // DoxieSkillReader actually reads from. Best-effort: a failure
        // here just means the agent will need to mkdir itself.
        var projectRoot = StorageOptions.ResolvePath(_workingDirectory ?? ".");
        var skillRoot = ResolveSkillRoot(projectRoot, agent);
        EnsureMemoriesDirectory(agent, skillRoot);

        // Snapshot the active provider once per run — subsequent default
        // changes from the Settings page only affect later dispatches.
        var provider = _resolver.Resolve();

        var effectiveCwd = !string.IsNullOrWhiteSpace(workingDirectoryOverride)
            ? workingDirectoryOverride
            : _workingDirectory;
        var resolvedCwd = !string.IsNullOrWhiteSpace(effectiveCwd)
            ? StorageOptions.ResolvePath(effectiveCwd)
            : null;
        var stdinProvider = provider as IHeadlessStdinAgentProvider;
        var canStreamPromptToStdin = stdinProvider is not null;
        var materialized = canStreamPromptToStdin
            ? KeepArgumentsInlineForStdinPrompt(requestedArguments)
            : MaterializeLargeArgumentsIfNeeded(requestedArguments, resolvedCwd, agent.SkillName);
        var effectiveArguments = materialized.Arguments;

        var run = new AgentRun(
            id: Guid.NewGuid().ToString("N"),
            agentId: agentId,
            arguments: string.IsNullOrWhiteSpace(displayArguments) ? materialized.DisplayArguments : displayArguments.Trim(),
            startedAt: DateTimeOffset.UtcNow);
        // Stamp the provider before persisting so the dashboard's
        // per-provider Spend breakdown can attribute usage correctly
        // even for runs that fail before emitting any output.
        run.ProviderId = provider.Id;

        _store.Add(run);

        // Resolve the executable via PATH + PATHEXT before handing it to
        // ProcessStartInfo. Process.Start (CreateProcessW) does not honour
        // PATHEXT, so npm-installed CLIs like "codex" are only found as
        // "codex.cmd" — and even a full path to a .cmd file needs cmd.exe
        // to interpret it. ExecutableResolver handles both halves.
        var resolvedExe = ExecutableResolver.Resolve(provider.Executable) ?? provider.Executable;
        var wrapInCmd = OperatingSystem.IsWindows()
            && (resolvedExe.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
                || resolvedExe.EndsWith(".bat", StringComparison.OrdinalIgnoreCase));

        var psi = new ProcessStartInfo(wrapInCmd ? "cmd.exe" : resolvedExe)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            // Claude CLI outputs UTF-8 (em-dashes, middle-dots, accented
            // characters). Without explicit encoding the .NET StreamReader
            // falls back to the OEM code page (CP437/CP850 on Windows),
            // which mangles those bytes into things like "Ã”Ã‡Ã¶" / "â”¬Ã€".
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };

        if (wrapInCmd)
        {
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add(resolvedExe);
        }
        if (!string.IsNullOrWhiteSpace(resolvedCwd))
        {
            psi.WorkingDirectory = resolvedCwd;
        }

        // Tell the spawned agent where it can POST a notification back to
        // DoxieOS. Inherited by curl/Bash invocations as $env:DOXIE_NOTIFY_URL.
        psi.Environment["DOXIE_NOTIFY_URL"] = "http://localhost:5034/api/notifications";
        psi.Environment["DOXIE_AGENT_ID"] = agent.Id;
        psi.Environment["DOXIE_AGENT_SKILL_ID"] = agent.SkillName;
        psi.Environment["DOXIE_AGENT_SKILL_ROOT"] = skillRoot;

        // Layer caller-supplied env vars on top of the orchestrator's
        // process env. Workflow-driven dispatches inject DOXIE_WORKFLOW_OUTPUT_DIR,
        // DOXIE_WORKFLOW_INPUT_DIRS, the workflow's own env block, and the
        // bound workspace's .env here. Last-write-wins, so the runner's
        // baseline (DOXIE_NOTIFY_URL) can be overridden by a workflow if it
        // ever needs to point notifications somewhere else.
        if (envOverrides is { Count: > 0 })
        {
            foreach (var pair in envOverrides)
            {
                psi.Environment[pair.Key] = pair.Value;
            }
        }

        var operatorHintsFile = BuildOperatorHintsFilePath(resolvedCwd, run.Id);
        EnsureOperatorHintsFile(operatorHintsFile);
        psi.Environment["DOXIE_OPERATOR_HINTS_FILE"] = operatorHintsFile;

        // The catalog can supply the agent's skill body so providers
        // without native skill resolution (CodexProvider) can inline it
        // into the prompt. Claude Code reads it natively from disk and
        // ignores this value. Pass the leading word of the arguments as a
        // subcommand hint so the catalog can return a more specific body
        // (e.g. from-files.md instead of the routing body.md).
        var subcommandHint = effectiveArguments.TrimStart('/')
            .Split(' ', 2, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();
        var skillBody = _catalog.ReadSkillBody(agent.SkillName, subcommandHint);

        // Per-skill memories — read from .doxie/skills/<id>/memories/*.md
        // and filtered against the live dispatch context (mode / provider /
        // workspace). Memories whose `condition` matches are appended to
        // the formatted prompt under a "Auto-loaded context" delimiter.
        // Skipped silently when the reader isn't wired (e.g. unit tests)
        // or when the skill has no memories folder yet.
        var memories = LoadAndFilterMemories(agent.SkillName, modeId, provider.Id, workspaceId);
        var fullFormattedPrompt = AppendOperatorHintInstructions(
            provider.FormatPrompt(agent, effectiveArguments, skillBody, memories),
            operatorHintsFile,
            resolvedCwd);
        var shouldStreamPromptToStdin = !keepSessionAlive
            && stdinProvider is not null
            && (fullFormattedPrompt.Length > MaxInlinePromptChars
                || string.Equals(provider.Id, "codex", StringComparison.OrdinalIgnoreCase));
        var formattedPrompt = shouldStreamPromptToStdin
            ? fullFormattedPrompt
            : MaterializeLargePromptIfNeeded(fullFormattedPrompt, resolvedCwd, agent, provider);

        if (keepSessionAlive)
        {
            psi.RedirectStandardInput = true;
            foreach (var arg in provider.BuildSessionArgs(agent))
            {
                psi.ArgumentList.Add(arg);
            }
        }
        else if (shouldStreamPromptToStdin)
        {
            psi.RedirectStandardInput = true;
            foreach (var arg in stdinProvider!.BuildHeadlessStdinArgs(agent))
            {
                psi.ArgumentList.Add(arg);
            }
        }
        else
        {
            foreach (var arg in provider.BuildHeadlessArgs(agent, formattedPrompt))
            {
                psi.ArgumentList.Add(arg);
            }
        }

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        // Capture the snapshotted provider in the handler closures so the
        // run is guaranteed to use the same provider end-to-end even if
        // the Settings-page default changes mid-flight.
        process.OutputDataReceived += (_, e) => HandleOutput(run, AgentRunOutputSource.Stdout, e.Data, provider);
        process.ErrorDataReceived += (_, e) => HandleOutput(run, AgentRunOutputSource.Stderr, e.Data, provider);
        process.Exited += (_, _) => HandleExited(run, process, provider, formattedPrompt);

        // Publish Running and register in active runs BEFORE Start, so a
        // process that exits sub-millisecond (e.g. `exit /b 0`) cannot fire
        // its Exited handler before subscribers see the Running transition.
        run.Status = AgentRunStatus.Running;
        _store.UpdateStatus(run.Id, run.Status, null, null);
        _activeRuns[run.Id] = new ActiveRun(
            run,
            process,
            provider,
            agent,
            operatorHintsFile,
            CanReceiveLiveStdin: keepSessionAlive);
        RunUpdated?.Invoke(run);

        try
        {
            process.Start();
            _processGuard.AssignProcess(process);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            if (keepSessionAlive)
            {
                provider.WriteInitialMessage(process.StandardInput, agent, formattedPrompt);
                // Deliberately do NOT close stdin — session must stay open
                // so cron entries created by /loop keep firing within it.
            }
            else if (shouldStreamPromptToStdin)
            {
                stdinProvider!.WriteHeadlessPrompt(process.StandardInput, agent, formattedPrompt);
            }
        }
        catch (Exception ex)
        {
            FailRun(run, $"failed to start process: {ex.Message}");
            _activeRuns.TryRemove(run.Id, out _);
            try { process.Dispose(); } catch { /* best effort */ }
        }

        return run;
    }

    public void Cancel(string runId)
    {
        if (!_activeRuns.TryGetValue(runId, out var active) || IsTerminal(active.Run.Status))
        {
            return;
        }

        // Mark Cancelled BEFORE killing — HandleExited fires on a thread
        // pool thread once Kill takes effect and re-checks the run status
        // via IsTerminal. If we kill first and then set the status, the
        // exit handler can race in, observe Running, and overwrite the
        // outcome with Completed/Failed.
        active.Run.Status = AgentRunStatus.Cancelled;
        active.Run.FinishedAt = DateTimeOffset.UtcNow;
        _store.UpdateStatus(active.Run.Id, active.Run.Status, active.Run.FinishedAt, null);
        AppendAndPersist(active.Run, AgentRunOutputSource.Stderr, "[orchestrator] cancelled by user");
        RunUpdated?.Invoke(active.Run);

        try
        {
            active.Process.Kill(entireProcessTree: true);
        }
        catch
        {
            /* process may have already exited; HandleExited will reconcile */
        }
    }

    public bool AddOperatorHint(string runId, string hint)
    {
        if (string.IsNullOrWhiteSpace(hint)) return false;
        if (!_activeRuns.TryGetValue(runId, out var active) || IsTerminal(active.Run.Status))
        {
            return false;
        }

        var clean = NormalizeOperatorHint(hint);
        AppendOperatorHintFile(active.OperatorHintsFile, clean);
        AppendAndPersist(active.Run, AgentRunOutputSource.Stderr, $"[operator hint] {clean}");

        var deliveredToStdin = false;
        if (active.CanReceiveLiveStdin && !active.Process.HasExited)
        {
            try
            {
                active.Provider.WriteInitialMessage(
                    active.Process.StandardInput,
                    active.Agent,
                    FormatLiveOperatorHintMessage(clean));
                deliveredToStdin = true;
            }
            catch (Exception ex)
            {
                AppendAndPersist(active.Run, AgentRunOutputSource.Stderr, $"[orchestrator] live hint stdin delivery failed: {ex.Message}");
            }
        }

        if (!deliveredToStdin)
        {
            AppendAndPersist(active.Run, AgentRunOutputSource.Stderr, "[orchestrator] hint recorded in DOXIE_OPERATOR_HINTS_FILE; agent prompt requires checking it at the next work cycle/instruction batch.");
        }

        RunUpdated?.Invoke(active.Run);
        return true;
    }

    /// <summary>
    /// Ensures the agent's canonical <c>memories/</c> folder exists
    /// before the agent is spawned. Without this, an agent that
    /// learned something on its first run would have to mkdir the folder
    /// itself before writing the memory file — which agents on minimal
    /// tool surfaces (Read/Write only, no Bash) literally cannot do.
    /// Best-effort: any IO error is swallowed because the read side
    /// (<see cref="DoxieSkillReader.LoadMemories"/>) already tolerates a
    /// missing folder, so a failure here only costs the agent the
    /// convenience — it doesn't break dispatch.
    /// </summary>
    private void EnsureMemoriesDirectory(AgentDescriptor agent, string skillRoot)
    {
        if (string.IsNullOrWhiteSpace(agent.SkillName)) return;
        try
        {
            var memoriesDir = Path.Combine(skillRoot, "memories");
            Directory.CreateDirectory(memoriesDir);
        }
        catch (IOException) { /* best-effort */ }
        catch (UnauthorizedAccessException) { /* best-effort */ }
    }

    private string ResolveSkillRoot(string projectRoot, AgentDescriptor agent)
    {
        if (_storage is not null)
        {
            var catalogRoot = string.IsNullOrWhiteSpace(agent.CatalogRoot)
                ? Path.GetFullPath(Path.Combine(StorageOptions.ResolvePath(_storage.SkillsDirectory), ".."))
                : agent.CatalogRoot;
            return Path.Combine(catalogRoot, "skills", agent.SkillName);
        }

        if (!string.IsNullOrWhiteSpace(agent.CatalogRoot))
        {
            return Path.Combine(agent.CatalogRoot, "skills", agent.SkillName);
        }

        var defaultRoot = Path.Combine(projectRoot, ".doxie", "skills");
        return Path.Combine(defaultRoot, agent.SkillName);
    }

    public AgentRun? Get(string runId)
    {
        if (_activeRuns.TryGetValue(runId, out var active))
        {
            return active.Run;
        }
        return _store.Get(runId);
    }

    public void Dispose()
    {
        // On Windows, closing the Job Object handle terminates every child
        // still assigned to it (KILL_ON_JOB_CLOSE — guaranteed by the kernel
        // even mid-crash). On POSIX, the guard walks its tracked PIDs and
        // kills each subtree explicitly.
        _processGuard.Dispose();
    }

    private void HandleOutput(AgentRun run, AgentRunOutputSource source, string? text, IAgentProvider provider)
    {
        if (text is null) return; // null indicates end-of-stream

        // For stdout, also probe for a usage event before rendering — the
        // result line is what carries token+cost accounting and lands once
        // near the very end. Persisting the first match is enough; later
        // result events (interactive / cron-driven sessions) overwrite,
        // which is fine: each cron tick burns its own tokens and the user
        // sees the latest tick's accounting.
        if (source == AgentRunOutputSource.Stdout)
        {
            var usage = provider.TryParseUsage(text);
            if (usage is not null)
            {
                run.Usage = usage;
                try { _store.UpdateUsage(run.Id, usage); }
                catch { /* don't let a store hiccup fail the run reporting */ }
            }
        }

        // For stdout, run the line through the provider's parser. stderr is
        // always passed through as-is (each provider can parse its own
        // stdout format, but stderr is universally human-readable).
        var emitted = source == AgentRunOutputSource.Stdout
            ? provider.ParseOutputLine(text)
            : new[] { text };

        if (emitted.Count == 0) return;

        foreach (var line in emitted)
        {
            AppendAndPersist(run, source, line);
        }
        RunUpdated?.Invoke(run);
    }

    private void HandleExited(AgentRun run, Process process, IAgentProvider provider, string formattedPrompt)
    {
        try
        {
            // Drains any remaining buffered output on the async pipes.
            process.WaitForExit();

            // If Cancel already set a terminal status, do not overwrite.
            if (IsTerminal(run.Status)) return;

            var exitCode = process.ExitCode;
            run.Status = exitCode == 0 ? AgentRunStatus.Completed : AgentRunStatus.Failed;
            run.FinishedAt = DateTimeOffset.UtcNow;
            run.ExitCode = exitCode;
            _store.UpdateStatus(run.Id, run.Status, run.FinishedAt, run.ExitCode);

            if (run.Usage is null
                && provider is IAgentUsageEstimator estimator
                && estimator.EstimateUsage(formattedPrompt, run.Output) is { } estimatedUsage)
            {
                run.Usage = estimatedUsage;
                try { _store.UpdateUsage(run.Id, estimatedUsage); }
                catch { /* don't let a store hiccup fail the run reporting */ }
            }

            // Best-effort: scan the tail of stdout for a JSON manifest with
            // a `report_dir` field. Producer agents (backlog-review,
            // business-refine, prompt) end their run by emitting one
            // such manifest. If found, persist the path so the UI can
            // render a file explorer rooted at it.
            var outputDir = ExtractOutputDirFromOutput(run.Output);
            if (outputDir is not null)
            {
                run.OutputDir = outputDir;
                try { _store.UpdateOutputDir(run.Id, outputDir); }
                catch { /* don't let a store hiccup fail the run reporting */ }
            }

            RunUpdated?.Invoke(run);
        }
        finally
        {
            _activeRuns.TryRemove(run.Id, out _);
            try { process.Dispose(); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Walks the run's output lines from latest to earliest, returning the
    /// first one that parses as JSON with a top-level <c>report_dir</c>
    /// (preferred) or <c>output_dir</c> field. Producer-agent contracts
    /// emit one such manifest as their final user-facing output.
    /// </summary>
    private static string? ExtractOutputDirFromOutput(IReadOnlyList<AgentRunOutputLine> output)
    {
        // Look at the last ~10 stdout lines — manifests are short and
        // always last; scanning the entire history is wasted work.
        const int LookbackLines = 10;
        var stdoutLines = output
            .Where(o => o.Source == AgentRunOutputSource.Stdout)
            .Select(o => o.Text)
            .Reverse()
            .Take(LookbackLines)
            .ToList();

        foreach (var line in stdoutLines)
        {
            var trimmed = line?.Trim();
            if (string.IsNullOrEmpty(trimmed)) continue;
            if (!trimmed.StartsWith('{') || !trimmed.EndsWith('}')) continue;

            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(trimmed);
                if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object) continue;

                if (doc.RootElement.TryGetProperty("report_dir", out var reportDir)
                    && reportDir.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    var s = reportDir.GetString();
                    if (!string.IsNullOrWhiteSpace(s)) return s;
                }

                if (doc.RootElement.TryGetProperty("output_dir", out var outputDir)
                    && outputDir.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    var s = outputDir.GetString();
                    if (!string.IsNullOrWhiteSpace(s)) return s;
                }
            }
            catch
            {
                // Not valid JSON — keep walking. The manifest is somewhere
                // else (or absent for non-producer agents).
            }
        }

        return null;
    }

    private void FailRun(AgentRun run, string message)
    {
        run.Status = AgentRunStatus.Failed;
        run.FinishedAt = DateTimeOffset.UtcNow;
        _store.UpdateStatus(run.Id, run.Status, run.FinishedAt, null);
        AppendAndPersist(run, AgentRunOutputSource.Stderr, $"[orchestrator] {message}");
        RunUpdated?.Invoke(run);
    }

    private void AppendAndPersist(AgentRun run, AgentRunOutputSource source, string text)
    {
        var line = new AgentRunOutputLine(DateTimeOffset.UtcNow, source, text);
        run.Append(line);
        _store.AppendOutput(run.Id, line);
    }

    private static bool IsTerminal(AgentRunStatus status) =>
        status is AgentRunStatus.Completed
            or AgentRunStatus.Failed
            or AgentRunStatus.Cancelled
            or AgentRunStatus.Interrupted;

    private sealed record ActiveRun(
        AgentRun Run,
        Process Process,
        IAgentProvider Provider,
        AgentDescriptor Agent,
        string OperatorHintsFile,
        bool CanReceiveLiveStdin);
}
