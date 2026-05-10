using Clip.Core;

namespace Clip.Tests;

public sealed class ClipboardPathTextTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Clip.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void ParsesQuotedExistingFilePath()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "invoice.pdf");
        File.WriteAllText(path, "test");

        var parsed = ClipboardPathText.TryParseExistingFilePaths($"\"{path}\"", out var paths);

        Assert.True(parsed);
        Assert.Equal([path], paths);
    }

    [Fact]
    public void ParsesMultipleExistingPathsOnSeparateLines()
    {
        Directory.CreateDirectory(_root);
        var first = Path.Combine(_root, "first.txt");
        var second = Path.Combine(_root, "Folder");
        File.WriteAllText(first, "test");
        Directory.CreateDirectory(second);

        var parsed = ClipboardPathText.TryParseExistingFilePaths($"{first}{Environment.NewLine}{second}", out var paths);

        Assert.True(parsed);
        Assert.Equal([first, second], paths);
    }

    [Fact]
    public void RejectsPlainTextAndMissingPaths()
    {
        var parsed = ClipboardPathText.TryParseExistingFilePaths("not a real file path", out var paths);

        Assert.False(parsed);
        Assert.Empty(paths);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
