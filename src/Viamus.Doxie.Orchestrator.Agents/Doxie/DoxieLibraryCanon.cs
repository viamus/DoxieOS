namespace Viamus.Doxie.Orchestrator.Agents.Doxie;

/// <summary>
/// Canonical representation of a DoxieOS library (memory pack). Libraries are
/// not provider-specific — they're a DoxieOS-internal concept consumed by
/// <see cref="ILibraryStore"/>. The pipeline is therefore simpler than skills/
/// agents: read all files under <c>.doxie/libraries/&lt;id&gt;/</c> verbatim,
/// emit them 1:1 to <c>.claude/libraries/&lt;id&gt;/</c> for Claude-side
/// compatibility. Codex gets mounted libraries through workspace
/// <c>AGENTS.md</c> generation rather than a global library shim.
///
/// Library files are carried as opaque bytes — the canon doesn't parse
/// <c>library.json</c> or memory <c>.md</c> contents; that stays
/// <see cref="FilesystemLibraryStore"/>'s job.
/// </summary>
public sealed record DoxieLibraryCanon(
    string Id,
    IReadOnlyDictionary<string, byte[]> Files);
