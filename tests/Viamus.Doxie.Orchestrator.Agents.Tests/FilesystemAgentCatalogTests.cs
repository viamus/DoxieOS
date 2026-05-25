using FluentAssertions;

namespace Viamus.Doxie.Orchestrator.Agents.Tests;

public sealed class FilesystemAgentCatalogTests : IDisposable
{
    private readonly string _root;

    public FilesystemAgentCatalogTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"orchestrator-fs-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
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
    public void GetAll_returns_empty_when_skills_directory_is_missing()
    {
        var catalog = new FilesystemAgentCatalog(Path.Combine(_root, "does-not-exist"));

        catalog.GetAll().Should().BeEmpty();
    }

    [Fact]
    public void Skills_without_orchestrator_sidecar_are_excluded()
    {
        WriteSkill("internal-helper", description: "an internal skill", subcommands: new[] { ("run", "do thing") });
        // No orchestrator.json — skill must be filtered out.

        var catalog = new FilesystemAgentCatalog(_root);

        catalog.GetAll().Should().BeEmpty();
    }

    [Fact]
    public void Skills_with_orchestrator_sidecar_are_included()
    {
        WriteSkill("watcher", description: "watches things", subcommands: new[] { ("check", "poll once") });
        WriteSidecar("watcher", category: "Inspector");

        var agents = new FilesystemAgentCatalog(_root).GetAll();

        agents.Should().ContainSingle();
        agents[0].Id.Should().Be("watcher");
        agents[0].Description.Should().Be("watches things");
        agents[0].Category.Should().Be(AgentCategory.Inspector);
    }

    [Fact]
    public void Modes_are_discovered_from_subcommand_files_with_matching_H1()
    {
        WriteSkill("watcher", "", new[]
        {
            ("check", "poll once"),
            ("list", "show watched items"),
        });
        WriteSidecar("watcher");

        var agent = new FilesystemAgentCatalog(_root).FindById("watcher")!;

        agent.Modes.Should().NotBeNull();
        agent.Modes!.Select(m => m.Id).Should().BeEquivalentTo(new[] { "check", "list" });
        agent.Modes.Single(m => m.Id == "check").Description.Should().Be("poll once");
    }

    [Fact]
    public void Files_whose_H1_does_not_match_skill_subcommand_pattern_are_ignored()
    {
        // Write a skill with one valid subcommand and a noise file.
        Directory.CreateDirectory(Path.Combine(_root, "watcher"));
        File.WriteAllText(Path.Combine(_root, "watcher", "SKILL.md"),
            "---\nname: watcher\ndescription: w\n---\n# Watcher\n");
        File.WriteAllText(Path.Combine(_root, "watcher", "check.md"),
            "# `/watcher check` — poll once\n\nbody\n");
        // Reference doc — not a subcommand.
        File.WriteAllText(Path.Combine(_root, "watcher", "state-schema.md"),
            "# State schema reference\n\nnot a subcommand\n");
        WriteSidecar("watcher");

        var agent = new FilesystemAgentCatalog(_root).FindById("watcher")!;

        agent.Modes!.Select(m => m.Id).Should().Equal("check");
    }

    [Fact]
    public void Sidecar_field_metadata_is_attached_to_corresponding_mode()
    {
        WriteSkill("watcher", "", new[]
        {
            ("add", "add a thing"),
            ("check", "poll"),
        });
        WriteSidecarWithFields("watcher", modeFields: new()
        {
            ["add"] = new[]
            {
                new AgentModeField("url", "URL", Placeholder: "https://...", Required: true),
            },
        });

        var agent = new FilesystemAgentCatalog(_root).FindById("watcher")!;

        var addMode = agent.Modes!.Single(m => m.Id == "add");
        addMode.Fields.Should().NotBeNull();
        addMode.Fields!.Should().ContainSingle();
        addMode.Fields[0].Id.Should().Be("url");
        addMode.Fields[0].Label.Should().Be("URL");
        addMode.Fields[0].Placeholder.Should().Be("https://...");
        addMode.ArgumentsTemplate.Should().Be("add {url}");

        var checkMode = agent.Modes!.Single(m => m.Id == "check");
        checkMode.Fields.Should().BeNull();
        checkMode.ArgumentsTemplate.Should().Be("check");
    }

