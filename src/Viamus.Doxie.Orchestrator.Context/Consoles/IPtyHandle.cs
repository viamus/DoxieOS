namespace Viamus.Doxie.Orchestrator.Context;

/// <summary>
/// Cross-platform abstraction over a live PTY-backed child process.
/// Decouples <see cref="ConsoleSession"/> from any specific PTY library
/// so the Windows backend (Pty.Net's ConPTY/WinPTY) and the POSIX
/// backend (forkpty over libutil) can both plug in.
/// </summary>
internal interface IPtyHandle : IDisposable
{
    /// <summary>Fires for every chunk of stdout/stderr the child emits, decoded as UTF-8.</summary>
    event Action<string>? Data;

    /// <summary>Fires once when the child process exits or the master fd is closed.</summary>
    event Action? Disconnected;

    Task WriteAsync(string data);
    void Resize(int cols, int rows);
}

internal static class PtyHandleFactory
{
    public static IPtyHandle Spawn(string command, int cols, int rows, string workingDirectory)
    {
        return OperatingSystem.IsWindows()
            ? new WindowsPtyHandle(command, cols, rows, workingDirectory)
            : new PosixPtyHandle(command, cols, rows, workingDirectory);
    }
}
