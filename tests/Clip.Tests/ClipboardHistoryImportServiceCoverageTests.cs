using Clip.Core;

namespace Clip.Tests;

public sealed class ClipboardHistoryImportServiceCoverageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Clip.Tests", Guid.NewGuid().ToString("N"));
    private readonly ClipboardHistoryStore _store;

    public ClipboardHistoryImportServiceCoverageTests()
    {
        _store = new ClipboardHistoryStore(_root);
    }

    [Fact]
    public async Task ImportReservesImagePathsInsideTheStoreContentRoot()
    {
        var source = new ReservingImageSource();

        var imported = await new ClipboardHistoryImportService(_store, source).ImportAsync(maxItems: 10);

        Assert.Equal(1, imported);
        var item = Assert.Single(_store.QueryItems());
        Assert.Equal(ClipboardItemKind.Image, item.Kind);
        Assert.NotNull(source.ReservedPath);
        Assert.StartsWith(Path.GetFullPath(_store.ContentRootPath), Path.GetFullPath(source.ReservedPath!), StringComparison.OrdinalIgnoreCase);
        // The snapshot carried no preview, so the importer supplied the default one.
        Assert.Equal("Image", item.Preview);
    }

    [Fact]
    public async Task ImportBuildsDefaultPreviewForTextWithoutOne()
    {
        var source = new FakeSource([
            new ClipboardHistorySnapshotItem
            {
                Kind = ClipboardItemKind.Text,
                Text = "text without a preview",
                Preview = "   ",
                CopiedAt = DateTimeOffset.UtcNow,
            },
        ]);

        var imported = await new ClipboardHistoryImportService(_store, source).ImportAsync(maxItems: 10);

        Assert.Equal(1, imported);
        var item = Assert.Single(_store.QueryItems());
        Assert.Equal(ClipboardHistoryStore.PreviewText("text without a preview"), item.Preview);
    }

    [Fact]
    public async Task ImportBuildsDefaultPreviewsForFileSnapshots()
    {
        var first = Path.Combine(_root, "report.pdf");
        var second = Path.Combine(_root, "notes.txt");
        File.WriteAllText(first, "pdf");
        File.WriteAllText(second, "notes");
        var source = new FakeSource([
            new ClipboardHistorySnapshotItem
            {
                Kind = ClipboardItemKind.Files,
                FilePaths = [first],
                CopiedAt = DateTimeOffset.UtcNow.AddMinutes(-2),
            },
            new ClipboardHistorySnapshotItem
            {
                Kind = ClipboardItemKind.Files,
                FilePaths = [first, second],
                CopiedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            },
        ]);

        var imported = await new ClipboardHistoryImportService(_store, source).ImportAsync(maxItems: 10);

        Assert.Equal(2, imported);
        var previews = _store.QueryItems().Select(item => item.Preview).OrderBy(preview => preview).ToList();
        Assert.Equal(["2 files", "report.pdf"], previews);
    }

    [Fact]
    public async Task ImportSkipsSnapshotsOfUnknownKind()
    {
        var source = new FakeSource([
            new ClipboardHistorySnapshotItem
            {
                Kind = (ClipboardItemKind)999,
                Text = "mystery payload",
                CopiedAt = DateTimeOffset.UtcNow,
            },
        ]);

        var imported = await new ClipboardHistoryImportService(_store, source).ImportAsync(maxItems: 10);

        Assert.Equal(0, imported);
        Assert.Empty(_store.QueryItems());
    }

    [Fact]
    public async Task ImportDeletesReservedAssetForDuplicateImageInsideContentRoot()
    {
        var firstImage = Path.Combine(_root, "first.png");
        File.WriteAllBytes(firstImage, [1, 2, 3]);
        _store.AddOrUpdate(new ClipboardHistoryItem
        {
            Kind = ClipboardItemKind.Image,
            AssetPath = firstImage,
            Preview = "Image",
        });
        var reserved = _store.NewAssetFilePath(ClipboardItemKind.Image, extension: ".png");
        File.WriteAllBytes(reserved, [1, 2, 3]);
        var source = new FakeSource([
            new ClipboardHistorySnapshotItem
            {
                Kind = ClipboardItemKind.Image,
                AssetPath = reserved,
                Preview = "Image",
                CopiedAt = DateTimeOffset.UtcNow,
            },
        ]);

        var imported = await new ClipboardHistoryImportService(_store, source).ImportAsync(maxItems: 10);

        Assert.Equal(0, imported);
        Assert.False(File.Exists(reserved));
    }

    [Fact]
    public async Task ImportToleratesReservedAssetItCannotDelete()
    {
        var firstImage = Path.Combine(_root, "first.png");
        File.WriteAllBytes(firstImage, [1, 2, 3]);
        _store.AddOrUpdate(new ClipboardHistoryItem
        {
            Kind = ClipboardItemKind.Image,
            AssetPath = firstImage,
            Preview = "Image",
        });
        var reserved = _store.NewAssetFilePath(ClipboardItemKind.Image, extension: ".png");
        File.WriteAllBytes(reserved, [1, 2, 3]);
        var source = new FakeSource([
            new ClipboardHistorySnapshotItem
            {
                Kind = ClipboardItemKind.Image,
                AssetPath = reserved,
                Preview = "Image",
                CopiedAt = DateTimeOffset.UtcNow,
            },
        ]);

        int imported;
        using (new FileStream(reserved, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            imported = await new ClipboardHistoryImportService(_store, source).ImportAsync(maxItems: 10);
        }

        Assert.Equal(0, imported);
        Assert.True(File.Exists(reserved));
    }

    [Fact]
    public async Task ImportToleratesAssetPathWithInvalidCharacters()
    {
        var source = new FakeSource([
            new ClipboardHistorySnapshotItem
            {
                Kind = ClipboardItemKind.Image,
                AssetPath = "bad\0asset.png",
                Preview = "Image",
                CopiedAt = DateTimeOffset.UtcNow,
            },
        ]);

        var imported = await new ClipboardHistoryImportService(_store, source).ImportAsync(maxItems: 10);

        Assert.Equal(0, imported);
        Assert.Empty(_store.QueryItems());
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            TestTemp.Delete(_root);
        }
    }

    private sealed class FakeSource(IReadOnlyList<ClipboardHistorySnapshotItem> items) : IClipboardHistorySource
    {
        public Task<IReadOnlyList<ClipboardHistorySnapshotItem>> GetItemsAsync(Func<string, string> reserveImagePath, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(items);
        }
    }

    private sealed class ReservingImageSource : IClipboardHistorySource
    {
        public string? ReservedPath { get; private set; }

        public Task<IReadOnlyList<ClipboardHistorySnapshotItem>> GetItemsAsync(Func<string, string> reserveImagePath, CancellationToken cancellationToken = default)
        {
            ReservedPath = reserveImagePath(".png");
            File.WriteAllBytes(ReservedPath, [9, 8, 7]);
            IReadOnlyList<ClipboardHistorySnapshotItem> items = [
                new ClipboardHistorySnapshotItem
                {
                    Kind = ClipboardItemKind.Image,
                    AssetPath = ReservedPath,
                    CopiedAt = DateTimeOffset.UtcNow,
                },
            ];
            return Task.FromResult(items);
        }
    }
}
