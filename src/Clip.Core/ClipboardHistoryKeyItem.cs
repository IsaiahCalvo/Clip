namespace Clip.Core;

internal sealed class ClipboardHistoryKeyItem
{
    public string Id { get; set; } = string.Empty;

    public ClipboardItemKind Kind { get; set; }

    public string? ContentHash { get; set; }

    public bool IsPinned { get; set; }

    public DateTimeOffset LastUsedAt { get; set; }

    public static ClipboardHistoryKeyItem From(ClipboardHistoryItem item) => new()
    {
        Id = item.Id,
        Kind = item.Kind,
        ContentHash = item.ContentHash,
        IsPinned = item.IsPinned,
        LastUsedAt = item.LastUsedAt,
    };
}