    [Fact]
    public void Skill_name_falls_back_to_directory_when_frontmatter_is_missing()
    {
        var dir = Path.Combine(_root, "no-frontmatter");
        Directory.CreateDirectory(dir);
        // SKILL.md without YAML frontmatter
        File.WriteAllText(Path.Combine(dir, "SKILL.md"), "# No frontmatter\n");
        File.WriteAllText(Path.Combine(dir, "orchestrator.json"), "{}");

        var agent = new FilesystemAgentCatalog(_root).FindById("no-frontmatter");

        agent.Should().NotBeNull();
        agent!.SkillName.Should().Be("no-frontmatter");
        agent.Name.Should().Be("No Frontmatter"); // pretty-printed
    }

    [Fact]
    public void Mode_marked_hidden_in_sidecar_is_excluded_from_catalog()
    {
        WriteSkill("sample-watch", "", new[]
        {
            ("add", "register"),
            ("check", "poll once"),
        });
        File.WriteAllText(
            Path.Combine(_root, "sample-watch", "orchestrator.json"),
            """{"modes":{"check":{"hidden":true}}}""");

        var modes = new FilesystemAgentCatalog(_root).FindById("sample-watch")!.Modes!;

        modes.Select(m => m.Id).Should().Equal("add");
    }

    [Fact]
    public void Synthetic_mode_in_sidecar_with_no_matching_md_is_added_to_catalog()
    {
        WriteSkill("sample-watch", "", new[] { ("add", "register") });
        File.WriteAllText(
            Path.Combine(_root, "sample-watch", "orchestrator.json"),
            """{"modes":{"run":{"argumentsTemplate":"/loop 5m /sample-watch check","keepSessionAlive":true,"description":"Start the watcher loop"}}}""");

        var modes = new FilesystemAgentCatalog(_root).FindById("sample-watch")!.Modes!;

        modes.Select(m => m.Id).Should().BeEquivalentTo(new[] { "add", "run" });
        var run = modes.Single(m => m.Id == "run");
        run.ArgumentsTemplate.Should().Be("/loop 5m /sample-watch check");
        run.KeepSessionAlive.Should().BeTrue();
        run.Description.Should().Be("Start the watcher loop");
    }

    [Fact]
    public void Synthetic_mode_without_argumentsTemplate_is_ignored()
    {
        WriteSkill("sample-watch", "", new[] { ("add", "register") });
        // "run" mode has no argumentsTemplate — incomplete, should be skipped
        File.WriteAllText(
            Path.Combine(_root, "sample-watch", "orchestrator.json"),
            """{"modes":{"run":{"description":"only description"}}}""");

        var modes = new FilesystemAgentCatalog(_root).FindById("sample-watch")!.Modes!;

        modes.Select(m => m.Id).Should().Equal("add");
    }

    [Fact]
    public void Sidecar_argumentsTemplate_override_replaces_default_template()
    {
        WriteSkill("sample-watch", "", new[] { ("add", "register a change") });
        File.WriteAllText(
            Path.Combine(_root, "sample-watch", "orchestrator.json"),
            """{"modes":{"add":{"fields":[{"id":"url","label":"URL"}],"argumentsTemplate":"add {url} --no-loop"}}}""");

        var addMode = new FilesystemAgentCatalog(_root).FindById("sample-watch")!.Modes!.Single();

        addMode.ArgumentsTemplate.Should().Be("add {url} --no-loop");
    }

    [Fact]
    public void Display_name_falls_back_to_title_cased_kebab()
    {
        WriteSkill("security-review", "", new[] { ("scan", "scan changes") });
        WriteSidecar("security-review");

        var agent = new FilesystemAgentCatalog(_root).FindById("security-review")!;

        agent.Name.Should().Be("Security Review");
    }

    // FileSystemWatcher on GitHub Actions ubuntu-latest reliably picks up
    // edits to existing files (see Watch_mode_picks_up_a_field_added_to_orchestrator_json
    // below) but does not deliver creation events for *new subdirectories*
    // under /tmp in any reasonable time (>30s observed). The Windows
    // ReadDirectoryChangesW path delivers them in <300ms, which is what
    // the orchestrator runs against in production. Treat the new-skill
    // detection as Windows-tested for now; the underlying Linux watcher
    // behaviour for new subdirs is a follow-up.
    [WindowsFact]
    public async Task Watch_mode_picks_up_a_new_skill_without_restart()
    {
        using var catalog = new FilesystemAgentCatalog(_root, watch: true);
        catalog.GetAll().Should().BeEmpty();

        WriteSkill("late-arrival", "added at runtime", new[] { ("run", "do thing") });
        WriteSidecar("late-arrival", category: "Inspector");

        // Watcher debounce is ~300ms; allow very generous slack for CI hosts.
        // GitHub Actions ubuntu-latest's inotify is noticeably slower than
        // Windows ReadDirectoryChangesW for new-subdirectory events — 15s
        // wasn't enough there.
        await WaitUntil(() => catalog.GetAll().Any(a => a.Id == "late-arrival"),
            timeout: TimeSpan.FromSeconds(30));

        catalog.GetAll().Should().ContainSingle(a => a.Id == "late-arrival");
    }

