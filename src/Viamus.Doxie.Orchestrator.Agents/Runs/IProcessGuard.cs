using System.Diagnostics;

namespace Viamus.Doxie.Orchestrator.Agents;

/// <summary>
/// Containment for spawned child processes — guarantees that nothing
/// the orchestrator started outlives the orchestrator itself. The
/// Windows implementation uses a Win32 Job Object with
/// KILL_ON_JOB_CLOSE; the POSIX implementation uses subreaper +
/// process-tree teardown on shutdown signals.
/// </summary>
public interface IProcessGuard : IDisposable
{
    void AssignProcess(Process process);
}

public static class ProcessGuard
{
    public static IProcessGuard Create()
    {
        return OperatingSystem.IsWindows()
            ? new WindowsJobObject()
            : new PosixProcessGuard();
    }
}
