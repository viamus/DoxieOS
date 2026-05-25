using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Viamus.Doxie.Orchestrator.Agents;
using Viamus.Doxie.Orchestrator.Runtime;

namespace Viamus.Doxie.Orchestrator.Services;

/// <summary>
/// Sweeps stale on-disk artefacts from previous runs:
/// <list type="bullet">
///   <item>Per-run scratch folders under <c>.runs/&lt;runId&gt;/</c></item>
///   <item>Per-standalone-run sandboxes under <c>.sandbox/&lt;tag&gt;/</c></item>
/// </list>
/// A folder is "stale" when its <c>LastWriteTimeUtc</c> is older than
/// <see cref="RetentionAge"/> (7 days). The sweep runs once 30 seconds
/// after the orchestrator boots (so DI / store reconciliation can
/// finish) and then every <see cref="SweepInterval"/> (6 hours).
///
/// <para>SQLite run records are NOT touched — only filesystem
/// accumulation. The History UI keeps showing past runs even after
/// their on-disk folders are pruned; clicking into one of them just
/// won't surface the file explorer.</para>
/// </summary>
public sealed class HousekeepingService : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromHours(6);
    private static readonly TimeSpan RetentionAge = TimeSpan.FromDays(7);
    private static readonly TimeSpan StartupGrace = TimeSpan.FromSeconds(30);

    private readonly StorageOptions _storage;
    private readonly ILogger<HousekeepingService> _logger;

    public HousekeepingService(StorageOptions storage, ILogger<HousekeepingService> logger)
    {
        _storage = storage;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Defer the first sweep so we don't race with orchestrator
        // startup (stores rehydrating, in-flight runs being marked
        // Interrupted, etc.). After that, sweep on the steady cadence.
        try { await Task.Delay(StartupGrace, stoppingToken); }
        catch (TaskCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                Sweep();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Housekeeping sweep failed; will retry next cycle");
            }

            try { await Task.Delay(SweepInterval, stoppingToken); }
            catch (TaskCanceledException) { break; }
        }
    }

    private void Sweep()
    {
        var cutoff = DateTime.UtcNow - RetentionAge;
        var runsRoot = StorageOptions.ResolvePath(_storage.WorkflowRunsDirectory);
        var sandboxesRoot = StorageOptions.ResolvePath(_storage.SandboxesDirectory);

        var deletedRuns = SweepStaleSubdirs(runsRoot, cutoff, "run");
        var deletedSandboxes = SweepStaleSubdirs(sandboxesRoot, cutoff, "sandbox");

        if (deletedRuns > 0 || deletedSandboxes > 0)
        {
            _logger.LogInformation(
                "Housekeeping pruned {RunCount} run folder(s) and {SandboxCount} sandbox(es) older than {Days} days",
                deletedRuns, deletedSandboxes, (int)RetentionAge.TotalDays);
        }
    }

    /// <summary>
    /// Recursively deletes any direct child of <paramref name="root"/>
    /// whose <c>LastWriteTimeUtc</c> is older than <paramref name="cutoff"/>.
    /// Errors are logged and swallowed — one stuck folder doesn't abort
    /// the whole sweep.
    /// </summary>
    private int SweepStaleSubdirs(string root, DateTime cutoff, string label)
    {
        if (!Directory.Exists(root)) return 0;

        var deleted = 0;
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            try
            {
                var info = new DirectoryInfo(dir);
                if (info.LastWriteTimeUtc >= cutoff) continue;

                RobustDirectoryDelete.Delete(dir);
                _logger.LogInformation(
                    "Housekeeping deleted stale {Label} folder {Dir} (last modified {LastWrite:u})",
                    label, dir, info.LastWriteTimeUtc);
                deleted++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Housekeeping failed to delete {Label} folder {Dir}", label, dir);
            }
        }
        return deleted;
    }
}
