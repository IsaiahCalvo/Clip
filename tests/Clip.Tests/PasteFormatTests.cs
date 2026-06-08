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

    [Fact]
    public void PasteVerificationRequiresExpectedText()
    {
        Assert.False(MainWindow.PasteLooksApplied("Old", "Something else", "Hello"));
    }

    [Fact]
    public void PasteVerificationAcceptsExactReplacement()
    {
        Assert.True(MainWindow.PasteLooksApplied("Old", "Hello", "Hello"));
    }

    [Fact]
    public void PasteVerificationAcceptsInsertedText()
    {
        Assert.True(MainWindow.PasteLooksApplied("Old", "Old Hello", "Hello"));
    }

    [Fact]
    public void GoogleEarthSearchUsesNoActivatePalette()
    {
        Assert.True(MainWindow.IsFocusSensitiveWebEdit(
            "chrome",
            System.Windows.Automation.ControlType.Edit,
            0,
            "Search Google Earth"));
    }

    [Fact]
    public void NormalChromeEditDoesNotUseNoActivatePalette()
    {
        Assert.False(MainWindow.IsFocusSensitiveWebEdit(
            "chrome",
            System.Windows.Automation.ControlType.Edit,
            0,
            "Message"));
    }

    [Fact]
    public void NormalPaletteOpenForcesActivation()
    {
        Assert.True(MainWindow.ShouldActivatePaletteWindow(noActivate: false));
        Assert.False(MainWindow.ShouldActivatePaletteWindow(noActivate: true));
    }

    [Fact]
    public void GoogleEarthFlutterTextGroupUsesNoActivatePalette()
    {
        Assert.True(MainWindow.IsFocusSensitiveWebEdit(
            "chrome",
            System.Windows.Automation.ControlType.Group,
            0,
            """<input tabindex="-1" placeholder="Search Google Earth" class="flt-text-editing transparentTextEditing">"""));
    }

    [Fact]
    public void GoogleEarthSearchIsRecognizedForCommit()
    {
        Assert.True(MainWindow.IsGoogleEarthSearchElement(
            "chrome",
            System.Windows.Automation.ControlType.Edit,
            0,
            "Search Google Earth"));
    }

    [Fact]
    public void GoogleEarthGenericSearchNameIsRecognizedFromWindowTitle()
    {
        Assert.True(MainWindow.IsGoogleEarthSearchElement(
            "chrome",
            System.Windows.Automation.ControlType.Edit,
            0,
            "Search",
            "Google Earth - Google Chrome"));
    }

    [Fact]
    public void GenericSearchOutsideGoogleEarthIsNotRecognized()
    {
        Assert.False(MainWindow.IsGoogleEarthSearchElement(
            "chrome",
            System.Windows.Automation.ControlType.Edit,
            0,
            "Search",
            "Google Search - Google Chrome"));
    }
}
