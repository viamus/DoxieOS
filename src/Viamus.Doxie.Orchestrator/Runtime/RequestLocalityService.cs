using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace Viamus.Doxie.Orchestrator.Runtime;

public sealed class FileBrowserOptions
{
    /// <summary>
    /// Tri-state mode: <c>auto</c> | <c>always</c> | <c>never</c>.
    /// </summary>
    public string Mode { get; set; } = "auto";
}

public sealed class RequestLocalityService : IRequestLocalityService
{
    private readonly IHttpContextAccessor _http;
    private readonly IOptionsMonitor<FileBrowserOptions> _options;

    public RequestLocalityService(IHttpContextAccessor http, IOptionsMonitor<FileBrowserOptions> options)
    {
        _http = http;
        _options = options;
    }

    public bool IsLocalRequest()
    {
        // Honour the explicit override first so production deployments
        // behind a reverse proxy can pin "always" without us having to
        // unwrap X-Forwarded-For (which has its own KnownProxies trust
        // model and is too easy to misconfigure).
        var mode = (_options.CurrentValue.Mode ?? "auto").Trim().ToLowerInvariant();
        switch (mode)
        {
            case "always": return false; // force web file browser
            case "never": return true;   // force desktop launcher
            // any other value (including "auto") falls through to detection
        }

        var ctx = _http.HttpContext;
        if (ctx is null)
        {
            // No HTTP context — background work, startup, etc. Default to
            // "local" so we don't accidentally hide desktop affordances
            // from non-request code paths.
            return true;
        }

        var remote = ctx.Connection.RemoteIpAddress;
        if (remote is null) return true; // unix socket or similar — treat as local

        // IPAddress.IsLoopback handles both 127.0.0.0/8 and ::1 plus the
        // IPv4-mapped IPv6 form ::ffff:127.0.0.1 — the latter is the
        // shape Kestrel hands us when bound to a dual-stack endpoint.
        return IPAddress.IsLoopback(remote);
    }
}
