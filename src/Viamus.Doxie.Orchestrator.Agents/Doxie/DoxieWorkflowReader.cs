using System.Text.Json;
using Viamus.Doxie.Orchestrator.Common;

namespace Viamus.Doxie.Orchestrator.Agents.Doxie;

/// <summary>
/// Loads workflow canons from <c>.doxie/workflows/&lt;id&gt;/workflow.json</c>.
/// Tolerant to malformed files — a broken workflow.json is skipped silently
/// (matching <see cref="DoxieSkillReader"/>'s posture) so one bad workflow
/// doesn't poison the entire regen pass and leave the user without shims.
/// </summary>
public sealed class DoxieWorkflowReader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _doxieRoot;
    private readonly Func<IReadOnlyList<DoxieCatalogRoot>>? _catalogRootsProvider;

    public DoxieWorkflowReader(string doxieRoot, Func<IReadOnlyList<DoxieCatalogRoot>>? catalogRootsProvider = null)
    {
        _doxieRoot = doxieRoot ?? throw new ArgumentNullException(nameof(doxieRoot));
        _catalogRootsProvider = catalogRootsProvider;
    }

    public IReadOnlyList<DoxieWorkflowCanon> LoadAll()
    {
        var workflows = new Dictionary<string, DoxieWorkflowCanon>(StringComparer.Ordinal);
        foreach (var root in EnumerateWorkflowRoots())
        {
            if (!Directory.Exists(root.WorkflowsDirectory)) continue;

            foreach (var dir in Directory.EnumerateDirectories(root.WorkflowsDirectory).OrderBy(p => p, StringComparer.Ordinal))
            {
                var canon = TryLoad(dir, root);
                if (canon is not null) workflows[canon.Id] = canon;
            }
        }

        return workflows.Values
            .OrderBy(w => w.Id, StringComparer.Ordinal)
            .ToList();
    }

    private IEnumerable<DoxieCatalogRoot> EnumerateWorkflowRoots()
    {
        if (_catalogRootsProvider is not null)
        {
            foreach (var root in _catalogRootsProvider())
            {
                yield return root;
            }
            yield break;
        }

        yield return new DoxieCatalogRoot(
            DoxieCatalogStore.DefaultCatalogId,
            "Default catalog",
            _doxieRoot,
            IsDefault: true);
    }

    private static DoxieWorkflowCanon? TryLoad(string dir, DoxieCatalogRoot catalog)
    {
        var path = Path.Combine(dir, "workflow.json");
        if (!File.Exists(path)) return null;

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;

            var id = GetString(root, "id") ?? Path.GetFileName(dir);
            var name = GetString(root, "name") ?? id;
            var description = GetString(root, "description") ?? string.Empty;
            var enabled = !root.TryGetProperty("enabled", out var en) || en.ValueKind != JsonValueKind.False;

            string triggerKind = "manual";
            string? cron = null;
            if (root.TryGetProperty("trigger", out var trigger) && trigger.ValueKind == JsonValueKind.Object)
            {
                triggerKind = GetString(trigger, "kind") ?? "manual";
                cron = GetString(trigger, "cronExpression");
            }

            return new DoxieWorkflowCanon(
                id,
                name,
                description,
                triggerKind,
                cron,
                enabled,
                IsPrivate: false,
                CatalogId: catalog.Id,
                CatalogName: catalog.Name,
                CatalogRoot: catalog.RootPath);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static string? GetString(JsonElement obj, string property) =>
        obj.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
}
