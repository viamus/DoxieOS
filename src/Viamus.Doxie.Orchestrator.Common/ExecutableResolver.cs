namespace Viamus.Doxie.Orchestrator.Common;

/// <summary>
/// PATH-based resolution for CLI executable names. Centralises the
/// "given <c>claude</c> on a Windows box where the actual file is
/// <c>codex.cmd</c> on PATH" logic so the Settings page status check,
/// the agent runner, and the PTY console all behave the same.
///
/// Important Windows nuance: <c>CreateProcessW</c> (and therefore
/// ConPTY / Pty.Net) does NOT honour <c>PATHEXT</c> and will not
/// auto-spawn a <c>.cmd</c> / <c>.bat</c> file as a real process —
/// those are batch scripts that need <c>cmd.exe /c</c> in front of
/// them. <see cref="ResolveCommand"/> handles both halves: walks
/// PATH with the right suffixes, and prepends the cmd-wrapper when
/// the resolved file is a batch script.
/// </summary>
public static class ExecutableResolver
{
    /// <summary>
    /// Walks PATH (with platform-appropriate suffixes) for a bare name
    /// and returns the absolute path of the first match, or <c>null</c>
    /// if none. An input that is already an absolute path returns
    /// itself (when the file exists) or <c>null</c>.
    /// </summary>
    public static string? Resolve(string exe)
    {
        if (string.IsNullOrWhiteSpace(exe)) return null;

        if (Path.IsPathRooted(exe))
        {
            return File.Exists(exe) ? exe : null;
        }

        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var separator = OperatingSystem.IsWindows() ? ';' : ':';
        var suffixes = OperatingSystem.IsWindows()
            ? new[] { ".exe", ".cmd", ".bat", "" }
            : new[] { "" };

        foreach (var dir in pathEnv.Split(separator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var suffix in suffixes)
            {
                var candidate = Path.Combine(dir.Trim(), exe + suffix);
                if (File.Exists(candidate)) return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// Returns a spawn-ready command string for a CLI name. On Linux /
    /// macOS this is just the resolved absolute path (or the bare name
    /// if PATH walk fails — let the kernel try). On Windows, a
    /// <c>.cmd</c> / <c>.bat</c> resolution is wrapped as
    /// <c>cmd.exe /c "&lt;path&gt;"</c> so it actually launches via
    /// the batch interpreter; <c>.exe</c> is returned quoted as-is.
    ///
    /// The returned string is meant to be the head of a command line
    /// that the caller appends arguments to verbatim. It must NOT be
    /// quoted again by the caller — the quoting is already applied
    /// where needed.
    /// </summary>
    public static string ResolveCommand(string exe)
    {
        var resolved = Resolve(exe);
        if (resolved is null)
        {
            // Last-ditch: hand the bare name to the OS and hope a shell
            // resolves it. On Linux this works; on Windows this is the
            // failing path the user just hit, but we surface it the
            // same way so the error message shows the real intent.
            return Quote(exe);
        }

        if (OperatingSystem.IsWindows())
        {
            var ext = Path.GetExtension(resolved);
            if (string.Equals(ext, ".cmd", StringComparison.OrdinalIgnoreCase)
                || string.Equals(ext, ".bat", StringComparison.OrdinalIgnoreCase))
            {
                // CreateProcessW cannot spawn a batch file directly —
                // it requires cmd.exe to interpret it.
                return $"cmd.exe /c {Quote(resolved)}";
            }
        }

        return Quote(resolved);
    }

    /// <summary>Adds shell-friendly double quotes only when the path
    /// contains whitespace.</summary>
    private static string Quote(string s) =>
        s.Contains(' ') || s.Contains('\t') ? $"\"{s}\"" : s;
}
