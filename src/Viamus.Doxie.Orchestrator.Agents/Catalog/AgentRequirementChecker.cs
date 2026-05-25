using System.Text.Json;

namespace Viamus.Doxie.Orchestrator.Agents;

/// <summary>
/// Default checker. All probes are filesystem/process-environment local —
/// no network, no IPC. Results aren't cached: a single page render does at
/// most a handful of cheap reads, and the user expects the indicator to
/// reflect the live state of the machine.
/// </summary>
public sealed class AgentRequirementChecker : IAgentRequirementChecker
{
    public AgentRequirementStatus Check(AgentRequirement requirement) =>
        Check(requirement, suppliedEnv: null);

    public AgentRequirementStatus Check(AgentRequirement requirement, IReadOnlyDictionary<string, string>? suppliedEnv) => requirement.Kind switch
    {
        AgentRequirementKind.Env => CheckEnvVar(requirement.Name, requirement.SearchPaths, suppliedEnv),
        AgentRequirementKind.Tool => CheckTool(requirement.Name),
        AgentRequirementKind.Mcp => CheckMcpServer(requirement.Name),
        _ => AgentRequirementStatus.Unknown,
    };

    private static AgentRequirementStatus CheckEnvVar(
        string name,
        IReadOnlyList<string>? searchPaths,
        IReadOnlyDictionary<string, string>? suppliedEnv)
    {
        // Per-run / per-workflow supplied env wins over the process env
        // when present — that matches the runtime merge order
        // (envOverrides are applied last in ClaudeProcessAgentRunner).
        if (suppliedEnv is not null
            && suppliedEnv.TryGetValue(name, out var supplied)
            && !string.IsNullOrWhiteSpace(supplied))
        {
            return AgentRequirementStatus.Ok;
        }

        var value = Environment.GetEnvironmentVariable(name);
        if (!string.IsNullOrWhiteSpace(value)) return AgentRequirementStatus.Ok;

        if (searchPaths is null) return AgentRequirementStatus.Missing;

        foreach (var path in searchPaths)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            // Resolve relative .env paths (e.g. ".sample-watch/.env") against
            // the project root, not against the orchestrator's process
            // cwd — which can be bin/Debug/net10.0/ when launched from
            // Visual Studio. Without this, the QUALITY_TOKEN check renders
            // as Missing even though the file is sitting at the expected
            // location alongside the project.
            var resolved = StorageOptions.ResolvePath(path);
            if (!File.Exists(resolved)) continue;
            try
            {
                foreach (var rawLine in File.ReadAllLines(resolved))
                {
                    var line = rawLine.Trim();
                    if (line.Length == 0 || line.StartsWith('#')) continue;
                    var idx = line.IndexOf('=');
                    if (idx <= 0) continue;
                    var key = line[..idx].Trim();
                    if (!string.Equals(key, name, StringComparison.Ordinal)) continue;
                    var raw = line[(idx + 1)..].Trim().Trim('"').Trim('\'');
                    if (!string.IsNullOrWhiteSpace(raw)) return AgentRequirementStatus.Ok;
                }
            }
            catch (IOException) { /* race / perms — skip and try next path */ }
        }
        return AgentRequirementStatus.Missing;
    }

    private static readonly string[] WindowsExecutableSuffixes =
        [".exe", ".cmd", ".bat", ".ps1", ".com", ""];

    private static AgentRequirementStatus CheckTool(string name)
    {
        // Split on whitespace so "dotnet script" probes for "dotnet" — the
        // base CLI is the meaningful binary; subcommands aren't separate
        // executables. Sidecars that need a tighter check should split it
        // into two requirement entries.
        var primary = name.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(primary))
        {
            return AgentRequirementStatus.Unknown;
        }

        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path))
        {
            return AgentRequirementStatus.Unknown;
        }

        var separator = OperatingSystem.IsWindows() ? ';' : ':';
        var suffixes = OperatingSystem.IsWindows()
            ? WindowsExecutableSuffixes
            : [""];

        foreach (var dir in path.Split(separator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var suffix in suffixes)
            {
                try
                {
                    var candidate = Path.Combine(dir.Trim(), primary + suffix);
                    if (File.Exists(candidate))
                    {
                        return AgentRequirementStatus.Ok;
                    }
                }
                catch (ArgumentException)
                {
                    // PATH entry contained illegal characters — skip and continue.
                }
            }
        }
        return AgentRequirementStatus.Missing;
    }

    private static AgentRequirementStatus CheckMcpServer(string name)
    {
        // Resolution order matches Claude Code's: project-scoped .mcp.json
        // wins over the user-scoped ~/.claude.json. We don't replicate the
        // full lookup chain — both files are JSON with a top-level
        // `mcpServers` object whose keys are the server names.
        // Project file is resolved against the project root (not raw cwd)
        // so it works even when DoxieOS is launched from bin/Debug/.
        var projectFile = StorageOptions.ResolvePath(".mcp.json");
        if (HasMcpServer(projectFile, name)) return AgentRequirementStatus.Ok;

        var userFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".claude.json");
        if (HasMcpServer(userFile, name)) return AgentRequirementStatus.Ok;

        return AgentRequirementStatus.Missing;
    }

    private static bool HasMcpServer(string path, string name)
    {
        if (!File.Exists(path)) return false;
        try
        {
            using var stream = File.OpenRead(path);
            using var doc = JsonDocument.Parse(stream);
            if (!doc.RootElement.TryGetProperty("mcpServers", out var servers)) return false;
            if (servers.ValueKind != JsonValueKind.Object) return false;
            foreach (var prop in servers.EnumerateObject())
            {
                if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        catch (JsonException) { /* malformed config — treat as not configured */ }
        catch (IOException) { /* file race or perms — treat as not configured */ }
        return false;
    }
}
