using System.IO.Compression;
using FluentAssertions;

namespace Viamus.Doxie.Orchestrator.Agents.Tests;

public sealed class AgentTransferTests : IDisposable
{
    private readonly string _root;
    private readonly string _skillsRoot;
    private readonly string _agentsRoot;

    public AgentTransferTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"agent-transfer-{Guid.NewGuid():N}");
        // Mirror the on-disk convention: skills + agents are siblings
        // under the project's .claude/.
        _skillsRoot = Path.Combine(_root, "skills");
        _agentsRoot = Path.Combine(_root, "agents");
        Directory.CreateDirectory(_skillsRoot);
        Directory.CreateDirectory(_agentsRoot);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch { /* best effort */ }
    }

    [Fact]
    public void Export_writes_zip_with_single_top_folder_named_after_id()
    {
        WriteSkill("watcher", new()
        {
            ["SKILL.md"] = "---\nname: watcher\ndescription: w\n---\n# watcher\n",
            ["orchestrator.json"] = "{\"category\":\"Inspector\"}",
            ["check.md"] = "# `/watcher check` — poll once\n",
        });

        using var ms = new MemoryStream();
        AgentTransfer.ExportToZip(_skillsRoot, _agentsRoot, "watcher", ms);

        ms.Position = 0;
        using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
        var paths = zip.Entries.Select(e => e.FullName).OrderBy(p => p).ToList();
        paths.Should().BeEquivalentTo(new[]
        {
            "watcher/SKILL.md",
            "watcher/check.md",
            "watcher/orchestrator.json",
        });
    }

    [Fact]
    public void Export_includes_paired_subagent_file_at_zip_root()
    {
        WriteSkill("sample-watch", new()
        {
            ["SKILL.md"] = "---\nname: sample-watch\ndescription: w\n---\n",
            ["orchestrator.json"] = "{}",
        });
        File.WriteAllText(Path.Combine(_agentsRoot, "sample-watch.md"),
            "---\nname: sample-watch\ntools: Read\n---\n# sample-watch agent\n");

        using var ms = new MemoryStream();
        AgentTransfer.ExportToZip(_skillsRoot, _agentsRoot, "sample-watch", ms);

        ms.Position = 0;
        using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
        var paths = zip.Entries.Select(e => e.FullName).OrderBy(p => p).ToList();
        paths.Should().BeEquivalentTo(new[]
        {
            "sample-watch.md",
            "sample-watch/SKILL.md",
            "sample-watch/orchestrator.json",
        });
        // And the content actually round-trips.
        var sub = zip.GetEntry("sample-watch.md")!;
        using var sr = new StreamReader(sub.Open());
        sr.ReadToEnd().Should().Contain("# sample-watch agent");
    }

    [Fact]
    public void Export_skips_subagent_when_agentsRoot_is_null()
    {
        WriteSkill("solo", new()
        {
            ["SKILL.md"] = "x",
            ["orchestrator.json"] = "{}",
        });
        // Even if a subagent file happens to exist on disk, a null
        // agentsRoot means the caller explicitly opted out.
        File.WriteAllText(Path.Combine(_agentsRoot, "solo.md"), "shouldn't ship");

        using var ms = new MemoryStream();
        AgentTransfer.ExportToZip(_skillsRoot, agentsRoot: null, "solo", ms);

        ms.Position = 0;
        using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
        zip.GetEntry("solo.md").Should().BeNull();
    }

    [Fact]
    public void Export_throws_when_agent_directory_is_missing()
    {
        using var ms = new MemoryStream();
        var act = () => AgentTransfer.ExportToZip(_skillsRoot, _agentsRoot, "nope", ms);
        act.Should().Throw<DirectoryNotFoundException>();
    }

    [Fact]
    public void Export_rejects_invalid_id()
    {
        using var ms = new MemoryStream();
        var act = () => AgentTransfer.ExportToZip(_skillsRoot, _agentsRoot, "Bad Id!", ms);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Import_round_trips_an_exported_zip_with_subagent()
    {
        WriteSkill("watcher", new()
        {
            ["SKILL.md"] = "---\nname: watcher\ndescription: w\n---\n",
            ["orchestrator.json"] = "{\"category\":\"Inspector\"}",
            ["check.md"] = "# `/watcher check` — poll\n",
        });
        File.WriteAllText(Path.Combine(_agentsRoot, "watcher.md"), "subagent body");

        using var ms = new MemoryStream();
        AgentTransfer.ExportToZip(_skillsRoot, _agentsRoot, "watcher", ms);
        // Wipe both halves so we know import recreated them from the zip.
        Directory.Delete(Path.Combine(_skillsRoot, "watcher"), recursive: true);
        File.Delete(Path.Combine(_agentsRoot, "watcher.md"));

        ms.Position = 0;
        var result = AgentTransfer.ImportFromZip(ms, _skillsRoot, _agentsRoot);

        result.Ok.Should().BeTrue();
        result.AgentId.Should().Be("watcher");
        result.HasSubagent.Should().BeTrue();
        File.Exists(Path.Combine(_skillsRoot, "watcher", "manifest.json")).Should().BeTrue();
        File.Exists(Path.Combine(_skillsRoot, "watcher", "body.md")).Should().BeTrue();
        File.Exists(Path.Combine(_skillsRoot, "watcher", "SKILL.md")).Should().BeFalse();
        File.Exists(Path.Combine(_skillsRoot, "watcher", "orchestrator.json")).Should().BeFalse();
        File.Exists(Path.Combine(_skillsRoot, "watcher", "check.md")).Should().BeTrue();
        File.ReadAllText(Path.Combine(_skillsRoot, "watcher", "manifest.json")).Should().Contain("\"id\": \"watcher\"");
        File.ReadAllText(Path.Combine(_skillsRoot, "watcher", "manifest.json")).Should().Contain("\"category\": \"Inspector\"");
        File.ReadAllText(Path.Combine(_agentsRoot, "watcher.md")).Should().Be("subagent body");
    }

    [Fact]
    public void Import_round_trips_canonical_doxie_zip()
    {
        WriteSkill("canon", new()
        {
            ["manifest.json"] = "{\"id\":\"canon\",\"description\":\"canonical agent\",\"category\":\"Builder\"}",
            ["body.md"] = "# Canon\n\nUse the canonical layout.",
            ["run.md"] = "# `/canon run`",
        });
        File.WriteAllText(Path.Combine(_agentsRoot, "canon.md"), "subagent body");

        using var ms = new MemoryStream();
        AgentTransfer.ExportToZip(_skillsRoot, _agentsRoot, "canon", ms);
        Directory.Delete(Path.Combine(_skillsRoot, "canon"), recursive: true);
        File.Delete(Path.Combine(_agentsRoot, "canon.md"));

        ms.Position = 0;
        var result = AgentTransfer.ImportFromZip(ms, _skillsRoot, _agentsRoot);

        result.Ok.Should().BeTrue();
        File.ReadAllText(Path.Combine(_skillsRoot, "canon", "manifest.json")).Should().Contain("\"id\":\"canon\"");
        File.ReadAllText(Path.Combine(_skillsRoot, "canon", "body.md")).Should().Contain("canonical layout");
        File.Exists(Path.Combine(_skillsRoot, "canon", "run.md")).Should().BeTrue();
        File.ReadAllText(Path.Combine(_agentsRoot, "canon.md")).Should().Be("subagent body");
    }

    [Fact]
    public void Import_round_trips_skill_only_zip()
    {
        WriteSkill("solo", new()
        {
            ["SKILL.md"] = "x",
            ["orchestrator.json"] = "{}",
        });
        using var ms = new MemoryStream();
        AgentTransfer.ExportToZip(_skillsRoot, _agentsRoot, "solo", ms);
        Directory.Delete(Path.Combine(_skillsRoot, "solo"), recursive: true);

        ms.Position = 0;
        var result = AgentTransfer.ImportFromZip(ms, _skillsRoot, _agentsRoot);

        result.Ok.Should().BeTrue();
        result.HasSubagent.Should().BeFalse();
        File.Exists(Path.Combine(_skillsRoot, "solo", "manifest.json")).Should().BeTrue();
        File.Exists(Path.Combine(_skillsRoot, "solo", "body.md")).Should().BeTrue();
        File.Exists(Path.Combine(_agentsRoot, "solo.md")).Should().BeFalse();
    }

    [Fact]
    public void Import_creates_agentsRoot_directory_on_demand()
    {
        var freshAgentsRoot = Path.Combine(_root, "fresh-agents");
        // Pre-condition: the dir doesn't yet exist.
        Directory.Exists(freshAgentsRoot).Should().BeFalse();

        var zipPath = BuildZip(
            ("watcher/SKILL.md", "x"),
            ("watcher/orchestrator.json", "{}"),
            ("watcher.md", "subagent"));
        using var fs = File.OpenRead(zipPath);

        var result = AgentTransfer.ImportFromZip(fs, _skillsRoot, freshAgentsRoot);

        result.Ok.Should().BeTrue();
        File.Exists(Path.Combine(freshAgentsRoot, "watcher.md")).Should().BeTrue();
    }

    [Fact]
    public void Import_refuses_zip_without_top_level_folder()
    {
        // No top-level folder, only a stray file that isn't even an
        // <id>.md — should fail with MissingTopFolder.
        var zipPath = BuildZip(("notes.txt", "x"));
        using var fs = File.OpenRead(zipPath);

        var result = AgentTransfer.ImportFromZip(fs, _skillsRoot, _agentsRoot);

        result.Ok.Should().BeFalse();
        result.Error.Should().Be(AgentTransfer.ImportError.MissingTopFolder);
    }

    [Fact]
    public void Import_refuses_zip_with_only_subagent_md_at_root()
    {
        // <id>.md alone is not enough — we need the skill folder.
        var zipPath = BuildZip(("watcher.md", "subagent body"));
        using var fs = File.OpenRead(zipPath);

        var result = AgentTransfer.ImportFromZip(fs, _skillsRoot, _agentsRoot);

        result.Ok.Should().BeFalse();
        result.Error.Should().Be(AgentTransfer.ImportError.Empty);
    }

    [Fact]
    public void Import_refuses_subagent_id_mismatch_with_folder()
    {
        var zipPath = BuildZip(
            ("watcher/SKILL.md", "x"),
            ("watcher/orchestrator.json", "{}"),
            ("other.md", "wrong subagent"));
        using var fs = File.OpenRead(zipPath);

        var result = AgentTransfer.ImportFromZip(fs, _skillsRoot, _agentsRoot);

        result.Ok.Should().BeFalse();
        result.Error.Should().Be(AgentTransfer.ImportError.MissingTopFolder);
    }

    [Fact]
    public void Import_refuses_zip_with_multiple_top_level_folders()
    {
        var zipPath = BuildZip(("a/SKILL.md", "x"), ("b/SKILL.md", "x"));
        using var fs = File.OpenRead(zipPath);

        var result = AgentTransfer.ImportFromZip(fs, _skillsRoot, _agentsRoot);

        result.Ok.Should().BeFalse();
        result.Error.Should().Be(AgentTransfer.ImportError.MissingTopFolder);
    }

    [Fact]
    public void Import_refuses_top_folder_with_invalid_id()
    {
        var zipPath = BuildZip(("Bad Name/SKILL.md", "x"), ("Bad Name/orchestrator.json", "{}"));
        using var fs = File.OpenRead(zipPath);

        var result = AgentTransfer.ImportFromZip(fs, _skillsRoot, _agentsRoot);

        result.Ok.Should().BeFalse();
        result.Error.Should().Be(AgentTransfer.ImportError.InvalidId);
    }

    [Fact]
    public void Import_refuses_zip_missing_skill_md()
    {
        var zipPath = BuildZip(("watcher/orchestrator.json", "{}"));
        using var fs = File.OpenRead(zipPath);

        var result = AgentTransfer.ImportFromZip(fs, _skillsRoot, _agentsRoot);

        result.Ok.Should().BeFalse();
        result.Error.Should().Be(AgentTransfer.ImportError.MissingFile);
    }

    [Fact]
    public void Import_refuses_zip_missing_orchestrator_json()
    {
        var zipPath = BuildZip(("watcher/SKILL.md", "x"));
        using var fs = File.OpenRead(zipPath);

        var result = AgentTransfer.ImportFromZip(fs, _skillsRoot, _agentsRoot);

        result.Ok.Should().BeFalse();
        result.Error.Should().Be(AgentTransfer.ImportError.MissingFile);
    }

    [Fact]
    public void Import_refuses_invalid_json_sidecar()
    {
        var zipPath = BuildZip(
            ("watcher/SKILL.md", "x"),
            ("watcher/orchestrator.json", "{ this is not json"));
        using var fs = File.OpenRead(zipPath);

        var result = AgentTransfer.ImportFromZip(fs, _skillsRoot, _agentsRoot);

        result.Ok.Should().BeFalse();
        result.Error.Should().Be(AgentTransfer.ImportError.InvalidJson);
    }

    [Fact]
    public void Import_refuses_zip_slip_path()
    {
        var zipPath = BuildZip(
            ("watcher/SKILL.md", "x"),
            ("watcher/orchestrator.json", "{}"),
            ("watcher/../escape.md", "evil"));
        using var fs = File.OpenRead(zipPath);

        var result = AgentTransfer.ImportFromZip(fs, _skillsRoot, _agentsRoot);

        result.Ok.Should().BeFalse();
        result.Error.Should().Be(AgentTransfer.ImportError.UnsafePath);
        File.Exists(Path.Combine(_skillsRoot, "escape.md")).Should().BeFalse();
    }

    [Fact]
    public void Import_collides_when_skill_folder_exists_and_overwrite_is_false()
    {
        WriteSkill("watcher", new()
        {
            ["SKILL.md"] = "existing",
            ["orchestrator.json"] = "{}",
        });
        var zipPath = BuildZip(
            ("watcher/SKILL.md", "imported"),
            ("watcher/orchestrator.json", "{}"));
        using var fs = File.OpenRead(zipPath);

        var result = AgentTransfer.ImportFromZip(fs, _skillsRoot, _agentsRoot, overwrite: false);

        result.Ok.Should().BeFalse();
        result.Error.Should().Be(AgentTransfer.ImportError.Collision);
        // Existing untouched.
        File.ReadAllText(Path.Combine(_skillsRoot, "watcher", "SKILL.md")).Should().Be("existing");
    }

    [Fact]
    public void Import_collides_when_only_subagent_file_exists_and_overwrite_is_false()
    {
        // Skill folder is brand new (no collision there) but a stale
        // subagent file from a previous import is still in the agents
        // dir — must still trigger the collision branch so the user
        // confirms.
        File.WriteAllText(Path.Combine(_agentsRoot, "watcher.md"), "stale");
        var zipPath = BuildZip(
            ("watcher/SKILL.md", "x"),
            ("watcher/orchestrator.json", "{}"),
            ("watcher.md", "fresh"));
        using var fs = File.OpenRead(zipPath);

        var result = AgentTransfer.ImportFromZip(fs, _skillsRoot, _agentsRoot, overwrite: false);

        result.Ok.Should().BeFalse();
        result.Error.Should().Be(AgentTransfer.ImportError.Collision);
        File.ReadAllText(Path.Combine(_agentsRoot, "watcher.md")).Should().Be("stale");
        Directory.Exists(Path.Combine(_skillsRoot, "watcher")).Should().BeFalse();
    }

    [Fact]
    public void Import_overwrites_both_halves_when_explicitly_requested()
    {
        WriteSkill("watcher", new()
        {
            ["SKILL.md"] = "existing",
            ["orchestrator.json"] = "{}",
            ["stale-extra.md"] = "should disappear",
        });
        File.WriteAllText(Path.Combine(_agentsRoot, "watcher.md"), "stale subagent");
        var zipPath = BuildZip(
            ("watcher/SKILL.md", "imported"),
            ("watcher/orchestrator.json", "{}"),
            ("watcher.md", "fresh subagent"));
        using var fs = File.OpenRead(zipPath);

        var result = AgentTransfer.ImportFromZip(fs, _skillsRoot, _agentsRoot, overwrite: true);

        result.Ok.Should().BeTrue();
        result.HasSubagent.Should().BeTrue();
        File.ReadAllText(Path.Combine(_skillsRoot, "watcher", "body.md")).Should().Be("imported");
        File.Exists(Path.Combine(_skillsRoot, "watcher", "stale-extra.md")).Should().BeFalse();
        File.ReadAllText(Path.Combine(_agentsRoot, "watcher.md")).Should().Be("fresh subagent");
    }

    [Fact]
    public void Import_refuses_a_non_zip_payload()
    {
        using var ms = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("not a zip"));
        var result = AgentTransfer.ImportFromZip(ms, _skillsRoot, _agentsRoot);
        result.Ok.Should().BeFalse();
        result.Error.Should().Be(AgentTransfer.ImportError.NotAZip);
    }

    [Fact]
    public void Import_fails_when_zip_carries_subagent_but_agentsRoot_is_null()
    {
        var zipPath = BuildZip(
            ("watcher/SKILL.md", "x"),
            ("watcher/orchestrator.json", "{}"),
            ("watcher.md", "subagent"));
        using var fs = File.OpenRead(zipPath);

        var result = AgentTransfer.ImportFromZip(fs, _skillsRoot, agentsRoot: null);

        result.Ok.Should().BeFalse();
        result.Error.Should().Be(AgentTransfer.ImportError.MissingFile);
    }

    private void WriteSkill(string id, Dictionary<string, string> files)
    {
        var dir = Path.Combine(_skillsRoot, id);
        Directory.CreateDirectory(dir);
        foreach (var (rel, content) in files)
        {
            File.WriteAllText(Path.Combine(dir, rel), content);
        }
    }

    private string BuildZip(params (string Path, string Content)[] entries)
    {
        var zipPath = Path.Combine(_root, $"in-{Guid.NewGuid():N}.zip");
        using var fs = File.Create(zipPath);
        using var archive = new ZipArchive(fs, ZipArchiveMode.Create);
        foreach (var (path, content) in entries)
        {
            var entry = archive.CreateEntry(path);
            using var es = entry.Open();
            using var sw = new StreamWriter(es);
            sw.Write(content);
        }
        return zipPath;
    }
}
