using FluentAssertions;
using Viamus.Doxie.Orchestrator.Agents;

namespace Viamus.Doxie.Orchestrator.Common.Tests;

public sealed class StorageOptionsTests
{
    [Fact]
    public void ResolvePath_returns_absolute_path_unchanged()
    {
        // Already-rooted paths short-circuit the project-root walk, so the
        // method must return the input untouched (modulo env-var expansion,
        // which doesn't apply when no %VAR% is present).
        var input = OperatingSystem.IsWindows() ? @"C:\some\absolute\path" : "/some/absolute/path";

        var resolved = StorageOptions.ResolvePath(input);

        resolved.Should().Be(input);
    }

    [Fact]
    public void ResolvePath_expands_environment_variables()
    {
        // %LOCALAPPDATA% is the canonical "where DoxieOS keeps state.db"
        // pattern; expansion must happen before the rooted-path check.
        var key = $"DOXIE_TEST_VAR_{Guid.NewGuid():N}";
        var value = OperatingSystem.IsWindows() ? @"C:\expanded\value" : "/expanded/value";
        Environment.SetEnvironmentVariable(key, value);
        try
        {
            var resolved = StorageOptions.ResolvePath($"%{key}%/state.db");

            resolved.Should().StartWith(value);
            resolved.Should().EndWith("state.db");
        }
        finally
        {
            Environment.SetEnvironmentVariable(key, null);
        }
    }

    [Fact]
    public void ResolvePath_translates_LOCALAPPDATA_token_cross_platform()
    {
        // Default DatabasePath in appsettings ships with %LOCALAPPDATA%;
        // on Linux/macOS Environment.ExpandEnvironmentVariables won't
        // touch it, so the resolver has to translate it explicitly to
        // SpecialFolder.LocalApplicationData. Without this, Linux users
        // get a literal "%LOCALAPPDATA%/..." folder in CWD.
        var resolved = StorageOptions.ResolvePath("%LOCALAPPDATA%/Viamus.Doxie.Orchestrator/state.db");

        resolved.Should().NotContain("%LOCALAPPDATA%",
            "the placeholder must be expanded on every platform, not just Windows");
        Path.IsPathRooted(resolved).Should().BeTrue();
        // Slash style depends on the input; the resolver doesn't normalise.
        // What matters is the trailing component is intact.
        resolved.Should().EndWith("state.db");
        resolved.Should().Contain("Viamus.Doxie.Orchestrator");

        var expectedRoot = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        resolved.Should().StartWith(expectedRoot);
    }

    [Fact]
    public void ResolvePath_anchors_relative_paths_under_some_root()
    {
        // Without a .claude/ marker above CWD the resolver falls back to
        // CWD itself, but either way the result must be absolute.
        var resolved = StorageOptions.ResolvePath(".workspace");

        Path.IsPathRooted(resolved).Should().BeTrue();
        resolved.Should().EndWith(".workspace");
    }
}
