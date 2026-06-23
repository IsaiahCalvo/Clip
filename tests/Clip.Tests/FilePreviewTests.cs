using Clip.Core;

namespace Clip.Tests;

public sealed class FilePreviewTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "clip-file-preview-tests", Guid.NewGuid().ToString("N"));

    public FilePreviewTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Theory]
    [InlineData("notes.txt", true)]
    [InlineData("data.json", true)]
    [InlineData("Program.cs", true)]
    [InlineData("readme.md", true)]
    [InlineData("photo.png", false)]
    [InlineData("report.pdf", false)]
    [InlineData("archive.zip", false)]
    [InlineData("noext", false)]
    public void IsTextFileMatchesKnownExtensions(string fileName, bool expected)
    {
        Assert.Equal(expected, FilePreview.IsTextFile(fileName));
    }

    [Fact]
    public void TryReadTextExcerptReturnsContentsForSingleTextFile()
    {
        var path = WriteText("snippet.txt", "line one\nline two");

        var ok = FilePreview.TryReadTextExcerpt(new[] { path }, 2_000, out var excerpt);

        Assert.True(ok);
        Assert.Contains("line one", excerpt);
        Assert.Contains("line two", excerpt);
    }

    [Fact]
    public void TryReadTextExcerptTruncatesLongFiles()
    {
        var path = WriteText("long.txt", new string('a', 50));

        var ok = FilePreview.TryReadTextExcerpt(new[] { path }, 10, out var excerpt);

        Assert.True(ok);
        Assert.Contains(TextFilePreviewReader.TruncatedMarker, excerpt);
    }

    [Fact]
    public void TryReadTextExcerptRejectsNonTextExtension()
    {
        var path = WriteText("image.png", "not really an image");

        Assert.False(FilePreview.TryReadTextExcerpt(new[] { path }, 2_000, out var excerpt));
        Assert.Equal(string.Empty, excerpt);
    }

    [Fact]
    public void TryReadTextExcerptRejectsMultipleFiles()
    {
        var a = WriteText("a.txt", "alpha");
        var b = WriteText("b.txt", "beta");

        Assert.False(FilePreview.TryReadTextExcerpt(new[] { a, b }, 2_000, out _));
    }

    [Fact]
    public void TryReadTextExcerptRejectsMissingFile()
    {
        var path = Path.Combine(_root, "does-not-exist.txt");

        Assert.False(FilePreview.TryReadTextExcerpt(new[] { path }, 2_000, out _));
    }

    [Fact]
    public void TryReadTextExcerptRejectsEmptyOrNullInput()
    {
        Assert.False(FilePreview.TryReadTextExcerpt(null, 2_000, out _));
        Assert.False(FilePreview.TryReadTextExcerpt(Array.Empty<string>(), 2_000, out _));
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
