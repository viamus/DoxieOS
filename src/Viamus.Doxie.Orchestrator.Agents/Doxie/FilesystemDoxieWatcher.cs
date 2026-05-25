namespace Viamus.Doxie.Orchestrator.Agents.Doxie;

/// <summary>
/// Watches <c>.doxie/skills/</c> and triggers <see cref="DoxieRegenerator.Regenerate"/>
/// (debounced) whenever a manifest, body, or companion file changes. Mirrors
/// <c>FilesystemAgentCatalog</c>'s 300ms debounce — editors save in bursts of
/// 3â€“5 events; we want exactly one regen pass after the dust settles.
///
/// Errors during regen are caught and surfaced via <see cref="OnError"/> so a
/// malformed manifest doesn't crash the host process. The watcher continues.
/// </summary>
public sealed class FilesystemDoxieWatcher : IDisposable
{
    private static readonly TimeSpan WatchDebounce = TimeSpan.FromMilliseconds(300);

    private readonly DoxieRegenerator _regenerator;
    private readonly string _doxieRoot;
    private readonly FileSystemWatcher? _watcher;
    private readonly System.Threading.Timer? _debounceTimer;

    public Action<Exception>? OnError { get; set; }

    public FilesystemDoxieWatcher(DoxieRegenerator regenerator, string workspaceRoot)
    {
        _regenerator = regenerator ?? throw new ArgumentNullException(nameof(regenerator));
        _doxieRoot = Path.Combine(workspaceRoot, ".doxie");
        if (!Directory.Exists(_doxieRoot))
        {
            return;
        }

        _debounceTimer = new System.Threading.Timer(
            _ => SafeRegenerate(),
            state: null,
            dueTime: Timeout.Infinite,
            period: Timeout.Infinite);

        _watcher = new FileSystemWatcher(_doxieRoot)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName
                         | NotifyFilters.LastWrite
                         | NotifyFilters.Size
                         | NotifyFilters.DirectoryName,
        };
        _watcher.Changed += OnFsEvent;
        _watcher.Created += OnFsEvent;
        _watcher.Deleted += OnFsEvent;
        _watcher.Renamed += OnFsEvent;
        _watcher.EnableRaisingEvents = true;
    }

    public void Start() { /* ctor already enables raising — kept for API symmetry */ }

    private void OnFsEvent(object? sender, FileSystemEventArgs e)
    {
        _debounceTimer?.Change(WatchDebounce, Timeout.InfiniteTimeSpan);
    }

    private void SafeRegenerate()
    {
        try
        {
            _regenerator.Regenerate();
        }
        catch (Exception ex)
        {
            OnError?.Invoke(ex);
        }
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _debounceTimer?.Dispose();
    }
}
