using FluentAssertions;
using Viamus.Doxie.Orchestrator.Context;

namespace Viamus.Doxie.Orchestrator.Context.Tests;

/// <summary>
/// Path-traversal regression tests for the workspace file browser. The
/// helper is the only thing standing between an HTTP caller and arbitrary
/// reads on the orchestrator host's filesystem, so the guard cases here
/// are the ones that historically bite (dotdot escape, sibling-prefix
/// confusion, symlink targets).
/// </summary>
public sealed class WorkspaceFileBrowserTests : IDisposable
{
    private readonly string _root;
    private readonly string _outsideRoot;

    public WorkspaceFileBrowserTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"wfb-tests-{Guid.NewGuid():N}");
        _outsideRoot = Path.Combine(Path.GetTempPath(), $"wfb-outside-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_outsideRoot);

        // Seed a small tree so the listing tests have something concrete
        // to assert against — top-level file, top-level folder with one
        // file inside.
        File.WriteAllText(Path.Combine(_root, "README.md"), "# hello\n\nworld");
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        File.WriteAllText(Path.Combine(_root, "src", "Program.cs"), "class Program { }");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
        try { Directory.Delete(_outsideRoot, recursive: true); } catch { }
    }

    [Fact]
    public void ResolveSafePath_returns_root_when_relative_path_is_null_or_empty()
    {
        var (resolved, error) = WorkspaceFileBrowser.ResolveSafePath(_root, null);
        error.Should().BeNull();
        resolved.Should().Be(Path.GetFullPath(_root));

        (resolved, error) = WorkspaceFileBrowser.ResolveSafePath(_root, string.Empty);
        error.Should().BeNull();
        resolved.Should().Be(Path.GetFullPath(_root));
    }

    [Fact]
    public void ResolveSafePath_resolves_in_bounds_files_normally()
    {
        var (resolved, error) = WorkspaceFileBrowser.ResolveSafePath(_root, "src/Program.cs");
        error.Should().BeNull();
        resolved.Should().Be(Path.GetFullPath(Path.Combine(_root, "src", "Program.cs")));
    }

    [Fact]
    public void ResolveSafePath_rejects_dotdot_escape()
    {
        // The classic ../etc/passwd attack: must come back as an error,
        // not a resolved path that lives outside _root.
        var (resolved, error) = WorkspaceFileBrowser.ResolveSafePath(_root, "../wfb-outside-leak");
        resolved.Should().BeNull();
        error.Should().NotBeNull();
    }

    [Fact]
    public void ResolveSafePath_rejects_absolute_paths()
    {
        // No matter how clean the canonicalization gets, the API must
        // never accept a rooted path — that would let a caller jump
        // anywhere on disk by passing C:\ or /etc.
        var abs = OperatingSystem.IsWindows() ? @"C:\Windows" : "/etc";
        var (resolved, error) = WorkspaceFileBrowser.ResolveSafePath(_root, abs);
        resolved.Should().BeNull();
        error.Should().NotBeNull();
    }

    [Fact]
    public void ResolveSafePath_rejects_sibling_prefix_collisions()
    {
        // Bug class: /home/u/ws + GetFullPath("../ws-backup") collapses
        // to /home/u/ws-backup which startsWith "/home/u/ws" if the
        // separator isn't appended to the root. The guard MUST normalize
        // both sides with a trailing separator. Reproduce by creating a
        // sibling folder whose name shares the root's prefix.
        var siblingPrefix = _root + "-leak";
        Directory.CreateDirectory(siblingPrefix);
        try
        {
            var leakRelative = $"../{Path.GetFileName(siblingPrefix)}/secrets";
            var (resolved, error) = WorkspaceFileBrowser.ResolveSafePath(_root, leakRelative);
            resolved.Should().BeNull("sibling-prefix paths must be rejected");
            error.Should().NotBeNull();
        }
        finally
        {
            try { Directory.Delete(siblingPrefix, recursive: true); } catch { }
        }
    }

    [Fact]
    public void ResolveSafePath_follows_symlinks_and_rejects_external_targets()
    {
        // Best-effort: symlink creation needs developer mode on Windows,
        // skip when it fails at create time. On POSIX it just works.
        var linkPath = Path.Combine(_root, "leak-link");
        try { Directory.CreateSymbolicLink(linkPath, _outsideRoot); }
        catch { return; /* skip */ }

        var (resolved, error) = WorkspaceFileBrowser.ResolveSafePath(_root, "leak-link");
        resolved.Should().BeNull("the symlink resolves outside the workspace root");
        error.Should().NotBeNull();
    }

    [Fact]
    public void ListFolder_orders_directories_before_files_alphabetically()
    {
        var (listing, error) = WorkspaceFileBrowser.ListFolder(_root, null, showHidden: false);
        error.Should().BeNull();
        listing.Should().NotBeNull();
        listing!.Entries.Should().NotBeEmpty();
        listing.Entries.First().IsDir.Should().BeTrue("directories sort first");
        listing.Entries.First().Name.Should().Be("src");
    }

    [Fact]
    public void ListFolder_hides_default_blocked_folders_unless_showHidden_is_true()
    {
        Directory.CreateDirectory(Path.Combine(_root, "node_modules"));
        Directory.CreateDirectory(Path.Combine(_root, ".git"));

        var (hidden, _) = WorkspaceFileBrowser.ListFolder(_root, null, showHidden: false);
        hidden!.Entries.Should().NotContain(e => e.Name == "node_modules");
        hidden.Entries.Should().NotContain(e => e.Name == ".git");

        var (shown, _) = WorkspaceFileBrowser.ListFolder(_root, null, showHidden: true);
        shown!.Entries.Should().Contain(e => e.Name == "node_modules");
        shown.Entries.Should().Contain(e => e.Name == ".git");
    }

    [Fact]
    public void ReadFile_classifies_extensions_into_kinds()
    {
        var (md, _) = WorkspaceFileBrowser.ReadFile(_root, "README.md");
        md!.Kind.Should().Be("markdown");
        md.Content.Should().Contain("# hello");

        var (cs, _) = WorkspaceFileBrowser.ReadFile(_root, "src/Program.cs");
        cs!.Kind.Should().Be("code");
        cs.Language.Should().Be("csharp");
    }

    [Fact]
    public void ReadFile_marks_files_above_soft_threshold_as_truncated()
    {
        // Write a 1.5 MB file: above the 1 MB soft truncate, below the
        // 10 MB hard cap, so we expect kind=text+truncated=true.
        var bigPath = Path.Combine(_root, "big.txt");
        using (var w = new StreamWriter(bigPath))
        {
            for (int i = 0; i < 30_000; i++)
            {
                w.WriteLine(new string('x', 60));
            }
        }
        var (read, _) = WorkspaceFileBrowser.ReadFile(_root, "big.txt");
        read!.Truncated.Should().BeTrue();
        read.Kind.Should().Be("text");
        read.Content.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void IsBinary_detects_null_bytes_in_first_8KB()
    {
        var path = Path.Combine(_root, "weird.bin");
        var bytes = new byte[1024];
        bytes[42] = 0; // null byte = binary
        File.WriteAllBytes(path, bytes);
        WorkspaceFileBrowser.IsBinary(path).Should().BeTrue();
    }

    [Fact]
    public void IsBinary_returns_true_for_blocked_extensions_even_without_null_bytes()
    {
        // A .exe with no null bytes (e.g. an MZ header truncated to ASCII
        // zeros... unlikely but make the rule explicit). The extension
        // rule short-circuits the sniff.
        var path = Path.Combine(_root, "innocent.exe");
        File.WriteAllText(path, "hello world plain ascii");
        WorkspaceFileBrowser.IsBinary(path).Should().BeTrue();
    }
}
