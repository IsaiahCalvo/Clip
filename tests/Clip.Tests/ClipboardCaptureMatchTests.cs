using Clip.Core;

namespace Clip.Tests;

/// <summary>
/// Regression tests for the "is the pending capture still on the clipboard?" settle check.
/// Capture normalizes color text to "#RRGGBB" before the check runs, so a raw string
/// comparison dropped every hex color that was not already in canonical form — the item
/// never reached the store and never showed up in history at all.
/// </summary>
public sealed class ClipboardCaptureMatchTests
{
    [Fact]
    public void PlainTextMatchesItself()
    {
        var item = new ClipboardHistoryItem { Kind = ClipboardItemKind.Text, Text = "hello world" };

        Assert.True(ClipboardCaptureMatch.MatchesClipboardText(item, "hello world"));
    }

    [Fact]
    public void PlainTextDoesNotMatchDifferentText()
    {
        var item = new ClipboardHistoryItem { Kind = ClipboardItemKind.Text, Text = "hello world" };

        Assert.False(ClipboardCaptureMatch.MatchesClipboardText(item, "something else"));
    }

    [Fact]
    public void UppercasedColorStillMatchesLowercaseClipboardText()
    {
        var item = new ClipboardHistoryItem
        {
            Kind = ClipboardItemKind.Color,
            Text = "#3B5BDB",
            SourceApplication = "chrome",
        };

        Assert.True(ClipboardCaptureMatch.MatchesClipboardText(item, "#3b5bdb"));
    }

    [Fact]
    public void ExpandedShorthandColorStillMatchesClipboardText()
    {
        var item = new ClipboardHistoryItem
        {
            Kind = ClipboardItemKind.Color,
            Text = "#33BBDD",
            SourceApplication = "chrome",
        };

        Assert.True(ClipboardCaptureMatch.MatchesClipboardText(item, "#3bd"));
    }

    [Fact]
    public void ColorPickerHexWithoutHashStillMatchesClipboardText()
    {
        var item = new ClipboardHistoryItem
        {
            Kind = ClipboardItemKind.Color,
            Text = "#3570FC",
            SourceApplication = "PowerToys.ColorPickerUI",
        };

        Assert.True(ClipboardCaptureMatch.MatchesClipboardText(item, "3570fc"));
    }

    [Fact]
    public void ColorWithTrailingNewlineStillMatchesClipboardText()
    {
        var item = new ClipboardHistoryItem
        {
            Kind = ClipboardItemKind.Color,
            Text = "#3B5BDB",
            SourceApplication = "chrome",
        };

        Assert.True(ClipboardCaptureMatch.MatchesClipboardText(item, "#3B5BDB\r\n"));
    }

    [Fact]
    public void DifferentColorDoesNotMatch()
    {
        var item = new ClipboardHistoryItem
        {
            Kind = ClipboardItemKind.Color,
            Text = "#3B5BDB",
            SourceApplication = "chrome",
        };

        Assert.False(ClipboardCaptureMatch.MatchesClipboardText(item, "#FF0000"));
    }

    [Fact]
    public void ReplacedClipboardDoesNotMatch()
    {
        var item = new ClipboardHistoryItem
        {
            Kind = ClipboardItemKind.Color,
            Text = "#3B5BDB",
            SourceApplication = "chrome",
        };

        Assert.False(ClipboardCaptureMatch.MatchesClipboardText(item, "a totally different copy"));
    }

    [Fact]
    public void MissingClipboardTextDoesNotMatch()
    {
        var item = new ClipboardHistoryItem { Kind = ClipboardItemKind.Text, Text = "hello" };

        Assert.False(ClipboardCaptureMatch.MatchesClipboardText(item, null));
        Assert.False(ClipboardCaptureMatch.MatchesClipboardText(null, "hello"));
    }

    [Fact]
    public void WhitespaceOnlyDifferenceDoesNotMatchEmptyText()
    {
        var item = new ClipboardHistoryItem { Kind = ClipboardItemKind.Text, Text = "   " };

        Assert.False(ClipboardCaptureMatch.MatchesClipboardText(item, "\t"));
    }
}
