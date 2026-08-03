using Clip.Shell;

namespace Clip.Tests;

/// <summary>
/// The player's buttons are drawn with Unicode glyphs rather than images. A tool that rewrites
/// MediaPreviewPage.cs with the wrong text encoding turns every one of them into mojibake, and the
/// damage is invisible in a diff unless you are looking for it - it shipped once already. These
/// tests pin the exact character behind each button.
///
/// Everything here is written as escapes so this file stays pure ASCII: a test for an encoding bug
/// is worthless if the test itself can catch the bug.
/// </summary>
public class PlayerGlyphTests
{
    private static string Page(bool detached) => Page(isVideo: true, detached);

    private static string Page(bool isVideo, bool detached) => MediaPreviewPage.Build(
        isVideo ? @"C:\clips\sample.mp4" : @"C:\clips\sample.m4a",
        "https://clip-preview.local/sample",
        isVideo,
        backgroundHex: "#1e1e1e",
        textHex: "#ffffff",
        detached: detached);

    /// <summary>
    /// Picture-in-picture is for watching something while working on something else, so it needs a
    /// picture. Offering it for an audio file is an option that cannot do anything.
    /// </summary>
    [Fact]
    public void AudioIsNotOfferedPictureInPicture()
    {
        Assert.DoesNotContain("Picture in picture", Page(isVideo: false, detached: false));
    }

    [Fact]
    public void VideoIsStillOfferedPictureInPicture()
    {
        Assert.Contains("Picture in picture", Page(isVideo: true, detached: false));
    }

    /// <summary>The mini window is already picture-in-picture; it offers a way back instead.</summary>
    [Fact]
    public void TheMiniWindowDoesNotOfferPictureInPictureAgain()
    {
        Assert.DoesNotContain("Picture in picture", Page(isVideo: true, detached: true));
    }

    [Theory]
    [InlineData("play", "\u25b6")]      // black right-pointing triangle
    [InlineData("more", "\u22ee")]      // vertical ellipsis
    [InlineData("fs", "\u26f6")]        // square four corners
    public void EmbeddedPlayerUsesTheIntendedGlyphs(string buttonId, string glyph)
    {
        Assert.Equal(glyph, LabelOf(Page(detached: false), buttonId));
    }

    [Theory]
    [InlineData("back", "\u2196")]      // north west arrow
    [InlineData("close", "\u2715")]     // multiplication x
    public void DetachedPlayerUsesTheIntendedCornerGlyphs(string buttonId, string glyph)
    {
        Assert.Equal(glyph, LabelOf(Page(detached: true), buttonId));
    }

    [Fact]
    public void PauseGlyphIsSwappedInWhenPlaybackStarts()
    {
        // Two heavy vertical bars, substituted into the play button while the media is running.
        Assert.Contains("'\u275a\u275a'", Page(detached: false));
    }

    [Fact]
    public void SpeedMenuUsesRealChevrons()
    {
        var html = Page(detached: false);
        Assert.Contains("\u203a", html); // > into the submenu
        Assert.Contains("\u2039", html); // < back out of it
    }

    /// <summary>
    /// Mojibake is the same bytes read through the wrong code page, so it always lands in the
    /// Latin-1 supplement. No glyph the player actually uses lives there, which makes the presence
    /// of one a reliable signal that the file was mangled on its way to disk.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PageCarriesNoMojibake(bool detached)
    {
        var offenders = Page(detached)
            .Where(c => c >= '\u0080' && c <= '\u00ff')
            .Distinct()
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            "Latin-1 characters in the player page mean the source file was re-encoded: "
                + string.Join(", ", offenders.Select(c => $"U+{(int)c:X4}")));
    }

    /// <summary>Returns the text a button shows, from its id through to its closing tag.</summary>
    private static string LabelOf(string html, string buttonId)
    {
        var start = html.IndexOf($"id=\"{buttonId}\"", StringComparison.Ordinal);
        Assert.True(start >= 0, $"no button with id \"{buttonId}\" in the player page");

        var open = html.IndexOf('>', start);
        var close = html.IndexOf("</button>", start, StringComparison.Ordinal);
        Assert.True(open >= 0 && close > open, $"button \"{buttonId}\" is never closed");

        return html[(open + 1)..close];
    }
}
