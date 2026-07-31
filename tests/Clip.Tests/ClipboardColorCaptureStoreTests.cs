using Clip.Core;

namespace Clip.Tests;

/// <summary>
/// The live capture path uses a store configured with enableLoadMaintenance:false and
/// retainLoadedItems:false, which takes a different write path than the default store the
/// other tests exercise. These cover color classification and swatch writing on that path.
/// </summary>
public sealed class ClipboardColorCaptureStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Clip.Tests", Guid.NewGuid().ToString("N"));
    private readonly ClipboardHistoryStore _store;

    public ClipboardColorCaptureStoreTests()
    {
        _store = new ClipboardHistoryStore(contentRootPath: _root, enableLoadMaintenance: false, retainLoadedItems: false);
    }

    [Fact]
    public void CaptureStoreClassifiesHexColorAndWritesSwatch()
    {
        var saved = _store.AddOrUpdate(TextItem("#3B5BDB"));

        Assert.Equal(ClipboardItemKind.Color, saved.Kind);
        Assert.Equal("#3B5BDB", saved.Text);
        Assert.Equal("#3B5BDB.png", Path.GetFileName(saved.AssetPath));
        Assert.Equal([0x89, 0x50, 0x4E, 0x47], File.ReadAllBytes(saved.AssetPath!)[..4]);
    }

    [Fact]
    public void CaptureStoreNormalizesLowercaseAndShorthandHex()
    {
        Assert.Equal("#3B5BDB", _store.AddOrUpdate(TextItem("#3b5bdb")).Text);
        Assert.Equal("#AABBCC", _store.AddOrUpdate(TextItem("#abc")).Text);
    }

    [Fact]
    public void CaptureStoreSurfacesColorKindInSummaries()
    {
        _store.AddOrUpdate(TextItem("just some text"));
        _store.AddOrUpdate(TextItem("#3B5BDB"));

        Assert.Contains(_store.QueryItemSummaries(), item => item.Kind == ClipboardItemKind.Color && item.Text == "#3B5BDB");
    }

    [Fact]
    public void CaptureStoreKeepsBareHexAsTextForOrdinaryApps()
    {
        var item = TextItem("c8cacd");
        item.SourceApplication = "Claude";

        Assert.Equal(ClipboardItemKind.Text, _store.AddOrUpdate(item).Kind);
    }

    private static ClipboardHistoryItem TextItem(string text) => new()
    {
        Kind = ClipboardItemKind.Text,
        Text = text,
        Preview = ClipboardHistoryStore.PreviewText(text),
    };

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch
        {
        }
    }
}
