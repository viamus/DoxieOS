using System.Text.Json;
using Viamus.Doxie.Orchestrator.Agents;
using Viamus.Doxie.Orchestrator.Common;

namespace Viamus.Doxie.Orchestrator.Agents.Doxie;

public sealed record CustomDoxieCatalog(string Id, string Name, string Path);

public interface IDoxieCatalogStore
{
    IReadOnlyList<DoxieCatalogRoot> ListCatalogs();
    DoxieCatalogRoot? FindCatalog(string? catalogId);
    IReadOnlyList<CustomDoxieCatalog> GetCustomCatalogs();
    IReadOnlyList<CustomDoxieCatalog> SaveCustomCatalogs(IEnumerable<CustomDoxieCatalog> catalogs);
}

public sealed class DoxieCatalogStore : IDoxieCatalogStore
{
    public const string DefaultCatalogId = "default";
    private const string SettingsKey = "doxie.catalog.customRoots";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly StorageOptions _storage;
    private readonly IKeyValueSettingsStore _settings;

    public DoxieCatalogStore(StorageOptions storage, IKeyValueSettingsStore settings)
    {
        _storage = storage;
        _settings = settings;
    }

    public IReadOnlyList<DoxieCatalogRoot> ListCatalogs()
    {
        var defaultRoot = Path.GetFullPath(Path.Combine(
            StorageOptions.ResolvePath(_storage.SkillsDirectory),
            ".."));

        var result = new List<DoxieCatalogRoot>
        {
            new(
                DefaultCatalogId,
                "Default catalog",
                defaultRoot,
                IsDefault: true,
                AgentsPath: StorageOptions.ResolvePath(_storage.AgentsDirectory),
                SkillsPath: StorageOptions.ResolvePath(_storage.SkillsDirectory),
                WorkflowsPath: StorageOptions.ResolvePath(_storage.WorkflowsDirectory),
                LibrariesPath: StorageOptions.ResolvePath(_storage.LibrariesDirectory)),
        };

        result.AddRange(GetCustomCatalogs().Select(c =>
            new DoxieCatalogRoot(c.Id, c.Name, StorageOptions.ResolvePath(c.Path))));

        return result;
    }

    public DoxieCatalogRoot? FindCatalog(string? catalogId)
    {
        var id = string.IsNullOrWhiteSpace(catalogId) ? DefaultCatalogId : catalogId.Trim();
        return ListCatalogs().FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    public IReadOnlyList<CustomDoxieCatalog> GetCustomCatalogs()
    {
        var raw = _settings.Get(SettingsKey);
        if (string.IsNullOrWhiteSpace(raw)) return Array.Empty<CustomDoxieCatalog>();

        try
        {
            var parsed = JsonSerializer.Deserialize<List<CustomDoxieCatalog>>(raw, JsonOptions);
            return Normalize(parsed ?? []);
        }
        catch (JsonException)
        {
            return Array.Empty<CustomDoxieCatalog>();
        }
    }

    public IReadOnlyList<CustomDoxieCatalog> SaveCustomCatalogs(IEnumerable<CustomDoxieCatalog> catalogs)
    {
        var normalized = Normalize(catalogs);
        _settings.Set(SettingsKey, JsonSerializer.Serialize(normalized, JsonOptions));
        EnsureFolders(normalized);
        return normalized;
    }

    private static IReadOnlyList<CustomDoxieCatalog> Normalize(IEnumerable<CustomDoxieCatalog> catalogs)
    {
        var result = new List<CustomDoxieCatalog>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var catalog in catalogs)
        {
            if (string.IsNullOrWhiteSpace(catalog.Path)) continue;

            var name = string.IsNullOrWhiteSpace(catalog.Name)
                ? Path.GetFileName(catalog.Path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                : catalog.Name.Trim();
            if (string.IsNullOrWhiteSpace(name)) name = "Custom catalog";

            var id = Slugify(string.IsNullOrWhiteSpace(catalog.Id) ? name : catalog.Id);
            var baseId = id;
            var suffix = 2;
            while (!ids.Add(id))
            {
                id = $"{baseId}-{suffix++}";
            }

            result.Add(new CustomDoxieCatalog(id, name, catalog.Path.Trim()));
        }

        return result;
    }

    private static void EnsureFolders(IEnumerable<CustomDoxieCatalog> catalogs)
    {
        foreach (var catalog in catalogs)
        {
            var root = StorageOptions.ResolvePath(catalog.Path);
            Directory.CreateDirectory(Path.Combine(root, "agents"));
            Directory.CreateDirectory(Path.Combine(root, "skills"));
            Directory.CreateDirectory(Path.Combine(root, "workflows"));
            Directory.CreateDirectory(Path.Combine(root, "libraries"));
        }
    }

    private static string Slugify(string value)
    {
        var chars = new List<char>();
        var previousDash = false;
        foreach (var c in value.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c))
            {
                chars.Add(c);
                previousDash = false;
            }
            else if (!previousDash)
            {
                chars.Add('-');
                previousDash = true;
            }
        }

        var slug = new string(chars.ToArray()).Trim('-');
        return string.IsNullOrWhiteSpace(slug) || string.Equals(slug, DefaultCatalogId, StringComparison.OrdinalIgnoreCase)
            ? $"catalog-{Guid.NewGuid():N}"[..20]
            : slug;
    }
}
