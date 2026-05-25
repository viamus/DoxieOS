using FluentAssertions;

namespace Viamus.Doxie.Orchestrator.Context.Tests;

public sealed class FilesystemLibraryStoreTests : IDisposable
{
    private readonly string _root;

    public FilesystemLibraryStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"orchestrator-lib-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { /* best effort */ }
    }

    [Fact]
    public void ListAll_returns_empty_when_directory_is_missing()
    {
        var store = new FilesystemLibraryStore(Path.Combine(_root, "does-not-exist"));

        store.ListAll().Should().BeEmpty();
    }

    [Fact]
    public void Library_id_is_the_subdirectory_name()
    {
        WriteMemory("personal-prefs", "feedback_naming.md", "naming convention", "feedback");

        var library = new FilesystemLibraryStore(_root).ListAll().Single();

        library.Id.Should().Be("personal-prefs");
    }

    [Fact]
    public void Library_name_falls_back_to_pretty_id_when_manifest_absent()
    {
        WriteMemory("team-dotnet-defaults", "feedback_dotnet.md", ".NET defaults", "feedback");

        var library = new FilesystemLibraryStore(_root).ListAll().Single();

        library.Name.Should().Be("Team Dotnet Defaults");
        library.Description.Should().BeEmpty();
    }

    [Fact]
    public void Library_manifest_overrides_name_and_description()
    {
        WriteMemory("personal-prefs", "feedback_naming.md", "naming", "feedback");
        File.WriteAllText(Path.Combine(_root, "personal-prefs", "library.json"),
            """{"name":"Personal Preferences","description":"Code style + UX preferences."}""");

        var library = new FilesystemLibraryStore(_root).GetById("personal-prefs")!;

        library.Name.Should().Be("Personal Preferences");
        library.Description.Should().Be("Code style + UX preferences.");
    }

    [Fact]
    public void Memories_inside_a_library_are_parsed_from_frontmatter()
    {
        WriteMemory("personal-prefs", "feedback_naming.md", "Use kebab-case", "feedback",
            description: "for files and skills",
            body: "## Why\nbecause ...");

        var memory = new FilesystemLibraryStore(_root).ListAll().Single().Memories.Single();

        memory.FileName.Should().Be("feedback_naming.md");
        memory.Name.Should().Be("Use kebab-case");
        memory.Description.Should().Be("for files and skills");
        memory.Type.Should().Be(MemoryType.Feedback);
        memory.Content.Should().StartWith("## Why");
    }

    [Fact]
    public void MEMORY_md_and_README_md_are_excluded_from_memory_list()
    {
        var libDir = Path.Combine(_root, "lib");
        Directory.CreateDirectory(libDir);
        File.WriteAllText(Path.Combine(libDir, "MEMORY.md"), "# Index\n");
        File.WriteAllText(Path.Combine(libDir, "README.md"), "# Readme\n");
        File.WriteAllText(Path.Combine(libDir, "real_memory.md"),
            "---\nname: real\ntype: project\n---\nbody");

        var memories = new FilesystemLibraryStore(_root).GetById("lib")!.Memories;

        memories.Should().ContainSingle().Which.FileName.Should().Be("real_memory.md");
    }

    [Fact]
    public void Memory_files_can_live_under_memory_subdirectory()
    {
        var libDir = Path.Combine(_root, "lib", "memory");
        Directory.CreateDirectory(libDir);
        File.WriteAllText(Path.Combine(libDir, "reference_paths.md"),
            "---\nname: Paths\ntype: reference\n---\nbody");

        var memories = new FilesystemLibraryStore(_root).GetById("lib")!.Memories;

        memories.Should().ContainSingle().Which.Name.Should().Be("Paths");
    }

    [Fact]
    public void Memory_files_can_live_under_memories_subdirectory()
    {
        var libDir = Path.Combine(_root, "lib", "memories");
        Directory.CreateDirectory(libDir);
        File.WriteAllText(Path.Combine(libDir, "architecture.md"),
            "---\ntitle: Architecture\ntype: architecture\n---\nbody");

        var memories = new FilesystemLibraryStore(_root).GetById("lib")!.Memories;

        memories.Should().ContainSingle().Which.Name.Should().Be("Architecture");
    }

    [Fact]
    public void Files_include_nested_non_memory_artifacts_and_folders()
    {
        var libDir = Path.Combine(_root, "lib");
        Directory.CreateDirectory(Path.Combine(libDir, "metadata"));
        Directory.CreateDirectory(Path.Combine(libDir, "indexes"));
        File.WriteAllText(Path.Combine(libDir, "library.json"), """{"name":"Lib"}""");
        File.WriteAllText(Path.Combine(libDir, "metadata", "repositories.json"), "[]");
        File.WriteAllText(Path.Combine(libDir, "indexes", "correlation-index.json"), "{}");

        var files = new FilesystemLibraryStore(_root).GetById("lib")!.Files;

        files.Should().Contain(f => f.IsDirectory && f.RelativePath == "metadata");
        files.Should().Contain(f => f.IsDirectory && f.RelativePath == "indexes");
        files.Should().Contain(f => !f.IsDirectory && f.RelativePath == "metadata/repositories.json");
        files.Should().Contain(f => !f.IsDirectory && f.RelativePath == "indexes/correlation-index.json");
        files.Should().Contain(f => !f.IsDirectory && f.RelativePath == "library.json");
    }

    [Fact]
    public void Libraries_are_listed_alphabetically_by_name()
    {
        WriteMemory("zulu", "x.md", "x", "user");
        WriteMemory("alpha", "x.md", "x", "user");
        WriteMemory("mike", "x.md", "x", "user");

        var ids = new FilesystemLibraryStore(_root).ListAll().Select(l => l.Id).ToList();

        ids.Should().Equal("alpha", "mike", "zulu");
    }

    [Fact]
    public void GetById_returns_null_for_unknown_library()
    {
        new FilesystemLibraryStore(_root).GetById("nope").Should().BeNull();
    }

    [Fact]
    public void Md_files_in_the_file_tree_have_their_content_populated()
    {
        var libDir = Path.Combine(_root, "lib");
        Directory.CreateDirectory(libDir);
        File.WriteAllText(Path.Combine(libDir, "note.md"), "---\nname: note\ntype: project\n---\nbody");

        var files = new FilesystemLibraryStore(_root).GetById("lib")!.Files;

        files.Should().Contain(f => f.Name == "note.md" && f.Content != null);
    }

    [Fact]
    public void Non_md_files_in_the_file_tree_have_null_content()
    {
        var libDir = Path.Combine(_root, "lib");
        Directory.CreateDirectory(libDir);
        File.WriteAllText(Path.Combine(libDir, "data.json"), "{}");

        var files = new FilesystemLibraryStore(_root).GetById("lib")!.Files;

        files.Should().Contain(f => f.Name == "data.json" && f.Content == null);
    }

    private void WriteMemory(string libraryId, string fileName, string name, string type,
        string description = "", string body = "body content")
    {
        var libDir = Path.Combine(_root, libraryId);
        Directory.CreateDirectory(libDir);
        var content = $"---\nname: {name}\ndescription: {description}\ntype: {type}\n---\n{body}";
        File.WriteAllText(Path.Combine(libDir, fileName), content);
    }
}
