using Clip.Core;

namespace Clip.Tests;

public sealed class TextFilePreviewReaderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "clip-text-preview-tests", Guid.NewGuid().ToString("N"));

    public TextFilePreviewReaderTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void ReadReturnsWholeFileWhenUnderLimit()
    {
        var path = WriteText("short.txt", "hello");

        Assert.Equal("hello", TextFilePreviewReader.Read(path, 10));
    }

    [Fact]
    public void ReadTruncatesWhenOverLimit()
    {
        var path = WriteText("long.txt", "abcdef");

        var preview = TextFilePreviewReader.Read(path, 3);

        Assert.StartsWith("abc", preview);
        Assert.Contains(TextFilePreviewReader.TruncatedMarker, preview);
        Assert.DoesNotContain("def", preview);
    }

    [Fact]
    public async Task ReadAsyncTruncatesWhenOverLimit()
    {
        var path = WriteText("async-long.txt", "abcdef");

        var preview = await TextFilePreviewReader.ReadAsync(path, 4);

        Assert.StartsWith("abcd", preview);
        Assert.Contains(TextFilePreviewReader.TruncatedMarker, preview);
        Assert.DoesNotContain("ef", preview);
    }

    [Fact]
    public void FormatReturnsWholeTextWhenUnderLimit()
    {
        Assert.Equal("hello", TextFilePreviewReader.Format("hello", 10));
    }

    [Fact]
    public void FormatTruncatesTextWhenOverLimit()
    {
        var preview = TextFilePreviewReader.Format("abcdef", 3);

        Assert.StartsWith("abc", preview);
        Assert.Contains(TextFilePreviewReader.TruncatedMarker, preview);
        Assert.DoesNotContain("def", preview);
    }

    [Fact]
    public void ReadRejectsInvalidLimit()
    {
        var path = WriteText("short.txt", "hello");

        Assert.Throws<ArgumentOutOfRangeException>(() => TextFilePreviewReader.Read(path, 0));
    }

    [Fact]
    public void FormatRejectsInvalidLimit()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TextFilePreviewReader.Format("hello", 0));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string WriteText(string fileName, string text)
    {
        var path = Path.Combine(_root, fileName);
        File.WriteAllText(path, text);
        return path;
    }
}
