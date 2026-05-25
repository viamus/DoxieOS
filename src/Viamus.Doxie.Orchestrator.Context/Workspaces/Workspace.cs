namespace Viamus.Doxie.Orchestrator.Context;

/// <summary>
/// A user-owned, persistent project folder under
/// <c>./.workspace/&lt;id&gt;/</c>. Distinct from agent-allocated
/// **sandboxes** (which live under <c>./.sandbox/</c> and are
/// transient): a Workspace is something the user creates explicitly,
/// keeps around, and can mount one or more libraries into so the
/// memories ship with that workspace's context.
/// </summary>
/// <param name="Id">Folder name on disk (kebab-case).</param>
/// <param name="Name">Display name (from manifest or derived from id).</param>
/// <param name="Description">One-line description, or empty string.</param>
/// <param name="CreatedAt">UTC timestamp when the workspace was created.</param>
/// <param name="MountedLibraryIds">Library ids the user has attached. Empty for now (Phase 2 wires the actual mounting).</param>
/// <param name="Path">Absolute path to the workspace folder on disk.</param>
public sealed record Workspace(
    string Id,
    string Name,
    string Description,
    DateTime CreatedAt,
    IReadOnlyList<string> MountedLibraryIds,
    string Path);
