namespace Viamus.Doxie.Orchestrator.Agents;

/// <summary>
/// A named operation an agent supports. The arguments string passed to the
/// runner is built from <see cref="ArgumentsTemplate"/> with any
/// <c>{field-id}</c> placeholders substituted by the values the user enters
/// for the corresponding <see cref="Fields"/>.
/// </summary>
public sealed record AgentMode(
    string Id,
    string Name,
    string Description,
    string ArgumentsTemplate,
    IReadOnlyList<AgentModeField>? Fields = null,
    bool KeepSessionAlive = false,
    bool RequiresWorkspace = false)
{
    public string BuildArguments(IReadOnlyDictionary<string, string>? fieldValues = null)
    {
        if (Fields is null || Fields.Count == 0)
        {
            return ArgumentsTemplate.Trim();
        }

        var values = fieldValues ?? new Dictionary<string, string>();
        var rendered = ArgumentsTemplate;
        foreach (var field in Fields)
        {
            var raw = values.TryGetValue(field.Id, out var v) ? v : string.Empty;
            rendered = rendered.Replace($"{{{field.Id}}}", raw.Trim());
        }
        return System.Text.RegularExpressions.Regex.Replace(rendered, @"\s+", " ").Trim();
    }
}
