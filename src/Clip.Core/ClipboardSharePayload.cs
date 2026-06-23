namespace Clip.Core;

public sealed class ClipboardSharePayload
{
    private const string CleanupMarkerFileName = ".clip-share-cleanup";

    private ClipboardSharePayload(IReadOnlyList<string> filePaths, IReadOnlyList<string> temporaryFiles)
    {
        FilePaths = filePaths;
        _temporaryFiles = temporaryFiles;
    }

    private readonly IReadOnlyList<string> _temporaryFiles;

    public IReadOnlyList<string> FilePaths { get; }

    public bool HasTemporaryFiles => _temporaryFiles.Count > 0;

    public static ClipboardSharePayload Create(ClipboardHistoryItem item, string? tempRoot = null)
    {
        if (item.Kind is ClipboardItemKind.Text or ClipboardItemKind.Link or ClipboardItemKind.Color)
        {
            var root = tempRoot ?? Path.Combine(Path.GetTempPath(), "Clip", "Share");
            Directory.CreateDirectory(root);
            var path = Path.Combine(root, $"clip-{DateTimeOffset.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.txt");
            File.WriteAllText(path, TextPayload(item));
            return new ClipboardSharePayload([path], [path]);
        }

        if (item.Kind == ClipboardItemKind.Image && !string.IsNullOrWhiteSpace(item.AssetPath) && File.Exists(item.AssetPath))
        {
            return new ClipboardSharePayload([item.AssetPath], []);
        }

        if (item.Kind == ClipboardItemKind.Files)
        {
            var existingPaths = item.FilePaths
                .Where(path => File.Exists(path) || Directory.Exists(path))
                .ToList();

            if (existingPaths.Count > 0)
            {
                return new ClipboardSharePayload(existingPaths, []);
            }
        }

        throw new InvalidOperationException("This item cannot be shared.");
    }

    public void Cleanup()
    {
        foreach (var path in _temporaryFiles)
        {
            TryDelete(path);
        }
    }

    public static bool CleanupStaleTemporaryFilesIfDue(
        string? tempRoot = null,
        TimeSpan? olderThan = null,
        TimeSpan? minimumInterval = null,
        DateTimeOffset? now = null)
    {
        var root = tempRoot ?? Path.Combine(Path.GetTempPath(), "Clip", "Share");
        if (!Directory.Exists(root))
        {
            return false;
        }

        var current = now ?? DateTimeOffset.Now;
        var interval = minimumInterval ?? TimeSpan.FromDays(1);
        var markerPath = Path.Combine(root, CleanupMarkerFileName);
        if (File.Exists(markerPath) &&
            File.GetLastWriteTime(markerPath) > (current - interval).LocalDateTime)
        {
            return false;
        }

        CleanupStaleTemporaryFiles(root, olderThan, current);
        TryWriteCleanupMarker(markerPath, current);
        return true;
    }

    public static void CleanupStaleTemporaryFiles(string? tempRoot = null, TimeSpan? olderThan = null, DateTimeOffset? now = null)
    {
        var root = tempRoot ?? Path.Combine(Path.GetTempPath(), "Clip", "Share");
        if (!Directory.Exists(root))
        {
            return;
        }

        var cutoff = (now ?? DateTimeOffset.Now) - (olderThan ?? TimeSpan.FromDays(1));
        foreach (var path in Directory.EnumerateFiles(root, "clip-*.txt"))
        {
            try
            {
                if (File.GetLastWriteTime(path) < cutoff.LocalDateTime)
                {
                    TryDelete(path);
                }
            }
            catch
            {
                // Temp cleanup is best effort.
            }
        }
    }

    private static void TryWriteCleanupMarker(string markerPath, DateTimeOffset timestamp)
    {
        try
        {
            File.WriteAllText(markerPath, timestamp.ToString("O"));
            File.SetLastWriteTime(markerPath, timestamp.LocalDateTime);
        }
        catch
        {
            // Temp cleanup bookkeeping is best effort.
        }
    }

    private static string TextPayload(ClipboardHistoryItem item) => item.Text ?? item.Preview ?? string.Empty;

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // The share target may still be reading it. A later cleanup pass will remove stale files.
        }
    }
}
