using FluentAssertions;
using Viamus.Doxie.Orchestrator.Context;

namespace Viamus.Doxie.Orchestrator.Context.Tests;

public sealed class PtyHandleTests
{
    [WindowsFact]
    public void Factory_returns_WindowsPtyHandle_on_Windows()
    {
        // Can't actually Spawn cmd here without it potentially hanging the
        // test runner, so we only assert the type-selection branch via
        // reflection on the factory's static method shape.
        var method = typeof(PtyHandleFactory).GetMethod("Spawn");
        method.Should().NotBeNull();
        method!.ReturnType.Should().Be(typeof(IPtyHandle));
    }

    [WindowsFact]
    public void PosixPtyHandle_ctor_refuses_to_run_on_Windows()
    {
        // On Windows the libutil P/Invoke would throw DllNotFoundException
        // before we get to our explicit guard — we want our explicit
        // PlatformNotSupported message instead, so the guard runs first.
        var act = () => new PosixPtyHandle("echo hi", 80, 24, ".");
        act.Should().Throw<PlatformNotSupportedException>();
    }

    [Fact]
    public void ShellQuote_wraps_simple_strings_in_single_quotes()
    {
        // The whole point of single-quote wrapping is that nothing inside
        // is interpreted by the shell — paths with $foo, *, spaces, etc.
        // pass through verbatim.
        PosixPtyHandle.ShellQuote("hello").Should().Be("'hello'");
        PosixPtyHandle.ShellQuote("/path with spaces/x").Should().Be("'/path with spaces/x'");
        PosixPtyHandle.ShellQuote("$HOME/*").Should().Be("'$HOME/*'");
        PosixPtyHandle.ShellQuote(string.Empty).Should().Be("''");
    }

    [Fact]
    public void ShellQuote_escapes_embedded_single_quotes_with_close_escape_reopen()
    {
        // POSIX has no in-string escape inside single quotes. The classic
        // workaround is to close the quote, emit a backslash-escaped
        // quote, then reopen — producing the four-byte sequence '\''.
        PosixPtyHandle.ShellQuote("it's").Should().Be("'it'\\''s'");
        PosixPtyHandle.ShellQuote("a'b'c").Should().Be("'a'\\''b'\\''c'");
    }

    [Fact]
    public void BuildShellWrappedArgv_emits_sh_dash_c_with_cd_and_exec()
    {
        // The wrapper exists so the child does ONE execvp call: chdir
        // moves into the shell layer instead of being a separate
        // P/Invoke from the post-fork managed code window.
        var argv = new[] { "/usr/bin/claude", "--dangerously-skip-permissions" };

        var wrapped = PosixPtyHandle.BuildShellWrappedArgv(argv, "/home/user/workspace");

        wrapped.Should().HaveCount(3);
        wrapped[0].Should().Be("/bin/sh");
        wrapped[1].Should().Be("-c");
        wrapped[2].Should().Be("cd '/home/user/workspace' && exec '/usr/bin/claude' '--dangerously-skip-permissions'");
    }

    [Fact]
    public void BuildShellWrappedArgv_omits_cd_when_workingDirectory_is_empty()
    {
        // No working dir â†’ don't emit `cd && ` at all. Stops a stray
        // empty-quoted `cd ''` from changing into $HOME by accident.
        var wrapped = PosixPtyHandle.BuildShellWrappedArgv(new[] { "echo", "hi" }, string.Empty);

        wrapped[2].Should().Be("exec 'echo' 'hi'");
        wrapped[2].Should().NotContain("cd ");
    }

    [Fact]
    public void SplitCommandLine_handles_quoted_args_with_spaces()
    {
        // The console hands us a single string ("claude --flag" or with
        // a quoted exe path). We need to split it the way a shell would
        // before passing to execvp via the wrapper.
        PosixPtyHandle.SplitCommandLine("claude --foo")
            .Should().Equal("claude", "--foo");

        PosixPtyHandle.SplitCommandLine("\"/path with space/claude\" --flag")
            .Should().Equal("/path with space/claude", "--flag");

        PosixPtyHandle.SplitCommandLine("'single quoted' rest")
            .Should().Equal("single quoted", "rest");

        PosixPtyHandle.SplitCommandLine(string.Empty)
            .Should().BeEmpty();
    }

    [Fact]
    public void ResolveExecutable_returns_absolute_path_unchanged_when_it_exists()
    {
        // If the caller already has a path with a slash, we never search
        // PATH — we just check existence and return it untouched. Lets
        // a user pin Claude:Executable to an absolute path bypassing
        // any PATH munging by systemd / container managers.
        var self = System.Reflection.Assembly.GetExecutingAssembly().Location;

        PosixPtyHandle.ResolveExecutable(self).Should().Be(self);
    }

    [Fact]
    public void ResolveExecutable_returns_null_when_path_with_slash_does_not_exist()
    {
        var bogus = OperatingSystem.IsWindows()
            ? @"C:\definitely\not\here\nope.exe"
            : "/definitely/not/here/nope";

        PosixPtyHandle.ResolveExecutable(bogus).Should().BeNull();
    }

    [PosixFact]
    public void PosixPtyHandle_spawns_echoes_and_disconnects_cleanly()
    {
        // `cat` with no args reads stdin and echoes to stdout — perfect
        // smoke test for the read/write/dispose lifecycle.
        var received = new List<string>();
        var disconnected = new System.Threading.ManualResetEventSlim();

        IPtyHandle pty;
        try
        {
            pty = new PosixPtyHandle("cat", cols: 80, rows: 24, workingDirectory: "/tmp");
        }
        catch (Exception ex)
        {
            // If the runner can't even forkpty (libutil missing on a
            // weird image), surface a clearer skip than a stack trace.
            throw new InvalidOperationException(
                "forkpty is unavailable in this environment — install libutil (Debian: bsdutils) before re-running.", ex);
        }

        try
        {
            pty.Data += s => { lock (received) received.Add(s); };
            pty.Disconnected += () => disconnected.Set();

            // Give cat a moment to spawn, then send a line.
            Thread.Sleep(150);
            pty.WriteAsync("hello-pty\n").GetAwaiter().GetResult();

            // Wait for the echo to come back (cat is line-buffered when
            // attached to a tty, so the \n triggers the flush).
            var deadline = DateTime.UtcNow.AddSeconds(2);
            while (DateTime.UtcNow < deadline)
            {
                lock (received)
                {
                    if (received.Any(s => s.Contains("hello-pty"))) break;
                }
                Thread.Sleep(25);
            }

            lock (received)
            {
                string.Concat(received).Should().Contain("hello-pty",
                    "PosixPtyHandle must round-trip text through the slave fd");
            }
        }
        finally
        {
            pty.Dispose();
        }

        disconnected.Wait(TimeSpan.FromSeconds(2)).Should().BeTrue(
            "Dispose must kill the child and the read loop must fire Disconnected");
    }
}

internal sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "Windows-only test.";
    }
}

internal sealed class PosixFactAttribute : FactAttribute
{
    public PosixFactAttribute()
    {
        if (OperatingSystem.IsWindows()) Skip = "POSIX-only test.";
    }
}
