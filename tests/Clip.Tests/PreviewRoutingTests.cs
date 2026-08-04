using System.Reflection;

namespace Clip.Tests;

/// <summary>
/// Guards the preview routing table.
///
/// This is the part that has historically broken: getting Excel working stopped Word working,
/// then Visio. Each format is asserted independently so a change that steals one extension from
/// another fails here rather than in the app.
/// </summary>
public sealed class PreviewRoutingTests
{
    private static readonly Assembly Shell = Assembly.Load("Clip");

    private static bool Call(string method, string extension)
    {
        var type = Shell.GetType("Clip.Shell.MainWindow")
            ?? throw new InvalidOperationException("MainWindow not found");
        var info = type.GetMethod(method, BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException($"{method} not found");
        return (bool)info.Invoke(null, [extension])!;
    }

    [Theory]
    [InlineData(".mp4")]
    [InlineData(".mov")]
    [InlineData(".mkv")]
    [InlineData(".webm")]
    [InlineData(".avi")]
    [InlineData(".wmv")]
    public void VideoExtensionsRouteToVideo(string extension)
    {
        Assert.True(Call("IsVideoFile", extension));
        Assert.False(Call("IsAudioFile", extension));
        Assert.False(Call("IsImageFile", extension));
    }

    [Theory]
    [InlineData(".mp3")]
    [InlineData(".wav")]
    [InlineData(".m4a")]
    [InlineData(".flac")]
    [InlineData(".aac")]
    [InlineData(".ogg")]
    public void AudioExtensionsRouteToAudio(string extension)
    {
        Assert.True(Call("IsAudioFile", extension));
        Assert.False(Call("IsVideoFile", extension));
        Assert.False(Call("IsImageFile", extension));
    }

    [Theory]
    [InlineData(".png")]
    [InlineData(".jpg")]
    [InlineData(".gif")]
    [InlineData(".webp")]
    public void ImageExtensionsRouteToImage(string extension)
    {
        Assert.True(Call("IsImageFile", extension));
        Assert.False(Call("IsVideoFile", extension));
        Assert.False(Call("IsAudioFile", extension));
    }

    [Theory]
    [InlineData(".png")]
    [InlineData(".mp4")]
    [InlineData(".mp3")]
    public void MediaCoversAllThreeKinds(string extension) => Assert.True(Call("IsMediaFile", extension));

    [Theory]
    [InlineData(".docx")]
    [InlineData(".xlsx")]
    [InlineData(".pptx")]
    [InlineData(".vsdx")]
    public void OfficeAndVisioKeepTheirOwnRoute(string extension)
    {
        // The regression that keeps recurring: an Office or Visio format quietly falling into
        // another bucket and losing its renderer.
        Assert.True(Call("IsOfficeOrVisio", extension));
        Assert.False(Call("IsMediaFile", extension));
        Assert.False(Clip.Shell.CodePreviewPage.IsCodeFile(extension));
    }

    [Theory]
    [InlineData(".doc")]
    [InlineData(".docx")]
    [InlineData(".xlsx")]
    [InlineData(".pptx")]
    public void PdfBackedFormatsKeepTheirRenderedImageFallback(string extension)
    {
        // Previewing through the exported PDF is the good path, not the only one. If the export or
        // the viewer fails these must still answer to the shared Office route that draws a picture
        // instead, or a failure would leave no preview at all rather than a lesser one.
        Assert.True(Call("IsPdfBackedOfficeFile", extension));
        Assert.True(Call("IsOfficeOrVisio", extension));
    }

    /// <summary>
    /// Word, Excel and PowerPoint all preview as their exported PDF, because a document is not one
    /// page and the viewer that scrolls a PDF scrolls a workbook's sheets and a deck's slides for
    /// free.
    /// </summary>
    [Theory]
    [InlineData(".docx")]
    [InlineData(".doc")]
    [InlineData(".xlsx")]
    [InlineData(".xlsm")]
    [InlineData(".xls")]
    [InlineData(".pptx")]
    [InlineData(".ppt")]
    public void PagedOfficeFormatsPreviewThroughTheirExportedPdf(string extension) =>
        Assert.True(Call("IsPdfBackedOfficeFile", extension));

    /// <summary>
    /// Visio draws one page per sheet of a drawing and has always previewed as a picture of the
    /// first, and a PDF is already a PDF. Neither belongs in the Office export route.
    /// </summary>
    [Theory]
    [InlineData(".vsdx")]
    [InlineData(".vsd")]
    [InlineData(".pdf")]
    public void VisioAndPdfStayOutOfTheOfficeExportRoute(string extension) =>
        Assert.False(Call("IsPdfBackedOfficeFile", extension));

    [Fact]
    public void PdfIsNotClaimedByAnyOtherRoute()
    {
        Assert.False(Call("IsMediaFile", ".pdf"));
        Assert.False(Call("IsOfficeOrVisio", ".pdf"));
        Assert.False(Clip.Shell.CodePreviewPage.IsCodeFile(".pdf"));
    }
}

public sealed class CodePreviewTests
{
    [Theory]
    [InlineData(".py")]
    [InlineData(".ps1")]
    [InlineData(".sh")]
    [InlineData(".cs")]
    [InlineData(".js")]
    [InlineData(".ts")]
    [InlineData(".json")]
    [InlineData(".yaml")]
    [InlineData(".sql")]
    public void SourceFilesAreTreatedAsCode(string extension) =>
        Assert.True(Clip.Shell.CodePreviewPage.IsCodeFile(extension));

    [Theory]
    [InlineData(".txt")]
    [InlineData(".log")]
    [InlineData(".pdf")]
    [InlineData(".docx")]
    [InlineData(".mp4")]
    public void NonSourceFilesAreNotTreatedAsCode(string extension) =>
        Assert.False(Clip.Shell.CodePreviewPage.IsCodeFile(extension));

    [Fact]
    public void FileContentCannotInjectMarkup()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".py");
        File.WriteAllText(path, "x = \"<script>alert(1)</script>\"\n");

        try
        {
            var html = Clip.Shell.CodePreviewPage.Build(path, "#111", "#eee", "#888", "#8ab");

            // The script tag must arrive escaped, never as a live element.
            Assert.DoesNotContain("<script>alert(1)</script>", html);
            Assert.Contains("&lt;script&gt;", html);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void HighlightingDoesNotLeakItsOwnMarkup()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".py");
        // "class" is a Python keyword, and the first implementation rewrote the class= attributes
        // of the spans it had just inserted, leaking raw markup into the page.
        File.WriteAllText(path, "class CameraTakeoff:\n    def __init__(self):\n        pass\n");

        try
        {
            var html = Clip.Shell.CodePreviewPage.Build(path, "#111", "#eee", "#888", "#8ab");
            Assert.DoesNotContain("class=&quot;", html);
            Assert.DoesNotContain("&lt;span", html);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
