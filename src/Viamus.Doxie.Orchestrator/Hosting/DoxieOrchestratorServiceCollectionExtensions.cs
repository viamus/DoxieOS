using MudBlazor.Services;
using Viamus.Doxie.Orchestrator.Agents;
using Viamus.Doxie.Orchestrator.Agents.Doxie;
using Viamus.Doxie.Orchestrator.Builder;
using Viamus.Doxie.Orchestrator.Common;
using Viamus.Doxie.Orchestrator.Context;
using Viamus.Doxie.Orchestrator.CronScheduling;
using Viamus.Doxie.Orchestrator.Hubs;
using Viamus.Doxie.Orchestrator.Notifications;
using Viamus.Doxie.Orchestrator.ReleaseNotes;
using Viamus.Doxie.Orchestrator.Runtime;
using Viamus.Doxie.Orchestrator.Services;
using Viamus.Doxie.Orchestrator.Workflows;

namespace Viamus.Doxie.Orchestrator.Hosting;

internal static class DoxieOrchestratorServiceCollectionExtensions
{
    public static IServiceCollection AddDoxieOrchestrator(this IServiceCollection services, IConfiguration configuration)
    {
        // Add services to the container.
        services.AddRazorComponents()
            .AddInteractiveServerComponents();

        services.AddMudServices();
        var storage = configuration.GetSection("Storage").Get<StorageOptions>() ?? new StorageOptions();
        services.AddSingleton(storage);
        // Per-key persistence (default provider, custom catalog roots, UI
        // preferences, ...) lives in the same SQLite file as run
        // history so DoxieOS has one durable state file.
        services.AddSingleton<IKeyValueSettingsStore>(_ =>
        {
            var resolved = StorageOptions.ResolvePath(storage.DatabasePath);
            var dir = Path.GetDirectoryName(resolved);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            return new SqliteKeyValueSettingsStore($"Data Source={resolved}");
        });
        services.AddSingleton<AppLanguageService>();
        services.AddSingleton<IDoxieCatalogStore, DoxieCatalogStore>();
        // Catalog reads directly from the canonical .doxie/skills/ layer (NOT
        // the .claude/skills/ shim). The watcher fires on every save under
        // .doxie/, so manual edits surface in the UI without an app restart.
        // Skills authored under .claude/skills/ that haven't been migrated stay
        // invisible until they are promoted into .doxie/; that's deliberate and
        // avoids splitting source-of-truth in two.
        services.AddSingleton<IAgentCatalog>(sp =>
            new DoxieAgentCatalog(
                StorageOptions.ResolvePath("."),
                watch: true,
                catalogRootsProvider: () => sp.GetRequiredService<IDoxieCatalogStore>().ListCatalogs()));
        services.AddSingleton<IAgentRequirementChecker, AgentRequirementChecker>();

        // Doxie shim pipeline: .doxie/ is the canonical, provider-neutral
        // authoring surface. On every write under it, every registered shim
        // emitter regenerates its output (.claude/ for Claude Code, .codex/ for
        // OpenAI Codex). Startup runs one synchronous pass before the rest of
        // the host begins reading shims, so first-page-load sees consistent
        // state. All emitters are idempotent (content-aware writes), so an
        // unchanged canon produces zero filesystem churn.
        var workspaceRoot = StorageOptions.ResolvePath(".");
        // Expose the absolute project root to every dispatched agent so they can
        // resolve `.doxie/skills/<id>/memories/` regardless of CWD (which varies
        // per dispatch mode: project root for standalone runs, the workspace
        // path for workspace-bound runs, or a per-session sandbox folder for
        // builder sessions). Process.Start and Pty.Net both inherit parent env
        // by default, so setting it on the orchestrator process is enough â€” no
        // per-spawn plumbing needed.
        Environment.SetEnvironmentVariable("DOXIE_PROJECT_ROOT", workspaceRoot);
        services.AddSingleton(sp => new DoxieRegenerator(
            workspaceRoot,
            new IShimEmitter[]
            {
                new ClaudeShimEmitter(),     // .claude/skills/<id>/{SKILL.md, orchestrator.json, ...}
                new ClaudeAgentEmitter(),    // .claude/agents/<id>.md
                new ClaudeLibraryEmitter(),  // .claude/libraries/<id>/...
                new CodexShimEmitter(),      // .codex/AGENTS.md (skills + subagents)
            },
            new DoxieRegistryReader(
                Path.Combine(workspaceRoot, ".doxie"),
                () => sp.GetRequiredService<IDoxieCatalogStore>().ListCatalogs())));
        services.AddSingleton(sp => new FilesystemDoxieWatcher(
            sp.GetRequiredService<DoxieRegenerator>(),
            workspaceRoot));
        // One-off skill reader shared with the Builder bootstrap path â€”
        // BuilderSessionFactory uses it to read .doxie/skills/<id>/body.md
        // when formatting the initial PTY message for non-Claude providers.
        // Doxie root mirrors the regenerator's so they read the same canon.
        services.AddSingleton(sp => new DoxieSkillReader(
            Path.Combine(workspaceRoot, ".doxie"),
            () => sp.GetRequiredService<IDoxieCatalogStore>().ListCatalogs()));
        services.AddHostedService<DoxieRegenerationService>();
        services.AddSingleton(_ => new LegacySkillsMigrator(workspaceRoot));

        // Release notes â€” markdown files embedded in the assembly under
        // docs/release-notes/. Auto-popup logic in MainLayout reads from the
        // shared IKeyValueSettingsStore (key "ui.lastSeenReleaseVersion") so a
        // freshly-upgraded user sees the modal once, then never again until the
        // next release.
        services.AddSingleton<IReleaseNotesService, EmbeddedReleaseNotesService>();

        services.AddSingleton<IAgentRunStore>(_ =>
        {
            var resolved = StorageOptions.ResolvePath(storage.DatabasePath);
            var dir = Path.GetDirectoryName(resolved);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            return new SqliteAgentRunStore($"Data Source={resolved}");
        });
        // Libraries are read from the canonical layer (StorageOptions defaults
        // LibrariesDirectory to .doxie/libraries/). The .claude/libraries/ copy
        // is just a shim emitted by ClaudeLibraryEmitter â€” DoxieOS itself reads
        // only from the canonical, never the shim.
        services.AddSingleton<ILibraryStore>(_ =>
            new FilesystemLibraryStore(
                StorageOptions.ResolvePath(storage.LibrariesDirectory),
                catalogRootsProvider: _.GetRequiredService<IDoxieCatalogStore>().ListCatalogs));
        services.AddSingleton<IWorkspaceStore>(_ =>
            new FilesystemWorkspaceStore(
                directory: StorageOptions.ResolvePath(storage.WorkspacesDirectory),
                librariesDirectory: StorageOptions.ResolvePath(storage.LibrariesDirectory),
                catalogRootsProvider: _.GetRequiredService<IDoxieCatalogStore>().ListCatalogs));
        // Live PTY-backed console sessions. Singleton because the subprocess
        // must outlive any browser tab; the broadcaster bridges the store's
        // per-session events to the SignalR hub for fan-out.
        services.AddSingleton<RunFileBrowser>();
        services.AddSingleton<ProviderEnvironmentStore>();
        services.AddHostedService<HousekeepingService>();
        // Console store resolves both the active provider's executable AND
        // its interactive-mode args on every new session â€” so switching the
        // default in /settings flips the next console to use that CLI
        // (claude with --dangerously-skip-permissions, codex bare, etc.)
        // without an orchestrator restart. The lambdas are captured by
        // reference, so IAgentProviderResolver is the live source of truth
        // at call time.
        //
        // The executable string flows through ExecutableResolver.ResolveCommand
        // before reaching the PTY:
        //   - PATH-walk with .exe / .cmd / .bat suffixes on Windows so a bare
        //     "codex" resolves to whatever shim PATH actually offers.
        //   - Wrap .cmd / .bat resolutions in `cmd.exe /c "..."` because
        //     CreateProcessW (and therefore ConPTY) cannot spawn a batch file
        //     directly â€” that's the root cause of the "Could not create process"
        //     error npm-installed CLIs surface.
        services.AddSingleton<IConsoleSessionStore>(sp =>
            new InMemoryConsoleSessionStore(
                sp.GetRequiredService<IWorkspaceStore>(),
                executableProvider: () =>
                {
                    var bare = sp.GetRequiredService<IAgentProviderResolver>().Resolve().Executable;
                    return ExecutableResolver.ResolveCommand(bare);
                },
                interactiveArgsProvider: () => string.Join(
                    ' ',
                    sp.GetRequiredService<IAgentProviderResolver>().Resolve().BuildInteractiveArgs()),
                // Display label uses the bare executable name ("claude" /
                // "codex"), not the wrapped command â€” so the consoles side
                // list reflects which CLI is actually running.
                labelProvider: () => sp.GetRequiredService<IAgentProviderResolver>().Resolve().Executable,
                launchProfileProvider: providerId =>
                {
                    var resolver = sp.GetRequiredService<IAgentProviderResolver>();
                    var provider = string.IsNullOrWhiteSpace(providerId)
                        ? resolver.Resolve()
                        : resolver.AllProviders.FirstOrDefault(p => string.Equals(p.Id, providerId, StringComparison.OrdinalIgnoreCase))
                            ?? throw new InvalidOperationException($"Provider '{providerId}' is not registered.");
                    return new ConsoleLaunchProfile(
                        ExecutableResolver.ResolveCommand(provider.Executable),
                        string.Join(' ', provider.BuildInteractiveArgs()),
                        provider.Executable,
                        provider.Id);
                }));
        services.AddSingleton<ConsoleHubBroadcaster>();

        // Agent / Workflow Builder â€” interactive PTY console + sandbox-backed
        // manifest authoring. Allocator picks a fresh sandbox under .sandbox/,
        // the factory wires up the console session, the promoter writes the
        // finished manifest into the canonical agents catalog on Save.
        services.AddSingleton<BuilderSandboxAllocator>();
        services.AddSingleton<BuilderSessionFactory>();
        services.AddSingleton<AgentManifestPromoter>();
        services.AddSingleton<WorkflowManifestPromoter>();
        services.AddSignalR();

        // Locality detection â€” decides whether the request comes from the same
        // machine that hosts DoxieOS (so Open-folder can spawn explorer.exe) or
        // from a different machine (so we fall back to the in-browser file
        // browser at /workspaces/{id}/files). IHttpContextAccessor is what the
        // service reads from to inspect Connection.RemoteIpAddress.
        services.AddHttpContextAccessor();
        services.Configure<FileBrowserOptions>(o =>
        {
            var raw = configuration["Doxie:FileBrowser"];
            if (!string.IsNullOrWhiteSpace(raw)) o.Mode = raw;
        });
        services.AddScoped<IRequestLocalityService, RequestLocalityService>();

        services.AddSingleton(
            configuration.GetSection("Claude").Get<ClaudeRunnerOptions>() ?? new ClaudeRunnerOptions());
        services.AddSingleton(
            configuration.GetSection("Codex").Get<CodexProviderOptions>() ?? new CodexProviderOptions());

        // Agent CLI providers â€” every IAgentProvider here is offered to the
        // user on the Settings page. Order matters: the first registered
        // provider is the fallback default until the user picks one explicitly.
        services.AddSingleton<IAgentProvider, ClaudeCodeProvider>();
        services.AddSingleton<IAgentProvider, CodexProvider>();

        // Resolver wraps the registered providers + the persisted default so
        // the runner picks the active one fresh on every Start (Settings-page
        // changes take effect for subsequent runs without a restart).
        services.AddSingleton<IAgentProviderResolver, AgentProviderResolver>();

        // Runner is built via factory because the working-directory string lives
        // on ClaudeRunnerOptions and DI can't pluck a primitive from there
        // implicitly.
        services.AddSingleton<IAgentRunner>(sp => new ClaudeProcessAgentRunner(
            sp.GetRequiredService<IAgentCatalog>(),
            sp.GetRequiredService<IAgentRunStore>(),
            sp.GetRequiredService<IAgentProviderResolver>(),
            sp.GetRequiredService<ClaudeRunnerOptions>().WorkingDirectory,
            sp.GetRequiredService<DoxieSkillReader>(),
            sp.GetRequiredService<IWorkspaceStore>(),
            sp.GetRequiredService<ILibraryStore>(),
            storage));

        // Workflows â€” compositions of agents wired into a graph that runs on
        // a trigger. The runner is currently a simulator (no real agent
        // processes spawned) but the store + REST shape are real so the UX
        // can be exercised end-to-end.
        services.AddSingleton<IWorkflowStore>(_ =>
            new FilesystemWorkflowStore(
                StorageOptions.ResolvePath(storage.WorkflowsDirectory),
                catalogRootsProvider: _.GetRequiredService<IDoxieCatalogStore>().ListCatalogs));
        // Workflow runs persisted in the same SQLite file as agent runs (just
        // different tables) so historical workflow runs survive an orchestrator
        // restart â€” without that, the dashboard's "Recent runs" panel showed
        // agent history but lost workflows on every reboot.
        services.AddSingleton<IWorkflowRunStore>(_ =>
        {
            var resolved = StorageOptions.ResolvePath(storage.DatabasePath);
            var dir = Path.GetDirectoryName(resolved);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            return new SqliteWorkflowRunStore($"Data Source={resolved}");
        });
        services.AddSingleton<IWorkflowRunner>(sp =>
            new OrchestratedWorkflowRunner(
                runStore: sp.GetRequiredService<IWorkflowRunStore>(),
                workspaceStore: sp.GetRequiredService<IWorkspaceStore>(),
                agentCatalog: sp.GetRequiredService<IAgentCatalog>(),
                agentRunner: sp.GetRequiredService<IAgentRunner>(),
                runsDirectory: StorageOptions.ResolvePath(storage.WorkflowRunsDirectory),
                storage: storage,
                catalogStore: sp.GetRequiredService<IDoxieCatalogStore>()));

        // Cron daemon â€” embedded BackgroundService that fires Cron-triggered
        // workflows once per minute. Embedded for the prototype; can extract to
        // a standalone Windows Service later without touching the rest of the
        // codebase (it depends only on IWorkflowStore + IWorkflowRunner +
        // IWorkflowRunStore via IServiceScopeFactory).
        services.AddHostedService<WorkflowCronDaemon>();

        services.AddSingleton<NotificationsBus>();
        services.AddHostedService<RunCompletionNotifier>();
        services.AddHostedService<WorkflowCompletionNotifier>();


        return services;
    }
}
