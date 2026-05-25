namespace Viamus.Doxie.Orchestrator.Agents;

public sealed partial class ClaudeProcessAgentRunner
{
    private static string BuildOperatorHintsFilePath(string? resolvedWorkingDirectory, string runId)
    {
        var root = string.IsNullOrWhiteSpace(resolvedWorkingDirectory)
            ? Directory.GetCurrentDirectory()
            : resolvedWorkingDirectory;
        var dir = Path.Combine(root, ".doxie", "run-inputs");
        return Path.Combine(dir, $"operator-hints-{runId}.md");
    }

    private static string AppendOperatorHintInstructions(string formattedPrompt, string operatorHintsFile, string? resolvedWorkingDirectory)
    {
        var root = string.IsNullOrWhiteSpace(resolvedWorkingDirectory)
            ? Directory.GetCurrentDirectory()
            : resolvedWorkingDirectory;
        var displayPath = Path.GetRelativePath(root, operatorHintsFile).Replace('\\', '/');
        return $"""
            {formattedPrompt}

            ---

            ## Live operator hints and conversation guidance

            DoxieOS may append human guidance while this run is executing.
            `$DOXIE_OPERATOR_HINTS_FILE` points to `{displayPath}` and is created before the run starts.

            Treat that file as live, high-priority operator steering:
            - Read it at the beginning of the run.
            - At the start of every new work cycle or instruction batch, check whether the file has new sections and incorporate them before continuing.
            - Re-read it after long tool/output pauses, before irreversible actions, and before the final response.
            - Incorporate new hints into the current plan or artifact; do not merely acknowledge them.
            - If a newer hint conflicts with the original task, follow the newest safe instruction and call out the conflict briefly.
            - In chat/builder sessions, treat follow-up messages as edit instructions and update the draft/work artifact when enough information is available.
            """;
    }

    private static void EnsureOperatorHintsFile(string path)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }

            if (!File.Exists(path))
            {
                File.WriteAllText(path, InitialOperatorHintsFileContent(), System.Text.Encoding.UTF8);
            }
        }
        catch
        {
            // Console output still records hints if the file cannot be prepared.
        }
    }

    private static string InitialOperatorHintsFileContent() =>
        """
        # Operator Hints

        DoxieOS appends human guidance for this live run below.

        - Treat newer hints as high-priority human steering.
        - Check this file at the start of every new work cycle or instruction batch.
        - Re-read this file after long tool/output pauses, before irreversible actions, and before final output.
        - Fold hints into the current plan or artifact; do not only acknowledge them.
        - If a hint conflicts with the original task, follow the newest safe instruction and mention the conflict.
        """;

    private static string FormatLiveOperatorHintMessage(string hint) =>
        $"""
        Operator guidance for the current run:

        {hint}

        Apply this hint before continuing the current work cycle. If it conflicts with earlier instructions, follow the newest safe instruction and mention the conflict briefly.
        """;

    private static void AppendOperatorHintFile(string path, string hint)
    {
        try
        {
            EnsureOperatorHintsFile(path);
            File.AppendAllText(
                path,
                $"{Environment.NewLine}## {DateTimeOffset.UtcNow:O}{Environment.NewLine}{hint}{Environment.NewLine}",
                System.Text.Encoding.UTF8);
        }
        catch
        {
            // Console output still records the hint if the file append fails.
        }
    }

    private static string NormalizeOperatorHint(string hint)
    {
        var clean = hint.Trim();
        return clean.Length <= 4_000 ? clean : clean[..4_000].TrimEnd() + "...";
    }
}
