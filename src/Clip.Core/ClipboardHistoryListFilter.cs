namespace Clip.Core;

public static class ClipboardHistoryListFilter
{
    public const string All = "all";
    public const string Pinned = "pinned";
    public const string Text = "text";
    public const string Links = "links";
    public const string Files = "files";
    public const string Images = "images";
    public const string Colors = "colors";

    public static bool Matches(string? filterId, ClipboardHistoryListItem item)
    {
        var normalized = string.IsNullOrWhiteSpace(filterId)
            ? All
            : filterId.Trim().ToLowerInvariant();

        return normalized switch
        {
            All => true,
            Pinned => item.IsPinned,
            Text => item.Kind.Equals("Text", StringComparison.OrdinalIgnoreCase),
            Links => item.Kind.Equals("Link", StringComparison.OrdinalIgnoreCase),
            Files => item.Kind.Equals("Files", StringComparison.OrdinalIgnoreCase),
            Images => item.Kind.Equals("Image", StringComparison.OrdinalIgnoreCase),
            Colors => item.Kind.Equals("Color", StringComparison.OrdinalIgnoreCase),
            _ => true,
        };
    }
}
