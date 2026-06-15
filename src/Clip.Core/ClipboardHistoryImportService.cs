namespace Clip.Core;

public interface IClipboardHistorySource
{
    Task<IReadOnlyList<ClipboardHistorySnapshotItem>> GetItemsAsync(Func<string, string> reserveImagePath, CancellationToken cancellationToken = default);
}

public sealed class ClipboardHistorySnapshotItem
{
    public ClipboardItemKind Kind { get; init; }
    public string Preview { get; init; } = string.Empty;
    public string? Text { get; init; }
    public string? HtmlText { get; init; }
    public string? RtfText { get; init; }
    public string? AssetPath { get; init; }
    public IReadOnlyList<string> FilePaths { get; init; } = [];
    public DateTimeOffset? CopiedAt { get; init; }
    public string? SourceApplication { get; init; }
    public string? SourceApplicationPath { get; init; }
    public int? ImageWidth { get; init; }
    public int? ImageHeight { get; init; }
}

public sealed class ClipboardHistoryImportService(ClipboardHistoryStore store, IClipboardHistorySource source)
{
    public async Task<int> ImportAsync(int maxItems = 500, CancellationToken cancellationToken = default)
    {
        var snapshots = await source.GetItemsAsync(
            extension => store.NewAssetFilePath(ClipboardItemKind.Image, extension: extension),
            cancellationToken);
        var imported = 0;

        foreach (var snapshot in snapshots.OrderBy(item => item.CopiedAt ?? DateTimeOffset.MinValue))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = ToHistoryItem(snapshot);
            if (!HasContent(item))
            {
                DeleteReservedAsset(item);
                continue;
            }

            if (store.ContainsEquivalent(item))
            {
                DeleteReservedAsset(item);
                continue;
            }

            store.AddOrUpdate(item, maxItems, refreshCopiedAt: false);
            imported++;
        }

        return imported;
    }

    private static ClipboardHistoryItem ToHistoryItem(ClipboardHistorySnapshotItem snapshot)
    {
        var copiedAt = snapshot.CopiedAt ?? DateTimeOffset.Now;
        return new ClipboardHistoryItem
        {
            Kind = snapshot.Kind,
            Preview = string.IsNullOrWhiteSpace(snapshot.Preview) ? DefaultPreview(snapshot) : snapshot.Preview,
            Text = snapshot.Text,
            HtmlText = snapshot.HtmlText,
            RtfText = snapshot.RtfText,
            AssetPath = snapshot.AssetPath,
            FilePaths = snapshot.FilePaths.ToList(),
            CreatedAt = copiedAt,
            FirstCopiedAt = copiedAt,
            LastCopiedAt = copiedAt,
            LastUsedAt = copiedAt,
            SourceApplication = snapshot.SourceApplication,
            SourceApplicationPath = snapshot.SourceApplicationPath,
            ImageWidth = snapshot.ImageWidth,
            ImageHeight = snapshot.ImageHeight,
        };
    }

    private static string DefaultPreview(ClipboardHistorySnapshotItem snapshot)
    {
        return snapshot.Kind switch
        {
            ClipboardItemKind.Text or ClipboardItemKind.Link or ClipboardItemKind.Color => ClipboardHistoryStore.PreviewText(snapshot.Text ?? string.Empty),
            ClipboardItemKind.Files => snapshot.FilePaths.Count == 1 ? Path.GetFileName(snapshot.FilePaths[0]) : $"{snapshot.FilePaths.Count} files",
            ClipboardItemKind.Image => "Image",
            _ => string.Empty,
        };
    }

    private static bool HasContent(ClipboardHistoryItem item)
    {
        return item.Kind switch
        {
            ClipboardItemKind.Text or ClipboardItemKind.Link or ClipboardItemKind.Color => !string.IsNullOrWhiteSpace(item.Text),
            ClipboardItemKind.Image => !string.IsNullOrWhiteSpace(item.AssetPath) && File.Exists(item.AssetPath),
            ClipboardItemKind.Files => item.FilePaths.Any(path => File.Exists(path) || Directory.Exists(path)),
            _ => false,
        };
    }

    private void DeleteReservedAsset(ClipboardHistoryItem item)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(item.AssetPath) &&
                IsInsideContentRoot(item.AssetPath) &&
                File.Exists(item.AssetPath))
            {
                File.Delete(item.AssetPath);
            }
        }
        catch
        {
        }
    }

    private bool IsInsideContentRoot(string path)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            var fullRoot = Path.GetFullPath(store.ContentRootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
