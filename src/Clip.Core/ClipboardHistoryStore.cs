using System.Text.Json;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using System.Text;

namespace Clip.Core;

public sealed class ClipboardHistoryStore
{
    private const string AppDataFolderName = "Clip";
    private static readonly string LegacyAppDataFolderName = "Ray" + "Clipboard";

    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.General)
    {
        WriteIndented = true,
    };
    private readonly object _sync = new();
    private List<ClipboardHistoryItem>? _itemsCache;

    public ClipboardHistoryStore(string? rootPath = null)
    {
        RootPath = rootPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            AppDataFolderName);
        AssetPath = Path.Combine(RootPath, "assets");
        HistoryFilePath = Path.Combine(RootPath, "history.json");
        if (rootPath is null)
        {
            MigrateLegacyStore(RootPath);
        }

        Directory.CreateDirectory(AssetPath);
    }

    public string RootPath { get; }

    public string AssetPath { get; }

    public string HistoryFilePath { get; }

    public IReadOnlyList<ClipboardHistoryItem> GetItems()
    {
        lock (_sync)
        {
            if (_itemsCache is not null)
            {
                return _itemsCache;
            }

            if (!File.Exists(HistoryFilePath))
            {
                _itemsCache = [];
                return _itemsCache;
            }

            var json = File.ReadAllText(HistoryFilePath);
            var items = JsonSerializer.Deserialize<List<ClipboardHistoryItem>>(json, _jsonOptions) ?? [];
            if (NormalizeLoadedItems(items))
            {
                SaveCore(items);
            }

            _itemsCache = items;
            return _itemsCache;
        }
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
                Contains(item.Text, query) ||
                item.FilePaths.Any(path => Contains(path, query)));
        }

        return items
            .OrderByDescending(item => item.IsPinned)
            .ThenBy(item => item.PinOrder == 0 ? int.MaxValue : item.PinOrder)
            .ThenByDescending(item => item.LastUsedAt)
            .ToList();
    }

    public ClipboardHistoryItem AddOrUpdate(ClipboardHistoryItem item, int maxItems = 200)
    {
        Enrich(item, refreshCopiedAt: true);
        var items = GetItems().ToList();
        var duplicate = FindDuplicate(items, item);
        if (duplicate is not null)
        {
            duplicate.LastUsedAt = DateTimeOffset.Now;
            duplicate.LastCopiedAt = DateTimeOffset.Now;
            duplicate.CopyCount++;
            duplicate.Preview = item.Preview;
            duplicate.Text = item.Text ?? duplicate.Text;
            duplicate.AssetPath = item.AssetPath ?? duplicate.AssetPath;
            duplicate.FilePaths = item.FilePaths.Count > 0 ? item.FilePaths : duplicate.FilePaths;
            duplicate.SourceApplication = item.SourceApplication ?? duplicate.SourceApplication;
            duplicate.AssetSizeBytes = item.AssetSizeBytes ?? duplicate.AssetSizeBytes;
            duplicate.ImageWidth = item.ImageWidth ?? duplicate.ImageWidth;
            duplicate.ImageHeight = item.ImageHeight ?? duplicate.ImageHeight;
            duplicate.CharacterCount = item.CharacterCount ?? duplicate.CharacterCount;
            duplicate.WordCount = item.WordCount ?? duplicate.WordCount;
            Save(items);
            return duplicate;
        }

        items.Insert(0, item);
        TrimUnpinned(items, maxItems);
        Save(items);
        return item;
    }

    public bool Delete(string id)
    {
        var items = GetItems().ToList();
        var removed = items.RemoveAll(item => item.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) > 0;
        if (removed)
        {
            Save(items);
        }

        return removed;
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
        Directory.CreateDirectory(RootPath);
        File.WriteAllText(HistoryFilePath, JsonSerializer.Serialize(items, _jsonOptions));
    }

    public string NewAssetFilePath(string extension)
    {
        Directory.CreateDirectory(AssetPath);
        return Path.Combine(AssetPath, $"{DateTimeOffset.Now:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}{extension}");
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

    private static void TrimUnpinned(List<ClipboardHistoryItem> items, int maxItems)
    {
        var unpinned = items.Where(item => !item.IsPinned).OrderByDescending(item => item.LastUsedAt).Take(maxItems).ToHashSet();
        items.RemoveAll(item => !item.IsPinned && !unpinned.Contains(item));
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

        if (item.Kind == ClipboardItemKind.Text && IsLinkOrEmail(item.Text))
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
            if (LooksLikeSavedImage(item))
            {
                item.Kind = ClipboardItemKind.Image;
            }

            NormalizeSource(item);
            RepairBadLoadCopiedAt(item);
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

    private static bool IsLinkOrEmail(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var trimmed = text.Trim();
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) && !string.IsNullOrWhiteSpace(uri.Scheme))
        {
            return true;
        }

        return Regex.IsMatch(trimmed, @"^[^@\s]+@[^@\s]+\.[^@\s]+$");
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
