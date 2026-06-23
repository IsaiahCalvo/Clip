using System.Threading;

namespace Clip.CommandPalette;

// Pushes "the store changed" notifications to the open palette so newly-copied items appear
// without the user retyping or re-filtering. A single FileSystemWatcher on the store's content
// root is near-zero cost when idle (far cheaper/"feels live" than polling). Clipboard captures
// rewrite history.json + the index files in a quick temp-file + File.Move burst, which fires
// several FSW events, so the callback is debounced to fire exactly once per burst (and to avoid
// reading a half-written file). All file I/O stays out of the callback: it only signals; the page
// does its cheap top-index reload + CurrentIndexStampUtc guard on the next GetItems.
internal sealed class ClipHistoryWatcher : IDisposable
{
    // Long enough to coalesce the multi-file temp+move write sequence into one reload, short
    // enough to still feel live.
    private const int DebounceMilliseconds = 200;

    private readonly Action _onChanged;
    private readonly string _contentRootPath;
    private readonly HashSet<string> _watchedFileNames;
    private readonly Timer _debounceTimer;
    private readonly object _sync = new();
    private FileSystemWatcher? _watcher;
    private bool _disposed;

    public ClipHistoryWatcher(string contentRootPath, IEnumerable<string> watchedFilePaths, Action onChanged)
    {
        _contentRootPath = contentRootPath;
        _onChanged = onChanged;
        _watchedFileNames = watchedFilePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // The timer never auto-fires; each event reschedules it to DebounceMilliseconds from now.
        _debounceTimer = new Timer(_ => Fire(), state: null, Timeout.Infinite, Timeout.Infinite);
        TryStart();
    }

    // Re-attempts to attach the watcher when the content root did not exist yet (first run before
    // any item is captured). Safe to call repeatedly; a no-op once watching.
    public void Rearm() => TryStart();

    private void TryStart()
    {
        lock (_sync)
        {
            if (_disposed || _watcher is not null || !Directory.Exists(_contentRootPath))
            {
                return;
            }

            try
            {
                var watcher = new FileSystemWatcher(_contentRootPath)
                {
                    // Captures touch LastWrite + Size on rename/move into place; FileName covers the
                    // temp-file -> final File.Move replace pattern the store uses.
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                    IncludeSubdirectories = false,
                };
                watcher.Changed += OnFileEvent;
                watcher.Created += OnFileEvent;
                watcher.Renamed += OnFileEvent;
                watcher.EnableRaisingEvents = true;
                _watcher = watcher;
            }
            catch (Exception)
            {
                // Watching is best-effort: if the OS denies the handle the page still works via the
                // existing mtime cache on the next user action.
                _watcher = null;
            }
        }
    }

    private void OnFileEvent(object sender, FileSystemEventArgs e)
    {
        // Only the history/index files matter; ignore asset PNGs, swatch writes, cleanup churn, etc.
        if (!_watchedFileNames.Contains(Path.GetFileName(e.Name) ?? string.Empty))
        {
            return;
        }

        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            // Reset the debounce window: fire once the burst settles.
            _debounceTimer.Change(DebounceMilliseconds, Timeout.Infinite);
        }
    }

    private void Fire()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }
        }

        try
        {
            _onChanged();
        }
        catch (Exception)
        {
            // A failed refresh must never crash the watcher thread; the next event retries.
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Changed -= OnFileEvent;
            _watcher.Created -= OnFileEvent;
            _watcher.Renamed -= OnFileEvent;
            _watcher.Dispose();
            _watcher = null;
        }

        _debounceTimer.Dispose();
    }
}
