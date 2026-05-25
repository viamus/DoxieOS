namespace Viamus.Doxie.Orchestrator.Agents;

public interface IAgentCatalog
{
    IReadOnlyList<AgentDescriptor> GetAll();

    AgentDescriptor? FindById(string id);

    /// <summary>
    /// Discards any cached agent list and forces the next
    /// <see cref="GetAll"/> call to re-scan the skills directory. Used
    /// after an external write (e.g. the Agent Builder promotes a draft
    /// to disk) so a freshly-saved agent appears without an app restart.
    /// </summary>
    void Refresh();

    /// <summary>
    /// Reads the body for the named skill. When <paramref name="subcommand"/>
    /// is supplied the catalog tries a subcommand-specific file first
    /// (e.g. <c>from-files.md</c> before <c>body.md</c>), falling back to
    /// the main body when no subcommand file exists. Returns <c>null</c>
    /// when neither file is found. Used by providers (notably
    /// <see cref="CodexProvider"/>) that need to inject the skill's
    /// natural-language instructions into the prompt — Claude Code reads
    /// it natively from disk via <c>/skill</c> and ignores the returned text.
    /// </summary>
    string? ReadSkillBody(string skillName, string? subcommand = null);
}
