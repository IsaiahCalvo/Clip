using Clip.Core;

namespace Clip.Tests;

public sealed class ClipboardItemLaunchCommandCoverageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Clip.Tests", Guid.NewGuid().ToString("N"));

    public ClipboardItemLaunchCommandCoverageTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void CreateOpenStartInfoReturnsNullWhenItemHasNoTarget()
    {
        var item = new ClipboardHistoryItem { Kind = ClipboardItemKind.Text, Text = "not a path" };

        Assert.Null(ClipboardItemLaunchCommand.CreateOpenStartInfo(item));
    }

    [Fact]
    public void GetOpenTargetReturnsAssetPathForImageWithExistingFile()
    {
        var asset = Path.Combine(_root, "picture.png");
        File.WriteAllBytes(asset, [1, 2, 3]);
        var item = new ClipboardHistoryItem { Kind = ClipboardItemKind.Image, AssetPath = asset };

        Assert.Equal(asset, ClipboardItemLaunchCommand.GetOpenTarget(item));
    }

    [Fact]
    public void LinkFallsBackToPreviewWhenTextIsEmpty()
    {
        var item = new ClipboardHistoryItem
        {
            Kind = ClipboardItemKind.Link,
            Text = null,
            Preview = "https://example.com/page",
        };

        var target = ClipboardItemLaunchCommand.GetOpenTarget(item);

        Assert.NotNull(target);
        Assert.StartsWith("https://example.com", target);
    }

    [Fact]
    public void LinkWithNoTextOrPreviewHasNoTarget()
    {
        var item = new ClipboardHistoryItem { Kind = ClipboardItemKind.Link, Text = null, Preview = string.Empty };

        Assert.Null(ClipboardItemLaunchCommand.GetOpenTarget(item));
        Assert.Null(ClipboardItemLaunchCommand.CreateOpenStartInfo(item));
    }

    [Fact]
    public void CreateOpenStartInfoWithAppPathQuotesTarget()
    {
        var asset = Path.Combine(_root, "with space.png");
        File.WriteAllBytes(asset, [1]);
        var item = new ClipboardHistoryItem { Kind = ClipboardItemKind.Image, AssetPath = asset };

        var info = ClipboardItemLaunchCommand.CreateOpenStartInfo(item, appPath: @"C:\Tools\viewer.exe");

        Assert.NotNull(info);
        Assert.Equal(@"C:\Tools\viewer.exe", info!.FileName);
        Assert.Equal("\"" + asset + "\"", info.Arguments);
        Assert.True(info.UseShellExecute);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            TestTemp.Delete(_root);
        }
    }
}
