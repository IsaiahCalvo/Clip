namespace Clip.Core;

/// <summary>
/// File sub-kind filter for clipboard history Files items (folder / pdf / word / excel /
/// powerpoint / visio / html / image / text plus any discovered extension kind). Mirrors
/// the standalone shell's File-pill dropdown, which is built dynamically from the kinds
/// present in history. Promotes the shell/store's private FileCategoryKey logic into a
/// single shared helper so every surface categorizes file paths identically rather than
/// duplicating the extension switch.
/// </summary>
public static class ClipboardHistoryFileKindFilter
{
    /// <summary>Prefix that namespaces file-kind filter ids so they never collide with kind/date ids.</summary>
    public const string IdPrefix = "file:";

    public const string All = IdPrefix + "all";

    /// <summary>
    /// Canonical kinds the categorizer can emit for well-known extensions. Other extensions
    /// surface as discovered kinds keyed by the bare extension (e.g. "rtf", "zip").
    /// </summary>
    public const string Folder = "folder";
    public const string Pdf = "pdf";
    public const string Word = "word";
    public const string Excel = "excel";
    public const string PowerPoint = "powerpoint";
    public const string Visio = "visio";
    public const string Html = "html";
    public const string Image = "image";
    public const string Text = "text";

    /// <summary>Builds a namespaced filter id from a bare file-kind key.</summary>
    public static string FilterIdFor(string kindKey) => IdPrefix + kindKey;

    /// <summary>
    /// Categorizes a single file path into its kind key. Folders return "folder"; known
    /// document/media extensions return their canonical key; anything else returns the bare
    /// extension (without the leading dot), matching the standalone shell's File pill.
    /// </summary>
    public static string KeyFor(string? path)
    {
        if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
        {
            return Folder;
        }

        var ext = string.IsNullOrWhiteSpace(path) ? string.Empty : Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".pdf" => Pdf,
            ".xls" or ".xlsx" or ".xlsm" => Excel,
            ".vsd" or ".vsdx" => Visio,
            ".html" or ".htm" => Html,
            ".doc" or ".docx" => Word,
            ".ppt" or ".pptx" => PowerPoint,
            ".jpg" or ".jpeg" or ".png" or ".gif" or ".bmp" or ".webp" => Image,
            ".txt" or ".log" or ".md" or ".csv" or ".json" or ".xml" or ".css" or ".js" or ".ts" or ".cs" or ".bat" or ".cmd" or ".ps1" or ".py" => Text,
            _ => ext.TrimStart('.'),
        };
    }

    /// <summary>
    /// True when <paramref name="filterId"/> selects a file sub-kind filter (i.e. it is the
    /// file "All" id or a "file:&lt;kind&gt;" id). Lets callers tell a file sub-kind selection
    /// apart from a kind/date/pinned selection on the single shared dropdown.
    /// </summary>
    public static bool IsFileKindFilter(string? filterId) =>
        !string.IsNullOrWhiteSpace(filterId) &&
        filterId.StartsWith(IdPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Returns true when the item is a Files item whose paths include the kind selected by
    /// <paramref name="filterId"/>. Non-file-kind ids (kind/date/pinned) and the file "All" id
    /// match everything, so this can be applied unconditionally alongside the other predicates.
    /// </summary>
    public static bool Matches(string? filterId, ClipboardHistoryListItem item)
    {
        if (!IsFileKindFilter(filterId) || string.Equals(filterId, All, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (item.FilePaths.Count == 0)
        {
            return false;
        }

        var wanted = filterId!.Trim()[IdPrefix.Length..].ToLowerInvariant();
        return item.FilePaths.Any(path =>
            string.Equals(KeyFor(path), wanted, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Distinct file-kind keys present across the supplied items' file paths, ordered with the
    /// well-known kinds first (in canonical order) and any discovered kinds appended
    /// alphabetically. Used to build the sub-kind dropdown dynamically from the current results.
    /// </summary>
    public static IReadOnlyList<string> DiscoverKinds(IEnumerable<ClipboardHistoryListItem> items)
    {
        var present = items
            .SelectMany(item => item.FilePaths)
            .Select(KeyFor)
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var ordered = new List<string>();
        foreach (var known in CanonicalOrder)
        {
            if (present.Remove(known))
            {
                ordered.Add(known);
            }
        }

        ordered.AddRange(present.OrderBy(key => key, StringComparer.OrdinalIgnoreCase));
        return ordered;
    }

    /// <summary>Human-readable label for a file-kind key or "file:&lt;kind&gt;" id.</summary>
    public static string LabelFor(string keyOrId)
    {
        if (string.IsNullOrWhiteSpace(keyOrId))
        {
            return "All files";
        }

        var key = keyOrId.StartsWith(IdPrefix, StringComparison.OrdinalIgnoreCase)
            ? keyOrId[IdPrefix.Length..]
            : keyOrId;

        return key.ToLowerInvariant() switch
        {
            "all" => "All files",
            Folder => "Folders",
            Pdf => "PDF",
            Word => "Word",
            Excel => "Excel",
            PowerPoint => "PowerPoint",
            Visio => "Visio",
            Html => "HTML",
            Image => "Images",
            Text => "Text",
            _ => key.ToUpperInvariant(),
        };
    }

    private static readonly string[] CanonicalOrder =
    [
        Folder, Pdf, Word, Excel, PowerPoint, Visio, Html, Image, Text,
    ];
}
