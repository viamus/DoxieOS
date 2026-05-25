namespace Viamus.Doxie.Orchestrator.Context;

/// <summary>
/// A single entry from Claude Code's auto-memory directory — one .md
/// file with YAML frontmatter (name / description / type).
/// </summary>
public sealed record MemoryEntry(
    string FileName,
    string Name,
    string Description,
    MemoryType Type,
    string Content);
