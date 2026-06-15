using Clip.Core;

namespace Clip.Tests;

public sealed class ClipboardHistoryImportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Clip.Tests", Guid.NewGuid().ToString("N"));
    private readonly ClipboardHistoryStore _store;

    public ClipboardHistoryImportTests()
    {
        _store = new ClipboardHistoryStore(_root);
    }

    [Fact]
    public async Task ImportAddsMissingWindowsHistoryItemsWithOriginalTimes()
    {
        var morning = new DateTimeOffset(2026, 6, 15, 9, 0, 0, TimeSpan.Zero);
        var afternoon = new DateTimeOffset(2026, 6, 15, 14, 0, 0, TimeSpan.Zero);
        var source = new FakeClipboardHistorySource([
            TextSnapshot("afternoon note", afternoon),
            TextSnapshot("morning note", morning),
        ]);

        var imported = await new ClipboardHistoryImportService(_store, source).ImportAsync(maxItems: 10);

        Assert.Equal(2, imported);
        var items = _store.QueryItems().ToList();
        Assert.Equal(["afternoon note", "morning note"], items.Select(item => item.Text).ToArray());
        Assert.Equal(afternoon, items[0].LastCopiedAt);
        Assert.Equal(morning, items[1].LastCopiedAt);
    }

    [Fact]
    public async Task ImportSkipsItemsAlreadySavedByClip()
    {
        var existing = _store.AddOrUpdate(TextItem("already saved"));
        var source = new FakeClipboardHistorySource([
            TextSnapshot("already saved", DateTimeOffset.UtcNow),
        ]);

        var imported = await new ClipboardHistoryImportService(_store, source).ImportAsync(maxItems: 10);

        Assert.Equal(0, imported);
        var item = Assert.Single(_store.QueryItems());
        Assert.Equal(existing.Id, item.Id);
        Assert.Equal(1, item.CopyCount);
    }

    [Fact]
    public async Task ImportDoesNotDeleteExternalDuplicateImage()
    {
        var firstImage = Path.Combine(_root, "first.png");
        var duplicateImage = Path.Combine(_root, "outside", "duplicate.png");
        Directory.CreateDirectory(Path.GetDirectoryName(duplicateImage)!);
        File.WriteAllBytes(firstImage, [1, 2, 3]);
        File.WriteAllBytes(duplicateImage, [1, 2, 3]);
        _store.AddOrUpdate(new ClipboardHistoryItem
        {
            Kind = ClipboardItemKind.Image,
            AssetPath = firstImage,
            Preview = "Image",
        });
        var source = new FakeClipboardHistorySource([
            new ClipboardHistorySnapshotItem
            {
                Kind = ClipboardItemKind.Image,
                AssetPath = duplicateImage,
                Preview = "Image",
                CopiedAt = DateTimeOffset.UtcNow,
            },
        ]);

        var imported = await new ClipboardHistoryImportService(_store, source).ImportAsync(maxItems: 10);

        Assert.Equal(0, imported);
        Assert.True(File.Exists(duplicateImage));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static ClipboardHistorySnapshotItem TextSnapshot(string text, DateTimeOffset copiedAt)
    {
        return new ClipboardHistorySnapshotItem
        {
            Kind = ClipboardItemKind.Text,
            Text = text,
            Preview = ClipboardHistoryStore.PreviewText(text),
            CopiedAt = copiedAt,
        };
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

    private sealed class FakeClipboardHistorySource(IReadOnlyList<ClipboardHistorySnapshotItem> items) : IClipboardHistorySource
    {
        public Task<IReadOnlyList<ClipboardHistorySnapshotItem>> GetItemsAsync(Func<string, string> reserveImagePath, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(items);
        }
    }
}
