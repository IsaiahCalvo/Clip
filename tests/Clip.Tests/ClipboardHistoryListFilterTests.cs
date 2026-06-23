using Clip.Core;

namespace Clip.Tests;

public sealed class ClipboardHistoryListFilterTests
{
    [Fact]
    public void MatchesPinnedFilterOnlyForPinnedItems()
    {
        var pinned = Item("Text", isPinned: true);
        var unpinned = Item("Text", isPinned: false);

        Assert.True(ClipboardHistoryListFilter.Matches(ClipboardHistoryListFilter.Pinned, pinned));
        Assert.False(ClipboardHistoryListFilter.Matches(ClipboardHistoryListFilter.Pinned, unpinned));
    }

    [Fact]
    public void MatchesKindFiltersByClipboardKind()
    {
        Assert.True(ClipboardHistoryListFilter.Matches(ClipboardHistoryListFilter.Files, Item("Files")));
        Assert.False(ClipboardHistoryListFilter.Matches(ClipboardHistoryListFilter.Files, Item("Text")));
        Assert.True(ClipboardHistoryListFilter.Matches(ClipboardHistoryListFilter.Images, Item("Image")));
        Assert.True(ClipboardHistoryListFilter.Matches(ClipboardHistoryListFilter.Links, Item("Link")));
    }

    [Fact]
    public void EmptyOrAllFilterMatchesEverything()
    {
        var item = Item("Color");

        Assert.True(ClipboardHistoryListFilter.Matches(null, item));
        Assert.True(ClipboardHistoryListFilter.Matches("", item));
        Assert.True(ClipboardHistoryListFilter.Matches(ClipboardHistoryListFilter.All, item));
    }

    private static ClipboardHistoryListItem Item(string kind, bool isPinned = false) => new(
        Id: Guid.NewGuid().ToString("N"),
        Kind: kind,
        Title: kind,
        Preview: kind,
        FilePaths: [],
        IsPinned: isPinned,
        PinOrder: 0,
        HasOriginalFormatting: false,
        SourceApplication: "Test",
        AssetSizeBytes: null,
        CharacterCount: null,
        WordCount: null,
        LastUsedAt: DateTimeOffset.Now,
        LastCopiedAt: DateTimeOffset.Now,
        CopyCount: 1,
        ImageWidth: null,
        ImageHeight: null,
        DefaultActionId: null,
        Actions: []);
}
