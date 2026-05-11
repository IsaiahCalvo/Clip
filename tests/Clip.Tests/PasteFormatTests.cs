using Clip.Core;
using Clip.Shell;

namespace Clip.Tests;

public sealed class PasteFormatTests
{
    [Fact]
    public void RichTextItemReportsOriginalFormattingWhenHtmlOrRtfExists()
    {
        var item = new ClipboardHistoryItem
        {
            Kind = ClipboardItemKind.Text,
            Text = "Hello",
            HtmlText = "<b>Hello</b>",
            RtfText = "{\\rtf1 Hello}",
        };

        Assert.True(ClipboardPasteData.HasOriginalFormatting(item));
    }

    [Fact]
    public void PlainTextItemDoesNotReportOriginalFormatting()
    {
        var item = new ClipboardHistoryItem
        {
            Kind = ClipboardItemKind.Text,
            Text = "Hello",
        };

        Assert.False(ClipboardPasteData.HasOriginalFormatting(item));
    }

    [Fact]
    public void PlainTextPreferenceBuildsTextOnlyPayload()
    {
        var item = new ClipboardHistoryItem
        {
            Kind = ClipboardItemKind.Text,
            Text = "Hello",
            HtmlText = "<b>Hello</b>",
            RtfText = "{\\rtf1 Hello}",
        };

        var payload = ClipboardPasteData.Create(item, PasteFormatPreference.PlainText);

        Assert.Equal("Hello", payload.Text);
        Assert.Null(payload.Html);
        Assert.Null(payload.Rtf);
    }

    [Fact]
    public void OriginalFormattingPreferenceIncludesRichPayload()
    {
        var item = new ClipboardHistoryItem
        {
            Kind = ClipboardItemKind.Text,
            Text = "Hello",
            HtmlText = "<b>Hello</b>",
            RtfText = "{\\rtf1 Hello}",
        };

        var payload = ClipboardPasteData.Create(item, PasteFormatPreference.OriginalFormatting);

        Assert.Equal("Hello", payload.Text);
        Assert.Equal("<b>Hello</b>", payload.Html);
        Assert.Equal("{\\rtf1 Hello}", payload.Rtf);
    }
}
