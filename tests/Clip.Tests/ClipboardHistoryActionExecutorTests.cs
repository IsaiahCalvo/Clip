using Clip.Core;

namespace Clip.Tests;

public sealed class ClipboardHistoryActionExecutorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Clip.ActionExecutor.Tests", Guid.NewGuid().ToString("N"));
    private readonly ClipboardHistoryStore _store;

    public ClipboardHistoryActionExecutorTests()
    {
        _store = new ClipboardHistoryStore(_root);
    }

    [Fact]
    public void ExecutePinActionPinsItem()
    {
        var item = AddText("pin me");
        var action = new ClipboardHistoryListAction("pin", "Pin", "Clip.Watcher.exe", ["pin", item.Id], RequiresFullItem: false);

        var result = ClipboardHistoryActionExecutor.Execute(_store, action);

        Assert.True(result.Handled);
        Assert.True(result.Succeeded);
        Assert.True(result.MutatedHistory);
        Assert.True(_store.GetItem(item.Id)?.IsPinned);
    }

    [Fact]
    public void ExecuteDeleteActionRemovesItem()
    {
        var item = AddText("delete me");
        var action = new ClipboardHistoryListAction("delete", "Delete", "Clip.Watcher.exe", ["delete", item.Id], RequiresFullItem: false);

        var result = ClipboardHistoryActionExecutor.Execute(_store, action);

        Assert.True(result.Handled);
        Assert.True(result.Succeeded);
        Assert.True(result.MutatedHistory);
        Assert.Null(_store.GetItem(item.Id));
    }

    [Fact]
    public void ExecuteUnknownActionIsNotHandled()
    {
        var item = AddText("copy me");
        var action = new ClipboardHistoryListAction("copy", "Copy", "Clip.Watcher.exe", ["copy", item.Id], RequiresFullItem: true);

        var result = ClipboardHistoryActionExecutor.Execute(_store, action);

        Assert.False(result.Handled);
        Assert.False(result.Succeeded);
        Assert.False(result.MutatedHistory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private ClipboardHistoryItem AddText(string text)
    {
        var item = new ClipboardHistoryItem
        {
            Kind = ClipboardItemKind.Text,
            Text = text,
            Preview = ClipboardHistoryStore.PreviewText(text),
            SourceApplication = "Test",
        };

        return _store.AddOrUpdate(item);
    }
}
