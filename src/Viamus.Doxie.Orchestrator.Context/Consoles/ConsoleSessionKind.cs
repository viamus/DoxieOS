namespace Viamus.Doxie.Orchestrator.Context;

/// <summary>
/// Discriminator for the two flavours of <see cref="ConsoleSession"/>.
/// <list type="bullet">
///   <item><b>Workspace</b> — the standard <c>/consoles</c> session: bound to a registered Workspace, shown in the Consoles page sidebar, used for ad-hoc human work.</item>
///   <item><b>Builder</b> — an internal session backing the Agent / Workflow Builder pages. Spawned in a sandbox folder (no Workspace), filtered out of the regular Consoles list, the only consumer is the matching builder page.</item>
/// </list>
/// </summary>
public enum ConsoleSessionKind
{
    Workspace,
    Builder,
    Auth,
}
