using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Clip.Shell;

/// <summary>
/// Reads an image off the clipboard reliably.
///
/// <see cref="System.Windows.Clipboard.GetImage"/> alone is not good enough. It hands back the
/// CF_DIB bitmap, and a great many apps write a 32-bit DIB whose alpha bytes are all zero because
/// they never used them. WPF then treats that as fully transparent, so the captured image is
/// completely blank — which is exactly what was happening to screenshots from some apps.
///
/// Two defences: prefer the PNG the source app usually also puts on the clipboard, and if we do
/// fall back to the DIB, detect the all-zero alpha channel and treat the image as opaque.
/// </summary>
internal static class ClipboardImageReader
{
    // CF_DIBV5. Only this format actually defines an alpha channel.
    private const string DibV5Format = "Format17";

    public static BitmapSource? Read()
    {
        if (TryReadPng() is { } png)
        {
            return png;
        }

        var dib = TryReadDib();
        if (dib is null)
        {
            return null;
        }

        // A plain CF_DIB has no alpha channel: in a 32-bit BI_RGB bitmap the fourth byte is
        // reserved and undefined, and plenty of apps leave it at zero. Honouring it is what made
        // captures come out invisible. Only trust alpha when the app actually published a DIBV5.
        return HasDibV5() ? RepairFullyTransparent(dib) : ForceOpaque(dib);
    }

    private static bool HasDibV5()
    {
        try
        {
            return System.Windows.Clipboard.ContainsData(DibV5Format);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Browsers, screenshot tools and design apps put a real PNG on the clipboard alongside the
    /// DIB. It has correct colour, correct alpha and the true pixel dimensions, so it wins.
    /// </summary>
    private static BitmapSource? TryReadPng()
    {
        foreach (var format in new[] { "PNG", "image/png" })
        {
            try
            {
                if (!System.Windows.Clipboard.ContainsData(format))
                {
                    continue;
                }

                if (System.Windows.Clipboard.GetData(format) is not MemoryStream stream || stream.Length == 0)
                {
                    continue;
                }

                stream.Position = 0;
                var decoder = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                if (decoder.Frames.Count == 0)
                {
                    continue;
                }

                // Must be detached from the decoder: a decoded frame stays bound to the thread
                // that created it, and capture continues on a background thread.
                return Detach(decoder.Frames[0]);
            }
            catch
            {
                // Fall through to the next format, then to the DIB.
            }
        }

        return null;
    }

    private static BitmapSource? TryReadDib()
    {
        try
        {
            return System.Windows.Clipboard.GetImage();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Copies pixels into a standalone bitmap with no decoder behind it, so the result can be
    /// handed to a background thread and frozen safely.
    /// </summary>
    private static BitmapSource? Detach(BitmapSource source)
    {
        try
        {
            var converted = source.Format == PixelFormats.Bgra32
                ? source
                : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);

            var width = converted.PixelWidth;
            var height = converted.PixelHeight;
            if (width <= 0 || height <= 0)
            {
                return null;
            }

            var stride = width * 4;
            var pixels = new byte[stride * (long)height];
            converted.CopyPixels(pixels, stride, 0);

            var copy = BitmapSource.Create(
                width,
                height,
                converted.DpiX > 0 ? converted.DpiX : 96,
                converted.DpiY > 0 ? converted.DpiY : 96,
                PixelFormats.Bgra32,
                null,
                pixels,
                stride);
            copy.Freeze();
            return copy;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Discards the alpha channel entirely, keeping the colour. Used when the source was a plain
    /// CF_DIB, where alpha carries no meaning.
    /// </summary>
    private static BitmapSource? ForceOpaque(BitmapSource source) => Rebuild(source, alwaysOpaque: true);

    /// <summary>
    /// Only rescues the degenerate case where a DIBV5 claims every single pixel is invisible.
    /// Genuine transparency is left alone.
    /// </summary>
    private static BitmapSource? RepairFullyTransparent(BitmapSource source) => Rebuild(source, alwaysOpaque: false);

    private static BitmapSource? Rebuild(BitmapSource source, bool alwaysOpaque)
    {
        try
        {
            var converted = source.Format == PixelFormats.Bgra32
                ? source
                : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);

            var width = converted.PixelWidth;
            var height = converted.PixelHeight;
            if (width <= 0 || height <= 0)
            {
                return Detach(source);
            }

            var stride = width * 4;
            var pixels = new byte[stride * (long)height];
            converted.CopyPixels(pixels, stride, 0);

            var makeOpaque = alwaysOpaque;
            if (!makeOpaque)
            {
                makeOpaque = true;
                for (var i = 3; i < pixels.Length; i += 4)
                {
                    if (pixels[i] != 0)
                    {
                        makeOpaque = false;
                        break;
                    }
                }
            }

            if (makeOpaque)
            {
                for (var i = 3; i < pixels.Length; i += 4)
                {
                    pixels[i] = 255;
                }
            }

            var rebuilt = BitmapSource.Create(
                width,
                height,
                converted.DpiX > 0 ? converted.DpiX : 96,
                converted.DpiY > 0 ? converted.DpiY : 96,
                PixelFormats.Bgra32,
                null,
                pixels,
                stride);
            rebuilt.Freeze();
            return rebuilt;
        }
        catch
        {
            return Detach(source);
        }
    }
}
