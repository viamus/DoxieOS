using System.Runtime.Versioning;
using Pty.Net;

namespace Viamus.Doxie.Orchestrator.Context;

/// <summary>
/// Windows backend: thin adapter around Pty.Net's ConPTY/WinPTY connection.
/// All the platform-specific work (selecting ConPTY on Windows 10+, falling
/// back to WinPTY otherwise) is handled inside Pty.Net.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsPtyHandle : IPtyHandle
{
    private readonly IPtyConnection _pty;

    public event Action<string>? Data;
    public event Action? Disconnected;

    public WindowsPtyHandle(string command, int cols, int rows, string workingDirectory)
    {
        _pty = PtyProvider.Spawn(
            command: command,
            width: cols,
            height: rows,
            workingDirectory: workingDirectory,
            options: new BackendOptions());
        _pty.PtyData += OnPtyData;
        _pty.PtyDisconnected += OnPtyDisconnected;
    }

    public Task WriteAsync(string data) => _pty.WriteAsync(data);

    public void Resize(int cols, int rows)
    {
        try { _pty.Resize(cols, rows); }
        catch { /* best-effort — terminal size is non-critical */ }
    }

    public void Dispose()
    {
        try { _pty.PtyData -= OnPtyData; } catch { }
        try { _pty.PtyDisconnected -= OnPtyDisconnected; } catch { }
        try { _pty.Dispose(); } catch { }
    }

    private void OnPtyData(object sender, string data) => Data?.Invoke(data);
    private void OnPtyDisconnected(object sender) => Disconnected?.Invoke();
}
