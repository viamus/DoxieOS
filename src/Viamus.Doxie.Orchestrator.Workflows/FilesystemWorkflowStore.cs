using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Viamus.Doxie.Orchestrator.Agents.Doxie;
using Viamus.Doxie.Orchestrator.Common;

namespace Viamus.Doxie.Orchestrator.Workflows;

/// <summary>
/// Workflows persisted as <c>workflow.json</c> files under
/// <c>.doxie/workflows/&lt;id&gt;/</c>. One folder per workflow so future
/// per-workflow assets (sample inputs, last-N runs as artefacts, an
/// auto-generated README) can sit next to the manifest without
/// polluting the canonical layer.
/// </summary>
public sealed class FilesystemWorkflowStore : IWorkflowStore
{
    private static readonly Regex KebabCase = new("^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.Compiled);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly string _directory;
    private readonly Func<IReadOnlyList<DoxieCatalogRoot>>? _catalogRootsProvider;

    public FilesystemWorkflowStore(string directory, Func<IReadOnlyList<DoxieCatalogRoot>>? catalogRootsProvider = null)
    {
        _directory = directory;
        _catalogRootsProvider = catalogRootsProvider;
    }

    public IReadOnlyList<WorkflowDefinition> ListAll()
    {
        var workflows = new Dictionary<string, WorkflowDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in WorkflowRoots())
        {
            if (!Directory.Exists(root.WorkflowsDirectory)) continue;

            foreach (var dir in Directory.EnumerateDirectories(root.WorkflowsDirectory))
            {
                var wf = TryLoad(dir, root);
                if (wf is not null) workflows[wf.Id] = wf;
            }
        }
        return workflows.Values
            .OrderBy(w => w.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public WorkflowDefinition? GetById(string id)
    {
        foreach (var root in WorkflowRoots())
        {
            var dir = Path.Combine(root.WorkflowsDirectory, id);
            if (Directory.Exists(dir)) return TryLoad(dir, root);
        }
        return null;
    }

    public void Save(WorkflowDefinition definition)
    {
        if (string.IsNullOrEmpty(definition.Id) || !KebabCase.IsMatch(definition.Id))
        {
            throw new InvalidOperationException($"Invalid workflow id '{definition.Id}' (must be kebab-case).");
        }

        var catalog = WorkflowRoots()
            .FirstOrDefault(c => string.Equals(c.Id, definition.CatalogId, StringComparison.OrdinalIgnoreCase))
            ?? WorkflowRoots().First();
        var targetRoot = catalog.WorkflowsDirectory;

        Directory.CreateDirectory(targetRoot);
        var dir = Path.Combine(targetRoot, definition.Id);
        Directory.CreateDirectory(dir);

        var path = Path.Combine(dir, "workflow.json");
        File.WriteAllText(path, JsonSerializer.Serialize(definition, JsonOptions));

        foreach (var otherRoot in WorkflowRoots().Where(c => !string.Equals(c.Id, catalog.Id, StringComparison.OrdinalIgnoreCase)))
        {
            var oldDir = Path.Combine(otherRoot.WorkflowsDirectory, definition.Id);
            if (!string.Equals(Path.GetFullPath(oldDir), Path.GetFullPath(dir), StringComparison.OrdinalIgnoreCase)
                && Directory.Exists(oldDir))
            {
                Directory.Delete(oldDir, recursive: true);
            }
        }
    }

    public void Delete(string id)
    {
        var dir = WorkflowRoots()
            .Select(r => Path.Combine(r.WorkflowsDirectory, id))
            .FirstOrDefault(Directory.Exists);
        if (!Directory.Exists(dir))
        {
            throw new InvalidOperationException($"Workflow '{id}' not found.");
        }
        Directory.Delete(dir, recursive: true);
    }

    private IEnumerable<DoxieCatalogRoot> WorkflowRoots()
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
            Path.GetFullPath(Path.Combine(_directory, "..")),
            IsDefault: true,
            WorkflowsPath: _directory);
    }

    private static WorkflowDefinition? TryLoad(string dir, DoxieCatalogRoot catalog)
    {
        var path = Path.Combine(dir, "workflow.json");
        if (!File.Exists(path)) return null;

        try
        {
            var workflow = JsonSerializer.Deserialize<WorkflowDefinition>(File.ReadAllText(path), JsonOptions);
            return workflow is null
                ? null
                : workflow with
                {
                    IsPrivate = false,
                    CatalogId = catalog.Id,
                };
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
