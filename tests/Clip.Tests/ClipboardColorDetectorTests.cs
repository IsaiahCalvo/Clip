using Clip.Core;

namespace Clip.Tests;

public sealed class ClipboardColorDetectorTests
{
    [Theory]
    [InlineData("#3B5BDB", "#3B5BDB")]
    [InlineData("#3b5bdb", "#3B5BDB")]
    [InlineData("  #3b5bdb  ", "#3B5BDB")]
    [InlineData("#3b5bdb\r\n", "#3B5BDB")]
    [InlineData("#abc", "#AABBCC")]
    public void HashPrefixedHexIsAColorFromAnySource(string text, string expected)
    {
        Assert.True(ClipboardColorDetector.TryNormalize(text, "chrome", out var hex));
        Assert.Equal(expected, hex);
    }

    [Theory]
    [InlineData("3570fc", "PowerToys.ColorPickerUI", "#3570FC")]
    [InlineData("3570FC", "ColorPicker", "#3570FC")]
    public void BareHexIsAColorOnlyFromColorPickers(string text, string source, string expected)
    {
        Assert.True(ClipboardColorDetector.TryNormalize(text, source, out var hex));
        Assert.Equal(expected, hex);
    }

    [Theory]
    [InlineData("c8cacd", "Claude")]
    [InlineData("deadbeef", "chrome")]
    [InlineData("#12345", "chrome")]
    [InlineData("#3b5bdb extra", "chrome")]
    [InlineData("", "chrome")]
    [InlineData(null, "chrome")]
    public void NonColorsAreRejected(string? text, string source)
    {
        Assert.False(ClipboardColorDetector.TryNormalize(text, source, out var hex));
        Assert.Equal(string.Empty, hex);
    }
}
