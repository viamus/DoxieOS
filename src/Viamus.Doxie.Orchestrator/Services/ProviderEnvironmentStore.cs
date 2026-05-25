using System.Text;
using System.Text.Json;
using Viamus.Doxie.Orchestrator.Agents;

namespace Viamus.Doxie.Orchestrator.Services;

public sealed class ProviderEnvironmentStore
{
    private const string McpServersKey = "provider_mcp_servers";
    private const string CodexBegin = "# BEGIN DOXIE MCP SERVERS";
    private const string CodexEnd = "# END DOXIE MCP SERVERS";
    private readonly IKeyValueSettingsStore _settings;
    private readonly string? _homePath;
    private readonly string? _workspaceRoot;
    private readonly bool _writeProviderConfigs;
    private readonly Func<string, string?> _environmentProvider;

    public ProviderEnvironmentStore(
        IKeyValueSettingsStore settings,
        string? homePath = null,
        string? workspaceRoot = null,
        bool writeProviderConfigs = true,
        Func<string, string?>? environmentProvider = null)
    {
        _settings = settings;
        _homePath = homePath;
        _workspaceRoot = workspaceRoot;
        _writeProviderConfigs = writeProviderConfigs;
        _environmentProvider = environmentProvider ?? Environment.GetEnvironmentVariable;
    }

    public string HomePath => _homePath ?? ResolveHomePath();
    private string WorkspaceRoot => _workspaceRoot ?? StorageOptions.ResolvePath(".");

    public IReadOnlyList<ProviderAuthStatus> GetAuthStatuses()
    {
        var home = HomePath;
        var claudeJson = Path.Combine(home, ".claude.json");
        var claudeDir = Path.Combine(home, ".claude");
        var codexDir = Path.Combine(home, ".codex");
        var codexAuth = Path.Combine(codexDir, "auth.json");
        var codexConfig = Path.Combine(codexDir, "config.toml");

        return new[]
        {
            new ProviderAuthStatus(
                "claude-code",
                home,
                claudeJson,
                !string.IsNullOrWhiteSpace(_environmentProvider("ANTHROPIC_API_KEY")) || File.Exists(claudeJson) || Directory.Exists(claudeDir),
                !string.IsNullOrWhiteSpace(_environmentProvider("ANTHROPIC_API_KEY"))
                    ? "ANTHROPIC_API_KEY configured"
                    : File.Exists(claudeJson)
                        ? "~/.claude.json found"
                        : Directory.Exists(claudeDir) ? "~/.claude found" : "No Claude auth/config files found"),
            new ProviderAuthStatus(
                "codex",
                home,
                codexConfig,
                !string.IsNullOrWhiteSpace(_environmentProvider("OPENAI_API_KEY")) || File.Exists(codexAuth) || File.Exists(codexConfig),
                !string.IsNullOrWhiteSpace(_environmentProvider("OPENAI_API_KEY"))
                    ? "OPENAI_API_KEY configured"
                    : File.Exists(codexAuth)
                        ? "~/.codex/auth.json found"
                        : File.Exists(codexConfig) ? "~/.codex/config.toml found" : "No Codex auth/config files found"),
        };
    }

    public IReadOnlyList<McpServerDefinition> ListMcpServers()
    {
        var json = _settings.Get(McpServersKey);
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<McpServerDefinition>();
        try
        {
            return DeduplicateMcpServers(JsonSerializer.Deserialize<List<McpServerDefinition>>(json, JsonOptions()) ?? new List<McpServerDefinition>());
        }
        catch
        {
            return Array.Empty<McpServerDefinition>();
        }
    }

