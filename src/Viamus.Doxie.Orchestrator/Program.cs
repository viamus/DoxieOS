using Viamus.Doxie.Orchestrator.Agents;
using System.Diagnostics;
using Serilog;
using Serilog.Events;
using Serilog.Sinks.SystemConsole.Themes;

using Viamus.Doxie.Orchestrator.Api;
using Viamus.Doxie.Orchestrator.Components;
using Viamus.Doxie.Orchestrator.Hosting;
using Viamus.Doxie.Orchestrator.Hubs;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Host.UseSerilog((context, services, logger) =>
{
    logger
        .MinimumLevel.Information()
        .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
        .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
        .MinimumLevel.Override("System", LogEventLevel.Warning)
        .Enrich.FromLogContext()
        .WriteTo.Console(
            theme: DoxieConsoleTheme(),
            outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss} [{Level:u3}] {Message:lj}  {SourceContext}{NewLine}{Exception}");
});
builder.Services.AddDoxieOrchestrator(builder.Configuration);

var app = builder.Build();

// Reconcile orphaned runs from previous orchestrator sessions: any run still
// marked Queued/Running was killed by the Job Object when the previous
// orchestrator exited (kill-on-close), so flip those rows to Interrupted
// before serving any UI request. See project_orchestrator_process_model.
app.Services.GetRequiredService<IAgentRunStore>().MarkOrphanedAsInterrupted();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAntiforgery();

app.Use(async (context, next) =>
{
    if (!ShouldLogRequest(context))
    {
        await next();
        return;
    }

    var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("DoxieOS.Api");
    var elapsed = Stopwatch.StartNew();
    var queryKeys = SafeQueryKeys(context.Request.Query);
    logger.LogInformation(
        "HTTP {Method} {Path} started from {RemoteIp} queryKeys={QueryKeys}",
        context.Request.Method,
        context.Request.Path.Value,
        context.Connection.RemoteIpAddress,
        queryKeys);

    try
    {
        await next();
    }
    finally
    {
        elapsed.Stop();
        if (context.Response.StatusCode >= StatusCodes.Status500InternalServerError)
        {
            logger.LogError(
                "HTTP {Method} {Path} failed {StatusCode} in {ElapsedMs}ms queryKeys={QueryKeys}",
                context.Request.Method,
                context.Request.Path.Value,
                context.Response.StatusCode,
                elapsed.ElapsedMilliseconds,
                queryKeys);
        }
        else if (context.Response.StatusCode >= StatusCodes.Status400BadRequest)
        {
            logger.LogWarning(
                "HTTP {Method} {Path} rejected {StatusCode} in {ElapsedMs}ms queryKeys={QueryKeys}",
                context.Request.Method,
                context.Request.Path.Value,
                context.Response.StatusCode,
                elapsed.ElapsedMilliseconds,
                queryKeys);
        }
        else
        {
            logger.LogInformation(
                "HTTP {Method} {Path} completed {StatusCode} in {ElapsedMs}ms queryKeys={QueryKeys}",
                context.Request.Method,
                context.Request.Path.Value,
                context.Response.StatusCode,
                elapsed.ElapsedMilliseconds,
                queryKeys);
        }
    }
});

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();
app.MapHub<ConsoleHub>("/hubs/console");

// Eagerly resolve the broadcaster so it subscribes to the store's
// SessionCreated/Removed events before any console is created.
_ = app.Services.GetRequiredService<ConsoleHubBroadcaster>();


app.MapRuntimeNotificationEndpoints();
app.MapDoxieMaintenanceEndpoints();
app.MapLibraryEndpoints();
app.MapWorkspaceEndpoints();
app.MapConsoleEndpoints();
app.MapWorkflowEndpoints();
app.MapAgentEndpoints();
app.MapBuilderEndpoints();

app.Run();

static AnsiConsoleTheme DoxieConsoleTheme() => new(new Dictionary<ConsoleThemeStyle, string>
{
    [ConsoleThemeStyle.Text] = "\x1b[38;5;252m",
    [ConsoleThemeStyle.SecondaryText] = "\x1b[38;5;240m",
    [ConsoleThemeStyle.TertiaryText] = "\x1b[38;5;238m",
    [ConsoleThemeStyle.Name] = "\x1b[38;5;252m",
    [ConsoleThemeStyle.String] = "\x1b[38;5;252m",
    [ConsoleThemeStyle.Number] = "\x1b[38;5;252m",
    [ConsoleThemeStyle.Boolean] = "\x1b[38;5;252m",
    [ConsoleThemeStyle.Scalar] = "\x1b[38;5;252m",
    [ConsoleThemeStyle.LevelVerbose] = "\x1b[38;5;244m",
    [ConsoleThemeStyle.LevelDebug] = "\x1b[38;5;147m",
    [ConsoleThemeStyle.LevelInformation] = "\x1b[38;5;39m",
    [ConsoleThemeStyle.LevelWarning] = "\x1b[38;5;220m",
    [ConsoleThemeStyle.LevelError] = "\x1b[38;5;196m",
    [ConsoleThemeStyle.LevelFatal] = "\x1b[38;5;199m",
});

static string SafeQueryKeys(IQueryCollection query)
{
    if (query.Count == 0) return "-";

    return string.Join(
        ",",
        query.Keys
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .OrderBy(key => key, StringComparer.OrdinalIgnoreCase));
}

static bool ShouldLogRequest(HttpContext context)
{
    var path = context.Request.Path;
    if (path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase)) return true;
    if (path.StartsWithSegments("/hubs", StringComparison.OrdinalIgnoreCase)) return true;
    if (path.StartsWithSegments("/_blazor/initializers", StringComparison.OrdinalIgnoreCase)) return false;

    if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method))
    {
        return false;
    }

    if (path.StartsWithSegments("/_framework", StringComparison.OrdinalIgnoreCase)) return false;
    if (path.StartsWithSegments("/_content", StringComparison.OrdinalIgnoreCase)) return false;

    var value = path.Value ?? "/";
    if (value.Equals("/", StringComparison.Ordinal)) return true;

    return string.IsNullOrWhiteSpace(Path.GetExtension(value));
}
