using Clip.Core;

namespace Clip.Tests;

public sealed class ClipboardItemRevealTargetCoverageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Clip.Tests", Guid.NewGuid().ToString("N"));

    public ClipboardItemRevealTargetCoverageTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void ImageWithExistingAssetReturnsAssetPath()
    {
        var asset = Path.Combine(_root, "shot.png");
        File.WriteAllBytes(asset, [1, 2, 3]);
        var item = new ClipboardHistoryItem { Kind = ClipboardItemKind.Image, AssetPath = asset };

        Assert.Equal(asset, ClipboardItemRevealTarget.GetPath(item));
    }

    [Fact]
    public void ImageWithMissingAssetReturnsNull()
    {
        var item = new ClipboardHistoryItem
        {
            Kind = ClipboardItemKind.Image,
            AssetPath = Path.Combine(_root, "gone.png"),
        };

        Assert.Null(ClipboardItemRevealTarget.GetPath(item));
    }

    [Fact]
    public void NonPathTextAndColorReturnNull()
    {
        var text = new ClipboardHistoryItem { Kind = ClipboardItemKind.Text, Text = "plain words" };
        var color = new ClipboardHistoryItem { Kind = ClipboardItemKind.Color, Text = "#AABBCC" };

        Assert.Null(ClipboardItemRevealTarget.GetPath(text));
        Assert.Null(ClipboardItemRevealTarget.GetPath(color));
    }

    [Fact]
    public void TextPointingAtExistingFileReturnsThatFile()
    {
        var file = Path.Combine(_root, "doc.txt");
        File.WriteAllText(file, "x");
        var item = new ClipboardHistoryItem { Kind = ClipboardItemKind.Text, Text = file };

        Assert.Equal(file, ClipboardItemRevealTarget.GetPath(item));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
