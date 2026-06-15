using System.Text.Json;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using System.Text;

namespace Clip.Core;

public sealed class ClipboardHistoryStore
{
    private const string AppDataFolderName = "Clip";
    private const string ContentFolderName = "Clipboard History";
    private const string PreviousContentFolderName = "Clipboard";
    private static readonly string LegacyAppDataFolderName = "Ray" + "Clipboard";
    private const string SidecarExtension = ".clip.json";
    private const string DirectorySidecarName = ".clip.json";

    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.General)
    {
        WriteIndented = true,
    };
    private readonly object _sync = new();
    private List<ClipboardHistoryItem>? _itemsCache;

    public ClipboardHistoryStore(string? rootPath = null, string? contentRootPath = null)
    {
        RootPath = rootPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            AppDataFolderName);
        ContentRootPath = string.IsNullOrWhiteSpace(contentRootPath)
            ? Path.Combine(RootPath, ContentFolderName)
            : contentRootPath;
        AssetPath = Path.Combine(ContentRootPath, "image");
        HistoryFilePath = Path.Combine(ContentRootPath, "history.json");
        if (rootPath is null)
        {
            MigrateLegacyStore(RootPath);
        }

        MigratePreviousDefaultContentFolder();
        EnsureContentFolders();
        EnsureHistoryFileInContentRoot();
        CleanupEmptyFileCategoryFolders();
    }

    public string RootPath { get; }

    public string ContentRootPath { get; private set; }

    public string AssetPath { get; private set; }

    public string HistoryFilePath { get; private set; }

    public IReadOnlyList<ClipboardHistoryItem> GetItems()
    {
        lock (_sync)
        {
            if (_itemsCache is not null)
            {
                if (ReconcileCachedItems(_itemsCache))
                {
                    SaveCore(_itemsCache);
                }

                return _itemsCache;
            }

            if (!File.Exists(HistoryFilePath))
            {
                _itemsCache = [];
                return _itemsCache;
            }

            var json = File.ReadAllText(HistoryFilePath);
            var items = JsonSerializer.Deserialize<List<ClipboardHistoryItem>>(json, _jsonOptions) ?? [];
            var changed = NormalizeLoadedItems(items);
            changed |= ReconcileExternalAssetRenames(items);
            changed |= BackfillContentAssets(items);
            changed |= EnsureFriendlyAssetNames(items);
            changed |= EnsureSidecars(items);
            CleanupEmptyFileCategoryFolders();
            if (changed)
            {
                SaveCore(items);
            }

            _itemsCache = items;
            return _itemsCache;
        }
    }

    private bool ReconcileCachedItems(List<ClipboardHistoryItem> items)
    {
        var changed = ReconcileExternalAssetRenames(items);
        changed |= EnsureFriendlyAssetNames(items);
        changed |= EnsureSidecars(items);
        CleanupEmptyFileCategoryFolders();
        return changed;
    }

    public ClipboardHistoryItem? GetItem(string id)
    {
        return GetItems().FirstOrDefault(item => item.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
    }

    public IReadOnlyList<ClipboardHistoryItem> QueryItems(string? query = null)
    {
        var items = GetItems().AsEnumerable();
        if (!string.IsNullOrWhiteSpace(query))
        {
            items = items.Where(item =>
                Contains(item.Preview, query) ||
                Contains(item.CustomTitle, query) ||
                Contains(item.Text, query) ||
                item.FilePaths.Any(path => Contains(path, query)));
        }

        return items
            .OrderByDescending(item => item.IsPinned)
            .ThenBy(item => item.PinOrder == 0 ? int.MaxValue : item.PinOrder)
            .ThenByDescending(item => item.LastUsedAt)
            .ToList();
    }

    public ClipboardHistoryItem AddOrUpdate(ClipboardHistoryItem item, int maxItems = 500, bool refreshCopiedAt = true)
    {
        Enrich(item, refreshCopiedAt);
        PersistContentAsset(item);
        var items = GetItems().ToList();
        var duplicate = FindDuplicate(items, item);
        if (duplicate is not null)
        {
            var redundantAssetPath = !string.IsNullOrWhiteSpace(item.AssetPath) &&
                !string.Equals(item.AssetPath, duplicate.AssetPath, StringComparison.OrdinalIgnoreCase)
                    ? item.AssetPath
                    : null;
            duplicate.LastUsedAt = DateTimeOffset.Now;
            duplicate.LastCopiedAt = DateTimeOffset.Now;
            duplicate.CopyCount++;
            duplicate.Preview = item.Preview;
            duplicate.CustomTitle = item.CustomTitle ?? duplicate.CustomTitle;
            duplicate.Text = item.Text ?? duplicate.Text;
            duplicate.HtmlText = item.HtmlText ?? duplicate.HtmlText;
            duplicate.RtfText = item.RtfText ?? duplicate.RtfText;
            duplicate.AssetPath ??= item.AssetPath;
            duplicate.FilePaths = item.FilePaths.Count > 0 ? item.FilePaths : duplicate.FilePaths;
            duplicate.SourceApplication = item.SourceApplication ?? duplicate.SourceApplication;
            duplicate.AssetSizeBytes = item.AssetSizeBytes ?? duplicate.AssetSizeBytes;
            duplicate.ImageWidth = item.ImageWidth ?? duplicate.ImageWidth;
            duplicate.ImageHeight = item.ImageHeight ?? duplicate.ImageHeight;
            duplicate.CharacterCount = item.CharacterCount ?? duplicate.CharacterCount;
            duplicate.WordCount = item.WordCount ?? duplicate.WordCount;
            TouchAsset(duplicate.AssetPath);
            WriteSidecar(duplicate);
            Save(items);
            DeleteAssetPath(redundantAssetPath);
            return duplicate;
        }

        EnsureFriendlyAssetName(item, items);
        WriteSidecar(item);
        items.Insert(0, item);
        var removed = TrimUnpinned(items, maxItems);
        Save(items);
        DeleteAssets(removed);
        return item;
    }

    public bool ContainsEquivalent(ClipboardHistoryItem item)
    {
        Enrich(item);
        return FindDuplicate(GetItems(), item) is not null;
    }

    public int ApplyHistoryLimit(int maxItems)
    {
        var items = GetItems().ToList();
        var before = items.Count;
        var removed = TrimUnpinned(items, maxItems);
        if (removed.Count == 0)
        {
            return 0;
        }

        Save(items);
        DeleteAssets(removed);
        return before - items.Count;
    }

    public bool Delete(string id)
    {
        var items = GetItems().ToList();
        var removedItems = items.Where(item => item.Id.Equals(id, StringComparison.OrdinalIgnoreCase)).ToList();
        if (removedItems.Count > 0)
        {
            items.RemoveAll(item => item.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            Save(items);
            DeleteAssets(removedItems);
        }

        return removedItems.Count > 0;
    }

    public int ClearHistory(bool includePinned)
    {
        var items = GetItems().ToList();
        var removed = items.Where(item => includePinned || !item.IsPinned).ToList();
        if (removed.Count == 0)
        {
            return 0;
        }

        items.RemoveAll(item => includePinned || !item.IsPinned);
        Save(items);
        DeleteAssets(removed);
        return removed.Count;
    }

    public bool SetPinned(string id, bool isPinned)
    {
        var items = GetItems().ToList();
        var item = items.FirstOrDefault(i => i.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (item is null)
        {
            return false;
        }

        item.IsPinned = isPinned;
        if (isPinned && item.PinOrder <= 0)
        {
            item.PinOrder = items.Where(i => i.IsPinned).Select(i => i.PinOrder).DefaultIfEmpty(0).Max() + 1;
        }

        if (!isPinned)
        {
            item.PinOrder = 0;
            item.LastUsedAt = DateTimeOffset.Now;
            item.LastCopiedAt = DateTimeOffset.Now;
        }

        Save(items);
        return true;
    }

    public bool MovePinned(string id, int direction)
    {
        var items = GetItems().ToList();
        var pins = items.Where(i => i.IsPinned).OrderBy(i => i.PinOrder).ToList();
        var index = pins.FindIndex(i => i.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        var target = index + Math.Sign(direction);
        if (index < 0 || target < 0 || target >= pins.Count)
        {
            return false;
        }

        (pins[index].PinOrder, pins[target].PinOrder) = (pins[target].PinOrder, pins[index].PinOrder);
        Save(items);
        return true;
    }

    public bool EditText(string id, string text)
    {
        var items = GetItems().ToList();
        var item = items.FirstOrDefault(i => i.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (item is null || item.Kind != ClipboardItemKind.Text)
        {
            return false;
        }

        item.Text = text;
        item.Preview = PreviewText(text);
        item.LastUsedAt = DateTimeOffset.Now;
        item.Kind = ClipboardItemKind.Text;
        item.ContentHash = null;
        item.HtmlText = null;
        item.RtfText = null;
        DeleteAssetPath(item.AssetPath);
        item.AssetPath = null;
        Enrich(item);
        PersistContentAsset(item);
        EnsureFriendlyAssetName(item, items);
        WriteSidecar(item);
        Save(items);
        return true;
    }

    public bool Rename(string id, string? title)
    {
        var items = GetItems().ToList();
        var item = items.FirstOrDefault(i => i.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (item is null)
        {
            return false;
        }

        var cleanTitle = string.IsNullOrWhiteSpace(title) ? null : title.Trim();
        if (cleanTitle?.Length > 120)
        {
            cleanTitle = cleanTitle[..120];
        }

        item.CustomTitle = cleanTitle;
        item.LastUsedAt = DateTimeOffset.Now;
        RenameAssetForItem(item, items);
        WriteSidecar(item);
        Save(items);
        return true;
    }

    public string SaveAsFile(string id, string? outputPath = null)
    {
        var item = GetItem(id) ?? throw new InvalidOperationException("Clipboard item not found.");
        var target = outputPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            DefaultFileName(item));

        if (item.Kind is ClipboardItemKind.Text or ClipboardItemKind.Link or ClipboardItemKind.Color)
        {
            File.WriteAllText(target, item.Text ?? string.Empty);
            return target;
        }

        if (item.Kind == ClipboardItemKind.Image && item.AssetPath is not null && File.Exists(item.AssetPath))
        {
            File.Copy(item.AssetPath, target, overwrite: true);
            return target;
        }

        throw new InvalidOperationException("This item cannot be saved as a file yet.");
    }

    public void Save(IEnumerable<ClipboardHistoryItem> items)
    {
        lock (_sync)
        {
            SaveCore(items);
            _itemsCache = items.ToList();
        }
    }

    private void SaveCore(IEnumerable<ClipboardHistoryItem> items)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(HistoryFilePath)!);
        File.WriteAllText(HistoryFilePath, JsonSerializer.Serialize(items, _jsonOptions));
    }

    public string NewAssetFilePath(string extension)
    {
        return NewAssetFilePath(ClipboardItemKind.Image, extension: extension);
    }

    public string NewAssetFilePath(ClipboardItemKind kind, string? sourcePath = null, string? extension = null)
    {
        var folder = ContentFolderFor(kind, sourcePath);
        Directory.CreateDirectory(folder);
        var safeExtension = string.IsNullOrWhiteSpace(extension) ? ".txt" : extension;
        return Path.Combine(folder, $"{DateTimeOffset.Now:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}{safeExtension}");
    }

    public void SetContentRootPath(string contentRootPath)
    {
        if (string.IsNullOrWhiteSpace(contentRootPath))
        {
            throw new ArgumentException("Clipboard folder path is required.", nameof(contentRootPath));
        }

        var items = GetItems().ToList();
        ContentRootPath = contentRootPath;
        AssetPath = Path.Combine(ContentRootPath, "image");
        HistoryFilePath = Path.Combine(ContentRootPath, "history.json");
        EnsureContentFolders();
        BackfillContentAssets(items);
        EnsureFriendlyAssetNames(items);
        CleanupEmptyFileCategoryFolders();
        SaveCore(items);
        _itemsCache = items;
    }

    public static string PreviewText(string text)
    {
        var oneLine = text.Replace("\r", " ").Replace("\n", " ").Trim();
        return oneLine.Length <= 120 ? oneLine : oneLine[..117] + "...";
    }

    private static ClipboardHistoryItem? FindDuplicate(IEnumerable<ClipboardHistoryItem> items, ClipboardHistoryItem incoming)
    {
        return incoming.Kind switch
        {
            ClipboardItemKind.Text or ClipboardItemKind.Link => items.FirstOrDefault(i => (i.Kind == ClipboardItemKind.Text || i.Kind == ClipboardItemKind.Link) && SameHash(i, incoming)),
            ClipboardItemKind.Color => items.FirstOrDefault(i => i.Kind == ClipboardItemKind.Color && SameHash(i, incoming)),
            ClipboardItemKind.Image => items.FirstOrDefault(i => i.Kind == ClipboardItemKind.Image && SameHash(i, incoming)),
            ClipboardItemKind.Files => items.FirstOrDefault(i => i.Kind == ClipboardItemKind.Files && SameHash(i, incoming)),
            _ => null,
        };
    }

    private static bool SameHash(ClipboardHistoryItem existing, ClipboardHistoryItem incoming)
    {
        return !string.IsNullOrEmpty(existing.ContentHash) &&
            existing.ContentHash.Equals(incoming.ContentHash, StringComparison.OrdinalIgnoreCase);
    }

    private static List<ClipboardHistoryItem> TrimUnpinned(List<ClipboardHistoryItem> items, int maxItems)
    {
        if (maxItems < 0)
        {
            return [];
        }

        var unpinned = items.Where(item => !item.IsPinned).OrderByDescending(item => item.LastUsedAt).Take(maxItems).ToHashSet();
        var removed = items.Where(item => !item.IsPinned && !unpinned.Contains(item)).ToList();
        items.RemoveAll(item => !item.IsPinned && !unpinned.Contains(item));
        return removed;
    }

    private void PersistContentAsset(ClipboardHistoryItem item)
    {
        if (!string.IsNullOrWhiteSpace(item.AssetPath))
        {
            return;
        }

        if (item.Kind is ClipboardItemKind.Text or ClipboardItemKind.Link)
        {
            var path = UniqueAssetPathForItem(item, []);
            WriteTextLikeAsset(item, path);
            item.AssetPath = path;
            return;
        }

        if (item.Kind == ClipboardItemKind.Color)
        {
            var path = UniqueAssetPathForItem(item, []);
            WriteColorSwatchAsset(item, path);
            item.AssetPath = path;
            return;
        }

        if (item.Kind != ClipboardItemKind.Files || item.FilePaths.Count == 0)
        {
            return;
        }

        item.AssetPath = PersistFileContent(item.FilePaths);
    }

    private string PersistFileContent(IReadOnlyList<string> filePaths)
    {
        var firstPath = filePaths[0];
        var folder = ContentFolderFor(ClipboardItemKind.Files, firstPath);
        Directory.CreateDirectory(folder);

        if (filePaths.Count == 1 && File.Exists(firstPath))
        {
            var copyPath = UniqueAssetPath(folder, Path.GetFileName(firstPath));
            File.Copy(firstPath, copyPath, overwrite: false);
            return copyPath;
        }

        var bundleFolder = UniqueAssetPath(folder, Path.GetFileName(firstPath));
        Directory.CreateDirectory(bundleFolder);
        foreach (var path in filePaths.Where(File.Exists))
        {
            File.Copy(path, UniqueAssetPath(bundleFolder, Path.GetFileName(path)), overwrite: false);
        }

        File.WriteAllLines(Path.Combine(bundleFolder, "original-paths.txt"), filePaths);
        return bundleFolder;
    }

    private static string UniqueAssetPath(string folder, string fileName, string? currentPath = null)
    {
        var extension = Path.GetExtension(fileName);
        var baseName = Path.GetFileNameWithoutExtension(fileName);
        if (string.IsNullOrWhiteSpace(baseName))
        {
            baseName = "Clipboard item";
        }

        baseName = TrimFileName(SanitizeFileName(baseName), 90);
        var safeExtension = SanitizeExtension(extension);
        var candidate = Path.Combine(folder, baseName + safeExtension);
        if (PathMatches(candidate, currentPath) || (!File.Exists(candidate) && !Directory.Exists(candidate)))
        {
            return candidate;
        }

        for (var index = 2; index < 1000; index++)
        {
            candidate = Path.Combine(folder, $"{baseName} ({index}){safeExtension}");
            if (PathMatches(candidate, currentPath) || (!File.Exists(candidate) && !Directory.Exists(candidate)))
            {
                return candidate;
            }
        }

        return Path.Combine(folder, $"{baseName} ({Guid.NewGuid():N}){safeExtension}");
    }

    private static string SanitizeFileName(string fileName)
    {
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            fileName = fileName.Replace(invalid, '-');
        }

        return fileName;
    }

    private static string SanitizeExtension(string extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
        {
            return string.Empty;
        }

        extension = extension.Trim();
        return extension.StartsWith('.') ? SanitizeFileName(extension) : "." + SanitizeFileName(extension);
    }

    private static bool PathMatches(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        try
        {
            return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static string TrimFileName(string fileName, int maxLength)
    {
        fileName = fileName.Trim();
        if (fileName.Length == 0)
        {
            return "Clipboard item";
        }

        fileName = fileName.TrimEnd('.');
        if (fileName.Length == 0)
        {
            return "Clipboard item";
        }

        return fileName.Length <= maxLength ? fileName : fileName[..maxLength].TrimEnd();
    }

    private string ContentFolderFor(ClipboardItemKind kind, string? sourcePath = null)
    {
        return kind switch
        {
            ClipboardItemKind.Text => Path.Combine(ContentRootPath, "text"),
            ClipboardItemKind.Link => Path.Combine(ContentRootPath, "links"),
            ClipboardItemKind.Color => Path.Combine(ContentRootPath, "color"),
            ClipboardItemKind.Image => Path.Combine(ContentRootPath, "image"),
            ClipboardItemKind.Files => Path.Combine(ContentRootPath, "file", FileCategoryKey(sourcePath)),
            _ => ContentRootPath,
        };
    }

    private void EnsureContentFolders()
    {
        Directory.CreateDirectory(Path.Combine(ContentRootPath, "text"));
        Directory.CreateDirectory(Path.Combine(ContentRootPath, "image"));
        Directory.CreateDirectory(Path.Combine(ContentRootPath, "links"));
        Directory.CreateDirectory(Path.Combine(ContentRootPath, "color"));
        Directory.CreateDirectory(Path.Combine(ContentRootPath, "file"));
    }

    private void EnsureHistoryFileInContentRoot()
    {
        if (File.Exists(HistoryFilePath))
        {
            return;
        }

        var previousHistoryPath = Path.Combine(RootPath, "history.json");
        if (!File.Exists(previousHistoryPath))
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(HistoryFilePath)!);
        File.Copy(previousHistoryPath, HistoryFilePath, overwrite: false);
    }

    private void MigratePreviousDefaultContentFolder()
    {
        var previousPath = Path.Combine(RootPath, PreviousContentFolderName);
        var currentPath = Path.Combine(RootPath, ContentFolderName);
        if (!ContentRootPath.Equals(currentPath, StringComparison.OrdinalIgnoreCase) ||
            !Directory.Exists(previousPath) ||
            Directory.Exists(currentPath))
        {
            return;
        }

        try
        {
            Directory.Move(previousPath, currentPath);
        }
        catch (IOException)
        {
            CopyDirectory(previousPath, currentPath);
            TryDeleteDirectory(previousPath);
        }
        catch (UnauthorizedAccessException)
        {
            CopyDirectory(previousPath, currentPath);
            TryDeleteDirectory(previousPath);
        }
    }

    private static string FileCategoryKey(string? path)
    {
        if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
        {
            return "folder";
        }

        var ext = string.IsNullOrWhiteSpace(path) ? "" : Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".pdf" => "pdf",
            ".xls" or ".xlsx" or ".xlsm" => "excel",
            ".vsd" or ".vsdx" => "visio",
            ".html" or ".htm" => "html",
            ".doc" or ".docx" => "word",
            ".ppt" or ".pptx" => "powerpoint",
            ".jpg" or ".jpeg" or ".png" or ".gif" or ".bmp" or ".webp" => "image",
            ".txt" or ".log" or ".md" or ".csv" or ".json" or ".xml" or ".css" or ".js" or ".ts" or ".cs" or ".bat" or ".cmd" or ".ps1" or ".py" => "text",
            _ => "other",
        };
    }

    private void DeleteAssets(IEnumerable<ClipboardHistoryItem> items)
    {
        foreach (var item in items)
        {
            DeleteAssetPath(item.AssetPath);
        }

        CleanupEmptyFileCategoryFolders();
    }

    private static void DeleteAssetPath(string? assetPath)
    {
        if (string.IsNullOrWhiteSpace(assetPath))
        {
            return;
        }

        try
        {
            if (File.Exists(SidecarPathFor(assetPath)))
            {
                File.Delete(SidecarPathFor(assetPath));
            }

            if (File.Exists(assetPath))
            {
                File.Delete(assetPath);
            }
            else if (Directory.Exists(assetPath))
            {
                Directory.Delete(assetPath, recursive: true);
            }
        }
        catch
        {
        }
    }

    private static bool EnsureSidecars(IEnumerable<ClipboardHistoryItem> items)
    {
        var changed = false;
        foreach (var item in items)
        {
            changed |= WriteSidecar(item);
        }

        return changed;
    }

    private bool BackfillContentAssets(List<ClipboardHistoryItem> items)
    {
        var changed = false;
        foreach (var item in items)
        {
            changed |= BackfillContentAsset(item);
        }

        return changed;
    }

    private bool EnsureFriendlyAssetNames(List<ClipboardHistoryItem> items)
    {
        var changed = false;
        foreach (var item in items)
        {
            changed |= EnsureFriendlyAssetName(item, items);
        }

        return changed;
    }

    private bool EnsureFriendlyAssetName(ClipboardHistoryItem item, IReadOnlyList<ClipboardHistoryItem> items)
    {
        if (string.IsNullOrWhiteSpace(item.AssetPath) || !IsInsideContentRoot(item.AssetPath))
        {
            return false;
        }

        if (!File.Exists(item.AssetPath) && !Directory.Exists(item.AssetPath))
        {
            return false;
        }

        var targetPath = UniqueAssetPathForItem(item, items);
        if (PathMatches(item.AssetPath, targetPath))
        {
            return false;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            if (File.Exists(item.AssetPath))
            {
                MoveSidecar(item.AssetPath, targetPath);
                File.Move(item.AssetPath, targetPath);
            }
            else
            {
                Directory.Move(item.AssetPath, targetPath);
            }

            item.AssetPath = targetPath;
            WriteSidecar(item);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private bool BackfillContentAsset(ClipboardHistoryItem item)
    {
        if (item.Kind is ClipboardItemKind.Text or ClipboardItemKind.Link)
        {
            if (IsInsideContentRoot(item.AssetPath) && File.Exists(item.AssetPath))
            {
                return false;
            }

            var path = UniqueAssetPathForItem(item, []);
            WriteTextLikeAsset(item, path);
            item.AssetPath = path;
            return true;
        }

        if (item.Kind == ClipboardItemKind.Color)
        {
            if (IsInsideContentRoot(item.AssetPath) && File.Exists(item.AssetPath) && Path.GetExtension(item.AssetPath).Equals(".png", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var oldPath = item.AssetPath;
            var path = UniqueAssetPathForItem(item, []);
            WriteColorSwatchAsset(item, path);
            item.AssetPath = path;
            if (!PathMatches(oldPath, path))
            {
                DeleteAssetPath(oldPath);
            }

            return true;
        }

        if (item.Kind == ClipboardItemKind.Image)
        {
            if (IsInsideContentRoot(item.AssetPath) && File.Exists(item.AssetPath))
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(item.AssetPath) || !File.Exists(item.AssetPath))
            {
                return false;
            }

            var extension = Path.GetExtension(item.AssetPath);
            var path = UniqueAssetPathForItem(item, []);
            File.Copy(item.AssetPath, path, overwrite: false);
            item.AssetPath = path;
            return true;
        }

        if (item.Kind != ClipboardItemKind.Files || item.FilePaths.Count == 0)
        {
            return false;
        }

        if (IsInsideContentRoot(item.AssetPath) && (File.Exists(item.AssetPath) || Directory.Exists(item.AssetPath)))
        {
            return false;
        }

        item.AssetPath = PersistFileContent(item.FilePaths);
        return true;
    }

    private bool ReconcileExternalAssetRenames(List<ClipboardHistoryItem> items)
    {
        var changed = false;
        foreach (var item in items)
        {
            if (string.IsNullOrWhiteSpace(item.AssetPath) ||
                File.Exists(item.AssetPath) ||
                Directory.Exists(item.AssetPath))
            {
                continue;
            }

            var foundPath = FindMatchingAssetByContent(item);
            if (foundPath is null)
            {
                continue;
            }

            item.AssetPath = foundPath;
            var title = TitleFromAssetPath(item, foundPath);
            item.CustomTitle = string.IsNullOrWhiteSpace(title) ? item.CustomTitle : title;
            WriteSidecar(item);
            changed = true;
        }

        return changed;
    }

    private string? FindMatchingAssetByContent(ClipboardHistoryItem item)
    {
        var folder = ContentFolderFor(item.Kind, item.FilePaths.FirstOrDefault());
        if (!Directory.Exists(folder) || string.IsNullOrWhiteSpace(item.ContentHash))
        {
            return null;
        }

        var sidecarMatch = FindMatchingAssetBySidecar(folder, item);
        if (sidecarMatch is not null)
        {
            return sidecarMatch;
        }

        foreach (var file in Directory.EnumerateFiles(folder))
        {
            if (ShouldIgnoreAssetCandidate(file))
            {
                continue;
            }

            if (AssetContentMatches(item, file))
            {
                return file;
            }
        }

        foreach (var directory in Directory.EnumerateDirectories(folder))
        {
            if (item.Kind == ClipboardItemKind.Files && AssetContentMatches(item, directory))
            {
                return directory;
            }
        }

        return null;
    }

    private string? FindMatchingAssetBySidecar(string folder, ClipboardHistoryItem item)
    {
        foreach (var sidecar in Directory.EnumerateFiles(folder, "*" + SidecarExtension, SearchOption.AllDirectories))
        {
            var metadata = ReadSidecar(sidecar);
            if (!string.Equals(metadata?.Id, item.Id, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var assetPath = AssetPathFromSidecarPath(sidecar);
            if (File.Exists(assetPath) || Directory.Exists(assetPath))
            {
                return assetPath;
            }

            if (Path.GetFileName(sidecar).Equals(DirectorySidecarName, StringComparison.OrdinalIgnoreCase))
            {
                var folderAsset = Path.GetDirectoryName(sidecar);
                if (!string.IsNullOrWhiteSpace(folderAsset) && Directory.Exists(folderAsset))
                {
                    return folderAsset;
                }
            }
        }

        return null;
    }

    private static bool ShouldIgnoreAssetCandidate(string path)
    {
        var name = Path.GetFileName(path);
        return name.Equals("history.json", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(SidecarExtension, StringComparison.OrdinalIgnoreCase);
    }

    private static bool AssetContentMatches(ClipboardHistoryItem item, string path)
    {
        try
        {
            if (item.Kind == ClipboardItemKind.Text)
            {
                var text = File.ReadAllText(path);
                return string.Equals(HashText(text), item.ContentHash, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(text, item.Text ?? string.Empty, StringComparison.Ordinal);
            }

            if (item.Kind == ClipboardItemKind.Color)
            {
                return string.Equals(Path.GetFileNameWithoutExtension(path), item.Text, StringComparison.OrdinalIgnoreCase);
            }

            if (item.Kind == ClipboardItemKind.Link)
            {
                var text = ReadLinkAssetText(path);
                return string.Equals(HashText(text), item.ContentHash, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(text, item.Text ?? string.Empty, StringComparison.Ordinal);
            }

            if ((item.Kind == ClipboardItemKind.Image || item.Kind == ClipboardItemKind.Files) && File.Exists(path))
            {
                return string.Equals(HashFile(path), item.ContentHash, StringComparison.OrdinalIgnoreCase);
            }

            if (item.Kind == ClipboardItemKind.Files && Directory.Exists(path))
            {
                var manifest = Path.Combine(path, "original-paths.txt");
                if (File.Exists(manifest))
                {
                    var text = string.Join("|", File.ReadAllLines(manifest).OrderBy(value => value, StringComparer.OrdinalIgnoreCase));
                    return string.Equals(HashText(text), item.ContentHash, StringComparison.OrdinalIgnoreCase);
                }
            }
        }
        catch
        {
        }

        return false;
    }

    private static string ReadLinkAssetText(string path)
    {
        if (!File.Exists(path))
        {
            return string.Empty;
        }

        if (!Path.GetExtension(path).Equals(".url", StringComparison.OrdinalIgnoreCase))
        {
            return File.ReadAllText(path);
        }

        foreach (var line in File.ReadLines(path))
        {
            if (line.StartsWith("URL=", StringComparison.OrdinalIgnoreCase))
            {
                return line[4..];
            }
        }

        return string.Empty;
    }

    private void RenameAssetForItem(ClipboardHistoryItem item, IReadOnlyList<ClipboardHistoryItem> items)
    {
        EnsureFriendlyAssetName(item, items);
    }

    private string UniqueAssetPathForItem(ClipboardHistoryItem item, IReadOnlyList<ClipboardHistoryItem> items)
    {
        var folder = ContentFolderFor(item.Kind, item.FilePaths.FirstOrDefault());
        Directory.CreateDirectory(folder);
        var fileName = DesiredAssetFileName(item);
        var currentPath = item.AssetPath;
        var reservedPaths = items
            .Where(existing => !ReferenceEquals(existing, item))
            .Select(existing => existing.AssetPath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var extension = Path.GetExtension(fileName);
        var baseName = Path.GetFileNameWithoutExtension(fileName);
        var target = UniqueAssetPath(folder, fileName, currentPath);
        if (!reservedPaths.Contains(target))
        {
            return target;
        }

        for (var index = 2; index < 1000; index++)
        {
            var candidate = Path.Combine(folder, $"{SanitizeFileName(baseName)} ({index}){SanitizeExtension(extension)}");
            if (PathMatches(candidate, currentPath) || (!reservedPaths.Contains(candidate) && !File.Exists(candidate) && !Directory.Exists(candidate)))
            {
                return candidate;
            }
        }

        return Path.Combine(folder, $"{SanitizeFileName(baseName)} ({Guid.NewGuid():N}){SanitizeExtension(extension)}");
    }

    private static string DesiredAssetFileName(ClipboardHistoryItem item)
    {
        var title = DisplayTitle(item);
        return item.Kind switch
        {
            ClipboardItemKind.Link => $"{title}.url",
            ClipboardItemKind.Text => $"{title}.txt",
            ClipboardItemKind.Color => $"{title}.png",
            ClipboardItemKind.Image => $"{title}{ImageAssetExtension(item)}",
            ClipboardItemKind.Files when item.FilePaths.Count == 1 && File.Exists(item.FilePaths[0]) => FileNameWithExtension(title, Path.GetExtension(item.FilePaths[0])),
            ClipboardItemKind.Files when !string.IsNullOrWhiteSpace(item.AssetPath) && File.Exists(item.AssetPath) => FileNameWithExtension(title, Path.GetExtension(item.AssetPath)),
            ClipboardItemKind.Files => title,
            _ => $"{title}.txt",
        };
    }

    private static string DisplayTitle(ClipboardHistoryItem item)
    {
        if (!string.IsNullOrWhiteSpace(item.CustomTitle))
        {
            return item.CustomTitle;
        }

        if (item.Kind == ClipboardItemKind.Files && item.FilePaths.Count == 1)
        {
            return Path.GetFileName(item.FilePaths[0]);
        }

        if (item.Kind == ClipboardItemKind.Image && string.IsNullOrWhiteSpace(item.Preview))
        {
            return item.ImageWidth is not null && item.ImageHeight is not null ? $"Image {item.ImageWidth} x {item.ImageHeight}" : "Image";
        }

        return string.IsNullOrWhiteSpace(item.Preview) ? item.Kind.ToString() : item.Preview;
    }

    private static string ImageAssetExtension(ClipboardHistoryItem item)
    {
        var extension = Path.GetExtension(item.AssetPath);
        return string.IsNullOrWhiteSpace(extension) ? ".png" : extension;
    }

    private static string FileNameWithExtension(string title, string extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
        {
            return title;
        }

        return title.EndsWith(extension, StringComparison.OrdinalIgnoreCase) ? title : title + extension;
    }

    private static string TitleFromAssetPath(ClipboardHistoryItem item, string path)
    {
        if (item.Kind == ClipboardItemKind.Files)
        {
            return Path.GetFileName(path);
        }

        return Path.GetFileNameWithoutExtension(path);
    }

    private static void WriteTextLikeAsset(ClipboardHistoryItem item, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (item.Kind == ClipboardItemKind.Link)
        {
            File.WriteAllText(path, $"[InternetShortcut]{Environment.NewLine}URL={item.Text ?? string.Empty}{Environment.NewLine}");
            return;
        }

        File.WriteAllText(path, item.Text ?? string.Empty);
    }

    private static void WriteColorSwatchAsset(ClipboardHistoryItem item, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var color = ParseHexColor(item.Text ?? item.Preview);
        WriteSolidPng(path, color.red, color.green, color.blue, 128, 128);
    }

    private static (byte red, byte green, byte blue) ParseHexColor(string? value)
    {
        var hex = (value ?? string.Empty).Trim().TrimStart('#');
        if (hex.Length == 3)
        {
            hex = string.Concat(hex.Select(ch => $"{ch}{ch}"));
        }

        if (hex.Length != 6 ||
            !byte.TryParse(hex[..2], System.Globalization.NumberStyles.HexNumber, null, out var red) ||
            !byte.TryParse(hex.Substring(2, 2), System.Globalization.NumberStyles.HexNumber, null, out var green) ||
            !byte.TryParse(hex.Substring(4, 2), System.Globalization.NumberStyles.HexNumber, null, out var blue))
        {
            return (0, 0, 0);
        }

        return (red, green, blue);
    }

    private static void WriteSolidPng(string path, byte red, byte green, byte blue, int width, int height)
    {
        var rowLength = 1 + (width * 3);
        var raw = new byte[rowLength * height];
        for (var y = 0; y < height; y++)
        {
            var rowStart = y * rowLength;
            raw[rowStart] = 0;
            for (var x = 0; x < width; x++)
            {
                var pixel = rowStart + 1 + (x * 3);
                raw[pixel] = red;
                raw[pixel + 1] = green;
                raw[pixel + 2] = blue;
            }
        }

        using var compressed = new MemoryStream();
        using (var zlib = new System.IO.Compression.ZLibStream(compressed, System.IO.Compression.CompressionLevel.SmallestSize, leaveOpen: true))
        {
            zlib.Write(raw);
        }

        using var stream = File.Create(path);
        stream.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

        var ihdr = new byte[13];
        WriteBigEndian(ihdr, 0, width);
        WriteBigEndian(ihdr, 4, height);
        ihdr[8] = 8;
        ihdr[9] = 2;
        ihdr[10] = 0;
        ihdr[11] = 0;
        ihdr[12] = 0;
        WritePngChunk(stream, "IHDR", ihdr);
        WritePngChunk(stream, "IDAT", compressed.ToArray());
        WritePngChunk(stream, "IEND", []);
    }

    private static void WritePngChunk(Stream stream, string type, byte[] data)
    {
        Span<byte> length = stackalloc byte[4];
        WriteBigEndian(length, 0, data.Length);
        stream.Write(length);

        var typeBytes = Encoding.ASCII.GetBytes(type);
        stream.Write(typeBytes);
        stream.Write(data);

        Span<byte> crcBytes = stackalloc byte[4];
        WriteBigEndian(crcBytes, 0, unchecked((int)Crc32(typeBytes, data)));
        stream.Write(crcBytes);
    }

    private static void WriteBigEndian(Span<byte> target, int offset, int value)
    {
        target[offset] = (byte)((value >> 24) & 0xFF);
        target[offset + 1] = (byte)((value >> 16) & 0xFF);
        target[offset + 2] = (byte)((value >> 8) & 0xFF);
        target[offset + 3] = (byte)(value & 0xFF);
    }

    private static uint Crc32(byte[] typeBytes, byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var value in typeBytes)
        {
            crc = UpdateCrc32(crc, value);
        }

        foreach (var value in data)
        {
            crc = UpdateCrc32(crc, value);
        }

        return crc ^ 0xFFFFFFFFu;
    }

    private static uint UpdateCrc32(uint crc, byte value)
    {
        crc ^= value;
        for (var bit = 0; bit < 8; bit++)
        {
            crc = (crc & 1) == 1 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }

        return crc;
    }

    private static void TouchAsset(string? assetPath)
    {
        if (string.IsNullOrWhiteSpace(assetPath))
        {
            return;
        }

        try
        {
            if (File.Exists(assetPath))
            {
                File.SetLastWriteTime(assetPath, DateTime.Now);
            }
            else if (Directory.Exists(assetPath))
            {
                Directory.SetLastWriteTime(assetPath, DateTime.Now);
            }
        }
        catch
        {
        }
    }

    private static bool WriteSidecar(ClipboardHistoryItem item)
    {
        if (string.IsNullOrWhiteSpace(item.AssetPath) ||
            (!File.Exists(item.AssetPath) && !Directory.Exists(item.AssetPath)))
        {
            return false;
        }

        try
        {
            var sidecarPath = SidecarPathFor(item.AssetPath);
            Directory.CreateDirectory(Path.GetDirectoryName(sidecarPath)!);
            var metadata = new ClipboardAssetMetadata
            {
                Id = item.Id,
                Kind = item.Kind.ToString(),
                ContentHash = item.ContentHash,
                Text = item.Kind is ClipboardItemKind.Text or ClipboardItemKind.Link or ClipboardItemKind.Color ? item.Text : null,
            };
            var json = JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true });
            var exists = File.Exists(sidecarPath);
            var oldJson = exists ? File.ReadAllText(sidecarPath) : null;
            var oldAttributes = exists ? File.GetAttributes(sidecarPath) : default;
            var shouldWrite = !exists || !string.Equals(oldJson, json, StringComparison.Ordinal);
            if (shouldWrite)
            {
                File.WriteAllText(sidecarPath, json);
            }

            var newAttributes = File.GetAttributes(sidecarPath) | FileAttributes.Hidden;
            if (oldAttributes != newAttributes)
            {
                File.SetAttributes(sidecarPath, newAttributes);
            }

            return shouldWrite || oldAttributes != newAttributes;
        }
        catch
        {
            return false;
        }
    }

    private static ClipboardAssetMetadata? ReadSidecar(string sidecarPath)
    {
        try
        {
            return JsonSerializer.Deserialize<ClipboardAssetMetadata>(File.ReadAllText(sidecarPath));
        }
        catch
        {
            return null;
        }
    }

    private static string SidecarPathFor(string assetPath)
    {
        return Directory.Exists(assetPath)
            ? Path.Combine(assetPath, DirectorySidecarName)
            : assetPath + SidecarExtension;
    }

    private static string AssetPathFromSidecarPath(string sidecarPath)
    {
        if (Path.GetFileName(sidecarPath).Equals(DirectorySidecarName, StringComparison.OrdinalIgnoreCase))
        {
            return Path.GetDirectoryName(sidecarPath) ?? sidecarPath;
        }

        return sidecarPath.EndsWith(SidecarExtension, StringComparison.OrdinalIgnoreCase)
            ? sidecarPath[..^SidecarExtension.Length]
            : sidecarPath;
    }

    private static void MoveSidecar(string oldAssetPath, string newAssetPath)
    {
        var oldSidecar = SidecarPathFor(oldAssetPath);
        if (!File.Exists(oldSidecar))
        {
            return;
        }

        try
        {
            var newSidecar = SidecarPathFor(newAssetPath);
            Directory.CreateDirectory(Path.GetDirectoryName(newSidecar)!);
            if (File.Exists(newSidecar))
            {
                File.Delete(newSidecar);
            }

            File.Move(oldSidecar, newSidecar);
            File.SetAttributes(newSidecar, File.GetAttributes(newSidecar) | FileAttributes.Hidden);
        }
        catch
        {
        }
    }

    private bool IsInsideContentRoot(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            var fullPath = Path.GetFullPath(path);
            var fullRoot = Path.GetFullPath(ContentRootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private void CleanupEmptyFileCategoryFolders()
    {
        var fileRoot = Path.Combine(ContentRootPath, "file");
        if (!Directory.Exists(fileRoot))
        {
            return;
        }

        foreach (var directory in Directory.GetDirectories(fileRoot))
        {
            try
            {
                if (!Directory.EnumerateFileSystemEntries(directory).Any())
                {
                    Directory.Delete(directory);
                }
            }
            catch
            {
            }
        }
    }

    private static string DefaultFileName(ClipboardHistoryItem item)
    {
        var stamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss");
        return item.Kind == ClipboardItemKind.Image ? $"clipboard-{stamp}.png" : $"clipboard-{stamp}.txt";
    }

    private static bool Contains(string? value, string query)
    {
        return value?.Contains(query, StringComparison.OrdinalIgnoreCase) == true;
    }

    private static void Enrich(ClipboardHistoryItem item, bool refreshCopiedAt = false)
    {
        item.FirstCopiedAt = item.FirstCopiedAt == default ? DateTimeOffset.Now : item.FirstCopiedAt;
        item.LastCopiedAt = refreshCopiedAt || item.LastCopiedAt == default ? DateTimeOffset.Now : item.LastCopiedAt;

        if (item.Kind == ClipboardItemKind.Text && ClipboardLinkDetector.IsLinkOrEmail(item.Text))
        {
            item.Kind = ClipboardItemKind.Link;
        }

        if (item.Kind == ClipboardItemKind.Text && TryNormalizeColorText(item.Text, item.SourceApplication, out var colorHex))
        {
            item.Kind = ClipboardItemKind.Color;
            item.Text = colorHex;
            item.Preview = colorHex;
            item.ContentHash = HashText(colorHex);
        }

        if (item.Kind is ClipboardItemKind.Text or ClipboardItemKind.Link or ClipboardItemKind.Color)
        {
            item.CharacterCount = item.Text?.Length ?? 0;
            item.WordCount = Regex.Matches(item.Text ?? string.Empty, @"\b[\w']+\b").Count;
            if (string.IsNullOrWhiteSpace(item.ContentHash) || item.ContentHash == HashText(string.Empty))
            {
                item.ContentHash = HashText(item.Text ?? string.Empty);
            }
        }

        if (item.Kind == ClipboardItemKind.Image && item.AssetPath is not null && File.Exists(item.AssetPath))
        {
            item.AssetSizeBytes = new FileInfo(item.AssetPath).Length;
            if (string.IsNullOrWhiteSpace(item.ContentHash) || item.ContentHash == HashText(string.Empty))
            {
                item.ContentHash = HashFile(item.AssetPath);
            }
        }

        if (item.Kind == ClipboardItemKind.Files)
        {
            item.ContentHash ??= HashText(string.Join("|", item.FilePaths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase)));
            item.AssetSizeBytes = item.FilePaths
                .Where(File.Exists)
                .Select(path => new FileInfo(path).Length)
                .Sum();
        }
    }

    private static bool NormalizeLoadedItems(List<ClipboardHistoryItem> items)
    {
        var changed = false;
        foreach (var item in items)
        {
            var before = item.ContentHash;
            var kindBefore = item.Kind;
            var copiedBefore = item.LastCopiedAt;
            NormalizeSource(item);
            RepairBadLoadCopiedAt(item);
            if (TryNormalizeColorText(item.Text ?? item.Preview, item.SourceApplication, out var colorHex))
            {
                item.Kind = ClipboardItemKind.Color;
                item.Text = colorHex;
                item.Preview = colorHex;
                item.ContentHash = HashText(colorHex);
            }
            else if (LooksLikeSavedImage(item))
            {
                item.Kind = ClipboardItemKind.Image;
            }

            Enrich(item);
            changed |= before != item.ContentHash;
            changed |= kindBefore != item.Kind;
            changed |= copiedBefore != item.LastCopiedAt;
        }

        var seen = new Dictionary<string, ClipboardHistoryItem>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index];
            if (string.IsNullOrEmpty(item.ContentHash))
            {
                continue;
            }

            var key = $"{item.Kind}:{item.ContentHash}";
            if (!seen.TryGetValue(key, out var existing))
            {
                seen[key] = item;
                continue;
            }

            existing.CopyCount += Math.Max(1, item.CopyCount);
            existing.FirstCopiedAt = existing.FirstCopiedAt < item.FirstCopiedAt ? existing.FirstCopiedAt : item.FirstCopiedAt;
            existing.LastCopiedAt = existing.LastCopiedAt > item.LastCopiedAt ? existing.LastCopiedAt : item.LastCopiedAt;
            existing.IsPinned |= item.IsPinned;
            existing.PinOrder = existing.PinOrder == 0 ? item.PinOrder : existing.PinOrder;
            items.RemoveAt(index);
            index--;
            changed = true;
        }

        return changed;
    }

    private static void RepairBadLoadCopiedAt(ClipboardHistoryItem item)
    {
        if (item.LastCopiedAt == default)
        {
            return;
        }

        var baseline = item.FirstCopiedAt != default ? item.FirstCopiedAt : item.CreatedAt;
        if (baseline == default)
        {
            return;
        }

        var lastUsed = item.LastUsedAt == default ? baseline : item.LastUsedAt;
        var copiedIsFarAfterOriginal = item.LastCopiedAt - baseline > TimeSpan.FromMinutes(10);
        var usedDidNotMoveWithCopy = item.LastCopiedAt - lastUsed > TimeSpan.FromMinutes(5);
        if (copiedIsFarAfterOriginal && usedDidNotMoveWithCopy)
        {
            item.LastCopiedAt = baseline;
        }
    }

    private static string HashText(string text)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(bytes);
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        var bytes = SHA256.HashData(stream);
        return Convert.ToHexString(bytes);
    }

    private static bool LooksLikeSavedImage(ClipboardHistoryItem item)
    {
        if (item.Kind == ClipboardItemKind.Image)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(item.AssetPath) || !File.Exists(item.AssetPath))
        {
            return false;
        }

        var extension = Path.GetExtension(item.AssetPath);
        return extension.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".bmp", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".gif", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryNormalizeColorText(string? text, string? source, out string hex)
    {
        hex = string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var trimmed = text.Trim();
        var match = Regex.Match(trimmed, @"^#?([0-9a-fA-F]{3}|[0-9a-fA-F]{6})$");
        if (!match.Success)
        {
            return false;
        }

        var sourceLooksLikeColorPicker = source?.Contains("ColorPicker", StringComparison.OrdinalIgnoreCase) == true ||
            source?.Contains("PowerToys", StringComparison.OrdinalIgnoreCase) == true ||
            source?.Equals("Clip", StringComparison.OrdinalIgnoreCase) == true ||
            source?.Equals("Clip.Shell", StringComparison.OrdinalIgnoreCase) == true;
        if (!trimmed.StartsWith('#') && !sourceLooksLikeColorPicker)
        {
            return false;
        }

        var value = match.Groups[1].Value;
        if (value.Length == 3)
        {
            value = string.Concat(value.Select(ch => $"{ch}{ch}"));
        }

        hex = "#" + value.ToUpperInvariant();
        return true;
    }

    private static void NormalizeSource(ClipboardHistoryItem item)
    {
        if (item.SourceApplication?.Equals("olk", StringComparison.OrdinalIgnoreCase) == true)
        {
            item.SourceApplication = "Outlook";
        }

        if (string.IsNullOrWhiteSpace(item.SourceApplicationPath) || !File.Exists(item.SourceApplicationPath))
        {
            return;
        }

        var appName = Path.GetFileNameWithoutExtension(item.SourceApplicationPath);
        if (!string.IsNullOrWhiteSpace(appName))
        {
            item.SourceApplication = appName.Equals("olk", StringComparison.OrdinalIgnoreCase) ? "Outlook" : appName;
        }
    }

    private static void MigrateLegacyStore(string rootPath)
    {
        if (File.Exists(Path.Combine(rootPath, "history.json")))
        {
            return;
        }

        var legacyRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            LegacyAppDataFolderName);
        var legacyHistory = Path.Combine(legacyRoot, "history.json");
        if (!File.Exists(legacyHistory))
        {
            return;
        }

        Directory.CreateDirectory(rootPath);
        var targetHistory = Path.Combine(rootPath, "history.json");
        File.Copy(legacyHistory, targetHistory, overwrite: false);

        var legacyAssets = Path.Combine(legacyRoot, "assets");
        var targetAssets = Path.Combine(rootPath, "assets");
        if (!Directory.Exists(legacyAssets) || Directory.Exists(targetAssets))
        {
            return;
        }

        CopyDirectory(legacyAssets, targetAssets);
        RewriteMigratedAssetPaths(targetHistory, legacyAssets, targetAssets);
    }

    private static void CopyDirectory(string sourceDirectory, string targetDirectory)
    {
        Directory.CreateDirectory(targetDirectory);
        foreach (var file in Directory.GetFiles(sourceDirectory))
        {
            File.Copy(file, Path.Combine(targetDirectory, Path.GetFileName(file)), overwrite: false);
        }

        foreach (var directory in Directory.GetDirectories(sourceDirectory))
        {
            CopyDirectory(directory, Path.Combine(targetDirectory, Path.GetFileName(directory)));
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (!Directory.Exists(path))
            {
                return;
            }

            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(path, recursive: true);
        }
        catch
        {
            // The new folder is already usable. Leaving the old folder is safer than blocking startup.
        }
    }

    private static void RewriteMigratedAssetPaths(string historyPath, string legacyAssets, string targetAssets)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.General)
        {
            WriteIndented = true,
        };
        var items = JsonSerializer.Deserialize<List<ClipboardHistoryItem>>(File.ReadAllText(historyPath), options);
        if (items is null)
        {
            return;
        }

        var changed = false;
        foreach (var item in items)
        {
            if (item.AssetPath?.StartsWith(legacyAssets, StringComparison.OrdinalIgnoreCase) != true)
            {
                continue;
            }

            item.AssetPath = targetAssets + item.AssetPath[legacyAssets.Length..];
            changed = true;
        }

        if (changed)
        {
            File.WriteAllText(historyPath, JsonSerializer.Serialize(items, options));
        }
    }
}

internal sealed class ClipboardAssetMetadata
{
    public string Id { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string? ContentHash { get; set; }
    public string? Text { get; set; }
}
