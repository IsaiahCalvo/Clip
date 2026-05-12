namespace Clip.Core;

public sealed class ClipboardHistoryItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public ClipboardItemKind Kind { get; set; }

    public string Preview { get; set; } = string.Empty;

    public string? CustomTitle { get; set; }

    public string? ContentHash { get; set; }

    public string? Text { get; set; }

    public string? HtmlText { get; set; }

    public string? RtfText { get; set; }

    public string? AssetPath { get; set; }

    public List<string> FilePaths { get; set; } = [];

    public bool IsPinned { get; set; }

    public int PinOrder { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;

    public DateTimeOffset LastUsedAt { get; set; } = DateTimeOffset.Now;

    public DateTimeOffset FirstCopiedAt { get; set; } = DateTimeOffset.Now;

    public DateTimeOffset LastCopiedAt { get; set; } = DateTimeOffset.Now;

    public int CopyCount { get; set; } = 1;

    public string? SourceApplication { get; set; }

    public string? SourceApplicationPath { get; set; }

    public string? IntegrationSource { get; set; }

    public long? AssetSizeBytes { get; set; }

    public int? ImageWidth { get; set; }

    public int? ImageHeight { get; set; }

    public int? CharacterCount { get; set; }

    public int? WordCount { get; set; }
}
