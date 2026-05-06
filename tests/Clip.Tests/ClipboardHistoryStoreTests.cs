using Clip.Core;
using System.Text.Json;

namespace Clip.Tests;

public sealed class ClipboardHistoryStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Clip.Tests", Guid.NewGuid().ToString("N"));
    private readonly ClipboardHistoryStore _store;

    public ClipboardHistoryStoreTests()
    {
        _store = new ClipboardHistoryStore(_root);
    }

    [Fact]
    public void QueryItemsReturnsPinnedItemsFirstInPinOrder()
    {
        var first = _store.AddOrUpdate(TextItem("first"));
        var second = _store.AddOrUpdate(TextItem("second"));
        var third = _store.AddOrUpdate(TextItem("third"));

        _store.SetPinned(third.Id, true);
        _store.SetPinned(first.Id, true);

        var items = _store.QueryItems().ToList();

        Assert.Equal([third.Id, first.Id, second.Id], items.Select(item => item.Id).ToArray());
    }

    [Fact]
    public void QueryItemsFiltersByPreviewOrText()
    {
        _store.AddOrUpdate(TextItem("alpha invoice"));
        _store.AddOrUpdate(TextItem("beta proposal"));

        var items = _store.QueryItems("invoice").ToList();

        Assert.Single(items);
        Assert.Equal("alpha invoice", items[0].Text);
    }

    [Fact]
    public void EditTextUpdatesPreview()
    {
        var item = _store.AddOrUpdate(TextItem("old text"));

        Assert.True(_store.EditText(item.Id, "new text"));

        var updated = _store.GetItem(item.Id);
        Assert.Equal("new text", updated?.Text);
        Assert.Equal("new text", updated?.Preview);
    }

    [Fact]
    public void AddOrUpdateUsesContentHashToAvoidImageDuplicates()
    {
        var first = _store.AddOrUpdate(ImageItem("same-hash"));
        var second = _store.AddOrUpdate(ImageItem("same-hash"));

        var items = _store.QueryItems();

        Assert.Single(items);
        Assert.Equal(first.Id, second.Id);
        Assert.Equal(2, items[0].CopyCount);
    }

    [Fact]
    public void LoadingHistoryNormalizesPowerToysColorPickerText()
    {
        var item = TextItem("3570fc");
        item.SourceApplication = "PowerToys.ColorPickerUI";
        Directory.CreateDirectory(_root);
        File.WriteAllText(_store.HistoryFilePath, JsonSerializer.Serialize(new[] { item }));

        var loaded = _store.QueryItems().Single();

        Assert.Equal(ClipboardItemKind.Color, loaded.Kind);
        Assert.Equal("#3570FC", loaded.Text);
        Assert.Equal("#3570FC", loaded.Preview);
    }

    [Fact]
    public void LoadingHistoryNormalizesOutlookSourceName()
    {
        var item = TextItem("outlook copy");
        item.SourceApplication = "OLK";
        Directory.CreateDirectory(_root);
        File.WriteAllText(_store.HistoryFilePath, JsonSerializer.Serialize(new[] { item }));

        var loaded = _store.QueryItems().Single();

        Assert.Equal("Outlook", loaded.SourceApplication);
    }

    [Fact]
    public void LoadingHistoryDoesNotRewriteLastCopiedAt()
    {
        var copiedAt = DateTimeOffset.Now.AddDays(-1);
        var item = TextItem("yesterday");
        item.FirstCopiedAt = copiedAt;
        item.LastCopiedAt = copiedAt;
        Directory.CreateDirectory(_root);
        File.WriteAllText(_store.HistoryFilePath, JsonSerializer.Serialize(new[] { item }));

        var loaded = _store.QueryItems().Single();

        Assert.Equal(copiedAt, loaded.LastCopiedAt);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static ClipboardHistoryItem TextItem(string text)
    {
        return new ClipboardHistoryItem
        {
            Kind = ClipboardItemKind.Text,
            ContentHash = text,
            Text = text,
            Preview = ClipboardHistoryStore.PreviewText(text),
        };
    }

    private static ClipboardHistoryItem ImageItem(string hash)
    {
        return new ClipboardHistoryItem
        {
            Kind = ClipboardItemKind.Image,
            ContentHash = hash,
            Preview = "Image 10 x 10",
            ImageWidth = 10,
            ImageHeight = 10,
        };
    }
}
