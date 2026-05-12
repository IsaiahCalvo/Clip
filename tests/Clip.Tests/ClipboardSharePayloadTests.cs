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

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
