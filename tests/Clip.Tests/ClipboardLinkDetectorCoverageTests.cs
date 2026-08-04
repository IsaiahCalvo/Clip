using Clip.Core;

namespace Clip.Tests;

public sealed class ClipboardLinkDetectorCoverageTests
{
    [Theory]
    [InlineData("user@example.com", true)]
    [InlineData("mailto:user@example.com", true)]
    [InlineData("user@example.com.", true)] // trailing punctuation is trimmed before matching
    [InlineData("https://example.com", false)]
    [InlineData("not an email", false)]
    [InlineData("user@", false)]
    [InlineData(null, false)]
    [InlineData("   ", false)]
    public void IsEmailSeparatesAddressesFromWebLinks(string? text, bool expected)
    {
        Assert.Equal(expected, ClipboardLinkDetector.IsEmail(text));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryNormalizeRejectsBlankText(string? text)
    {
        Assert.False(ClipboardLinkDetector.TryNormalize(text, out var normalized));
        Assert.Equal(string.Empty, normalized);
        Assert.False(ClipboardLinkDetector.IsLinkOrEmail(text));
    }
}
