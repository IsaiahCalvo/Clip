using System.Reflection;
using Clip.Core;

namespace Clip.Tests;

[CollectionDefinition("OcrStatics")]
public sealed class OcrStaticsCollection;

// Serialized with the other OCR test classes because the engine-null test swaps the
// cached OcrEngine fields by reflection.
[Collection("OcrStatics")]
public sealed class OcrSeamCoverageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Clip.Tests", Guid.NewGuid().ToString("N"));

    public OcrSeamCoverageTests()
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
    public async Task TryExtractAsyncReturnsNullWhenNoEngineIsAvailable()
    {
        var engineField = typeof(OcrTextExtractor).GetField("_engine", BindingFlags.NonPublic | BindingFlags.Static)!;
        var resolvedField = typeof(OcrTextExtractor).GetField("_engineResolved", BindingFlags.NonPublic | BindingFlags.Static)!;
        var path = Path.Combine(_root, "img.bmp");
        File.WriteAllBytes(path, CreateWhiteBmp(8, 8));
        try
        {
            engineField.SetValue(null, null);
            resolvedField.SetValue(null, true);

            Assert.False(OcrTextExtractor.IsAvailable);
            Assert.Null(await OcrTextExtractor.TryExtractAsync(path));
        }
        finally
        {
            engineField.SetValue(null, null);
            resolvedField.SetValue(null, false); // force a clean re-resolve for other tests
        }
    }

    [Fact]
    public async Task TryExtractAsyncDownscalesOversizedImages()
    {
        if (!OcrTextExtractor.IsAvailable)
        {
            return; // No OCR language pack on this machine; the downscale path is unreachable.
        }

        var width = (int)Windows.Media.Ocr.OcrEngine.MaxImageDimension + 1;
        var path = Path.Combine(_root, "wide.bmp");
        File.WriteAllBytes(path, CreateWhiteBmp(width, 60));

        Assert.Null(await OcrTextExtractor.TryExtractAsync(path));
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
        BitConverter.GetBytes(54).CopyTo(bytes, 10);
        BitConverter.GetBytes(40).CopyTo(bytes, 14);
        BitConverter.GetBytes(width).CopyTo(bytes, 18);
        BitConverter.GetBytes(height).CopyTo(bytes, 22);
        BitConverter.GetBytes((short)1).CopyTo(bytes, 26);
        BitConverter.GetBytes((short)24).CopyTo(bytes, 28);
        for (var i = 54; i < fileSize; i++)
        {
            bytes[i] = 0xFF;
        }

        return bytes;
    }
}
