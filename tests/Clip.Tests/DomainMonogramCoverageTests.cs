using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Media.Imaging;
using Clip.Shell;

namespace Clip.Tests;

/// <summary>
/// The monogram exists so links get a distinct mark without any network call. These tests pin the
/// host parsing (what earns a tile at all), the shared cache, and the rendered tile itself.
/// Rendering runs on a private STA thread and never shows a window.
/// </summary>
public sealed class DomainMonogramCoverageTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("https://")]
    [InlineData("ftp://files.example.com/archive.zip")]
    [InlineData("file:///C:/temp/notes.txt")]
    public void NonWebTextGetsNoMonogram(string? url) =>
        Assert.Null(DomainMonogram.For(url, 16));

    [Fact]
    public void ABareDomainIsTreatedAsAWebAddress()
    {
        var image = Sta(() => DomainMonogram.For("bare-domain-coverage.test", 16));
        Assert.NotNull(image);
    }

    [Fact]
    public void WwwAliasAndLetterCaseShareOneCachedTile()
    {
        var canonical = Sta(() => DomainMonogram.For("https://www.monogram-cache.test/page", 20));
        var alias = Sta(() => DomainMonogram.For("HTTPS://Monogram-Cache.TEST/other?q=1", 20));

        Assert.NotNull(canonical);
        Assert.Same(canonical, alias);
    }

    [Fact]
    public void TileIsRenderedAtTheRequestedSizeAndFrozen()
    {
        var bitmap = Assert.IsAssignableFrom<BitmapSource>(
            Sta(() => DomainMonogram.For("https://tile-size.test", 32)));

        Assert.Equal(32, bitmap.PixelWidth);
        Assert.Equal(32, bitmap.PixelHeight);
        Assert.True(bitmap.IsFrozen, "the tile crosses threads, so it must be frozen");

        // Rounded corners: the very corner sits outside the tile, the middle of the left edge is
        // solid fill.
        Assert.Equal(0, PixelAt(bitmap, 0, 0).A);
        Assert.Equal(255, PixelAt(bitmap, 4, 16).A);
    }

    /// <summary>
    /// The colour comes from a stable hash of the host, so the same site must get the same colour
    /// whatever size the tile is rendered at. String.GetHashCode would break this per process.
    /// </summary>
    [Fact]
    public void AHostKeepsItsColourAtEverySize()
    {
        var small = (BitmapSource)Sta(() => DomainMonogram.For("https://stable-colour.test", 16))!;
        var large = (BitmapSource)Sta(() => DomainMonogram.For("https://stable-colour.test", 48))!;

        Assert.NotSame(small, large);

        // Sampled at the middle of the left edge, which is background fill at any size — the
        // letter sits in the centre and the rounded corners only cut the tile's corners.
        var a = PixelAt(small, 2, 8);
        var b = PixelAt(large, 2, 24);
        Assert.Equal(255, a.A);
        Assert.Equal((a.R, a.G, a.B), (b.R, b.G, b.B));
    }

    private static (byte B, byte G, byte R, byte A) PixelAt(BitmapSource bitmap, int x, int y)
    {
        var pixel = new byte[4];
        bitmap.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
        return (pixel[0], pixel[1], pixel[2], pixel[3]);
    }

    /// <summary>WPF text rendering wants an STA thread; the test runner's threads are MTA.</summary>
    private static T Sta<T>(Func<T> work)
    {
        T result = default!;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = work();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        thread.Join();

        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }

        return result;
    }
}
