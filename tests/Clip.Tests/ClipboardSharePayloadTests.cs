using Clip.Core;

namespace Clip.Tests;

public sealed class ClipboardSharePayloadTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Clip.Share.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void CreateForTextWritesTemporaryTxtFile()
    {
        var item = new ClipboardHistoryItem
        {
            Kind = ClipboardItemKind.Text,
            Text = "hello from clip",
            Preview = "hello from clip",
        };

        var payload = ClipboardSharePayload.Create(item, _root);

        var path = Assert.Single(payload.FilePaths);
        Assert.True(payload.HasTemporaryFiles);
        Assert.Equal(".txt", Path.GetExtension(path));
        Assert.Equal("hello from clip", File.ReadAllText(path));
    }

    [Fact]
    public void CleanupDeletesTemporaryTextFile()
    {
        var item = new ClipboardHistoryItem
        {
            Kind = ClipboardItemKind.Link,
            Text = "https://example.com",
            Preview = "https://example.com",
        };
        var payload = ClipboardSharePayload.Create(item, _root);
        var path = Assert.Single(payload.FilePaths);

        payload.Cleanup();

        Assert.False(File.Exists(path));
    }

    [Fact]
    public void CreateForImageUsesExistingImageFile()
    {
        Directory.CreateDirectory(_root);
        var image = Path.Combine(_root, "image.png");
        File.WriteAllText(image, "not really an image");
        var item = new ClipboardHistoryItem
        {
            Kind = ClipboardItemKind.Image,
            AssetPath = image,
            Preview = "Image",
        };

        var payload = ClipboardSharePayload.Create(item, _root);

        Assert.Equal(image, Assert.Single(payload.FilePaths));
        Assert.False(payload.HasTemporaryFiles);
    }

    [Fact]
    public void CleanupStaleTemporaryFilesIfDueSkipsWhenRecentlyRun()
    {
        Directory.CreateDirectory(_root);
        var staleFile = Path.Combine(_root, "clip-stale.txt");
        File.WriteAllText(staleFile, "old");
        File.SetLastWriteTime(staleFile, DateTime.Now.AddDays(-2));
        var marker = Path.Combine(_root, ".clip-share-cleanup");
        File.WriteAllText(marker, "recent");
        File.SetLastWriteTime(marker, DateTime.Now);

        var ran = ClipboardSharePayload.CleanupStaleTemporaryFilesIfDue(_root);

        Assert.False(ran);
        Assert.True(File.Exists(staleFile));
    }

    [Fact]
    public void CleanupStaleTemporaryFilesIfDueDeletesOldFilesWhenDue()
    {
        Directory.CreateDirectory(_root);
        var now = new DateTimeOffset(2026, 6, 19, 12, 0, 0, TimeSpan.Zero);
        var staleFile = Path.Combine(_root, "clip-stale.txt");
        File.WriteAllText(staleFile, "old");
        File.SetLastWriteTime(staleFile, now.AddDays(-2).LocalDateTime);
        var marker = Path.Combine(_root, ".clip-share-cleanup");
        File.WriteAllText(marker, "old-marker");
        File.SetLastWriteTime(marker, now.AddDays(-2).LocalDateTime);

        var ran = ClipboardSharePayload.CleanupStaleTemporaryFilesIfDue(_root, now: now);

        Assert.True(ran);
        Assert.False(File.Exists(staleFile));
        Assert.True(File.Exists(marker));
    }

    [Fact]
    public void CleanupStaleTemporaryFilesIfDueReturnsFalseWhenRootIsMissing()
    {
        Assert.False(ClipboardSharePayload.CleanupStaleTemporaryFilesIfDue(_root));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
