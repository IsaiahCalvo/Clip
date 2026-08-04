using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Clip.Shell;

namespace Clip.Tests;

public sealed class FaviconCacheCoverageTests
{
    private static MethodInfo Private(string name) =>
        typeof(FaviconCache).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException($"FaviconCache.{name} not found");

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("github.com/some/path", "github.com")]
    [InlineData("https://github.com/a?b=c", "github.com")]
    [InlineData("http://sub.example.co.uk/x", "sub.example.co.uk")]
    [InlineData("ftp://example.com/file", null)]
    [InlineData("localhost", null)]
    [InlineData("http://localhost/x", null)]
    [InlineData("https://127.0.0.1/", null)]
    [InlineData("https://10.0.0.5/", null)]
    [InlineData("https://172.16.0.1/", null)]
    [InlineData("https://172.31.9.9/", null)]
    [InlineData("https://172.32.0.1/", "172.32.0.1")]
    [InlineData("https://192.168.1.1/", null)]
    [InlineData("https://169.254.1.1/", null)]
    [InlineData("https://8.8.8.8/", "8.8.8.8")]
    [InlineData("not a url at all", null)]
    public void HostOfExtractsOnlyPublicWebHosts(string? url, string? expected)
    {
        Assert.Equal(expected, FaviconCache.HostOf(url));
    }

    [Fact]
    public void TryGetCachedMissesForUnknownHost()
    {
        var host = $"missing-{Guid.NewGuid():N}.example";

        Assert.False(FaviconCache.TryGetCached(host, out var icon));
        Assert.Null(icon);
    }

    [Fact]
    public void IconHrefsPrefersLargestDeclaredIcon()
    {
        var html =
            """<link rel="icon" href="/small.png" sizes="16x16"><link rel="apple-touch-icon" href="/apple.png"><link rel="stylesheet" href="/style.css"><link rel="icon" href="/big.png" sizes="192x192"><link rel="icon">""";

        var hrefs = ((IEnumerable<string>)Private("IconHrefs").Invoke(null, new object[] { html })!).ToList();

        Assert.Equal(new[] { "/big.png", "/apple.png", "/small.png" }, hrefs);
    }

    [Fact]
    public void IconHrefsIgnoresNonIconLinks()
    {
        var html = """<link rel="stylesheet" href="/style.css"><p>no icons here</p>""";

        var hrefs = (IEnumerable<string>)Private("IconHrefs").Invoke(null, new object[] { html })!;

        Assert.Empty(hrefs);
    }

    [Theory]
    [InlineData("<link rel='icon' href='/x'>", "rel", "icon")]
    [InlineData("<link rel=\"apple-touch-icon\" href=\"/a.png\">", "href", "/a.png")]
    [InlineData("<link href='/x'>", "rel", null)]
    public void AttributeReadsQuotedValues(string tag, string name, string? expected)
    {
        Assert.Equal(expected, (string?)Private("Attribute").Invoke(null, new object[] { tag, name }));
    }

    [Fact]
    public void DecodeReadsPngBytes()
    {
        var icon = (ImageSource?)Private("Decode").Invoke(null, new object[] { PngBytes(8, 8) });

        Assert.NotNull(icon);
        Assert.True(icon!.IsFrozen);
    }

    [Fact]
    public void DecodeRasterizesSvg()
    {
        var svg = Encoding.UTF8.GetBytes(
            """<svg xmlns="http://www.w3.org/2000/svg" width="10" height="10"><rect width="10" height="10" fill="#ff0000"/></svg>""");

        var icon = (ImageSource?)Private("Decode").Invoke(null, new object[] { svg });

        Assert.NotNull(icon);
    }

    [Theory]
    [InlineData(new byte[0])]
    [InlineData(new byte[] { 1, 2, 3, 4 })]
    public void DecodeReturnsNullForGarbage(byte[] bytes)
    {
        Assert.Null((ImageSource?)Private("Decode").Invoke(null, new object[] { bytes }));
    }

    [Fact]
    public void DecodeReturnsNullForXmlThatIsNotSvg()
    {
        var bytes = Encoding.UTF8.GetBytes("""<?xml version="1.0"?><notsvg/>""");

        Assert.Null((ImageSource?)Private("Decode").Invoke(null, new object[] { bytes }));
    }

    [Fact]
    public async Task ReadCappedReturnsSmallBodiesVerbatim()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html>hello</html>"),
        };

        var text = await (Task<string>)Private("ReadCappedAsync").Invoke(null, new object[] { response })!;

        Assert.Equal("<html>hello</html>", text);
    }

    [Fact]
    public async Task ReadCappedStopsAfterCap()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(new string('x', 300_000)),
        };

        var text = await (Task<string>)Private("ReadCappedAsync").Invoke(null, new object[] { response })!;

        Assert.True(text.Length < 300_000);
        Assert.True(text.Length >= 200_000);
    }

    internal static byte[] PngBytes(int width, int height)
    {
        var bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var memory = new MemoryStream();
        encoder.Save(memory);
        return memory.ToArray();
    }
}
