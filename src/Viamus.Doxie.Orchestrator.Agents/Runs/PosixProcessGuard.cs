using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Viamus.Doxie.Orchestrator.Agents;

/// <summary>
/// POSIX equivalent of <see cref="WindowsJobObject"/>: there is no
/// kernel primitive on Linux that mirrors KILL_ON_JOB_CLOSE exactly,
/// so we approximate it with two layers.
///
/// 1. <c>PR_SET_CHILD_SUBREAPER</c> (Linux only) makes the orchestrator
///    inherit any orphaned descendant instead of letting it reparent
///    to <c>init</c>. That keeps us aware of the full subtree even
///    after intermediate shells exit.
/// 2. We track every assigned <see cref="Process"/> and kill the whole
///    process tree on graceful shutdown — Dispose, AppDomain.ProcessExit,
///    SIGINT/SIGTERM via Console.CancelKeyPress / PosixSignalRegistration.
///
/// A SIGKILL of the orchestrator itself can still leave orphans on
/// Linux — that's a kernel limitation, not a bug. For production on
/// systemd, run the orchestrator as a unit with <c>KillMode=control-group</c>
/// to get the same guarantee Windows Job Objects give for free.
/// </summary>
[UnsupportedOSPlatform("windows")]
public sealed class PosixProcessGuard : IProcessGuard
{
    private const int PR_SET_CHILD_SUBREAPER = 36;

    private readonly object _lock = new();
    private readonly List<Process> _processes = new();
    private readonly List<IDisposable> _signalRegistrations = new();
    private bool _disposed;

    public PosixProcessGuard()
    {
        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                $"{nameof(PosixProcessGuard)} is for non-Windows platforms; use {nameof(WindowsJobObject)} on Windows.");
        }

        // Best-effort: become a subreaper so that if the claude CLI
        // forks helpers and then exits, those helpers reparent to us
        // (visible in our process table) instead of init.
        TrySetChildSubreaper();

        // Wire shutdown hooks. ProcessExit fires on a normal exit;
        // CancelKeyPress on Ctrl-C; PosixSignalRegistration on
        // SIGTERM / SIGHUP / SIGQUIT (the signals systemd / docker
        // send when stopping a container).
        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
        Console.CancelKeyPress += OnCancelKeyPress;

        TryRegisterSignal(PosixSignal.SIGTERM);
        TryRegisterSignal(PosixSignal.SIGHUP);
        TryRegisterSignal(PosixSignal.SIGQUIT);
    }

    public void AssignProcess(Process process)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _processes.Add(process);
        }

        // Drop terminated processes from the tracking list automatically
        // so we don't accumulate dead Process handles for a long-running
        // orchestrator.
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) =>
        {
            lock (_lock) _processes.Remove(process);
        };
    }

    public void Dispose()
    {
        AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
        Console.CancelKeyPress -= OnCancelKeyPress;

        lock (_lock)
        {
            foreach (var reg in _signalRegistrations)
            {
                try { reg.Dispose(); } catch { /* best effort */ }
            }
            _signalRegistrations.Clear();
        }

        KillAll();
    }

    private void OnProcessExit(object? sender, EventArgs e) => KillAll();

    private void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs e)
    {
        // Don't cancel the cancel — let the host shutdown sequence run.
        // We just take the opportunity to tear down children promptly.
        KillAll();
    }

    private void OnSignal(PosixSignalContext ctx)
    {
        // Acknowledge the signal so the runtime continues its normal
        // shutdown sequence (which will also fire ProcessExit). We just
        // need an early hook to start killing children before the host
        // tears the rest of the world down.
        ctx.Cancel = false;
        KillAll();
    }

    private void TryRegisterSignal(PosixSignal signal)
    {
        try
        {
            var reg = PosixSignalRegistration.Create(signal, OnSignal);
            lock (_lock) _signalRegistrations.Add(reg);
        }
        catch
        {
            // Some signals aren't supported on every POSIX flavour
            // (e.g. SIGHUP semantics differ on macOS) — we don't want
            // a missing signal to stop the orchestrator from booting.
        }
    }

    private void KillAll()
    {
        Process[] snapshot;
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            snapshot = _processes.ToArray();
            _processes.Clear();
        }

        foreach (var p in snapshot)
        {
            try
            {
                if (!p.HasExited)
                {
                    // entireProcessTree walks /proc on Linux to catch
                    // any helper the spawned CLI itself forked.
                    p.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // Process may have just exited; this is best-effort cleanup.
            }
        }
    }

    [DllImport("libc", SetLastError = true, EntryPoint = "prctl")]
    private static extern int prctl(int option, ulong arg2, ulong arg3, ulong arg4, ulong arg5);

    private static void TrySetChildSubreaper()
    {
        if (!OperatingSystem.IsLinux()) return; // prctl is Linux-only
        try
        {
            prctl(PR_SET_CHILD_SUBREAPER, 1, 0, 0, 0);
        }
        catch
        {
            // libc lookup or syscall failed — non-fatal, we still have
            // the process-tracking + signal-handler safety net.
        }
    }
}
