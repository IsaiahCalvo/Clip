using System.Drawing;
using System.Text;
using Clip.Watcher;

namespace Clip.Tests;

public sealed class WatcherPdfRenderCoverageTests : IDisposable
{
    private static readonly byte[] PngSignature = [137, 80, 78, 71, 13, 10, 26, 10];

    private readonly string _root = Path.Combine(Path.GetTempPath(), "Clip.Tests", Guid.NewGuid().ToString("N"));

    public WatcherPdfRenderCoverageTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            TestTemp.Delete(_root);
        }
        catch
        {
        }
    }

    [Fact]
    public void TryRenderFirstPageToFileReturnsFalseForMissingPdf()
    {
        var output = Path.Combine(_root, "missing.png");

        Assert.False(WindowsPdfRenderer.TryRenderFirstPageToFile(Path.Combine(_root, "missing.pdf"), output, 96));
        Assert.False(File.Exists(output));
    }

    [Fact]
    public void TryRenderFirstPageToFileReturnsFalseForCorruptPdf()
    {
        var pdfPath = Path.Combine(_root, "corrupt.pdf");
        File.WriteAllText(pdfPath, "this is not a pdf at all");
        var output = Path.Combine(_root, "corrupt.png");

        Assert.False(WindowsPdfRenderer.TryRenderFirstPageToFile(pdfPath, output, 96));
        Assert.False(File.Exists(output));
    }

    [Fact]
    public void TryRenderFirstPageToFileRendersValidPdfAtRequestedDpi()
    {
        var pdfPath = Path.Combine(_root, "page.pdf");
        File.WriteAllBytes(pdfPath, CreateSinglePagePdf(width: 200, height: 100));
        var output = Path.Combine(_root, "out", "page.png");

        Assert.True(WindowsPdfRenderer.TryRenderFirstPageToFile(pdfPath, output, 96));
        Assert.True(File.Exists(output));

        var header = new byte[8];
        using (var stream = File.OpenRead(output))
        {
            stream.ReadExactly(header);
        }

        Assert.Equal(PngSignature, header);

        // Windows.Data.Pdf reports page size in DIPs (1/96") and RenderToStreamAsync scales
        // DestinationWidth by the process's display-DPI context, so the exact pixel width is
        // machine-dependent. Assert the scale-independent contract: 2:1 aspect kept, sane size.
        using var image = Image.FromFile(output);
        Assert.InRange(image.Width, 200, 1200);
        Assert.Equal(2.0, (double)image.Width / image.Height, 1);
    }

    [Fact]
    public void RenderPreviewThumbRendersPdfToRequestedPng()
    {
        var pdfPath = Path.Combine(_root, "thumb.pdf");
        File.WriteAllBytes(pdfPath, CreateSinglePagePdf(width: 300, height: 150));
        var output = Path.Combine(_root, "thumbs", "thumb.png");

        var exitCode = Program.RenderPreviewThumb(["preview-thumb", pdfPath, output]);

        Assert.Equal(0, exitCode);
        Assert.True(File.Exists(output));

        var header = new byte[8];
        using (var stream = File.OpenRead(output))
        {
            stream.ReadExactly(header);
        }

        Assert.Equal(PngSignature, header);
    }

    // Minimal but structurally complete one-page PDF: catalog, page tree, page with a MediaBox
    // and a tiny content stream, plus a correct xref table (all ASCII, so string offsets are
    // byte offsets).
    private static byte[] CreateSinglePagePdf(int width, int height)
    {
        var content = "0.2 0.4 0.8 rg 10 10 100 50 re f\n";
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {width} {height}] /Resources << >> /Contents 4 0 R >>",
            $"<< /Length {content.Length} >>\nstream\n{content}endstream",
        };

        var builder = new StringBuilder();
        builder.Append("%PDF-1.4\n");
        var offsets = new int[objects.Length];
        for (var index = 0; index < objects.Length; index++)
        {
            offsets[index] = builder.Length;
            builder.Append($"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
        }

        var xrefOffset = builder.Length;
        builder.Append($"xref\n0 {objects.Length + 1}\n");
        builder.Append("0000000000 65535 f \n");
        foreach (var offset in offsets)
        {
            builder.Append($"{offset:D10} 00000 n \n");
        }

        builder.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xrefOffset}\n%%EOF");
        return Encoding.ASCII.GetBytes(builder.ToString());
    }
}
