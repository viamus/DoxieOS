using System.Diagnostics;
using FluentAssertions;

namespace Viamus.Doxie.Orchestrator.Agents.Tests;

public sealed class ProcessGuardTests
{
    [WindowsFact]
    public void Create_returns_WindowsJobObject_on_Windows()
    {
        using var guard = ProcessGuard.Create();
        guard.Should().BeOfType<WindowsJobObject>();
    }

    [PosixFact]
    public void Create_returns_PosixProcessGuard_on_non_Windows()
    {
        using var guard = ProcessGuard.Create();
        guard.Should().BeOfType<PosixProcessGuard>();
    }

    [WindowsFact]
    public void PosixProcessGuard_ctor_refuses_to_run_on_Windows()
    {
        var act = () => new PosixProcessGuard();
        act.Should().Throw<PlatformNotSupportedException>();
    }

    [PosixFact]
    public void PosixProcessGuard_kills_assigned_process_on_dispose()
    {
        var psi = new ProcessStartInfo("sleep", "30")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.Start();

        try
        {
            // Sanity-check the test fixture: child must really be alive
            // before we assert Dispose tears it down. Otherwise a failure
            // to spawn would falsely "pass" the kill assertion.
            process.HasExited.Should().BeFalse("the sleep child should still be running");

            using (var guard = new PosixProcessGuard())
            {
                guard.AssignProcess(process);
            }

            process.WaitForExit(milliseconds: 5_000).Should().BeTrue(
                "PosixProcessGuard.Dispose must kill every assigned child");
        }
        finally
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch { /* best effort */ }
        }
    }
}

internal sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Windows-only test.";
        }
    }
}

internal sealed class PosixFactAttribute : FactAttribute
{
    public PosixFactAttribute()
    {
        if (OperatingSystem.IsWindows())
        {
            Skip = "POSIX-only test.";
        }
    }
}