    public int ImportGlobalMcpServers()
    {
        var current = DeduplicateMcpServers(ListMcpServers());
        var before = current.Select(McpFingerprint).Where(f => !string.IsNullOrWhiteSpace(f)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var merged = current;

        foreach (var discovered in DiscoverGlobalMcpServers())
        {
            merged = DeduplicateMcpServers(merged.Concat(new[] { discovered }));
        }

        var saved = SaveMcpServers(merged);
        return saved
            .Select(McpFingerprint)
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Count(f => !before.Contains(f));
    }

    public void SyncProviderConfigs()
    {
        var servers = DeduplicateMcpServers(ListMcpServers());
        if (!_writeProviderConfigs) return;
        WriteClaudeMcpJson(servers);
        WriteCodexConfig(servers);
    }

    public IReadOnlyList<McpServerDefinition> SaveMcpServers(IEnumerable<McpServerDefinition> servers)
    {
        var clean = DeduplicateMcpServers(servers);

        _settings.Set(McpServersKey, JsonSerializer.Serialize(clean, JsonOptions()));
        if (_writeProviderConfigs)
        {
            WriteClaudeMcpJson(clean);
            WriteCodexConfig(clean);
        }
        return clean;
    }

    private static List<McpServerDefinition> DeduplicateMcpServers(IEnumerable<McpServerDefinition> servers)
    {
        // Guardrail: provider configs can expose the same MCP more than once
        // under different ids (common when Doxie re-emits a managed block
        // beside a pre-existing global Codex/Claude entry). The Doxie registry
        // treats id as primary identity, but falls back to a stable endpoint
        // fingerprint so "same command+args" or "same HTTP URL" cannot produce
        // duplicate rows or duplicate provider config on save/import.
        var byId = new Dictionary<string, McpServerDefinition>(StringComparer.OrdinalIgnoreCase);
        var byFingerprint = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in servers
            .Select(Normalize)
            .Where(s => !string.IsNullOrWhiteSpace(s.Id)))
        {
            var fingerprint = McpFingerprint(candidate);
            if (byId.TryGetValue(candidate.Id, out var sameId))
            {
                byId[candidate.Id] = MergeMcp(sameId, candidate);
                if (!string.IsNullOrWhiteSpace(fingerprint)) byFingerprint[fingerprint] = candidate.Id;
                continue;
            }

            if (!string.IsNullOrWhiteSpace(fingerprint) &&
                byFingerprint.TryGetValue(fingerprint, out var existingId) &&
                byId.TryGetValue(existingId, out var sameMcp))
            {
                byId[existingId] = MergeMcp(sameMcp, candidate);
                continue;
            }

            byId[candidate.Id] = candidate;
            if (!string.IsNullOrWhiteSpace(fingerprint)) byFingerprint[fingerprint] = candidate.Id;
        }

        return byId.Values
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static McpServerDefinition Normalize(McpServerDefinition server)
    {
        var id = Slug(server.Id);
        if (string.IsNullOrWhiteSpace(id)) id = Slug(server.Name);
        var name = string.IsNullOrWhiteSpace(server.Name) ? id : server.Name.Trim();
        var transport = string.Equals(server.Transport, "http", StringComparison.OrdinalIgnoreCase)
            ? "http"
            : "stdio";
        var providers = (server.Providers ?? Array.Empty<string>())
            .Where(p => p is "claude-code" or "codex")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return server with
        {
            Id = id,
            Name = name,
            Transport = transport,
            Command = server.Command?.Trim(),
            Arguments = server.Arguments?.Trim(),
            Url = server.Url?.Trim(),
            Environment = server.Environment?.Trim(),
            ExtraJson = string.IsNullOrWhiteSpace(server.ExtraJson) ? null : server.ExtraJson.Trim(),
            Providers = providers,
        };
    }

    private static McpServerDefinition MergeMcp(McpServerDefinition current, McpServerDefinition incoming)
    {
        var providers = current.Providers
            .Concat(incoming.Providers)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return current with
        {
            Name = Prefer(current.Name, incoming.Name, current.Id),
            Transport = Prefer(current.Transport, incoming.Transport, "stdio"),
            Command = Prefer(current.Command, incoming.Command),
            Arguments = Prefer(current.Arguments, incoming.Arguments),
            Url = Prefer(current.Url, incoming.Url),
            Environment = Prefer(current.Environment, incoming.Environment),
            ExtraJson = MergeExtraJson(current.ExtraJson, incoming.ExtraJson),
            Providers = providers,
            Enabled = current.Enabled || incoming.Enabled,
        };
    }

    private static string Prefer(string? current, string? incoming, string fallback = "") =>
        !string.IsNullOrWhiteSpace(current) ? current
        : !string.IsNullOrWhiteSpace(incoming) ? incoming
        : fallback;

    private static string? MergeExtraJson(string? current, string? incoming)
    {
        if (string.IsNullOrWhiteSpace(current)) return incoming;
        if (string.IsNullOrWhiteSpace(incoming)) return current;
        try
        {
            var currentObject = JsonSerializer.Deserialize<Dictionary<string, object?>>(current, JsonOptions())
                ?? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            var incomingObject = JsonSerializer.Deserialize<Dictionary<string, object?>>(incoming, JsonOptions())
                ?? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, value) in incomingObject)
            {
                currentObject.TryAdd(key, value);
            }
            return JsonSerializer.Serialize(currentObject, JsonOptions());
        }
        catch
        {
            return current;
        }
    }

    private static string McpFingerprint(McpServerDefinition server)
    {
        if (server.Transport == "http")
        {
            return string.IsNullOrWhiteSpace(server.Url)
                ? string.Empty
                : $"http|{server.Url.Trim().TrimEnd('/').ToLowerInvariant()}";
        }

        var command = (server.Command ?? string.Empty).Trim().ToLowerInvariant();
        var args = string.Join(' ', SplitArgs(server.Arguments)).Trim().ToLowerInvariant();
        return string.IsNullOrWhiteSpace(command) && string.IsNullOrWhiteSpace(args)
            ? string.Empty
            : $"stdio|{command}|{args}";
    }

    private static string Slug(string? value)
    {
        var sb = new StringBuilder();
        foreach (var ch in (value ?? string.Empty).Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch)) sb.Append(ch);
            else if (ch is '-' or '_' or ' ') sb.Append('-');
        }
        return string.Join('-', sb.ToString().Split('-', StringSplitOptions.RemoveEmptyEntries));
    }

    private void WriteClaudeMcpJson(IReadOnlyList<McpServerDefinition> servers)
    {
        var selected = servers
            .Where(s => s.Enabled && s.Providers.Contains("claude-code", StringComparer.OrdinalIgnoreCase))
            .ToList();
        var global = DiscoverClaudeMcpServers().Select(Normalize).ToList();
        selected = selected
            .Where(s => !HasMcpEquivalent(s, global))
            .ToList();

        var root = WorkspaceRoot;
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, ".mcp.json");
        var mcpServers = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var server in selected)
        {
            mcpServers[server.Id] = ToClaudeServer(server);
        }

        var payload = new Dictionary<string, object?>
        {
            ["mcpServers"] = mcpServers,
        };
        File.WriteAllText(path, JsonSerializer.Serialize(payload, JsonOptions()), Encoding.UTF8);
    }

    private IEnumerable<McpServerDefinition> DiscoverGlobalMcpServers()
    {
        foreach (var server in DiscoverClaudeMcpServers())
        {
            yield return server;
        }
        foreach (var server in DiscoverCodexMcpServers())
        {
            yield return server;
        }
    }

    private IEnumerable<McpServerDefinition> DiscoverClaudeMcpServers()
    {
        var home = HomePath;
        var candidates = new List<string>
        {
            Path.Combine(home, ".claude.json"),
            Path.Combine(home, ".claude", "mcp.json"),
        };

        foreach (var path in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var server in ReadClaudeMcpServers(path))
            {
                yield return server;
            }
        }
    }

    private IEnumerable<McpServerDefinition> ReadClaudeMcpServers(string path)
    {
        if (!File.Exists(path)) yield break;

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
        }
        catch
        {
            yield break;
        }

        using (document)
        {
            if (!document.RootElement.TryGetProperty("mcpServers", out var mcpServers) ||
                mcpServers.ValueKind != JsonValueKind.Object)
            {
                yield break;
            }

            foreach (var serverProperty in mcpServers.EnumerateObject())
            {
                if (TryReadJsonMcpServer(serverProperty.Name, serverProperty.Value, "claude-code") is { } server)
                {
                    yield return server;
                }
            }
        }
    }

    private IEnumerable<McpServerDefinition> DiscoverCodexMcpServers()
    {
        var path = Path.Combine(HomePath, ".codex", "config.toml");
        if (!File.Exists(path)) yield break;

        foreach (var server in ReadCodexMcpServers(RemoveGeneratedBlock(File.ReadAllText(path, Encoding.UTF8))))
        {
            yield return server;
        }
    }

    private static IEnumerable<McpServerDefinition> ReadCodexMcpServers(string content)
    {
        var valuesById = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var envById = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var extraById = new Dictionary<string, Dictionary<string, Dictionary<string, string>>>(StringComparer.OrdinalIgnoreCase);
        var currentId = string.Empty;
        var currentNested = string.Empty;
        var inEnv = false;

        foreach (var raw in ReadLines(content))
        {
            var line = raw.Trim();
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#')) continue;
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                inEnv = false;
                var section = line.Trim('[', ']');
                if (section.StartsWith("mcp_servers.", StringComparison.OrdinalIgnoreCase))
                {
                    var rest = section["mcp_servers.".Length..];
                    var parts = rest.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    currentId = parts.Length > 0 ? parts[0] : string.Empty;
                    currentNested = parts.Length > 1 ? string.Join('.', parts.Skip(1)) : string.Empty;
                    if (string.Equals(currentNested, "env", StringComparison.OrdinalIgnoreCase))
                    {
                        inEnv = true;
                    }
                    _ = valuesById.TryAdd(currentId, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
                    _ = envById.TryAdd(currentId, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
                    _ = extraById.TryAdd(currentId, new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase));
                    if (!string.IsNullOrWhiteSpace(currentNested) && !inEnv)
                    {
                        _ = extraById[currentId].TryAdd(currentNested, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
                    }
                }
                else
                {
                    currentId = string.Empty;
                    currentNested = string.Empty;
                }
                continue;
            }

            if (string.IsNullOrWhiteSpace(currentId)) continue;
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim();
            if (inEnv)
            {
                envById[currentId][key] = UnquoteToml(value);
            }
            else if (!string.IsNullOrWhiteSpace(currentNested))
            {
                extraById[currentId][currentNested][key] = UnquoteToml(value);
            }
            else
            {
                valuesById[currentId][key] = value;
            }
        }

        foreach (var id in valuesById.Keys.Concat(envById.Keys).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            valuesById.TryGetValue(id, out var values);
            envById.TryGetValue(id, out var env);
            yield return FromCodexSection(
                id,
                values ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                env ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                extraById.TryGetValue(id, out var extra) ? extra : new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase));
        }
    }

    private static McpServerDefinition FromCodexSection(
        string id,
        Dictionary<string, string> values,
        Dictionary<string, string> env,
        Dictionary<string, Dictionary<string, string>> extra)
    {
        var transport = values.TryGetValue("transport", out var transportValue)
            ? UnquoteToml(transportValue)
            : values.ContainsKey("url") ? "http" : "stdio";
        var args = values.TryGetValue("args", out var argsValue)
            ? string.Join(' ', ParseTomlArray(argsValue))
            : null;

        return new McpServerDefinition(
            id,
            id,
            transport,
            values.TryGetValue("command", out var command) ? UnquoteToml(command) : null,
            args,
            values.TryGetValue("url", out var url) ? UnquoteToml(url) : null,
            env.Count == 0 ? null : string.Join(Environment.NewLine, env.Select(kv => $"{kv.Key}={kv.Value}")),
            new[] { "codex" },
            true,
            ExtraJsonFromNestedToml(extra));
    }

    private static McpServerDefinition? TryReadJsonMcpServer(string id, JsonElement value, string providerId)
    {
        if (value.ValueKind != JsonValueKind.Object) return null;

        var transport = TryGetString(value, "transport") ?? TryGetString(value, "type") ?? (TryGetString(value, "url") is null ? "stdio" : "http");
        var args = value.TryGetProperty("args", out var argsElement) && argsElement.ValueKind == JsonValueKind.Array
            ? string.Join(' ', argsElement.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()).Where(s => !string.IsNullOrWhiteSpace(s)))
            : null;
        var env = value.TryGetProperty("env", out var envElement) && envElement.ValueKind == JsonValueKind.Object
            ? string.Join(Environment.NewLine, envElement.EnumerateObject().Select(p => $"{p.Name}={JsonElementToString(p.Value)}"))
            : null;
        var extraJson = ExtraJsonFromJson(value);

        return new McpServerDefinition(
            id,
            id,
            transport,
            TryGetString(value, "command"),
            args,
            TryGetString(value, "url"),
            env,
            new[] { providerId },
            true,
            extraJson);
    }

    private void WriteCodexConfig(IReadOnlyList<McpServerDefinition> servers)
    {
        var selected = servers
            .Where(s => s.Enabled && s.Providers.Contains("codex", StringComparer.OrdinalIgnoreCase))
            .ToList();

        var codexDir = Path.Combine(HomePath, ".codex");
        Directory.CreateDirectory(codexDir);
        var path = Path.Combine(codexDir, "config.toml");
        var existing = File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : string.Empty;
        var global = ReadCodexMcpServers(RemoveGeneratedBlock(existing)).Select(Normalize).ToList();
        selected = selected
            .Where(s => !HasMcpEquivalent(s, global))
            .ToList();
        var generated = BuildCodexMcpBlock(selected);
        File.WriteAllText(path, ReplaceGeneratedBlock(existing, generated), Encoding.UTF8);
    }

    private static Dictionary<string, object?> ToClaudeServer(McpServerDefinition server)
    {
        if (server.Transport == "http")
        {
            var httpPayload = ReadExtraJsonObject(server.ExtraJson);
            httpPayload["type"] = "http";
            httpPayload["url"] = server.Url;
            return httpPayload;
        }

        var payload = ReadExtraJsonObject(server.ExtraJson);
        payload["command"] = server.Command;
        var args = SplitArgs(server.Arguments);
        if (args.Count > 0) payload["args"] = args;
        var env = ParseEnvironment(server.Environment);
        if (env.Count > 0) payload["env"] = env;
        return payload;
    }

    private static string BuildCodexMcpBlock(IReadOnlyList<McpServerDefinition> servers)
    {
        var sb = new StringBuilder();
        sb.AppendLine(CodexBegin);
        foreach (var server in servers)
        {
            sb.AppendLine();
            sb.Append("[mcp_servers.").Append(TomlBareKey(server.Id)).AppendLine("]");
            if (server.Transport == "http")
            {
                sb.AppendLine("transport = \"http\"");
                sb.Append("url = ").AppendLine(TomlString(server.Url ?? string.Empty));
                AppendExtraToml(sb, server.Id, server.ExtraJson);
            }
            else
            {
                sb.Append("command = ").AppendLine(TomlString(server.Command ?? string.Empty));
                var args = SplitArgs(server.Arguments);
                if (args.Count > 0)
                {
                    sb.Append("args = [");
                    sb.Append(string.Join(", ", args.Select(TomlString)));
                    sb.AppendLine("]");
                }
                var env = ParseEnvironment(server.Environment);
                if (env.Count > 0)
                {
                    sb.Append("[mcp_servers.").Append(TomlBareKey(server.Id)).AppendLine(".env]");
                    foreach (var (key, value) in env.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
                    {
                        sb.Append(TomlBareKey(key)).Append(" = ").AppendLine(TomlString(value));
                    }
                }
                AppendExtraToml(sb, server.Id, server.ExtraJson);
            }
        }
        sb.AppendLine();
        sb.AppendLine(CodexEnd);
        return sb.ToString().TrimEnd();
    }

    private static string ReplaceGeneratedBlock(string existing, string generated)
    {
        if (string.IsNullOrWhiteSpace(existing)) return generated + Environment.NewLine;
        var withoutGenerated = RemoveGeneratedBlock(existing);
        return string.IsNullOrWhiteSpace(withoutGenerated)
            ? generated + Environment.NewLine
            : withoutGenerated.TrimEnd() + Environment.NewLine + Environment.NewLine + generated + Environment.NewLine;
    }

    private static string RemoveGeneratedBlock(string existing)
    {
        if (string.IsNullOrWhiteSpace(existing)) return existing;
        var start = existing.IndexOf(CodexBegin, StringComparison.Ordinal);
        var end = existing.IndexOf(CodexEnd, StringComparison.Ordinal);
        if (start < 0 || end < start) return existing;
        end += CodexEnd.Length;
        return existing[..start].TrimEnd() + Environment.NewLine + existing[end..].TrimStart();
    }

    private static bool HasMcpEquivalent(McpServerDefinition server, IReadOnlyList<McpServerDefinition> servers)
    {
        var candidate = Normalize(server);
        var fingerprint = McpFingerprint(candidate);
        return servers.Any(existing =>
            string.Equals(existing.Id, candidate.Id, StringComparison.OrdinalIgnoreCase) ||
            (!string.IsNullOrWhiteSpace(fingerprint) &&
             string.Equals(McpFingerprint(existing), fingerprint, StringComparison.OrdinalIgnoreCase)));
    }

    private static IEnumerable<string> ReadLines(string content)
    {
        using var reader = new StringReader(content);
        while (reader.ReadLine() is { } line)
        {
            yield return line;
        }
    }

    private static IReadOnlyList<string> SplitArgs(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? Array.Empty<string>()
            : value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string? TryGetString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property)) return null;
        if (property.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return null;
        return property.ValueKind == JsonValueKind.String ? property.GetString() : JsonElementToString(property);
    }

    private static string JsonElementToString(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString() ?? string.Empty,
        JsonValueKind.Number => element.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => element.GetRawText(),
    };

    private static string UnquoteToml(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[^1] == '"')
        {
            try
            {
                return JsonSerializer.Deserialize<string>(trimmed) ?? string.Empty;
            }
            catch
            {
                return trimmed[1..^1];
            }
        }
        return trimmed;
    }

    private static IReadOnlyList<string> ParseTomlArray(string value)
    {
        var trimmed = value.Trim();
        if (!trimmed.StartsWith('[') || !trimmed.EndsWith(']')) return Array.Empty<string>();

        var result = new List<string>();
        var current = new StringBuilder();
        var inString = false;
        var escaped = false;
        foreach (var ch in trimmed[1..^1])
        {
            if (escaped)
            {
                current.Append(ch);
                escaped = false;
                continue;
            }
            if (ch == '\\' && inString)
            {
                escaped = true;
                current.Append(ch);
                continue;
            }
            if (ch == '"')
            {
                inString = !inString;
                current.Append(ch);
                continue;
            }
            if (ch == ',' && !inString)
            {
                AddTomlArrayValue(result, current.ToString());
                current.Clear();
                continue;
            }
            current.Append(ch);
        }
        AddTomlArrayValue(result, current.ToString());
        return result;
    }

    private static void AddTomlArrayValue(List<string> values, string raw)
    {
        var value = UnquoteToml(raw);
        if (!string.IsNullOrWhiteSpace(value)) values.Add(value);
    }

    private static string? ExtraJsonFromJson(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) return null;
        var extra = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in value.EnumerateObject())
        {
            if (IsKnownJsonMcpProperty(property.Name)) continue;
            extra[property.Name] = property.Value.Clone();
        }
        return extra.Count == 0 ? null : JsonSerializer.Serialize(extra, JsonOptions());
    }

    private static string? ExtraJsonFromNestedToml(Dictionary<string, Dictionary<string, string>> extra)
    {
        if (extra.Count == 0) return null;
        var payload = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (section, values) in extra)
        {
            Dictionary<string, object?> cursor = payload;
            var parts = section.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            for (var i = 0; i < parts.Length; i++)
            {
                var part = parts[i];
                if (i == parts.Length - 1)
                {
                    cursor[part] = values;
                    continue;
                }

                if (!cursor.TryGetValue(part, out var next) || next is not Dictionary<string, object?> nested)
                {
                    nested = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                    cursor[part] = nested;
                }
                cursor = nested;
            }
        }
        return JsonSerializer.Serialize(payload, JsonOptions());
    }

    private static Dictionary<string, object?> ReadExtraJsonObject(string? extraJson)
    {
        if (string.IsNullOrWhiteSpace(extraJson)) return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, object?>>(extraJson, JsonOptions())
                ?? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static void AppendExtraToml(StringBuilder sb, string serverId, string? extraJson)
    {
        var extra = ReadExtraJsonObject(extraJson);
        foreach (var (key, value) in extra)
        {
            AppendTomlValue(sb, $"mcp_servers.{TomlBareKey(serverId)}.{TomlBareKey(key)}", value);
        }
    }

    private static void AppendTomlValue(StringBuilder sb, string section, object? value)
    {
        if (value is JsonElement json)
        {
            if (json.ValueKind == JsonValueKind.Object)
            {
                sb.Append('[').Append(section).AppendLine("]");
                foreach (var property in json.EnumerateObject())
                {
                    if (property.Value.ValueKind == JsonValueKind.Object)
                    {
                        AppendTomlValue(sb, $"{section}.{TomlBareKey(property.Name)}", property.Value);
                    }
                    else
                    {
                        sb.Append(TomlBareKey(property.Name)).Append(" = ").AppendLine(TomlScalar(property.Value));
                    }
                }
            }
            return;
        }

        if (value is Dictionary<string, object?> map)
        {
            sb.Append('[').Append(section).AppendLine("]");
            foreach (var (key, child) in map)
            {
                AppendTomlValue(sb, $"{section}.{TomlBareKey(key)}", child);
            }
        }
    }

    private static string TomlScalar(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => TomlString(value.GetString() ?? string.Empty),
        JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Array => "[" + string.Join(", ", value.EnumerateArray().Select(TomlScalar)) + "]",
        _ => TomlString(value.GetRawText()),
    };

    private static bool IsKnownJsonMcpProperty(string name) =>
        name is "command" or "args" or "env" or "type" or "transport" or "url";

    private static Dictionary<string, string> ParseEnvironment(string? value)
    {
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(value)) return env;
        foreach (var line in value.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var index = line.IndexOf('=');
            if (index <= 0) continue;
            env[line[..index].Trim()] = line[(index + 1)..].Trim();
        }
        return env;
    }

    private static string TomlBareKey(string value) =>
        string.Join("_", value.Split(new[] { '-', ' ', '.' }, StringSplitOptions.RemoveEmptyEntries));

    private static string TomlString(string value) =>
        JsonSerializer.Serialize(value);

    private static JsonSerializerOptions JsonOptions() => new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static string ResolveHomePath()
    {
        var envHome = Environment.GetEnvironmentVariable("HOME");
        if (!string.IsNullOrWhiteSpace(envHome)) return envHome;
        var userProfile = Environment.GetEnvironmentVariable("USERPROFILE");
        if (!string.IsNullOrWhiteSpace(userProfile)) return userProfile;
        return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }
}

public sealed record ProviderAuthStatus(
    string ProviderId,
    string HomePath,
    string ConfigPath,
    bool IsAuthenticated,
    string Detail);

public sealed record McpServerDefinition(
    string Id,
    string Name,
    string Transport,
    string? Command,
    string? Arguments,
    string? Url,
    string? Environment,
    string[] Providers,
    bool Enabled,
    string? ExtraJson = null);
