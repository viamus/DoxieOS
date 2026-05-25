namespace Viamus.Doxie.Orchestrator.Runtime;

/// <summary>
/// Tells the app whether the current HTTP request is coming from the same
/// machine that hosts DoxieOS (so we can launch desktop helpers like
/// <c>explorer.exe</c>) or from a different machine over the network (so
/// we should fall back to web-only views like the in-browser file browser).
///
/// The decision is configurable via <c>Doxie:FileBrowser</c> in
/// appsettings:
/// <list type="bullet">
///   <item><description><c>auto</c> (default) — inspect the connection's remote IP and treat loopback as local</description></item>
///   <item><description><c>always</c> — force the web file browser, even from loopback (useful behind a reverse proxy where everything looks like localhost)</description></item>
///   <item><description><c>never</c> — force the desktop launcher (current behavior; useful for headless dev boxes where the user is always local)</description></item>
/// </list>
/// </summary>
public interface IRequestLocalityService
{
    /// <summary>
    /// True when the current HTTP request should be treated as coming from
    /// the same machine that hosts DoxieOS. Returns <c>true</c> outside an
    /// HTTP context (e.g. background services) so the safe default is
    /// "local" and not "deny".
    /// </summary>
    bool IsLocalRequest();
}
