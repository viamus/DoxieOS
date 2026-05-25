namespace Viamus.Doxie.Orchestrator.Components.Builder;

public enum BuilderChatRole
{
    Assistant,
    User,
    ToolCall,
}

/// <summary>
/// Minimal mock message shape for the AI Builder chat panel mockup.
/// Not persisted — purely for laying out the visual.
/// </summary>
public sealed record BuilderChatMessage(
    BuilderChatRole Role,
    string Body,
    string? Detail = null,
    string? Icon = null);
