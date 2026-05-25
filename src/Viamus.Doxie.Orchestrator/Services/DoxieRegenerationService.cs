using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Viamus.Doxie.Orchestrator.Agents.Doxie;

namespace Viamus.Doxie.Orchestrator.Services;

/// <summary>
/// Boots the <c>.doxie/</c> â†’ <c>.claude/</c> + <c>.codex/</c> codegen pipeline
/// alongside the host:
/// <list type="bullet">
///   <item>On startup, runs one synchronous regen pass so the shims are
///         consistent before the rest of the orchestrator (FilesystemAgentCatalog,
///         CodexProvider, etc.) starts reading them.</item>
///   <item>Owns the <see cref="FilesystemDoxieWatcher"/> for the host's lifetime
///         so write-triggered regens fire on every save under <c>.doxie/</c>.</item>
/// </list>
///
/// Errors are caught and logged — a malformed manifest must not bring down
/// DoxieOS, since the user is probably mid-edit.
/// </summary>
public sealed class DoxieRegenerationService : IHostedService, IDisposable
{
    private readonly DoxieRegenerator _regenerator;
    private readonly FilesystemDoxieWatcher _watcher;
    private readonly ILogger<DoxieRegenerationService> _logger;

    public DoxieRegenerationService(
        DoxieRegenerator regenerator,
        FilesystemDoxieWatcher watcher,
        ILogger<DoxieRegenerationService> logger)
    {
        _regenerator = regenerator;
        _watcher = watcher;
        _logger = logger;
        _watcher.OnError = ex => _logger.LogError(ex, "Doxie regen failed (watcher-triggered).");
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var result = _regenerator.Regenerate();
            _logger.LogInformation(
                "Doxie regen on startup: {Skills} skill(s), {Agents} agent(s), {Libraries} library/-ies, {Workflows} workflow(s) â†’ {Emitters} shim(s).",
                result.SkillCount, result.AgentCount, result.LibraryCount, result.WorkflowCount, result.EmitterCount);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Doxie regen failed on startup. Shims may be stale.");
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public void Dispose() => _watcher.Dispose();
}
