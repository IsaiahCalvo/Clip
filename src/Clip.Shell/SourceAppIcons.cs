using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Clip.Shell;

/// <summary>
/// Resolves the icon for the app an item was copied from.
///
/// Everything goes through <c>IShellItemImageFactory</c>, which is the only shell API that
/// takes an arbitrary pixel size and returns a 32bpp image with alpha. <c>ExtractAssociatedIcon</c>
/// and <c>SHGetFileInfo</c> are capped at 32x32, so at 150% scaling they can only ever produce a
/// blurry upscale. The same call also resolves packaged (MSIX) apps through
/// <c>shell:AppsFolder\{aumid}</c>, which is how apps like Claude and Raycast get a real icon
/// instead of the ApplicationFrameHost placeholder.
/// </summary>
internal static class SourceAppIcons
{
    private const int MaxCacheEntries = 300;

    // Native icon rungs. Requesting an in-between size makes the shell scale for us; asking for
    // the next rung up and letting WPF scale down keeps edges crisp.
    private static readonly int[] NativeSizes = [16, 20, 24, 32, 40, 48, 64, 96, 256];

    private static readonly object Gate = new();
    private static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly LinkedList<string> Recent = new();

    /// <summary>
    /// Returns a frozen icon for the given identity, or null when nothing could be resolved.
    /// <paramref name="aumid"/> wins when present because packaged apps have no usable exe path.
    /// Must be called from an STA thread — shell extensions require it.
    /// </summary>
    public static ImageSource? Resolve(string? aumid, string? exePath, int logicalSize, double dpiScale)
    {
        var parsingName = ParsingNameFor(aumid, exePath);
        if (parsingName is null)
        {
            return null;
        }

        var pixelSize = NativeSizeFor(logicalSize, dpiScale);
        var key = $"{parsingName}|{pixelSize}";

        lock (Gate)
        {
            if (Cache.TryGetValue(key, out var cached))
            {
                Touch(key);
                return cached;
            }
        }

        var resolved = TryLoad(parsingName, pixelSize);

        lock (Gate)
        {
            Cache[key] = resolved;
            Touch(key);
            Evict();
        }

        return resolved;
    }

    public static void Clear()
    {
        lock (Gate)
        {
            Cache.Clear();
            Recent.Clear();
        }
    }

    private static string? ParsingNameFor(string? aumid, string? exePath)
    {
        if (!string.IsNullOrWhiteSpace(aumid))
        {
            return $@"shell:AppsFolder\{aumid}";
        }

        if (!string.IsNullOrWhiteSpace(exePath) && File.Exists(exePath))
        {
            return exePath;
        }

        return null;
    }

    private static int NativeSizeFor(int logicalSize, double dpiScale)
    {
        var wanted = (int)Math.Ceiling(logicalSize * (dpiScale <= 0 ? 1.0 : dpiScale));
        foreach (var size in NativeSizes)
        {
            if (size >= wanted)
            {
                return size;
            }
        }

        return NativeSizes[^1];
    }

    private static void Touch(string key)
    {
        Recent.Remove(key);
        Recent.AddFirst(key);
    }

    private static void Evict()
    {
        while (Recent.Count > MaxCacheEntries)
        {
            var oldest = Recent.Last;
            if (oldest is null)
            {
                return;
            }

            Recent.RemoveLast();
            Cache.Remove(oldest.Value);
        }
    }

    private static ImageSource? TryLoad(string parsingName, int pixelSize)
    {
        object? factoryObject = null;
        var bitmap = IntPtr.Zero;
        try
        {
            var iid = typeof(IShellItemImageFactory).GUID;
            var hr = SHCreateItemFromParsingName(parsingName, IntPtr.Zero, ref iid, out factoryObject);
            if (hr != 0 || factoryObject is not IShellItemImageFactory factory)
            {
                return null;
            }

            var size = new SIZE { cx = pixelSize, cy = pixelSize };
            // BIGGERSIZEOK lets the shell hand back a larger native asset that we scale down.
            // SCALEUP is deliberately not set — it would blur small icons back up.
            hr = factory.GetImage(size, SIIGBF.ICONONLY | SIIGBF.BIGGERSIZEOK, out bitmap);
            if (hr != 0 || bitmap == IntPtr.Zero)
            {
                return null;
            }

            return FromHBitmap(bitmap);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (bitmap != IntPtr.Zero)
            {
                DeleteObject(bitmap);
            }

            if (factoryObject is not null && Marshal.IsComObject(factoryObject))
            {
                Marshal.ReleaseComObject(factoryObject);
            }
        }
    }

    /// <summary>
    /// Copies a 32bpp HBITMAP into a frozen WPF bitmap, keeping the alpha channel.
    /// <c>CreateBitmapSourceFromHBitmap</c> is deliberately avoided: it yields Bgr32 and drops
    /// alpha, which renders an icon's transparent corners as opaque black.
    /// </summary>
    private static ImageSource? FromHBitmap(IntPtr bitmap)
    {
        if (GetObject(bitmap, Marshal.SizeOf<BITMAP>(), out var info) == 0 || info.bmBitsPixel != 32)
        {
            return null;
        }

        var width = info.bmWidth;
        var height = info.bmHeight;
        if (width <= 0 || height <= 0)
        {
            return null;
        }

        var stride = width * 4;
        var pixels = new byte[stride * height];

        var header = new BITMAPINFOHEADER
        {
            biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = width,
            // Negative height requests a top-down DIB, matching WPF's row order.
            biHeight = -height,
            biPlanes = 1,
            biBitCount = 32,
            biCompression = 0,
        };

        var screen = GetDC(IntPtr.Zero);
        try
        {
            if (GetDIBits(screen, bitmap, 0, (uint)height, pixels, ref header, 0) == 0)
            {
                return null;
            }
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, screen);
        }

        // The shell returns premultiplied alpha, so Pbgra32 is the matching format. A fully
        // zero alpha channel means the source had no alpha at all; treat it as opaque rather
        // than rendering an invisible icon.
        var format = HasAlpha(pixels) ? PixelFormats.Pbgra32 : PixelFormats.Bgr32;
        var source = BitmapSource.Create(width, height, 96, 96, format, null, pixels, stride);
        source.Freeze();
        return source;
    }

    private static bool HasAlpha(byte[] pixels)
    {
        for (var i = 3; i < pixels.Length; i += 4)
        {
            if (pixels[i] != 0)
            {
                return true;
            }
        }

        return false;
    }

    [Flags]
    private enum SIIGBF
    {
        RESIZETOFIT = 0x00,
        BIGGERSIZEOK = 0x01,
        MEMORYONLY = 0x02,
        ICONONLY = 0x04,
        THUMBNAILONLY = 0x08,
        INCACHEONLY = 0x10,
        SCALEUP = 0x100,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE
    {
        public int cx;
        public int cy;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int bmType;
        public int bmWidth;
        public int bmHeight;
        public int bmWidthBytes;
        public ushort bmPlanes;
        public ushort bmBitsPixel;
        public IntPtr bmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public int biCompression;
        public int biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public int biClrUsed;
        public int biClrImportant;
    }

    [ComImport]
    [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(SIZE size, SIIGBF flags, out IntPtr bitmap);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(
        string path,
        IntPtr bindContext,
        ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out object item);

    [DllImport("gdi32.dll")]
    private static extern int GetObject(IntPtr handle, int count, out BITMAP info);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(
        IntPtr dc,
        IntPtr bitmap,
        uint startScan,
        uint scanLines,
        byte[] bits,
        ref BITMAPINFOHEADER info,
        uint usage);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
}
