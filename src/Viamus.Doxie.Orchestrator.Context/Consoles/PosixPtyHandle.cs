using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Viamus.Doxie.Orchestrator.Context;

/// <summary>
/// POSIX PTY backend implemented over <c>posix_spawn(3)</c> + libc PTY
/// primitives (<c>posix_openpt</c> / <c>grantpt</c> / <c>unlockpt</c> /
/// <c>ptsname_r</c>). Targets Linux + macOS.
///
/// **Why posix_spawn and not forkpty:** plain <c>fork(2)</c> from a
/// .NET process is unsafe — the child inherits all the parent's CLR
/// threads as dead, but any locks those threads were holding (GC heap,
/// JIT, the marshaler cache) stay locked forever. Even a single P/Invoke
/// in the child can SIGSEGV trying to take one of those locks. This is
/// exactly why <see cref="System.Diagnostics.Process.Start"/> uses
/// posix_spawn internally instead of forking.
///
/// **PTY setup with posix_spawn:** the parent allocates the master with
/// <c>posix_openpt</c>, gets the slave path with <c>ptsname_r</c>, and
/// uses <c>posix_spawn_file_actions_addopen</c> to make the kernel open
/// the slave path on fds 0/1/2 inside the child after exec — no managed
/// code in the child window. <c>POSIX_SPAWN_SETSID</c> makes the child
/// a session leader before the open, so the first tty the kernel opens
/// becomes the controlling terminal automatically (no <c>ioctl(TIOCSCTTY)</c>
/// gymnastics required).
/// </summary>
[UnsupportedOSPlatform("windows")]
internal sealed class PosixPtyHandle : IPtyHandle
{
    private const int SIGHUP = 1;
    private const int SIGKILL = 9;
    private const int EINTR = 4;
    private const ulong TIOCSWINSZ = 0x5414; // Linux constant; same on glibc-based systems
    private const int O_RDWR = 2;
    private const int O_NOCTTY = 0x100; // 0o400 — Linux value; macOS = 0x20000 (handled below if needed)
    private const short POSIX_SPAWN_SETSID = 0x80; // glibc 2.26+

    // posix_spawn_file_actions_t and posix_spawnattr_t are opaque structs
    // with implementation-defined sizes. On glibc x86_64 the actions
    // struct is ~80 bytes and the attr ~336 bytes. Allocating 1024 bytes
    // for each gives generous headroom across glibc/musl/BSD libc and
    // future ABI growth.
    private const int OpaqueSpawnStructBytes = 1024;

    private readonly int _masterFd;
    private readonly int _childPid;
    private readonly Thread _readThread;
    private readonly Decoder _utf8Decoder = new UTF8Encoding(false, throwOnInvalidBytes: false).GetDecoder();
    private readonly object _writeLock = new();
    private int _disposed;
    private int _childReaped;
    private bool _anyDataReceived;

    public event Action<string>? Data;
    public event Action? Disconnected;

