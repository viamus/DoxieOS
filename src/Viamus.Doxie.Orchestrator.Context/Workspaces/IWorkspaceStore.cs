namespace Viamus.Doxie.Orchestrator.Context;

public interface IWorkspaceStore
{
    IReadOnlyList<Workspace> ListAll();

    Workspace? GetById(string id);

    /// <summary>
    /// Creates a new workspace folder with the conventional scaffold
    /// (<c>WORKSPACE.md</c>, <c>workspace.json</c>, <c>memory/MEMORY.md</c>).
    /// Optionally pre-mounts a list of libraries — written into the
    /// manifest the same way <see cref="SetMountedLibraries"/> does it.
    /// Returns the created workspace, or throws if the id collides with
    /// an existing one.
    /// </summary>
    Workspace Create(
        string id,
        string? displayName,
        string? description,
        IReadOnlyList<string>? mountedLibraryIds = null,
        IReadOnlyList<string>? mountedAgentIds = null);

    /// <summary>
    /// Recursively deletes a workspace folder. Throws if the workspace
    /// doesn't exist; returns silently if the deletion succeeds.
    /// </summary>
    void Delete(string id);

    /// <summary>
    /// Replaces the mounted-library set on a workspace's manifest with
    /// the supplied list (deduped, kebab-case-validated). Returns the
    /// updated workspace. Throws if the workspace doesn't exist.
    /// </summary>
    Workspace SetMountedLibraries(string id, IReadOnlyList<string> libraryIds);

    /// <summary>
    /// Replaces the mounted-agent set on a workspace's manifest with the
    /// supplied list (deduped, kebab-case-validated by the caller). Returns
    /// the updated workspace. Throws if the workspace doesn't exist.
    /// </summary>
    Workspace SetMountedAgents(string id, IReadOnlyList<string> agentIds);
}
