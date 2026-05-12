using Clip.Core;
using Clip.Shell;

namespace Clip.Tests;

public sealed class MaxItemSizeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Clip.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void FileItemOverLimitIsRejected()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "large.bin");
        File.WriteAllBytes(path, new byte[12]);
        var item = new ClipboardHistoryItem
        {
            Kind = ClipboardItemKind.Files,
            FilePaths = [path],
            Preview = "large.bin",
        };

        Assert.False(ClipItemSizeLimit.Allows(item, maxBytes: 10));
        Assert.True(ClipItemSizeLimit.Allows(item, maxBytes: 12));
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
