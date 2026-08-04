using Clip.Core;

namespace Clip.Tests;

public sealed class OcrTextExtractorCoverageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Clip.Tests", Guid.NewGuid().ToString("N"));

    public OcrTextExtractorCoverageTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void IsAvailableIsStableAcrossCalls()
    {
        // The engine is resolved once and cached; asking twice must agree and never throw.
        var first = OcrTextExtractor.IsAvailable;
        var second = OcrTextExtractor.IsAvailable;

        Assert.Equal(first, second);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task TryExtractAsyncReturnsNullForBlankPath(string? path)
    {
        Assert.Null(await OcrTextExtractor.TryExtractAsync(path!));
    }

    [Fact]
    public async Task TryExtractAsyncReturnsNullForMissingFile()
    {
        Assert.Null(await OcrTextExtractor.TryExtractAsync(Path.Combine(_root, "missing.png")));
    }

    [Fact]
    public async Task TryExtractAsyncReturnsNullForUndecodableImageBytes()
    {
        var path = Path.Combine(_root, "not-an-image.png");
        File.WriteAllText(path, "this is not image data");

        Assert.Null(await OcrTextExtractor.TryExtractAsync(path));
    }

    [Fact]
    public async Task TryExtractAsyncReturnsNullForTinyBlankImage()
    {
        // 2x2 forces the upscale-to-minimum-dimension path; a blank image recognizes no text.
        var path = Path.Combine(_root, "blank.bmp");
        File.WriteAllBytes(path, CreateWhiteBmp(2, 2));

        Assert.Null(await OcrTextExtractor.TryExtractAsync(path));
    }

    [Fact]
    public async Task TryExtractAsyncPropagatesCancellation()
    {
        if (!OcrTextExtractor.IsAvailable)
        {
            return; // No OCR language pack on this machine; the extractor bails out before the cancellable work.
        }

        var path = Path.Combine(_root, "cancelled.bmp");
        File.WriteAllBytes(path, CreateWhiteBmp(64, 64));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => OcrTextExtractor.TryExtractAsync(path, cts.Token));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static byte[] CreateWhiteBmp(int width, int height)
    {
        var rowBytes = width * 3;
        var padding = (4 - rowBytes % 4) % 4;
        var dataSize = (rowBytes + padding) * height;
        var fileSize = 54 + dataSize;
        var bytes = new byte[fileSize];
        bytes[0] = (byte)'B';
        bytes[1] = (byte)'M';
        BitConverter.GetBytes(fileSize).CopyTo(bytes, 2);
        BitConverter.GetBytes(54).CopyTo(bytes, 10); // pixel data offset
        BitConverter.GetBytes(40).CopyTo(bytes, 14); // BITMAPINFOHEADER size
        BitConverter.GetBytes(width).CopyTo(bytes, 18);
        BitConverter.GetBytes(height).CopyTo(bytes, 22);
        BitConverter.GetBytes((short)1).CopyTo(bytes, 26); // planes
        BitConverter.GetBytes((short)24).CopyTo(bytes, 28); // bits per pixel, BI_RGB
        for (var i = 54; i < fileSize; i++)
        {
            bytes[i] = 0xFF; // white pixels; row padding bytes are harmless
        }

        return bytes;
    }
}