    [Fact]
    public async Task Watch_mode_picks_up_a_field_added_to_orchestrator_json()
    {
        WriteSkill("editable", "", new[] { ("add", "register") });
        File.WriteAllText(Path.Combine(_root, "editable", "orchestrator.json"), "{}");

        using var catalog = new FilesystemAgentCatalog(_root, watch: true);
        catalog.FindById("editable")!.Modes!.Single().Fields.Should().BeNull();

        // Simulate a hand-edit: append a field to the sidecar.
        File.WriteAllText(
            Path.Combine(_root, "editable", "orchestrator.json"),
            """{"modes":{"add":{"fields":[{"id":"url","label":"URL"}]}}}""");

        await WaitUntil(() => catalog.FindById("editable")?.Modes?.Single().Fields is { Count: > 0 },
            timeout: TimeSpan.FromSeconds(30));

        var field = catalog.FindById("editable")!.Modes!.Single().Fields!.Single();
        field.Id.Should().Be("url");
        field.Label.Should().Be("URL");
    }

    private static async Task WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(50);
        }
        throw new TimeoutException(
            $"WaitUntil condition was not satisfied within {timeout.TotalSeconds:0.#}s");
    }

    [Fact]
    public void ReadSkillBody_returns_the_SKILL_md_content_for_a_known_skill()
    {
        WriteSkill("library", description: "the library skill", subcommands: new[] { ("run", "go") });
        WriteSidecar("library");

        var body = new FilesystemAgentCatalog(_root).ReadSkillBody("library");

        body.Should().NotBeNull();
        body.Should().Contain("name: library");
        body.Should().Contain("description: the library skill");
    }

    [Fact]
    public void ReadSkillBody_returns_null_for_an_unknown_skill()
    {
        new FilesystemAgentCatalog(_root).ReadSkillBody("does-not-exist").Should().BeNull();
    }

    [Fact]
    public void ReadSkillBody_returns_null_when_skillName_is_empty_or_whitespace()
    {
        var catalog = new FilesystemAgentCatalog(_root);
        catalog.ReadSkillBody("").Should().BeNull();
        catalog.ReadSkillBody("   ").Should().BeNull();
    }

    [Fact]
    public void Sidecar_displayName_overrides_the_default()
    {
        WriteSkill("sample-watch", "", new[] { ("check", "poll") });
        File.WriteAllText(
            Path.Combine(_root, "sample-watch", "orchestrator.json"),
            """{"displayName": "Sample Watch"}""");

        var agent = new FilesystemAgentCatalog(_root).FindById("sample-watch")!;

        agent.Name.Should().Be("Sample Watch");
    }

    private void WriteSkill(string name, string description, IEnumerable<(string Sub, string Desc)> subcommands)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(
            Path.Combine(dir, "SKILL.md"),
            $"---\nname: {name}\ndescription: {description}\n---\n# {name}\n");
        foreach (var (sub, desc) in subcommands)
        {
            File.WriteAllText(
                Path.Combine(dir, $"{sub}.md"),
                $"# `/{name} {sub}` — {desc}\n\nbody\n");
        }
    }

    private void WriteSidecar(string skillName, string? category = null)
    {
        var dir = Path.Combine(_root, skillName);
        Directory.CreateDirectory(dir);
        var json = category is null
            ? "{}"
            : $"{{\"category\": \"{category}\"}}";
        File.WriteAllText(Path.Combine(dir, "orchestrator.json"), json);
    }

    private void WriteSidecarWithFields(string skillName, Dictionary<string, AgentModeField[]> modeFields)
    {
        var dir = Path.Combine(_root, skillName);
        Directory.CreateDirectory(dir);
        var modesJson = string.Join(",", modeFields.Select(kv =>
        {
            var fieldsJson = string.Join(",", kv.Value.Select(f =>
                $"{{\"id\":\"{f.Id}\",\"label\":\"{f.Label}\",\"placeholder\":\"{f.Placeholder}\",\"required\":{(f.Required ? "true" : "false")}}}"));
            return $"\"{kv.Key}\":{{\"fields\":[{fieldsJson}]}}";
        }));
        var json = $"{{\"modes\":{{{modesJson}}}}}";
        File.WriteAllText(Path.Combine(dir, "orchestrator.json"), json);
    }
}
