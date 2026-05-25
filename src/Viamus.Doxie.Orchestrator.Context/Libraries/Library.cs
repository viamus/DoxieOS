namespace Viamus.Doxie.Orchestrator.Context;

/// <summary>
/// A reusable memory pack stored under <c>.doxie/libraries/&lt;id&gt;/</c>.
/// A library can contain injectible memory <c>.md</c> files plus auxiliary
/// artifacts such as metadata JSON, indexes, examples, and nested folders.
/// </summary>
public sealed record Library(
    string Id,
    string Name,
    string Description,
    IReadOnlyList<MemoryEntry> Memories,
    IReadOnlyList<LibraryFileEntry> Files,
    string CatalogId = "default",
    string CatalogName = "Default catalog",
    string CatalogRoot = "");

public sealed record LibraryFileEntry(
    string RelativePath,
    string Name,
    bool IsDirectory,
    long? SizeBytes,
    string? Content = null);
