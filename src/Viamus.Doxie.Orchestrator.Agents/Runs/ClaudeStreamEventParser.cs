using System.Text.Json;

namespace Viamus.Doxie.Orchestrator.Agents;

/// <summary>
/// Converts a single line of Claude CLI <c>--output-format stream-json</c>
/// output into zero or more human-readable lines for the UI. The CLI emits
/// one JSON event per line; switching to this format is what unblocks
/// real-time streaming, since plain text mode buffers stdout when not a TTY.
///
/// Lines that are not JSON (empty, plain text from a non-streaming child
/// process, parse errors) pass through as-is so non-Claude executables
/// (cmd.exe in tests) still work.
/// </summary>
public static class ClaudeStreamEventParser
{
    public static IEnumerable<string> Parse(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            yield break;
        }

        var trimmed = line.Trim();
        if (!trimmed.StartsWith('{'))
        {
            yield return trimmed;
            yield break;
        }

        JsonDocument? doc = TryParse(trimmed);
        if (doc is null)
        {
            yield return trimmed;
            yield break;
        }

        using (doc)
        {
            foreach (var pretty in Render(doc.RootElement))
            {
                yield return pretty;
            }
        }
    }

    /// <summary>
    /// Pulls token + cost accounting out of a Claude stream-json
    /// <c>result</c> event. Returns null for any other line shape
    /// (non-JSON, non-result events, malformed JSON), so callers can
    /// safely invoke this on every stdout line and only react when a
    /// usage event finally lands.
    ///
    /// Surface mapped from the CLI's <c>{"type":"result", ...}</c>:
    /// <list type="bullet">
    /// <item><c>usage.input_tokens</c> â†’ <see cref="AgentRunUsage.InputTokens"/></item>
    /// <item><c>usage.output_tokens</c> â†’ <see cref="AgentRunUsage.OutputTokens"/></item>
    /// <item><c>usage.cache_read_input_tokens</c> â†’ <see cref="AgentRunUsage.CacheReadTokens"/></item>
    /// <item><c>usage.cache_creation_input_tokens</c> â†’ <see cref="AgentRunUsage.CacheCreationTokens"/></item>
    /// <item><c>total_cost_usd</c> â†’ <see cref="AgentRunUsage.TotalCostUsd"/> (null when absent)</item>
    /// </list>
    /// Missing token fields default to 0 so the record stays usable
    /// for old/partial CLI versions that omit the cache split.
    /// </summary>
    public static AgentRunUsage? TryParseUsage(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        var trimmed = line.Trim();
        if (!trimmed.StartsWith('{')) return null;

        using var doc = TryParse(trimmed);
        if (doc is null) return null;

        var root = doc.RootElement;
        if (!root.TryGetProperty("type", out var typeElement)
            || typeElement.ValueKind != JsonValueKind.String
            || typeElement.GetString() != "result")
        {
            return null;
        }

        var input = 0L;
        var output = 0L;
        var cacheRead = 0L;
        var cacheCreation = 0L;
        if (root.TryGetProperty("usage", out var usage)
            && usage.ValueKind == JsonValueKind.Object)
        {
            input = GetInt64(usage, "input_tokens");
            output = GetInt64(usage, "output_tokens");
            cacheRead = GetInt64(usage, "cache_read_input_tokens");
            cacheCreation = GetInt64(usage, "cache_creation_input_tokens");
        }

        decimal? cost = null;
        if (root.TryGetProperty("total_cost_usd", out var costElement)
            && costElement.ValueKind == JsonValueKind.Number
            && costElement.TryGetDouble(out var costDouble))
        {
            cost = (decimal)costDouble;
        }

        // If a result event lands with no usage block AND no cost, it
        // carries no accounting signal — skip it so we don't overwrite
        // a real prior usage with zeros.
        if (input == 0 && output == 0 && cacheRead == 0 && cacheCreation == 0 && cost is null)
        {
            return null;
        }

        return new AgentRunUsage(input, output, cacheRead, cacheCreation, cost);
    }

    private static long GetInt64(JsonElement obj, string propertyName) =>
        obj.TryGetProperty(propertyName, out var v)
        && v.ValueKind == JsonValueKind.Number
        && v.TryGetInt64(out var value)
            ? value
            : 0L;

    private static JsonDocument? TryParse(string json)
    {
        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static IEnumerable<string> Render(JsonElement root)
    {
        if (!root.TryGetProperty("type", out var typeElement)
            || typeElement.ValueKind != JsonValueKind.String)
        {
            yield break;
        }

        switch (typeElement.GetString())
        {
            case "assistant":
                foreach (var l in RenderAssistant(root)) yield return l;
                break;
            case "result":
                yield return RenderResult(root);
                break;
            // "system" init events and "user" tool_result echoes are
            // intentionally suppressed — they are noise for the UI.
        }
    }

    private static IEnumerable<string> RenderAssistant(JsonElement root)
    {
        if (!root.TryGetProperty("message", out var message)) yield break;
        if (!message.TryGetProperty("content", out var content)) yield break;
        if (content.ValueKind != JsonValueKind.Array) yield break;

        foreach (var block in content.EnumerateArray())
        {
            if (!block.TryGetProperty("type", out var blockType)
                || blockType.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            switch (blockType.GetString())
            {
                case "text":
                    if (block.TryGetProperty("text", out var text)
                        && text.ValueKind == JsonValueKind.String)
                    {
                        var raw = text.GetString() ?? string.Empty;
                        foreach (var l in raw.Split('\n'))
                        {
                            yield return l.TrimEnd('\r');
                        }
                    }
                    break;

                case "tool_use":
                    var toolName = block.TryGetProperty("name", out var n)
                        && n.ValueKind == JsonValueKind.String
                        ? n.GetString() ?? "?"
                        : "?";
                    var detail = FormatToolInput(toolName, block);
                    yield return string.IsNullOrEmpty(detail)
                        ? $"▶ {toolName}"
                        : $"▶ {toolName}  {detail}";
                    break;

                // "thinking" blocks intentionally suppressed.
            }
        }
    }

    /// <summary>
    /// Pulls the most useful single-line summary out of a tool_use's
    /// <c>input</c> object so the streamed line is informative ("read this
    /// file", "ran this command") instead of just "▶ Read".
    /// </summary>
    private static string FormatToolInput(string toolName, JsonElement block)
    {
        if (!block.TryGetProperty("input", out var input)
            || input.ValueKind != JsonValueKind.Object)
        {
            return string.Empty;
        }

        // Tool-specific primary fields. Chosen by what the user actually
        // wants to see in the activity log.
        string? primary = toolName switch
        {
            "Read" or "Edit" or "Write" or "NotebookEdit" or "MultiEdit"
                => GetString(input, "file_path"),
            "Bash" or "PowerShell"
                => GetString(input, "description") ?? Truncate(GetString(input, "command"), 80),
            "Grep"
                => FormatGrep(input),
            "Glob"
                => GetString(input, "pattern"),
            "ToolSearch" or "WebSearch"
                => GetString(input, "query"),
            "WebFetch"
                => GetString(input, "url"),
            "Task" or "Agent"
                => GetString(input, "description"),
            "TodoWrite"
                => "(todos updated)",
            _ => null,
        };

        if (!string.IsNullOrEmpty(primary))
        {
            return primary!;
        }

        // Generic fallback: first short string value in the input object.
        foreach (var prop in input.EnumerateObject())
        {
            if (prop.Value.ValueKind == JsonValueKind.String)
            {
                var value = prop.Value.GetString();
                if (!string.IsNullOrEmpty(value))
                {
                    return $"{prop.Name}={Truncate(value, 80)}";
                }
            }
        }
        return string.Empty;
    }

    private static string? FormatGrep(JsonElement input)
    {
        var pattern = GetString(input, "pattern");
        if (string.IsNullOrEmpty(pattern)) return null;
        var path = GetString(input, "path");
        return string.IsNullOrEmpty(path) ? pattern : $"{pattern}  in {path}";
    }

    private static string? GetString(JsonElement obj, string propertyName) =>
        obj.TryGetProperty(propertyName, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static string? Truncate(string? value, int maxLength)
    {
        if (value is null) return null;
        return value.Length <= maxLength ? value : value[..maxLength] + "…";
    }

    private static string RenderResult(JsonElement root)
    {
        var subtype = root.TryGetProperty("subtype", out var s)
            && s.ValueKind == JsonValueKind.String
            ? s.GetString()
            : "?";

        var durationMs = root.TryGetProperty("duration_ms", out var d) && d.TryGetInt64(out var ms)
            ? ms
            : 0L;

        var marker = subtype == "success" ? "✓" : "✗";
        return $"{marker} {subtype} · {durationMs}ms";
    }
}
