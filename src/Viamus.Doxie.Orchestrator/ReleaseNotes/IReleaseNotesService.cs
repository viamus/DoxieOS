namespace Viamus.Doxie.Orchestrator.ReleaseNotes;

/// <summary>
/// Lists release notes shipped with this build of DoxieOS. Backed by markdown
/// files embedded as resources so deployments don't depend on disk layout.
/// </summary>
public interface IReleaseNotesService
{
    /// <summary>All notes, newest first.</summary>
    IReadOnlyList<ReleaseNote> ListAll();

    /// <summary>The latest released version, or <c>null</c> if no notes are shipped.</summary>
    ReleaseNote? Latest();

    /// <summary>Notes strictly newer than <paramref name="lastSeenVersion"/>, newest first. Empty if none.</summary>
    IReadOnlyList<ReleaseNote> Unseen(string? lastSeenVersion);
}
