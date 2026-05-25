using System.Collections.Concurrent;
using Viamus.Doxie.Orchestrator.Workflows;

namespace Viamus.Doxie.Orchestrator.CronScheduling;

/// <summary>
/// Embedded cron daemon: ticks once per minute, fires every workflow whose
/// trigger is <see cref="WorkflowTriggerKind.Cron"/>, is currently
/// <see cref="WorkflowDefinition.Enabled"/>, and whose next-occurrence-since-
/// last-fired has passed. Single-instance per workflow — if the workflow has a
/// non-terminal run already in flight (manual or cron), we skip and re-check
/// next tick (don't advance the per-workflow cursor) so we fire as soon as
/// the in-flight run finishes, on the next valid cron beat.
///
/// <para>Lives inside the orchestrator process via <c>IHostedService</c> —
/// deliberate prototype trade-off, matches our current life-stage. When
/// the orchestrator stops, crons stop. Future iteration could split into a
/// standalone Windows Service so crons survive UI restarts; the inputs
/// (IWorkflowStore + IWorkflowRunner) are already DI-resolved so nothing
/// else changes when that happens.</para>
///
/// <para>First-sighting semantics: when a workflow first appears (orchestrator
/// startup, or a freshly created cron workflow), we anchor its cursor to "now"
/// rather than backfilling missed occurrences — restarts shouldn't surprise
/// the user with a flurry of catch-up runs.</para>
/// </summary>
public sealed class WorkflowCronDaemon : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<WorkflowCronDaemon> _log;
    private readonly ConcurrentDictionary<string, DateTime> _cursors = new();

    public WorkflowCronDaemon(IServiceScopeFactory scopeFactory, ILogger<WorkflowCronDaemon> log)
    {
        _scopeFactory = scopeFactory;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("Cron daemon starting; next tick at next minute boundary");

        // Sleep to the next minute boundary so we tick predictably at :00.
        // Saves the "fired at xx:34:42" awkwardness in the logs.
        await DelayUntilNextMinuteBoundary(stoppingToken).ConfigureAwait(false);

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        do
        {
            try
            {
                Tick();
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _log.LogError(ex, "Cron daemon tick failed");
            }
        } while (await SafeWaitForNextTickAsync(timer, stoppingToken).ConfigureAwait(false));

        _log.LogInformation("Cron daemon stopping");
    }

    private void Tick()
    {
        var now = DateTime.UtcNow;
        // Resolve the store + runner per tick: they're singletons so this
        // is essentially free, but the scope keeps the pattern consistent
        // for any future scoped collaborators.
        using var scope = _scopeFactory.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IWorkflowStore>();
        var runner = scope.ServiceProvider.GetRequiredService<IWorkflowRunner>();
        var runStore = scope.ServiceProvider.GetRequiredService<IWorkflowRunStore>();

        foreach (var wf in store.ListAll())
        {
            if (wf.Trigger.Kind != WorkflowTriggerKind.Cron) continue;
            if (!wf.Enabled) continue;
            if (!CronExpression.IsValid(wf.Trigger.CronExpression)) continue;

            // First-sighting anchor — don't backfill missed runs from
            // before the daemon (or the workflow) existed.
            var since = _cursors.GetOrAdd(wf.Id, _ => now);
            var nextFire = CronExpression.NextOccurrenceAfter(wf.Trigger.CronExpression, since);
            if (nextFire is null) continue;
            if (nextFire > now) continue;

            // Single-instance guard. Don't advance the cursor — re-check
            // every minute so we fire on the very next valid beat after
            // the in-flight run finishes (vs. permanently dropping the
            // missed firings).
            var inFlight = runStore.ListByWorkflow(wf.Id)
                .Any(r => r.Status is WorkflowRunStatus.Queued or WorkflowRunStatus.Running);
            if (inFlight)
            {
                _log.LogInformation(
                    "Cron skipped workflow {Workflow} — previous run still in progress",
                    wf.Id);
                continue;
            }

            _cursors[wf.Id] = now;
            try
            {
                var run = runner.Start(wf, triggeredBy: $"cron:{wf.Trigger.CronExpression}");
                _log.LogInformation(
                    "Cron fired workflow {Workflow} ({Cron}) â†’ run {Run}",
                    wf.Id, wf.Trigger.CronExpression, run.Id);
            }
            catch (InvalidOperationException ex)
            {
                // Disabled-since-Read race or unknown agent: just log,
                // don't crash the daemon.
                _log.LogWarning(ex,
                    "Cron failed to start workflow {Workflow}",
                    wf.Id);
            }
        }
    }

    private static async Task DelayUntilNextMinuteBoundary(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var msUntilNextMinute = (60 - now.Second) * 1000 - now.Millisecond;
        if (msUntilNextMinute <= 0 || msUntilNextMinute > 60000) return;
        try
        {
            await Task.Delay(msUntilNextMinute, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* shutdown — fine */ }
    }

    private static async Task<bool> SafeWaitForNextTickAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try
        {
            return await timer.WaitForNextTickAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
