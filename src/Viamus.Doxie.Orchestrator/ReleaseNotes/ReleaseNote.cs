namespace Viamus.Doxie.Orchestrator.ReleaseNotes;

/// <summary>
/// One release note, parsed from <c>docs/release-notes/v&lt;version&gt;.md</c>.
/// The markdown file's frontmatter supplies metadata; the body is the rendered
/// content shown in <c>ReleaseNotesModal</c>.
/// </summary>
/// <param name="Version">SemVer-ish string ("0.5.0"). Sort key for the timeline.</param>
/// <param name="Title">One-line headline shown in the timeline + modal header.</param>
/// <param name="ReleaseDate">When the version shipped (ISO 8601).</param>
/// <param name="Highlights">Short bullet list shown on the sidebar before clicking in.</param>
/// <param name="BodyMarkdown">Full markdown body (without frontmatter), rendered to HTML at display time.</param>
/// <param name="LottieAssetPath">Optional path to a companion <c>*.lottie.json</c> served from <c>/release-notes/</c>.</param>
public sealed record ReleaseNote(
    string Version,
    string Title,
    DateOnly ReleaseDate,
    IReadOnlyList<string> Highlights,
    string BodyMarkdown,
    string? LottieAssetPath);
