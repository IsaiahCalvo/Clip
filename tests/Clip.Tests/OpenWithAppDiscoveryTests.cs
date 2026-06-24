using Clip.Core;

namespace Clip.Tests;

// Covers the WPF-free "Open with" discovery service promoted into Clip.Core. The list-item
// "has open target" gating decides whether the "Open with…" command is offered by the Command
// Palette surfaces, and is the pure, isolated logic worth pinning. The live
// OpenWithAppDiscovery.GetApps reads OS state (registry, Start Menu, Get-StartApps), so it is
// exercised only to confirm it returns the synthetic "Default app" row and never throws.
public sealed class OpenWithAppDiscoveryTests
{
    [Fact]
    public void GetAppsAlwaysIncludesDefaultAppAndDoesNotThrow()
    {
        var apps = OpenWithAppDiscovery.GetApps(Path.Combine(Path.GetTempPath(), "sample-open-with.txt"));

        Assert.Contains(apps, app => app.IsDefault);
    }

    [Fact]
    public void TryGetOpenWithTargetReturnsFalseForLinkItem()
    {
        // A URL has no app picker; the standalone shell excludes links from "Open with".
        var item = ListItemWithActions("Link", openable: false, preview: "https://example.com");

        Assert.False(item.TryGetOpenWithTarget(out _));
    }

    [Fact]
    public void TryGetOpenWithTargetReturnsAssetPathForImage()
    {
        var item = ListItemWithActions("Image", openable: true, assetPath: @"C:\pics\shot.png");

        Assert.True(item.TryGetOpenWithTarget(out var target));
        Assert.Equal(@"C:\pics\shot.png", target);
    }

    [Fact]
    public void TryGetOpenWithTargetReturnsFirstFilePathForFiles()
    {
        var item = ListItemWithActions("Files", openable: true, filePaths: [@"C:\docs\a.pdf", @"C:\docs\b.pdf"]);

        Assert.True(item.TryGetOpenWithTarget(out var target));
        Assert.Equal(@"C:\docs\a.pdf", target);
    }

    [Fact]
    public void TryGetOpenWithTargetReturnsPreviewForPathLikeText()
    {
        var item = ListItemWithActions("Text", openable: true, preview: @"C:\docs\notes.txt");

        Assert.True(item.TryGetOpenWithTarget(out var target));
        Assert.Equal(@"C:\docs\notes.txt", target);
    }

    [Fact]
    public void TryGetOpenWithTargetReturnsFalseWhenNoOpenAction()
    {
        var item = ListItemWithActions("Text", openable: false, preview: "just some text");

        Assert.False(item.TryGetOpenWithTarget(out _));
    }

    [Fact]
    public void TryGetOpenWithTargetReturnsFalseForImageWithoutAsset()
    {
        var item = ListItemWithActions("Image", openable: true, assetPath: null);

        Assert.False(item.TryGetOpenWithTarget(out _));
    }

    private static ClipboardHistoryListItem ListItemWithActions(
        string kind,
        bool openable,
        string? assetPath = null,
        IReadOnlyList<string>? filePaths = null,
        string preview = "")
    {
        var actions = new List<ClipboardHistoryListAction>();
        if (openable)
        {
            actions.Add(new ClipboardHistoryListAction("open", "Open", "Clip.Watcher.exe", ["open", "id"], RequiresFullItem: true));
        }

        return new ClipboardHistoryListItem(
            Id: "id",
            Kind: kind,
            Title: "title",
            Preview: preview,
            FilePaths: filePaths ?? [],
            IsPinned: false,
            PinOrder: 0,
            HasOriginalFormatting: false,
            SourceApplication: null,
            AssetSizeBytes: null,
            CharacterCount: null,
            WordCount: null,
            LastUsedAt: DateTimeOffset.UtcNow,
            LastCopiedAt: DateTimeOffset.UtcNow,
            CopyCount: 1,
            ImageWidth: null,
            ImageHeight: null,
            DefaultActionId: actions.FirstOrDefault()?.Id,
            Actions: actions)
        {
            AssetPath = assetPath,
        };
    }
}
