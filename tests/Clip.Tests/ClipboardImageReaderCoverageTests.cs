using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Clip.Shell;

namespace Clip.Tests;

/// <summary>
/// The reader's whole reason to exist is the alpha repair: plain CF_DIB alpha bytes are noise and
/// honouring them made captures come out invisible. The repair pipeline is pure pixel work, so it
/// is tested directly on constructed bitmaps; the clipboard-facing entry points cannot run here
/// (they would read the user's live clipboard) and are exercised only in the app.
///
/// The pixel methods are private by design — reflection reaches them rather than widening the
/// class's surface for the test's sake.
/// </summary>
public sealed class ClipboardImageReaderCoverageTests
{
    /// <summary>A DIB's alpha bytes carry no meaning, so every pixel must come out opaque.</summary>
    [Fact]
    public void ForceOpaqueDiscardsMeaninglessDibAlphaButKeepsColour()
    {
        var source = Bgra32(2, 1, [10, 20, 30, 0, 40, 50, 60, 128]);

        var result = Call("ForceOpaque", source);

        Assert.NotNull(result);
        Assert.True(result!.IsFrozen);
        Assert.Equal(new byte[] { 10, 20, 30, 255, 40, 50, 60, 255 }, PixelsOf(result));
    }

    /// <summary>The degenerate DIBV5 — every pixel claims to be invisible — is made opaque.</summary>
    [Fact]
    public void RepairRescuesAnAllInvisibleImage()
    {
        var source = Bgra32(2, 2,
        [
            1, 2, 3, 0, 4, 5, 6, 0,
            7, 8, 9, 0, 10, 11, 12, 0,
        ]);

        var result = Call("RepairFullyTransparent", source);

        Assert.NotNull(result);
        Assert.Equal(
            new byte[]
            {
                1, 2, 3, 255, 4, 5, 6, 255,
                7, 8, 9, 255, 10, 11, 12, 255,
            },
            PixelsOf(result!));
    }

    /// <summary>One genuinely visible pixel means the alpha is real and must be left alone.</summary>
    [Fact]
    public void GenuineTransparencyIsLeftAlone()
    {
        var pixels = new byte[] { 10, 20, 30, 0, 40, 50, 60, 200 };
        var source = Bgra32(2, 1, pixels);

        var result = Call("RepairFullyTransparent", source);

        Assert.NotNull(result);
        Assert.Equal(pixels, PixelsOf(result!));
    }

    /// <summary>A 24-bit source has no alpha at all; it converts and comes out opaque.</summary>
    [Fact]
    public void NonBgraSourcesAreConvertedBeforeTheAlphaWork()
    {
        var source = Bgr24(2, 1, [10, 20, 30, 40, 50, 60]);

        var result = Call("ForceOpaque", source);

        Assert.NotNull(result);
        Assert.Equal(PixelFormats.Bgra32, result!.Format);
        Assert.Equal(new byte[] { 10, 20, 30, 255, 40, 50, 60, 255 }, PixelsOf(result));
    }

    /// <summary>
    /// Detach exists so a decoded frame can leave the thread that decoded it: the copy must be a
    /// new, frozen bitmap with the same pixels.
    /// </summary>
    [Fact]
    public void DetachProducesAFrozenStandaloneCopy()
    {
        var pixels = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        var source = Bgra32(2, 1, pixels);

        var result = Call("Detach", source);

        Assert.NotNull(result);
        Assert.NotSame(source, result);
        Assert.True(result!.IsFrozen);
        Assert.Equal(PixelFormats.Bgra32, result.Format);
        Assert.Equal(pixels, PixelsOf(result));
    }

    [Fact]
    public void DetachNormalisesForeignPixelFormats()
    {
        var source = Bgr24(1, 1, [9, 8, 7]);

        var result = Call("Detach", source);

        Assert.NotNull(result);
        Assert.Equal(PixelFormats.Bgra32, result!.Format);
        Assert.Equal(new byte[] { 9, 8, 7, 255 }, PixelsOf(result));
    }

    private static BitmapSource? Call(string method, BitmapSource source)
    {
        var target = typeof(ClipboardImageReader).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Static);
        Assert.True(target is not null, $"ClipboardImageReader.{method} no longer exists");
        return Sta(() => (BitmapSource?)target!.Invoke(null, [source]));
    }

    private static BitmapSource Bgra32(int width, int height, byte[] pixels)
    {
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    private static BitmapSource Bgr24(int width, int height, byte[] pixels)
    {
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr24, null, pixels, width * 3);
        bitmap.Freeze();
        return bitmap;
    }

    private static byte[] PixelsOf(BitmapSource bitmap)
    {
        var data = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(data, bitmap.PixelWidth * 4, 0);
        return data;
    }

    /// <summary>Pixel-format conversion is WPF imaging work; run it on a private STA thread.</summary>
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
