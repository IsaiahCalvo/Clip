using Clip.Watcher;

namespace Clip.Tests;

/// <summary>
/// The PDF preview tool lookup used to fall back to
/// Directory.EnumerateFiles(localAppData, name, SearchOption.AllDirectories), which walks
/// into the legacy "Application Data" junction: it loops back on itself and throws
/// UnauthorizedAccessException, aborting the whole search on every PDF preview.
/// </summary>
public sealed class PdfToolLookupTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Clip.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void FindsFileWithinDepth()
    {
        var directory = Path.Combine(_root, "one", "two");
        Directory.CreateDirectory(directory);
        var expected = Path.Combine(directory, "pdftoppm.exe");
        File.WriteAllText(expected, "stub");

        Assert.Equal(expected, PdfPreviewRenderer.FindFileWithinDepth(_root, "pdftoppm.exe", 4));
    }

    [Fact]
    public void FindsFileInRoot()
    {
        Directory.CreateDirectory(_root);
        var expected = Path.Combine(_root, "pdftoppm.exe");
        File.WriteAllText(expected, "stub");

        Assert.Equal(expected, PdfPreviewRenderer.FindFileWithinDepth(_root, "pdftoppm.exe", 0));
    }

    [Fact]
    public void StopsAtTheDepthLimitInsteadOfWalkingTheWholeTree()
    {
        var directory = Path.Combine(_root, "a", "b", "c", "d", "e", "f");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "pdftoppm.exe"), "stub");

        Assert.Null(PdfPreviewRenderer.FindFileWithinDepth(_root, "pdftoppm.exe", 3));
    }

    [Fact]
    public void MissingRootReturnsNullInsteadOfThrowing()
    {
        Assert.Null(PdfPreviewRenderer.FindFileWithinDepth(Path.Combine(_root, "nope"), "pdftoppm.exe", 4));
        Assert.Null(PdfPreviewRenderer.FindFileWithinDepth(string.Empty, "pdftoppm.exe", 4));
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
        }
    }
}
