using System.Text;
using FluentAssertions;
using Viamus.Doxie.Orchestrator.Agents.Doxie;
using Viamus.Doxie.Orchestrator.Common;

namespace Viamus.Doxie.Orchestrator.Agents.Tests;

public sealed class DoxiePipelineTests : IDisposable
{
    private readonly string _root;

    public DoxiePipelineTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"doxie-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { /* best effort */ }
    }

    // ---------- Reader ----------

    [Fact]
    public void Reader_returns_empty_when_doxie_dir_is_missing()
    {
        new DoxieSkillReader(Path.Combine(_root, "does-not-exist"))
            .LoadAll()
            .Should().BeEmpty();
    }

    [Fact]
    public void Reader_loads_skill_with_manifest_and_body()
    {
        WriteCanon("jarvis", description: "Read voice transcription from Jarvis daemon.", body: "# Jarvis\n\nDo the thing.\n");

        var skills = new DoxieSkillReader(Path.Combine(_root, ".doxie")).LoadAll();

        skills.Should().HaveCount(1);
        skills[0].Manifest.Id.Should().Be("jarvis");
        skills[0].Manifest.Description.Should().StartWith("Read voice");
        skills[0].BodyMarkdown.Should().Contain("Do the thing");
    }

    [Fact]
    public void Reader_loads_skills_from_custom_catalog()
    {
        WriteCanon("shared-helper", description: "shared", body: "# Shared\n", catalogRoot: "shared-team");

        var skills = new DoxieSkillReader(Path.Combine(_root, ".doxie"), CatalogRoots).LoadAll();

        skills.Should().ContainSingle();
        skills[0].Manifest.Id.Should().Be("shared-helper");
        skills[0].IsPrivate.Should().BeFalse();
        skills[0].CatalogId.Should().Be("shared-team");
    }

    [Fact]
    public void Reader_prefers_custom_catalog_body_when_ids_collide()
    {
        WriteCanon("same-id", description: "public", body: "public body");
        WriteCanon("same-id", description: "custom", body: "custom body", catalogRoot: "shared-team");

        var reader = new DoxieSkillReader(Path.Combine(_root, ".doxie"), CatalogRoots);

        reader.LoadAll().Should().ContainSingle(s => s.Manifest.Id == "same-id" && s.CatalogId == "shared-team" && !s.IsPrivate);
        reader.TryLoadBody("same-id").Should().Be("custom body");
    }

    [Fact]
    public void Reader_throws_when_manifest_id_does_not_match_folder_name()
    {
        WriteCanon("right-name", id: "wrong-name", description: "x", body: "x");

        var act = () => new DoxieSkillReader(Path.Combine(_root, ".doxie")).LoadAll();

        act.Should().Throw<InvalidDataException>().WithMessage("*does not match folder name*");
    }

    [Fact]
    public void Reader_skips_folders_missing_manifest_or_body()
    {
        var partial = Path.Combine(_root, ".doxie", "skills", "draft");
        Directory.CreateDirectory(partial);
        File.WriteAllText(Path.Combine(partial, "body.md"), "wip");
        // No manifest.json — partial draft.

        new DoxieSkillReader(Path.Combine(_root, ".doxie"))
            .LoadAll()
            .Should().BeEmpty();
    }

    [Fact]
    public void Reader_loads_companion_files_recursively()
    {
        WriteCanon("sample-watch", description: "watches", body: "# router");
        var skillDir = Path.Combine(_root, ".doxie", "skills", "sample-watch");
        File.WriteAllText(Path.Combine(skillDir, "check.md"), "# /sample-watch check");
        Directory.CreateDirectory(Path.Combine(skillDir, "scripts"));
        File.WriteAllText(Path.Combine(skillDir, "scripts", "helper.csx"), "Console.WriteLine();");

        var skill = new DoxieSkillReader(Path.Combine(_root, ".doxie")).LoadAll().Single();

        skill.CompanionFiles.Keys.Should().BeEquivalentTo(new[] { "check.md", "scripts/helper.csx" });
    }

    // ---------- Claude shim emitter ----------

    [Fact]
    public void ClaudeEmitter_writes_skill_md_with_frontmatter()
    {
        var canon = MakeCanon("jarvis", "Read voice transcription.", "# Jarvis body\n");

        new ClaudeShimEmitter().Emit(SkillsOnly(canon), _root);

        var skillMd = File.ReadAllText(Path.Combine(_root, ".claude", "skills", "jarvis", "SKILL.md"));
        skillMd.Should().StartWith("---\nname: jarvis\ndescription: Read voice transcription.\n---\n\n");
        skillMd.Should().Contain("# Jarvis body");
    }

    [Fact]
    public void ClaudeEmitter_skips_orchestrator_json_when_manifest_has_no_sidecar_fields()
    {
        var canon = MakeCanon("plain", "no sidecar.", "body");

        new ClaudeShimEmitter().Emit(SkillsOnly(canon), _root);

        File.Exists(Path.Combine(_root, ".claude", "skills", "plain", "orchestrator.json"))
            .Should().BeFalse();
    }

    [Fact]
    public void ClaudeEmitter_writes_orchestrator_json_when_manifest_has_modes()
    {
        var canon = new DoxieSkillCanon(
            new DoxieSkillManifest
            {
                Id = "watcher",
                Description = "watches",
                DisplayName = "Watcher",
                Category = nameof(AgentCategory.Inspector),
                Modes = new() { ["check"] = new DoxieMode { Description = "poll once" } },
            },
            BodyMarkdown: "body",
            CompanionFiles: new Dictionary<string, byte[]>());

        new ClaudeShimEmitter().Emit(SkillsOnly(canon), _root);

        var sidecar = File.ReadAllText(Path.Combine(_root, ".claude", "skills", "watcher", "orchestrator.json"));
        sidecar.Should().Contain("\"displayName\": \"Watcher\"");
        sidecar.Should().Contain("\"category\": \"Inspector\"");
        sidecar.Should().Contain("\"check\"");
    }

    [Fact]
    public void ClaudeEmitter_copies_companion_files_verbatim()
    {
        var canon = MakeCanon("sample-watch", "watches", "body", new()
        {
            ["check.md"] = Encoding.UTF8.GetBytes("# /sample-watch check\n"),
            ["scripts/helper.csx"] = Encoding.UTF8.GetBytes("// helper"),
        });

        new ClaudeShimEmitter().Emit(SkillsOnly(canon), _root);

        File.ReadAllText(Path.Combine(_root, ".claude", "skills", "sample-watch", "check.md"))
            .Should().Be("# /sample-watch check\n");
        File.ReadAllText(Path.Combine(_root, ".claude", "skills", "sample-watch", "scripts", "helper.csx"))
            .Should().Be("// helper");
    }

    [Fact]
    public void ClaudeEmitter_is_idempotent_byte_for_byte()
    {
        var canon = MakeCanon("jarvis", "Voice.", "# body\n", new()
        {
            ["aux.md"] = Encoding.UTF8.GetBytes("aux"),
        });

        var emitter = new ClaudeShimEmitter();
        emitter.Emit(SkillsOnly(canon), _root);
        var firstSnapshot = SnapshotShim(_root);

        emitter.Emit(SkillsOnly(canon), _root);
        var secondSnapshot = SnapshotShim(_root);

        secondSnapshot.Should().BeEquivalentTo(firstSnapshot);
    }

    [Fact]
    public void ClaudeEmitter_does_not_rewrite_unchanged_files()
    {
        var canon = MakeCanon("jarvis", "Voice.", "# body\n");
        var emitter = new ClaudeShimEmitter();

        emitter.Emit(SkillsOnly(canon), _root);
        var skillMdPath = Path.Combine(_root, ".claude", "skills", "jarvis", "SKILL.md");
        var firstWrite = File.GetLastWriteTimeUtc(skillMdPath);

        Thread.Sleep(20);
        emitter.Emit(SkillsOnly(canon), _root);
        var secondWrite = File.GetLastWriteTimeUtc(skillMdPath);

        secondWrite.Should().Be(firstWrite, "content-aware writes must skip unchanged files");
    }

    [Fact]
    public void ClaudeEmitter_removes_orphan_companion_files()
    {
        var withAux = MakeCanon("jarvis", "v.", "body\n", new()
        {
            ["aux.md"] = Encoding.UTF8.GetBytes("aux"),
        });
        var emitter = new ClaudeShimEmitter();
        emitter.Emit(SkillsOnly(withAux), _root);

        var withoutAux = MakeCanon("jarvis", "v.", "body\n");
        emitter.Emit(SkillsOnly(withoutAux), _root);

        File.Exists(Path.Combine(_root, ".claude", "skills", "jarvis", "aux.md"))
            .Should().BeFalse();
    }

    [Fact]
    public void ClaudeEmitter_removes_managed_skill_folder_when_canon_disappears()
    {
        var emitter = new ClaudeShimEmitter();
        emitter.Emit(SkillsOnly(MakeCanon("foo", "f.", "body")), _root);
        Directory.Exists(Path.Combine(_root, ".claude", "skills", "foo")).Should().BeTrue();

        emitter.Emit(DoxieRegistry.Empty, _root);

        Directory.Exists(Path.Combine(_root, ".claude", "skills", "foo")).Should().BeFalse();
    }

    [Fact]
    public void ClaudeEmitter_does_not_touch_unmanaged_skill_folders()
    {
        var hand = Path.Combine(_root, ".claude", "skills", "user-authored");
        Directory.CreateDirectory(hand);
        File.WriteAllText(Path.Combine(hand, "SKILL.md"), "manual");

        new ClaudeShimEmitter().Emit(SkillsOnly(MakeCanon("doxie-skill", "x.", "y")), _root);

        File.Exists(Path.Combine(hand, "SKILL.md")).Should().BeTrue("orphan cleanup must respect non-doxie folders");
    }

    [Fact]
    public void ClaudeEmitter_flattens_multiline_description_to_single_line()
    {
        var canon = MakeCanon("jarvis", "line one\nline two\nline three", "body");

        new ClaudeShimEmitter().Emit(SkillsOnly(canon), _root);

        var skillMd = File.ReadAllText(Path.Combine(_root, ".claude", "skills", "jarvis", "SKILL.md"));
        skillMd.Should().Contain("description: line one line two line three\n");
    }

    // ---------- Codex shim emitter ----------

    [Fact]
    public void CodexEmitter_writes_single_AGENTS_md_with_all_skills()
    {
        var skills = new[]
        {
            MakeCanon("alpha", "first skill", "body"),
            MakeCanon("beta", "second skill", "body"),
        };

        new CodexShimEmitter().Emit(new DoxieRegistry(skills, Array.Empty<DoxieAgentCanon>(), Array.Empty<DoxieLibraryCanon>(), Array.Empty<DoxieWorkflowCanon>()), _root);

        var path = Path.Combine(_root, ".codex", "AGENTS.md");
        File.Exists(path).Should().BeTrue();
        var content = File.ReadAllText(path);
        File.Exists(Path.Combine(_root, "AGENTS.md")).Should().BeTrue();
        content.Should().Contain("## /alpha");
        content.Should().Contain("first skill");
        content.Should().Contain("## /beta");
        content.Should().Contain("doxie/generated");
        content.Should().Contain("## Doxie resolution rules");
        content.Should().Contain("before treating them as shell commands");
    }

    [Fact]
    public void CodexEmitter_does_not_overwrite_user_root_AGENTS_md()
    {
        File.WriteAllText(Path.Combine(_root, "AGENTS.md"), "# Human rules\n");

        new CodexShimEmitter().Emit(SkillsOnly(MakeCanon("alpha", "first skill", "body")), _root);

        File.ReadAllText(Path.Combine(_root, "AGENTS.md")).Should().Be("# Human rules\n");
        File.ReadAllText(Path.Combine(_root, ".codex", "AGENTS.md")).Should().Contain("## /alpha");
    }

    [Fact]
    public void CodexEmitter_includes_subcommands_and_requirements()
    {
        var canon = new DoxieSkillCanon(
            new DoxieSkillManifest
            {
                Id = "watcher",
                Description = "watches",
                Modes = new()
                {
                    ["check"] = new DoxieMode { Description = "poll once" },
                    ["list"] = new DoxieMode { Description = "show state" },
                },
                Requirements = new()
                {
                    new DoxieRequirement { Kind = AgentRequirementKind.Mcp, Name = "mcp-example", Required = true },
                    new DoxieRequirement { Kind = AgentRequirementKind.Env, Name = "QUALITY_TOKEN", Required = false },
                },
            },
            BodyMarkdown: "body",
            CompanionFiles: new Dictionary<string, byte[]>());

        new CodexShimEmitter().Emit(SkillsOnly(canon), _root);

        var content = File.ReadAllText(Path.Combine(_root, ".codex", "AGENTS.md"));
        content.Should().Contain("`check` — poll once");
        content.Should().Contain("`list` — show state");
        content.Should().Contain("**Requires:** mcp-example, QUALITY_TOKEN (optional)");
    }

    [Fact]
    public void CodexEmitter_handles_empty_skill_set()
    {
        new CodexShimEmitter().Emit(DoxieRegistry.Empty, _root);

        var content = File.ReadAllText(Path.Combine(_root, ".codex", "AGENTS.md"));
        content.Should().Contain("Nothing authored yet.");
    }

    [Fact]
    public void CodexEmitter_is_idempotent()
    {
        var canon = MakeCanon("jarvis", "voice", "body");
        var emitter = new CodexShimEmitter();

        emitter.Emit(SkillsOnly(canon), _root);
        var first = File.ReadAllBytes(Path.Combine(_root, ".codex", "AGENTS.md"));

        Thread.Sleep(20);
        emitter.Emit(SkillsOnly(canon), _root);
        var second = File.ReadAllBytes(Path.Combine(_root, ".codex", "AGENTS.md"));

        second.Should().Equal(first);
    }

    [Fact]
    public void CodexEmitter_emits_workflows_section()
    {
        var registry = new DoxieRegistry(
            Array.Empty<DoxieSkillCanon>(),
            Array.Empty<DoxieAgentCanon>(),
            Array.Empty<DoxieLibraryCanon>(),
            new[]
            {
                new DoxieWorkflowCanon(
                    Id: "sample-product-daily-brief",
                    Name: "Sample Product Daily Brief",
                    Description: "Fan out to research + backlog-review every morning.",
                    TriggerKind: "cron",
                    CronExpression: "0 8 * * *",
                    Enabled: true),
                new DoxieWorkflowCanon(
                    Id: "manual-thing",
                    Name: "manual-thing",
                    Description: "",
                    TriggerKind: "manual",
                    CronExpression: null,
                    Enabled: false),
            });

        new CodexShimEmitter().Emit(registry, _root);

        var content = File.ReadAllText(Path.Combine(_root, ".codex", "AGENTS.md"));
        content.Should().Contain("## Workflows");
        content.Should().Contain("### sample-product-daily-brief");
        content.Should().Contain("**Name:** Sample Product Daily Brief");
        content.Should().Contain("**Trigger:** cron `0 8 * * *`");
        content.Should().Contain("### manual-thing _(disabled)_");
        content.Should().Contain("**Trigger:** manual");
    }

    [Fact]
    public void CodexEmitter_skips_hidden_modes()
    {
        var canon = new DoxieSkillCanon(
            new DoxieSkillManifest
            {
                Id = "mixed",
                Description = "x",
                Modes = new()
                {
                    ["public"] = new DoxieMode { Description = "shown" },
                    ["secret"] = new DoxieMode { Description = "hidden", Hidden = true },
                },
            },
            BodyMarkdown: "x",
            CompanionFiles: new Dictionary<string, byte[]>());

        new CodexShimEmitter().Emit(SkillsOnly(canon), _root);

        var content = File.ReadAllText(Path.Combine(_root, ".codex", "AGENTS.md"));
        content.Should().Contain("`public`");
        content.Should().NotContain("`secret`");
    }

    // ---------- Regenerator ----------

    [Fact]
    public void Regenerator_runs_every_emitter_in_order()
    {
        WriteCanon("foo", "a skill", "# body");

        var regenerator = new DoxieRegenerator(_root, new IShimEmitter[]
        {
            new ClaudeShimEmitter(),
            new CodexShimEmitter(),
        });

        var result = regenerator.Regenerate();

        result.SkillCount.Should().Be(1);
        result.EmitterCount.Should().Be(2);
        File.Exists(Path.Combine(_root, ".claude", "skills", "foo", "SKILL.md")).Should().BeTrue();
        File.Exists(Path.Combine(_root, ".codex", "AGENTS.md")).Should().BeTrue();
    }

    // ---------- Agent pipeline ----------

    [Fact]
    public void AgentReader_loads_md_with_frontmatter()
    {
        WriteAgent("explorer", "Search-only agent for locating code.", systemPrompt: "Be terse.");

        var agents = new DoxieAgentReader(Path.Combine(_root, ".doxie")).LoadAll();

        agents.Should().HaveCount(1);
        agents[0].Manifest.Id.Should().Be("explorer");
        agents[0].Manifest.Description.Should().StartWith("Search-only");
        agents[0].SystemPrompt.Should().Be("Be terse.\n");
    }

    [Fact]
    public void AgentReader_loads_agents_from_custom_catalog()
    {
        WriteAgent("shared-explorer", "Shared search-only agent.", systemPrompt: "Be terse.", catalogRoot: "shared-team");

        var agents = new DoxieAgentReader(Path.Combine(_root, ".doxie"), CatalogRoots).LoadAll();

        agents.Should().ContainSingle();
        agents[0].Manifest.Id.Should().Be("shared-explorer");
        agents[0].SystemPrompt.Should().Be("Be terse.\n");
    }

    [Fact]
    public void AgentReader_throws_when_frontmatter_name_does_not_match_filename()
    {
        var dir = Path.Combine(_root, ".doxie", "agents");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "right.md"),
            "---\nname: wrong\ndescription: x\n---\n\nbody\n");

        var act = () => new DoxieAgentReader(Path.Combine(_root, ".doxie")).LoadAll();

        act.Should().Throw<InvalidDataException>().WithMessage("*does not match the filename*");
    }

    [Fact]
    public void ClaudeAgentEmitter_writes_md_with_frontmatter_and_body()
    {
        var agent = new DoxieAgentCanon(
            new DoxieAgentManifest { Id = "explorer", Description = "search.", Tools = "Grep,Glob", Model = "claude-sonnet-4-5" },
            SystemPrompt: "Be terse.\n");

        new ClaudeAgentEmitter().Emit(AgentsOnly(agent), _root);

        var md = File.ReadAllText(Path.Combine(_root, ".claude", "agents", "explorer.md"));
        md.Should().Contain("name: explorer");
        md.Should().Contain("description: search.");
        md.Should().Contain("tools: Grep,Glob");
        md.Should().Contain("model: claude-sonnet-4-5");
        md.Should().EndWith("Be terse.\n");
    }

    [Fact]
    public void ClaudeAgentEmitter_removes_managed_orphans_but_preserves_hand_authored()
    {
        var emitter = new ClaudeAgentEmitter();
        var first = new DoxieAgentCanon(new DoxieAgentManifest { Id = "first", Description = "x" }, "p\n");
        emitter.Emit(AgentsOnly(first), _root);

        // Hand-authored agent file the user wrote outside the .doxie pipeline
        File.WriteAllText(Path.Combine(_root, ".claude", "agents", "user-authored.md"), "manual");

        // Re-emit with a different agent — first/.md must be removed, user-authored.md kept.
        var second = new DoxieAgentCanon(new DoxieAgentManifest { Id = "second", Description = "y" }, "q\n");
        emitter.Emit(AgentsOnly(second), _root);

        File.Exists(Path.Combine(_root, ".claude", "agents", "first.md")).Should().BeFalse();
        File.Exists(Path.Combine(_root, ".claude", "agents", "second.md")).Should().BeTrue();
        File.Exists(Path.Combine(_root, ".claude", "agents", "user-authored.md")).Should().BeTrue();
    }

    [Fact]
    public void CodexEmitter_includes_subagents_section()
    {
        var skill = MakeCanon("foo", "f.", "body");
        var agent = new DoxieAgentCanon(
            new DoxieAgentManifest { Id = "explorer", Description = "search-only", Tools = "Grep" },
            SystemPrompt: "p\n");
        var registry = new DoxieRegistry(new[] { skill }, new[] { agent }, Array.Empty<DoxieLibraryCanon>(), Array.Empty<DoxieWorkflowCanon>());

        new CodexShimEmitter().Emit(registry, _root);

        var content = File.ReadAllText(Path.Combine(_root, ".codex", "AGENTS.md"));
        content.Should().Contain("## Skills");
        content.Should().Contain("## Subagents");
        content.Should().Contain("### explorer");
        content.Should().Contain("**Tools:** Grep");
    }

    // ---------- Library pipeline ----------

    [Fact]
    public void LibraryReader_loads_files_recursively()
    {
        var dir = Path.Combine(_root, ".doxie", "libraries", "sample-tooling");
        Directory.CreateDirectory(Path.Combine(dir, "memory"));
        File.WriteAllText(Path.Combine(dir, "library.json"), "{\"id\":\"sample-tooling\"}");
        File.WriteAllText(Path.Combine(dir, "memory", "MEMORY.md"), "# index");

        var libraries = new DoxieLibraryReader(Path.Combine(_root, ".doxie")).LoadAll();

        libraries.Should().HaveCount(1);
        libraries[0].Id.Should().Be("sample-tooling");
        libraries[0].Files.Keys.Should().BeEquivalentTo(new[] { "library.json", "memory/MEMORY.md" });
    }

    [Fact]
    public void ClaudeLibraryEmitter_copies_files_byte_for_byte()
    {
        var library = new DoxieLibraryCanon("sample-product", new Dictionary<string, byte[]>
        {
            ["library.json"] = Encoding.UTF8.GetBytes("{\"id\":\"sample-product\"}\n"),
            ["memory/foo.md"] = Encoding.UTF8.GetBytes("# foo\n"),
        });
        var registry = new DoxieRegistry(Array.Empty<DoxieSkillCanon>(), Array.Empty<DoxieAgentCanon>(), new[] { library }, Array.Empty<DoxieWorkflowCanon>());

        new ClaudeLibraryEmitter().Emit(registry, _root);

        File.ReadAllText(Path.Combine(_root, ".claude", "libraries", "sample-product", "library.json"))
            .Should().Be("{\"id\":\"sample-product\"}\n");
        File.ReadAllText(Path.Combine(_root, ".claude", "libraries", "sample-product", "memory", "foo.md"))
            .Should().Be("# foo\n");
    }

    [Fact]
    public void ClaudeLibraryEmitter_removes_managed_orphans_but_preserves_hand_authored()
    {
        var first = new DoxieLibraryCanon("first", new Dictionary<string, byte[]> { ["x.md"] = Encoding.UTF8.GetBytes("x") });
        var emitter = new ClaudeLibraryEmitter();
        emitter.Emit(new DoxieRegistry(Array.Empty<DoxieSkillCanon>(), Array.Empty<DoxieAgentCanon>(), new[] { first }, Array.Empty<DoxieWorkflowCanon>()), _root);

        var handAuthored = Path.Combine(_root, ".claude", "libraries", "hand", "library.json");
        Directory.CreateDirectory(Path.GetDirectoryName(handAuthored)!);
        File.WriteAllText(handAuthored, "{}");

        emitter.Emit(DoxieRegistry.Empty, _root);

        Directory.Exists(Path.Combine(_root, ".claude", "libraries", "first")).Should().BeFalse();
        File.Exists(handAuthored).Should().BeTrue();
    }

    // ---------- Legacy skills migrator (v0.1 â†’ v1.0 upgrade path) ----------

    [Fact]
    public void Migrator_detects_unmanaged_skills_and_skips_managed_ones()
    {
        // Two existing .claude/skills/ folders: one hand-authored (legacy),
        // one already managed by the doxie pipeline (has the marker).
        var legacyDir = Path.Combine(_root, ".claude", "skills", "legacy");
        Directory.CreateDirectory(legacyDir);
        File.WriteAllText(Path.Combine(legacyDir, "SKILL.md"), "---\nname: legacy\ndescription: x\n---\n\nbody");

        var managedDir = Path.Combine(_root, ".claude", "skills", "managed");
        Directory.CreateDirectory(managedDir);
        File.WriteAllText(Path.Combine(managedDir, "SKILL.md"), "---\nname: managed\ndescription: x\n---\n\nbody");
        Directory.CreateDirectory(Path.Combine(managedDir, "doxie"));
        File.WriteAllText(Path.Combine(managedDir, "doxie", "generated"), "");

        var detected = new LegacySkillsMigrator(_root).Detect();

        detected.Should().ContainSingle();
        detected[0].Id.Should().Be("legacy");
        detected[0].HasSidecar.Should().BeFalse();
    }

    [Fact]
    public void Migrator_converts_SKILL_md_plus_orchestrator_json_into_canonical_shape()
    {
        var legacyDir = Path.Combine(_root, ".claude", "skills", "old-skill");
        Directory.CreateDirectory(legacyDir);
        File.WriteAllText(Path.Combine(legacyDir, "SKILL.md"),
            "---\nname: old-skill\ndescription: Does the thing.\n---\n\n# Body header\n\nBody content.\n");
        File.WriteAllText(Path.Combine(legacyDir, "orchestrator.json"), """
            {
              "displayName": "Old Skill",
              "category": "Inspector",
              "modes": {
                "run": { "description": "do it" }
              }
            }
            """);
        File.WriteAllText(Path.Combine(legacyDir, "run.md"), "# /old-skill run");

        var migrator = new LegacySkillsMigrator(_root);
        var detected = migrator.Detect();
        var results = migrator.Migrate(detected);

        results.Should().ContainSingle();
        results[0].Migrated.Should().BeTrue();

        var canonicalDir = Path.Combine(_root, ".doxie", "skills", "old-skill");
        File.Exists(Path.Combine(canonicalDir, "manifest.json")).Should().BeTrue();
        File.Exists(Path.Combine(canonicalDir, "body.md")).Should().BeTrue();
        File.Exists(Path.Combine(canonicalDir, "run.md")).Should().BeTrue("companions copy verbatim");

        var manifest = File.ReadAllText(Path.Combine(canonicalDir, "manifest.json"));
        manifest.Should().Contain("\"id\": \"old-skill\"");
        manifest.Should().Contain("\"displayName\": \"Old Skill\"");
        manifest.Should().Contain("\"category\": \"Inspector\"");
        manifest.Should().Contain("\"run\"");

        var body = File.ReadAllText(Path.Combine(canonicalDir, "body.md"));
        body.Should().StartWith("# Body header");
        body.Should().NotContain("---");
    }

    [Fact]
    public void Migrator_round_trips_through_regen_to_byte_equivalent_shim()
    {
        // Set up a legacy skill, migrate it, run regen, verify SKILL.md
        // still parses and contains the same description+body.
        var legacyDir = Path.Combine(_root, ".claude", "skills", "round-trip");
        Directory.CreateDirectory(legacyDir);
        File.WriteAllText(Path.Combine(legacyDir, "SKILL.md"),
            "---\nname: round-trip\ndescription: Round-trip skill.\n---\n\n# Round trip\n\nbody\n");

        new LegacySkillsMigrator(_root).Migrate(new LegacySkillsMigrator(_root).Detect());
        new DoxieRegenerator(_root, new IShimEmitter[] { new ClaudeShimEmitter() }).Regenerate();

        var emitted = File.ReadAllText(Path.Combine(_root, ".claude", "skills", "round-trip", "SKILL.md"));
        emitted.Should().Contain("name: round-trip");
        emitted.Should().Contain("description: Round-trip skill.");
        emitted.Should().Contain("# Round trip");
        emitted.Should().Contain("body");
        File.Exists(Path.Combine(_root, ".claude", "skills", "round-trip", "doxie", "generated"))
            .Should().BeTrue("regen stamps the marker so subsequent Detect() skips this skill");
    }

    // ---------- DoxieAgentCatalog (canonical-only, no shim fallback) ----------

    [Fact]
    public void DoxieAgentCatalog_returns_skills_authored_in_doxie()
    {
        WriteCanonWithSidecar("watcher", "watches things",
            displayName: "Watcher",
            category: "Inspector",
            modesJson: """{"check": {"description": "poll once"}}""");

        var catalog = new DoxieAgentCatalog(_root);
        var agents = catalog.GetAll();

        agents.Should().ContainSingle();
        agents[0].Id.Should().Be("watcher");
        agents[0].Name.Should().Be("Watcher");
        agents[0].Description.Should().Be("watches things");
        agents[0].Category.Should().Be(AgentCategory.Inspector);
        agents[0].Modes.Should().ContainSingle();
        agents[0].Modes![0].Id.Should().Be("check");
        agents[0].Modes![0].Description.Should().Be("poll once");
    }

    [Fact]
    public void DoxieAgentCatalog_does_not_fall_back_to_claude_shim()
    {
        // A skill in .claude/skills/ that hasn't been migrated to .doxie/
        // must NOT show up — that's the deliberate hard-cutover behaviour
        // that makes .doxie/ the single source of truth.
        var legacyDir = Path.Combine(_root, ".claude", "skills", "unmigrated");
        Directory.CreateDirectory(legacyDir);
        File.WriteAllText(Path.Combine(legacyDir, "SKILL.md"),
            "---\nname: unmigrated\ndescription: x\n---\n\nbody");

        new DoxieAgentCatalog(_root).GetAll().Should().BeEmpty();
    }

    [Fact]
    public void DoxieAgentCatalog_returns_empty_when_doxie_missing()
    {
        new DoxieAgentCatalog(_root).GetAll().Should().BeEmpty();
    }

    [Fact]
    public void DoxieAgentCatalog_ReadSkillBody_returns_canonical_body_md()
    {
        WriteCanon("jarvis", "voice", "# Body header\n\nBody content.\n");

        var catalog = new DoxieAgentCatalog(_root);

        catalog.ReadSkillBody("jarvis").Should().Contain("Body header");
        catalog.ReadSkillBody("nonexistent").Should().BeNull();
    }

    [Fact]
    public void DoxieAgentCatalog_falls_back_displayName_to_titlecased_id()
    {
        // Skill without a sidecar override falls back to title-casing the id.
        WriteCanon("multi-word-skill", "x.", "body");

        var agent = new DoxieAgentCatalog(_root).GetAll().Single();

        agent.Name.Should().Be("Multi Word Skill");
    }

    // ---------- LegacySkillsMigrator: libraries ----------

    [Fact]
    public void Migrator_detects_libraries_only_in_claude_libraries()
    {
        var legacyLib = Path.Combine(_root, ".claude", "libraries", "old-pack");
        Directory.CreateDirectory(legacyLib);
        File.WriteAllText(Path.Combine(legacyLib, "library.json"), "{\"id\":\"old-pack\"}");
        File.WriteAllText(Path.Combine(legacyLib, "MEMORY.md"), "# index");

        // Already-canonical lib must NOT be flagged.
        var canonical = Path.Combine(_root, ".doxie", "libraries", "new-pack");
        Directory.CreateDirectory(canonical);
        var alreadyShim = Path.Combine(_root, ".claude", "libraries", "new-pack");
        Directory.CreateDirectory(alreadyShim);

        var detected = new LegacySkillsMigrator(_root).DetectLibraries();

        detected.Should().ContainSingle();
        detected[0].Id.Should().Be("old-pack");
    }

    [Fact]
    public void Migrator_copies_legacy_library_into_doxie_byte_for_byte()
    {
        var legacyLib = Path.Combine(_root, ".claude", "libraries", "sample-pack");
        Directory.CreateDirectory(Path.Combine(legacyLib, "memory"));
        File.WriteAllText(Path.Combine(legacyLib, "library.json"), "{\"id\":\"sample-pack\",\"name\":\"Sample Pack\"}");
        File.WriteAllText(Path.Combine(legacyLib, "memory", "user.md"), "# user");

        var migrator = new LegacySkillsMigrator(_root);
        var detected = migrator.DetectLibraries();
        var results = migrator.MigrateLibraries(detected);

        results.Should().ContainSingle();
        results[0].Migrated.Should().BeTrue();

        var canonical = Path.Combine(_root, ".doxie", "libraries", "sample-pack");
        File.ReadAllText(Path.Combine(canonical, "library.json"))
            .Should().Contain("\"name\":\"Sample Pack\"");
        File.ReadAllText(Path.Combine(canonical, "memory", "user.md"))
            .Should().Be("# user");
    }

    private void WriteCanonWithSidecar(string folderName, string description,
        string? displayName = null, string? category = null, string? modesJson = null)
    {
        var dir = Path.Combine(_root, ".doxie", "skills", folderName);
        Directory.CreateDirectory(dir);
        var sb = new System.Text.StringBuilder();
        sb.Append("{\n");
        sb.Append($"  \"id\": \"{folderName}\",\n");
        sb.Append($"  \"description\": \"{description}\"");
        if (!string.IsNullOrWhiteSpace(displayName)) sb.Append($",\n  \"displayName\": \"{displayName}\"");
        if (!string.IsNullOrWhiteSpace(category)) sb.Append($",\n  \"category\": \"{category}\"");
        if (!string.IsNullOrWhiteSpace(modesJson)) sb.Append($",\n  \"modes\": {modesJson}");
        sb.Append("\n}\n");
        File.WriteAllText(Path.Combine(dir, "manifest.json"), sb.ToString());
        File.WriteAllText(Path.Combine(dir, "body.md"), "body");
    }

    // ---------- POC: /aggregate migration round-trip ----------

    [Fact]
    public void POC_aggregate_canonical_regenerates_to_original_claude_shim()
    {
        // Given the canonical form authored at .doxie/skills/aggregate/...
        var manifestJson = """
            {
              "id": "aggregate",
              "description": "Joins outputs from N upstream workflow nodes into a single payload that downstream nodes can consume. Acts as a synchronization barrier — the workflow engine waits for every upstream node to succeed before this node fires, then forwards the merged result to its dependents.",
              "displayName": "Aggregate",
              "category": "Connector",
              "requirements": [],
              "modes": {
                "merge": {
                  "description": "Wait for every upstream node to succeed, then forward a merged payload to dependents. No fields — the engine wires the upstream nodes via the workflow's edges.",
                  "argumentsTemplate": "merge"
                }
              }
            }
            """;
        var bodyMd = """
            # Aggregate Skill — Workflow join node

            A built-in workflow primitive. When placed inside a `WorkflowDefinition`, it tells the engine "wait until every node feeding into me has succeeded, then surface a unified payload to whatever depends on me." It does not invoke any Claude process — the runner handles it in-process.

            ## When to use

            - Two or more parallel branches need to converge before a final write step.
            - A summarising / writing agent downstream needs to consume the union of several upstream artifacts.

            ## Modes

            - `merge` — default behaviour. No fields. The runner counts the upstream nodes that succeeded and emits a one-line summary of the joined payload.

            ## Notes

            This skill has no executable side effect on its own. It exists in the catalog so the workflow builder can list it as a pickable node type. The actual join semantics live inside `OrchestratedWorkflowRunner` (and any future runners).

            """;
        var dir = Path.Combine(_root, ".doxie", "skills", "aggregate");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "manifest.json"), manifestJson);
        File.WriteAllText(Path.Combine(dir, "body.md"), bodyMd);

        // When the regen pipeline runs ...
        var regenerator = new DoxieRegenerator(_root, new IShimEmitter[] { new ClaudeShimEmitter() });
        regenerator.Regenerate();

        // ... the emitted SKILL.md preserves the description and the body
        // exactly as authored — i.e. Claude Code reads the same content
        // it would have read from a hand-authored SKILL.md.
        var emittedSkillMd = File.ReadAllText(Path.Combine(_root, ".claude", "skills", "aggregate", "SKILL.md"));
        emittedSkillMd.Should().StartWith("---\nname: aggregate\ndescription: Joins outputs from N upstream workflow nodes");
        emittedSkillMd.Should().Contain("# Aggregate Skill — Workflow join node");
        emittedSkillMd.Should().Contain("OrchestratedWorkflowRunner");
        emittedSkillMd.Should().EndWith("\n");

        // ... and the emitted orchestrator.json contains every field the
        // FilesystemAgentCatalog needs to expose the skill in the UI.
        var emittedJson = File.ReadAllText(Path.Combine(_root, ".claude", "skills", "aggregate", "orchestrator.json"));
        emittedJson.Should().Contain("\"displayName\": \"Aggregate\"");
        emittedJson.Should().Contain("\"category\": \"Connector\"");
        emittedJson.Should().Contain("\"merge\"");
        emittedJson.Should().Contain("\"argumentsTemplate\": \"merge\"");
    }

    // ---------- Watcher ----------

    [WindowsFact]
    public void Watcher_regenerates_after_canon_write()
    {
        // Pre-create the watcher root so FilesystemDoxieWatcher arms its
        // FileSystemWatcher (the ctor no-ops on a missing root).
        Directory.CreateDirectory(Path.Combine(_root, ".doxie", "skills"));

        var regenerator = new DoxieRegenerator(_root, new IShimEmitter[] { new ClaudeShimEmitter() });
        using var watcher = new FilesystemDoxieWatcher(regenerator, _root);

        WriteCanon("hello", "x", "# body\n");

        // Watcher debounce is 300ms; allow generous slack for CI.
        WaitUntil(() => File.Exists(Path.Combine(_root, ".claude", "skills", "hello", "SKILL.md")),
            timeout: TimeSpan.FromSeconds(15));
    }

    private static void WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            Thread.Sleep(100);
        }
        condition().Should().BeTrue("condition should hold within {0}", timeout);
    }

    // ---------- Helpers ----------

    private IReadOnlyList<DoxieCatalogRoot> CatalogRoots() => new[]
    {
        new DoxieCatalogRoot(
            "default",
            "Default catalog",
            Path.Combine(_root, ".doxie"),
            IsDefault: true),
        new DoxieCatalogRoot(
            "shared-team",
            "Shared team",
            Path.Combine(_root, "shared-team-catalog")),
    };

    private void WriteCanon(string folderName, string description, string body, string? id = null, bool isPrivate = false, string? catalogRoot = null)
    {
        var dir = catalogRoot switch
        {
            "shared-team" => Path.Combine(_root, "shared-team-catalog", "skills", folderName),
            _ => Path.Combine(_root, ".doxie", "skills", folderName),
        };
        Directory.CreateDirectory(dir);
        var manifest = $$"""
            {
              "id": "{{id ?? folderName}}",
              "description": "{{description}}"
            }
            """;
        File.WriteAllText(Path.Combine(dir, "manifest.json"), manifest);
        File.WriteAllText(Path.Combine(dir, "body.md"), body);
    }

    private static DoxieSkillCanon MakeCanon(
        string id,
        string description,
        string body,
        Dictionary<string, byte[]>? companions = null) =>
        new(
            new DoxieSkillManifest { Id = id, Description = description },
            BodyMarkdown: body,
            CompanionFiles: companions ?? new Dictionary<string, byte[]>());

    private static DoxieRegistry SkillsOnly(params DoxieSkillCanon[] skills) =>
        new(skills, Array.Empty<DoxieAgentCanon>(), Array.Empty<DoxieLibraryCanon>(), Array.Empty<DoxieWorkflowCanon>());

    private static DoxieRegistry AgentsOnly(params DoxieAgentCanon[] agents) =>
        new(Array.Empty<DoxieSkillCanon>(), agents, Array.Empty<DoxieLibraryCanon>(), Array.Empty<DoxieWorkflowCanon>());

    private void WriteAgent(string id, string description, string systemPrompt, string? catalogRoot = null)
    {
        var dir = catalogRoot switch
        {
            "shared-team" => Path.Combine(_root, "shared-team-catalog", "agents"),
            _ => Path.Combine(_root, ".doxie", "agents"),
        };
        Directory.CreateDirectory(dir);
        var content = $"---\nname: {id}\ndescription: {description}\n---\n\n{systemPrompt}\n";
        File.WriteAllText(Path.Combine(dir, $"{id}.md"), content);
    }

    private static Dictionary<string, byte[]> SnapshotShim(string root)
    {
        var shim = Path.Combine(root, ".claude");
        if (!Directory.Exists(shim)) return new();
        var snapshot = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var f in Directory.EnumerateFiles(shim, "*", SearchOption.AllDirectories))
        {
            snapshot[Path.GetRelativePath(shim, f).Replace('\\', '/')] = File.ReadAllBytes(f);
        }
        return snapshot;
    }
}
