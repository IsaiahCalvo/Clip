using Clip.Core;
using Clip.Shell;

namespace Clip.Tests;

public sealed class MaxItemSizeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Clip.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void FileItemSizeCountsPathTextNotFileContent()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "large.bin");
        File.WriteAllBytes(path, new byte[10_000_000]);
        var item = new ClipboardHistoryItem
        {
            Kind = ClipboardItemKind.Files,
            FilePaths = [path],
            Preview = "large.bin",
        };

        var pathBytes = System.Text.Encoding.UTF8.GetByteCount(path);
        Assert.False(ClipItemSizeLimit.Allows(item, maxBytes: pathBytes - 1));
        Assert.True(ClipItemSizeLimit.Allows(item, maxBytes: pathBytes));
        Assert.True(ClipItemSizeLimit.Allows(item, maxBytes: null));
    }

    [Fact]
    public void TextItemUsesUtf8ByteSize()
    {
        var item = new ClipboardHistoryItem
        {
            Kind = ClipboardItemKind.Text,
            Text = "hello",
            Preview = "hello",
        };

        Assert.False(ClipItemSizeLimit.Allows(item, maxBytes: 4));
        Assert.True(ClipItemSizeLimit.Allows(item, maxBytes: 5));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
