using Viamus.Doxie.Orchestrator.Common;

namespace Viamus.Doxie.Orchestrator.Agents;

public sealed partial class ClaudeProcessAgentRunner
{
    private static (string Arguments, string DisplayArguments) MaterializeLargeArgumentsIfNeeded(
        string arguments,
        string? resolvedWorkingDirectory,
        string skillName)
    {
        if (arguments.Length <= MaxInlineArgumentsChars)
        {
            return (arguments, arguments);
        }

        var root = string.IsNullOrWhiteSpace(resolvedWorkingDirectory)
            ? Directory.GetCurrentDirectory()
            : resolvedWorkingDirectory;
        Directory.CreateDirectory(root);

        var dir = Path.Combine(root, ".doxie", "run-inputs");
        Directory.CreateDirectory(dir);
        var fileName = $"{SanitizeFileStem(skillName)}-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.md";
        var path = Path.Combine(dir, fileName);
        File.WriteAllText(path, arguments, System.Text.Encoding.UTF8);

        var displayPath = Path.GetRelativePath(root, path).Replace('\\', '/');
        var shortArguments = $"""
            The original invocation was too large to pass safely on the command line.
            Read the full invocation from `{displayPath}` and execute it exactly as if it had been provided inline.
            Do not summarize the file before acting.
            """;

        return (shortArguments.Trim(), $"input-file:{displayPath}");
    }

    private static (string Arguments, string DisplayArguments) KeepArgumentsInlineForStdinPrompt(string arguments)
    {
        if (arguments.Length <= MaxInlineArgumentsChars)
        {
            return (arguments, arguments);
        }

        return (arguments, $"stdin-payload:{arguments.Length} chars");
    }

    private static string MaterializeLargePromptIfNeeded(
        string formattedPrompt,
        string? resolvedWorkingDirectory,
        AgentDescriptor agent,
        IAgentProvider provider)
    {
        if (formattedPrompt.Length <= MaxInlinePromptChars)
        {
            return formattedPrompt;
        }

        var root = string.IsNullOrWhiteSpace(resolvedWorkingDirectory)
            ? Directory.GetCurrentDirectory()
            : resolvedWorkingDirectory;
        Directory.CreateDirectory(root);

        var dir = Path.Combine(root, ".doxie", "run-inputs");
        Directory.CreateDirectory(dir);
        var fileName = $"{SanitizeFileStem(agent.SkillName)}-prompt-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.md";
        var path = Path.Combine(dir, fileName);
        File.WriteAllText(path, formattedPrompt, System.Text.Encoding.UTF8);

        var displayPath = Path.GetRelativePath(root, path).Replace('\\', '/');
        if (string.Equals(provider.Id, "claude-code", StringComparison.OrdinalIgnoreCase))
        {
            var slashPrefix = ExtractSlashCommandPrefix(formattedPrompt) ?? $"/{agent.SkillName}";
            return $"""
                {slashPrefix} The full DoxieOS invocation was too large to pass safely on the command line.
                Read the full invocation from `{displayPath}` and execute the instructions in that file exactly.
                Do not summarize the file before acting.
                """.Trim();
        }

        return $"""
            The full DoxieOS invocation was too large to pass safely on the command line.
            Read the full invocation from `{displayPath}` and execute the instructions in that file exactly.
            Do not summarize the file before acting.
            """.Trim();
    }

    private static string? ExtractSlashCommandPrefix(string formattedPrompt)
    {
        var trimmed = formattedPrompt.TrimStart();
        if (!trimmed.StartsWith('/')) return null;

        var end = trimmed.IndexOfAny([' ', '\r', '\n', '\t']);
        var prefix = end < 0 ? trimmed : trimmed[..end];
        return prefix.Length > 1 ? prefix : null;
    }

    private static string SanitizeFileStem(string value)
    {
        var chars = value.Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '-').ToArray();
        var sanitized = new string(chars).Trim('-');
        return string.IsNullOrWhiteSpace(sanitized) ? "agent" : sanitized;
    }
}