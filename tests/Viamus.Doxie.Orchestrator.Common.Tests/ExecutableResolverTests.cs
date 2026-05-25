using FluentAssertions;

namespace Viamus.Doxie.Orchestrator.Common.Tests;

public sealed class ExecutableResolverTests : IDisposable
{
    private readonly string _scratchDir;

    public ExecutableResolverTests()
    {
        _scratchDir = Path.Combine(Path.GetTempPath(), $"exec-resolver-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_scratchDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_scratchDir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void Resolve_returns_null_for_empty_or_whitespace_input()
    {
        ExecutableResolver.Resolve("").Should().BeNull();
        ExecutableResolver.Resolve("   ").Should().BeNull();
    }

    [Fact]
    public void Resolve_returns_absolute_path_when_file_exists()
    {
        var path = Path.Combine(_scratchDir, "tool.exe");
        File.WriteAllText(path, "fake binary");

        ExecutableResolver.Resolve(path).Should().Be(path);
    }

    [Fact]
    public void Resolve_returns_null_for_absolute_path_that_does_not_exist()
    {
        var path = Path.Combine(_scratchDir, "does-not-exist.exe");

        ExecutableResolver.Resolve(path).Should().BeNull();
    }

    [Fact]
    public void Resolve_finds_a_bare_name_via_PATH_with_appropriate_suffix()
    {
        // Drop a fake file matching what the OS would expect on PATH.
        var suffix = OperatingSystem.IsWindows() ? ".exe" : "";
        var binaryName = "doxie-fake";
        var path = Path.Combine(_scratchDir, binaryName + suffix);
        File.WriteAllText(path, "fake");

        var originalPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            // Prepend our scratch dir so the resolver finds it first.
            Environment.SetEnvironmentVariable(
                "PATH",
                _scratchDir + (OperatingSystem.IsWindows() ? ";" : ":") + originalPath);

            ExecutableResolver.Resolve(binaryName).Should().Be(path);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath);
        }
    }

    [Fact]
    public void ResolveCommand_returns_bare_name_when_PATH_lookup_fails()
    {
        // Last-ditch: hand off to the OS shell. The string is whatever we
        // got, possibly quoted. The orchestrator surfaces the kernel-side
        // failure via ConsoleSession's catch block in this case.
        var bogus = "definitely-not-on-path-" + Guid.NewGuid().ToString("N")[..6];

        ExecutableResolver.ResolveCommand(bogus).Should().Be(bogus);
    }

    [Fact]
    public void ResolveCommand_quotes_paths_that_contain_spaces()
    {
        var dirWithSpace = Path.Combine(_scratchDir, "with space");
        Directory.CreateDirectory(dirWithSpace);
        var exe = Path.Combine(dirWithSpace, "tool.exe");
        File.WriteAllText(exe, "fake");

        var cmd = ExecutableResolver.ResolveCommand(exe);

        cmd.Should().StartWith("\"").And.EndWith("\"");
        cmd.Should().Contain("with space");
    }

    [Fact]
    public void ResolveCommand_wraps_cmd_files_with_cmd_exe_on_Windows()
    {
        if (!OperatingSystem.IsWindows())
        {
            // CreateProcessW + .cmd interpretation is a Windows-only
            // concern; on POSIX the resolver doesn't apply this rule.
            return;
        }

        var cmdFile = Path.Combine(_scratchDir, "shim.cmd");
        File.WriteAllText(cmdFile, "@echo fake\r\n");

        var resolved = ExecutableResolver.ResolveCommand(cmdFile);

        resolved.Should().StartWith("cmd.exe /c ");
        resolved.Should().Contain("shim.cmd");
    }

    [Fact]
    public void ResolveCommand_does_not_wrap_exe_files()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // .exe is irrelevant on POSIX
        }

        var exe = Path.Combine(_scratchDir, "tool.exe");
        File.WriteAllText(exe, "fake");

        var resolved = ExecutableResolver.ResolveCommand(exe);

        resolved.Should().NotContain("cmd.exe /c");
        resolved.Should().Contain("tool.exe");
    }
}