    public PosixPtyHandle(string command, int cols, int rows, string workingDirectory)
    {
        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                $"{nameof(PosixPtyHandle)} is for non-Windows platforms; use {nameof(WindowsPtyHandle)} on Windows.");
        }

        var argv = SplitCommandLine(command);
        if (argv.Length == 0)
        {
            throw new ArgumentException("Command line is empty.", nameof(command));
        }

        // Pre-flight: resolve argv[0] against $PATH ourselves so a missing
        // binary fails loudly with the actual lookup path, instead of the
        // child silently exiting 127 inside execvp and the user seeing a
        // bare "[session exited]" with zero context.
        argv[0] = ResolveExecutable(argv[0])
            ?? throw new InvalidOperationException(
                $"Executable '{argv[0]}' not found on PATH ({Environment.GetEnvironmentVariable("PATH")}). " +
                "Set Claude:Executable in appsettings.json to the absolute path " +
                "(e.g. /home/<user>/.local/bin/claude).");

        // PTY apps assume TERM is set — Ink (claude's UI library) refuses
        // to render without it and the child exits immediately. systemd
        // units and bare `dotnet run` from non-tty parents don't get TERM
        // for free, so we backfill a sane default in our own env (which
        // the fork inherits). Harmless to the orchestrator process.
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TERM")))
        {
            Environment.SetEnvironmentVariable("TERM", "xterm-256color");
        }

        // chdir is handled by a `/bin/sh -c 'cd <cwd> && exec <cmd>'`
        // wrapper so we don't need posix_spawn_file_actions_addchdir_np
        // (a GNU extension that doesn't exist on macOS or older glibcs).
        var spawnArgv = BuildShellWrappedArgv(argv, workingDirectory);

        // 1. Allocate the PTY pair on our side. O_NOCTTY because the
        //    parent must not get the slave as its controlling tty.
        int masterFd = posix_openpt(O_RDWR | O_NOCTTY);
        if (masterFd < 0)
        {
            throw new InvalidOperationException(
                $"posix_openpt failed (errno={Marshal.GetLastWin32Error()}).");
        }

        try
        {
            if (grantpt(masterFd) != 0)
            {
                throw new InvalidOperationException($"grantpt failed (errno={Marshal.GetLastWin32Error()}).");
            }
            if (unlockpt(masterFd) != 0)
            {
                throw new InvalidOperationException($"unlockpt failed (errno={Marshal.GetLastWin32Error()}).");
            }

            // ptsname_r writes the slave path (e.g. "/dev/pts/3") into
            // a caller-supplied buffer. Plain ptsname is not thread-safe
            // because it returns a pointer to an internal static buffer.
            var slaveBuf = new byte[256];
            int rc;
            unsafe
            {
                fixed (byte* p = slaveBuf)
                {
                    rc = ptsname_r(masterFd, (IntPtr)p, (UIntPtr)slaveBuf.Length);
                }
            }
            if (rc != 0)
            {
                throw new InvalidOperationException($"ptsname_r failed (rc={rc}).");
            }
            int nul = Array.IndexOf(slaveBuf, (byte)0);
            var slavePath = Encoding.UTF8.GetString(slaveBuf, 0, nul < 0 ? slaveBuf.Length : nul);

            // 2. Set the initial winsize on the master so the slave inherits
            //    the right dimensions when the child opens it.
            var ws = new WinSize
            {
                ws_row = (ushort)Math.Max(1, rows),
                ws_col = (ushort)Math.Max(1, cols),
            };
            try { ioctl(masterFd, TIOCSWINSZ, ref ws); } catch { /* best-effort */ }

            // 3. posix_spawn the child with file_actions that open the
            //    slave path on fds 0/1/2 INSIDE the new process. Combined
            //    with POSIX_SPAWN_SETSID, the first such open becomes
            //    the child's controlling terminal automatically.
            var argvNative = MarshalArgv(spawnArgv);
            IntPtr slavePathNative = Marshal.StringToCoTaskMemUTF8(slavePath);
            IntPtr fa = Marshal.AllocHGlobal(OpaqueSpawnStructBytes);
            IntPtr attr = Marshal.AllocHGlobal(OpaqueSpawnStructBytes);
            bool faInit = false, attrInit = false;
            IntPtr[]? envpNative = null;

            try
            {
                if (posix_spawn_file_actions_init(fa) != 0)
                {
                    throw new InvalidOperationException("posix_spawn_file_actions_init failed.");
                }
                faInit = true;

                if (posix_spawnattr_init(attr) != 0)
                {
                    throw new InvalidOperationException("posix_spawnattr_init failed.");
                }
                attrInit = true;

                // Open the slave on stdin/stdout/stderr in the child.
                for (int fd = 0; fd <= 2; fd++)
                {
                    int e = posix_spawn_file_actions_addopen(fa, fd, slavePathNative, O_RDWR, 0);
                    if (e != 0)
                    {
                        throw new InvalidOperationException(
                            $"posix_spawn_file_actions_addopen(fd={fd}) failed (errno={e}).");
                    }
                }

                if (posix_spawnattr_setflags(attr, POSIX_SPAWN_SETSID) != 0)
                {
                    throw new InvalidOperationException("posix_spawnattr_setflags failed.");
                }

                // Pass the parent's environment explicitly. POSIX leaves the
                // behavior of envp=NULL implementation-defined, and glibc
                // interprets it as "empty environment" — which strips PATH,
                // HOME, USER, and TERM from the child. Marshal our env vars
                // into a NULL-terminated KEY=VALUE table and hand it in.
                envpNative = BuildEnvp();
                int spawnRc = posix_spawnp(out int childPid, argvNative[0], fa, attr, argvNative, envpNative);
                if (spawnRc != 0)
                {
                    throw new InvalidOperationException(
                        $"posix_spawnp failed (errno={spawnRc}). Command was: /bin/sh -c \"...\"");
                }

                _masterFd = masterFd;
                _childPid = childPid;
                masterFd = -1; // ownership transferred — the catch-cleanup below must NOT close it
            }
            finally
            {
                if (faInit) { try { posix_spawn_file_actions_destroy(fa); } catch { } }
                if (attrInit) { try { posix_spawnattr_destroy(attr); } catch { } }
                Marshal.FreeHGlobal(fa);
                Marshal.FreeHGlobal(attr);
                if (slavePathNative != IntPtr.Zero) Marshal.FreeCoTaskMem(slavePathNative);
                FreeArgv(argvNative);
                if (envpNative is not null) FreeArgv(envpNative);
            }
        }
        catch
        {
            if (masterFd >= 0) { try { close(masterFd); } catch { } }
            throw;
        }

        _readThread = new Thread(ReadLoop)
        {
            IsBackground = true,
            Name = $"posix-pty-{_childPid}",
        };
        _readThread.Start();
    }

    public Task WriteAsync(string data)
    {
        if (string.IsNullOrEmpty(data) || _disposed != 0) return Task.CompletedTask;

        // PTY writes are usually small (keystrokes / paste chunks); a
        // synchronous write off the SignalR thread is fine and avoids
        // queue growth. If profiling shows contention, swap for a
        // pipelined Channel<byte[]>.
        var bytes = Encoding.UTF8.GetBytes(data);
        lock (_writeLock)
        {
            int offset = 0;
            while (offset < bytes.Length)
            {
                long written;
                unsafe
                {
                    fixed (byte* p = &bytes[offset])
                    {
                        written = write(_masterFd, (IntPtr)p, (UIntPtr)(bytes.Length - offset));
                    }
                }
                if (written < 0)
                {
                    var err = Marshal.GetLastWin32Error();
                    if (err == EINTR) continue;
                    // Master fd closed or child gone — drop the keystroke.
                    break;
                }
                offset += (int)written;
            }
        }
        return Task.CompletedTask;
    }

    public void Resize(int cols, int rows)
    {
        if (_disposed != 0) return;
        var ws = new WinSize
        {
            ws_row = (ushort)Math.Max(1, rows),
            ws_col = (ushort)Math.Max(1, cols),
            ws_xpixel = 0,
            ws_ypixel = 0,
        };
        try { ioctl(_masterFd, TIOCSWINSZ, ref ws); }
        catch { /* best-effort */ }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        // If the read loop already noticed EOF and reaped the child, skip
        // signalling — kill on a recycled PID is a real risk on busy hosts.
        if (Volatile.Read(ref _childReaped) == 0)
        {
            // Polite first, lethal second. SIGHUP gives `claude` a chance
            // to flush its terminal state; if the kernel hasn't reaped the
            // process within ~250 ms we send SIGKILL.
            try { kill(_childPid, SIGHUP); } catch { }

            var killDeadline = DateTime.UtcNow.AddMilliseconds(250);
            int status;
            while (DateTime.UtcNow < killDeadline)
            {
                var w = waitpid(_childPid, out status, 1 /*WNOHANG*/);
                if (w == _childPid || w < 0) { Interlocked.Exchange(ref _childReaped, 1); break; }
                Thread.Sleep(20);
            }
            if (Volatile.Read(ref _childReaped) == 0)
            {
                try { kill(_childPid, SIGKILL); } catch { }
                try { waitpid(_childPid, out status, 0); } catch { }
                Interlocked.Exchange(ref _childReaped, 1);
            }
        }

        // Closing the master fd unblocks the read thread (read returns 0).
        try { close(_masterFd); } catch { }
    }

    private void ReadLoop()
    {
        var buf = new byte[8 * 1024];
        var charBuf = new char[buf.Length];
        try
        {
            while (_disposed == 0)
            {
                long n;
                unsafe
                {
                    fixed (byte* p = buf)
                    {
                        n = read(_masterFd, (IntPtr)p, (UIntPtr)buf.Length);
                    }
                }

                if (n < 0)
                {
                    var err = Marshal.GetLastWin32Error();
                    if (err == EINTR) continue;
                    break; // I/O error — treat as disconnect
                }
                if (n == 0) break; // EOF — child exited / master closed

                // UTF-8 decoder is stateful: a multibyte sequence split
                // across two reads is reassembled across calls.
                int charCount = _utf8Decoder.GetChars(buf, 0, (int)n, charBuf, 0, flush: false);
                if (charCount > 0)
                {
                    _anyDataReceived = true;
                    var chunk = new string(charBuf, 0, charCount);
                    try { Data?.Invoke(chunk); }
                    catch { /* a bad subscriber must not kill the read loop */ }
                }
            }
        }
        catch
        {
            // Any unexpected exception — surface as disconnect rather than crash.
        }
        finally
        {
            // Reap the child if it exited on its own (EOF on master fd
            // means the kernel closed the slave because the child died).
            // Only emit a diagnostic if the user never saw any output —
            // a child that ran for a while and then exited cleanly
            // doesn't need an error pinned to the bottom of the screen.
            var diag = ReapChildAndDescribeStatus();
            if (!_anyDataReceived && diag is not null)
            {
                try
                {
                    Data?.Invoke($"\r\n\x1b[31m[orchestrator] {diag}\x1b[0m\r\n");
                }
                catch { }
            }
            try { Disconnected?.Invoke(); } catch { }
        }
    }

    /// <summary>
    /// Calls <c>waitpid(pid, &amp;status, WNOHANG)</c> and decodes the result
    /// into a human-readable string for surfacing in the terminal. Returns
    /// <c>null</c> if the child was already reaped or if waitpid couldn't
    /// be called. Idempotent — subsequent calls are no-ops.
    /// </summary>
    private string? ReapChildAndDescribeStatus()
    {
        if (Interlocked.Exchange(ref _childReaped, 1) != 0) return null;

        int status;
        int w;
        try { w = waitpid(_childPid, out status, 1 /*WNOHANG*/); }
        catch { return null; }

        if (w <= 0) return null; // not reapable / unknown — silent

        // POSIX status decoding without including <sys/wait.h>: the low byte
        // is the signal that killed the process (0 if exited normally),
        // and the second byte is the exit code if WIFEXITED.
        if ((status & 0x7f) == 0)
        {
            int exitCode = (status >> 8) & 0xff;
            if (exitCode == 0) return null; // clean exit, no message
            return exitCode == 127
                ? $"child exited 127 — execvp couldn't run the binary (PATH miss / not executable / wrong arch)."
                : $"child exited with code {exitCode}.";
        }
        else
        {
            int sig = status & 0x7f;
            return $"child killed by signal {sig}.";
        }
    }

    /// <summary>
    /// Wraps <paramref name="argv"/> in <c>/bin/sh -c 'cd &lt;cwd&gt; &amp;&amp; exec &lt;cmd&gt;'</c>
    /// so the child can move chdir + final-exec out of the post-fork
    /// managed code window. Returned argv has exactly three elements:
    /// the shell path, <c>-c</c>, and the script. Single-quote-escapes
    /// every arg so paths with spaces or special chars survive intact.
    /// </summary>
    internal static string[] BuildShellWrappedArgv(string[] argv, string workingDirectory)
    {
        var script = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(workingDirectory))
        {
            script.Append("cd ").Append(ShellQuote(workingDirectory)).Append(" && ");
        }
        script.Append("exec");
        foreach (var a in argv)
        {
            script.Append(' ').Append(ShellQuote(a));
        }
        return new[] { "/bin/sh", "-c", script.ToString() };
    }

    /// <summary>
    /// POSIX shell single-quote escape: wrap in single quotes and replace
    /// any embedded <c>'</c> with the four-byte sequence <c>'\''</c>
    /// (close, escaped quote, reopen). Safer than double quotes because
    /// nothing is interpreted inside single quotes.
    /// </summary>
    internal static string ShellQuote(string s) => "'" + s.Replace("'", "'\\''") + "'";

    /// <summary>
    /// If <paramref name="exec"/> contains a slash, return it as-is (it's a
    /// path). Otherwise walk $PATH like execvp would and return the first
    /// hit, or <c>null</c> if none of the directories has it. Resolving
    /// here (instead of letting execvp do it) lets us throw with a useful
    /// error message in the parent before forking.
    /// </summary>
    internal static string? ResolveExecutable(string exec)
    {
        if (exec.Contains('/')) return File.Exists(exec) ? exec : null;
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path)) return null;
        foreach (var dir in path.Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim(), exec);
                if (File.Exists(candidate)) return candidate;
            }
            catch { /* malformed PATH entry — skip */ }
        }
        return null;
    }

    internal static string[] SplitCommandLine(string command)
    {
        // Same parsing rules POSIX shells use for unquoted/quoted args.
        // Simple state machine — handles single-quoted, double-quoted,
        // and backslash-escaped runs. No env-var expansion.
        var args = new List<string>();
        var sb = new StringBuilder();
        char quote = '\0';
        for (int i = 0; i < command.Length; i++)
        {
            char c = command[i];
            if (quote != '\0')
            {
                if (c == quote) { quote = '\0'; continue; }
                if (c == '\\' && quote == '"' && i + 1 < command.Length)
                {
                    sb.Append(command[++i]);
                    continue;
                }
                sb.Append(c);
            }
            else if (c == '"' || c == '\'')
            {
                quote = c;
            }
            else if (c == '\\' && i + 1 < command.Length)
            {
                sb.Append(command[++i]);
            }
            else if (char.IsWhiteSpace(c))
            {
                if (sb.Length > 0) { args.Add(sb.ToString()); sb.Clear(); }
            }
            else
            {
                sb.Append(c);
            }
        }
        if (sb.Length > 0) args.Add(sb.ToString());
        return args.ToArray();
    }

    private static IntPtr[] MarshalArgv(string[] argv)
    {
        // execvp expects a null-terminated char** so the array we hand
        // it must have one extra IntPtr.Zero slot at the end.
        var native = new IntPtr[argv.Length + 1];
        for (int i = 0; i < argv.Length; i++)
        {
            native[i] = Marshal.StringToCoTaskMemUTF8(argv[i]);
        }
        native[argv.Length] = IntPtr.Zero;
        return native;
    }

    /// <summary>
    /// Snapshot the parent's environment into a NULL-terminated <c>KEY=VALUE</c>
    /// char**. Required because <c>posix_spawn(envp=NULL)</c> on glibc gives
    /// the child an empty environment — no PATH, no HOME, no USER — which
    /// breaks anything the user actually wants to run from the console.
    /// </summary>
    private static IntPtr[] BuildEnvp()
    {
        var entries = new List<string>();
        foreach (System.Collections.DictionaryEntry kv in Environment.GetEnvironmentVariables())
        {
            var key = kv.Key as string;
            if (string.IsNullOrEmpty(key) || key.Contains('=')) continue; // malformed
            entries.Add(key + "=" + (kv.Value as string ?? string.Empty));
        }
        return MarshalArgv(entries.ToArray());
    }

    private static void FreeArgv(IntPtr[] argv)
    {
        for (int i = 0; i < argv.Length; i++)
        {
            if (argv[i] != IntPtr.Zero) Marshal.FreeCoTaskMem(argv[i]);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinSize
    {
        public ushort ws_row;
        public ushort ws_col;
        public ushort ws_xpixel;
        public ushort ws_ypixel;
    }

    // glibc and Apple libSystem both expose the PTY + posix_spawn family
    // from libc. The .NET runtime resolves "libc" to the right .so/.dylib
    // automatically (libc.so.6 on Linux, libSystem.B.dylib on macOS).
    private const string LibC = "libc";

    [DllImport(LibC, EntryPoint = "posix_openpt", SetLastError = true)]
    private static extern int posix_openpt(int flags);

    [DllImport(LibC, EntryPoint = "grantpt", SetLastError = true)]
    private static extern int grantpt(int fd);

    [DllImport(LibC, EntryPoint = "unlockpt", SetLastError = true)]
    private static extern int unlockpt(int fd);

    [DllImport(LibC, EntryPoint = "ptsname_r", SetLastError = true)]
    private static extern int ptsname_r(int fd, IntPtr buf, UIntPtr buflen);

    [DllImport(LibC, EntryPoint = "posix_spawn_file_actions_init")]
    private static extern int posix_spawn_file_actions_init(IntPtr fa);

    [DllImport(LibC, EntryPoint = "posix_spawn_file_actions_destroy")]
    private static extern int posix_spawn_file_actions_destroy(IntPtr fa);

    [DllImport(LibC, EntryPoint = "posix_spawn_file_actions_addopen")]
    private static extern int posix_spawn_file_actions_addopen(IntPtr fa, int fd, IntPtr path, int oflag, int mode);

    [DllImport(LibC, EntryPoint = "posix_spawnattr_init")]
    private static extern int posix_spawnattr_init(IntPtr attr);

    [DllImport(LibC, EntryPoint = "posix_spawnattr_destroy")]
    private static extern int posix_spawnattr_destroy(IntPtr attr);

    [DllImport(LibC, EntryPoint = "posix_spawnattr_setflags")]
    private static extern int posix_spawnattr_setflags(IntPtr attr, short flags);

    [DllImport(LibC, EntryPoint = "posix_spawnp")]
    private static extern int posix_spawnp(out int pid, IntPtr file, IntPtr fa, IntPtr attr, IntPtr[] argv, IntPtr[] envp);

    [DllImport(LibC, EntryPoint = "read", SetLastError = true)]
    private static extern long read(int fd, IntPtr buf, UIntPtr count);

    [DllImport(LibC, EntryPoint = "write", SetLastError = true)]
    private static extern long write(int fd, IntPtr buf, UIntPtr count);

    [DllImport(LibC, EntryPoint = "close", SetLastError = true)]
    private static extern int close(int fd);

    [DllImport(LibC, EntryPoint = "kill", SetLastError = true)]
    private static extern int kill(int pid, int sig);

    [DllImport(LibC, EntryPoint = "waitpid", SetLastError = true)]
    private static extern int waitpid(int pid, out int status, int options);

    [DllImport(LibC, EntryPoint = "ioctl", SetLastError = true)]
    private static extern int ioctl(int fd, ulong request, ref WinSize argp);
}
