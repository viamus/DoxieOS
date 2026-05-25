namespace Viamus.Doxie.Orchestrator.Agents;

/// <summary>
/// Configuration for the OpenAI Codex CLI provider. Override the
/// executable path in <c>appsettings.json</c> under the <c>Codex</c>
/// section if the binary is not on PATH or has a different name on a
/// particular machine.
/// </summary>
public sealed class CodexProviderOptions
{
    /// <summary>
    /// Executable spawned for each agent run via the Codex provider.
    /// Default <c>codex</c> — works out of the box if the OpenAI Codex
    /// CLI (<c>github.com/openai/codex</c>) is installed and on PATH.
    /// </summary>
    public string Executable { get; set; } = "codex";

    /// <summary>
    /// Model passed to <c>codex exec --model</c>. DoxieOS uses this to
    /// estimate token spend when the Codex JSON stream reports tokens
    /// but not USD cost.
    /// </summary>
    public string Model { get; set; } = "gpt-5.5";

    /// <summary>
    /// When <see langword="true"/>, passes
    /// <c>--dangerously-bypass-approvals-and-sandbox</c> in interactive
    /// (TUI) mode so Codex auto-approves every tool call without
    /// prompting the user. Defaults to <see langword="false"/>: the user
    /// is assumed to be at the terminal and should confirm calls
    /// themselves.
    /// </summary>
    public bool AutoApprove { get; set; } = false;
}
