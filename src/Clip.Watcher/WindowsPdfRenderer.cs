using System;
using System.IO;
using System.Threading.Tasks;
using Windows.Data.Pdf;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Clip.Watcher;

/// <summary>
/// Renders the first page of a PDF using the PDF engine built into Windows.
///
/// This is the piece that was missing. Clip previously shelled out to pdftoppm.exe, which is not
/// present on a normal machine — and because Word, Excel and PowerPoint previews are produced by
/// exporting to PDF first, that single missing tool silently killed four formats at once. Visio
/// was unaffected only because it exports straight to an image.
///
/// Nothing is added to the download: this ships with Windows.
/// </summary>
internal static class WindowsPdfRenderer
{
    private const int MaxPixels = 4000;

    public static bool TryRenderFirstPageToFile(string pdfPath, string outputPngPath, int dpi)
    {
        try
        {
            if (!File.Exists(pdfPath))
            {
                return false;
            }

            // Run on the thread pool deliberately. Callers may be on an STA thread with no message
            // pump, and awaiting a WinRT operation there can deadlock.
            var bytes = Task.Run(() => RenderAsync(pdfPath, dpi)).GetAwaiter().GetResult();
            if (bytes is null || bytes.Length == 0)
            {
                return false;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(outputPngPath)!);
            File.WriteAllBytes(outputPngPath, bytes);
            return true;
        }
        catch (Exception ex)
        {
            Program.LogDebug($"Windows PDF render failed path={pdfPath} error={ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private static async Task<byte[]?> RenderAsync(string pdfPath, int dpi)
    {
        var file = await StorageFile.GetFileFromPathAsync(pdfPath);
        var document = await PdfDocument.LoadFromFileAsync(file);
        if (document.PageCount == 0)
        {
            return null;
        }

        using var page = document.GetPage(0);

        // Page size is in points (1/72 inch), so this turns the requested DPI into real pixels.
        var width = (uint)Math.Clamp(Math.Round(page.Size.Width * dpi / 72.0), 1, MaxPixels);

        using var stream = new InMemoryRandomAccessStream();
        await page.RenderToStreamAsync(stream, new PdfPageRenderOptions
        {
            DestinationWidth = width,
        });

        stream.Seek(0);
        var buffer = new byte[stream.Size];
        using var reader = new DataReader(stream.GetInputStreamAt(0));
        await reader.LoadAsync((uint)stream.Size);
        reader.ReadBytes(buffer);
        return buffer;
    }
}
