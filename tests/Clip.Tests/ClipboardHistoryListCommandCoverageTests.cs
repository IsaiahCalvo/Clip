using Clip.Core;

namespace Clip.Tests;

public sealed class ClipboardHistoryListCommandCoverageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Clip.Tests", Guid.NewGuid().ToString("N"));
    private readonly ClipboardHistoryStore _store;

    public ClipboardHistoryListCommandCoverageTests()
    {
        _store = new ClipboardHistoryStore(_root);
    }

    [Fact]
    public void IsJsonRequestDetectsFlagAfterCommandName()
    {
        Assert.True(ClipboardHistoryListCommand.IsJsonRequest(["list", "--JSON"]));
        Assert.False(ClipboardHistoryListCommand.IsJsonRequest(["list", "invoice"]));
        Assert.False(ClipboardHistoryListCommand.IsJsonRequest(["--json"]));
    }

    [Fact]
    public void ParseSupportsEqualsStyleLimitAndQuery()
    {
        _store.AddOrUpdate(TextItem("alpha invoice"));
        _store.AddOrUpdate(TextItem("beta proposal"));

        var result = ClipboardHistoryListCommand.Create(_store, ["list", "--limit=1", "--query=invoice"]);

        Assert.Equal(1, result.Limit);
        Assert.Equal("invoice", result.Query);
        var item = Assert.Single(result.Items);
        Assert.Equal("alpha invoice", item.Preview);
    }

    [Fact]
    public void ParseJoinsPositionalArgumentsIntoQuery()
    {
        _store.AddOrUpdate(TextItem("green tea latte"));
        _store.AddOrUpdate(TextItem("black coffee"));

        var result = ClipboardHistoryListCommand.Create(_store, ["list", "green", "tea"]);

        Assert.Equal("green tea", result.Query);
        var item = Assert.Single(result.Items);
        Assert.Equal("green tea latte", item.Preview);
    }

    [Fact]
    public void ParseFallsBackToDefaultLimitOnUnparsableValue()
    {
        _store.AddOrUpdate(TextItem("anything"));

        var result = ClipboardHistoryListCommand.Create(_store, ["list", "--limit=notanumber"]);

        Assert.Equal(ClipboardHistoryListCommand.DefaultLimit, result.Limit);
    }

    [Fact]
    public void TitleFallsBackToKindWhenPreviewAndCustomTitleAreBlank()
    {
        var item = new ClipboardHistoryItem { Kind = ClipboardItemKind.Image, Preview = string.Empty };

        var listItem = ClipboardHistoryListItem.FromHistoryItem(item);

        Assert.Equal("Image", listItem.Title);
    }

    [Fact]
    public void FilesItemExposesFirstPathAsOpenWithTarget()
    {
        var item = new ClipboardHistoryItem
        {
            Kind = ClipboardItemKind.Files,
            Preview = "report.txt",
            FilePaths = [@"C:\Reports\report.txt", @"C:\Reports\other.txt"],
        };

        var listItem = ClipboardHistoryListItem.FromHistoryItem(item);

        Assert.True(listItem.TryGetOpenWithTarget(out var target));
        Assert.Equal(@"C:\Reports\report.txt", target);
    }

    [Fact]
    public void LinkItemGetsOpenActionViaNormalizedUrl()
    {
        var item = new ClipboardHistoryItem
        {
            Kind = ClipboardItemKind.Link,
            Text = "https://example.com/docs",
            Preview = "https://example.com/docs",
        };

        var actions = ClipboardHistoryListAction.ForHistoryItem(item);

        Assert.Contains(actions, action => action.Id == "open");
        Assert.DoesNotContain(actions, action => action.Id == "reveal");
    }

    [Fact]
    public void PathLikeTextGetsOpenAndRevealActions()
    {
        var item = new ClipboardHistoryItem
        {
            Kind = ClipboardItemKind.Text,
            Text = @"C:\Temp\notes.txt",
            Preview = @"C:\Temp\notes.txt",
        };

        var actions = ClipboardHistoryListAction.ForHistoryItem(item);

        Assert.Contains(actions, action => action.Id == "open");
        Assert.Contains(actions, action => action.Id == "reveal");
    }

    [Fact]
    public void BlankTextItemGetsNoOpenOrRevealActions()
    {
        var item = new ClipboardHistoryItem { Kind = ClipboardItemKind.Text, Text = null, Preview = string.Empty };

        var actions = ClipboardHistoryListAction.ForHistoryItem(item);

        Assert.DoesNotContain(actions, action => action.Id == "open");
        Assert.DoesNotContain(actions, action => action.Id == "reveal");
        Assert.Contains(actions, action => action.Id == "delete");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            TestTemp.Delete(_root);
        }
    }

    private static ClipboardHistoryItem TextItem(string text)
    {
        return new ClipboardHistoryItem
        {
            Kind = ClipboardItemKind.Text,
            Text = text,
            Preview = ClipboardHistoryStore.PreviewText(text),
        };
    }
}
