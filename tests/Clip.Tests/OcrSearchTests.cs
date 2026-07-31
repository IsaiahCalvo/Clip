using Clip.Core;

namespace Clip.Tests;

public sealed class OcrSearchTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "clip-ocr-tests", Guid.NewGuid().ToString("N"));

    private ClipboardHistoryStore NewStore() => new(_root);

    private static ClipboardHistoryItem ImageItem(string preview) => new()
    {
        Kind = ClipboardItemKind.Image,
        Preview = preview,
        ContentHash = Guid.NewGuid().ToString("N"),
    };

    [Fact]
    public void RecognizedTextMakesAnImageSearchable()
    {
        var store = NewStore();
        var item = store.AddOrUpdate(ImageItem("Image 600 x 200"));

        Assert.Empty(store.QueryItemSummaries("Kent Place"));

        var updated = store.SetOcrText(new Dictionary<string, string?> { [item.Id] = "Invoice 4471 Kent Place" });

        Assert.Equal(1, updated);
        var match = Assert.Single(store.QueryItemSummaries("Kent Place"));
        Assert.Equal(item.Id, match.Id);
    }

    [Fact]
    public void SearchingRecognizedTextWorksAfterReload()
    {
        // The list reads a prebuilt summary index rather than the history file, so recognized
        // text has to survive into that index or search silently misses it.
        var store = NewStore();
        var item = store.AddOrUpdate(ImageItem("Image 800 x 600"));
        store.SetOcrText(new Dictionary<string, string?> { [item.Id] = "quarterly revenue chart" });

        var reloaded = new ClipboardHistoryStore(_root);
        var match = Assert.Single(reloaded.QueryItemSummaries("quarterly revenue"));
        Assert.Equal(item.Id, match.Id);
    }

    [Fact]
    public void EmptyResultIsRecordedSoTheImageIsNotScannedForever()
    {
        var store = NewStore();
        var item = store.AddOrUpdate(ImageItem("Image 40 x 40"));

        store.SetOcrText(new Dictionary<string, string?> { [item.Id] = null });

        // Empty, not null: null means "never attempted" and would requeue the same image forever.
        Assert.Equal(string.Empty, store.GetItem(item.Id)?.OcrText);
    }

    [Fact]
    public void RecopyingAnImageKeepsTextAlreadyRecognized()
    {
        var store = NewStore();
        var first = ImageItem("Image 600 x 200");
        first.ContentHash = "same-image";
        var stored = store.AddOrUpdate(first);
        store.SetOcrText(new Dictionary<string, string?> { [stored.Id] = "receipt total 42.00" });

        var again = ImageItem("Image 600 x 200");
        again.ContentHash = "same-image";
        store.AddOrUpdate(again);

        var match = Assert.Single(store.QueryItemSummaries("receipt total"));
        Assert.Equal(stored.Id, match.Id);
    }

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
            // Test cleanup only.
        }
    }
}
