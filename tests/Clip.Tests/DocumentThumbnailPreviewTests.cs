using Clip.Watcher;

namespace Clip.Tests;

public sealed class DocumentThumbnailPreviewTests
{
    [Fact]
    public void RenderPreviewThumbRejectsMissingArguments()
    {
        Assert.Equal(2, Program.RenderPreviewThumb(["preview-thumb"]));
        Assert.Equal(2, Program.RenderPreviewThumb(["preview-thumb", "only-one-arg"]));
    }

    [Fact]
    public void RenderPreviewThumbRejectsMissingSourceFile()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"clip-missing-{Guid.NewGuid():N}.pdf");
        var outPng = Path.Combine(Path.GetTempPath(), $"clip-out-{Guid.NewGuid():N}.png");

        Assert.Equal(2, Program.RenderPreviewThumb(["preview-thumb", missing, outPng]));
        Assert.False(File.Exists(outPng));
    }

    [Fact]
    public void RenderPreviewThumbRejectsUnsupportedDocumentType()
    {
        var source = Path.Combine(Path.GetTempPath(), $"clip-thumb-{Guid.NewGuid():N}.zip");
        var outPng = Path.Combine(Path.GetTempPath(), $"clip-out-{Guid.NewGuid():N}.png");
        File.WriteAllText(source, "not a document");
        try
        {
            // .zip is not a renderable first-page document, so the verb reports bad-args (2) without
            // attempting a render and without writing an output file.
            Assert.Equal(2, Program.RenderPreviewThumb(["preview-thumb", source, outPng]));
            Assert.False(File.Exists(outPng));
        }
        finally
        {
            File.Delete(source);
        }
    }

    [Fact]
    public void RenderPreviewThumbFailsGracefullyWhenRendererCannotProduceImage()
    {
        // A .pdf with bogus bytes is a supported type but cannot render; the verb must return the
        // "no image produced" code (3) without throwing, regardless of whether pdftoppm is present.
        var source = Path.Combine(Path.GetTempPath(), $"clip-thumb-{Guid.NewGuid():N}.pdf");
        var outPng = Path.Combine(Path.GetTempPath(), $"clip-out-{Guid.NewGuid():N}.png");
        File.WriteAllText(source, "this is not a real pdf");
        try
        {
            Assert.Equal(3, Program.RenderPreviewThumb(["preview-thumb", source, outPng]));
            Assert.False(File.Exists(outPng));
        }
        finally
        {
            File.Delete(source);
        }
    }

    [Fact]
    public void WatcherHelperExposesPreviewThumbVerbReusingFirstPageRenderers()
    {
        var program = File.ReadAllText(RepoPath("src", "Clip.Watcher", "Program.cs"));

        Assert.Contains("case \"preview-thumb\":", program);
        Assert.Contains("RenderPreviewThumb(args)", program);
        Assert.Contains("preview-thumb <srcPath> <outPng>", program);

        // The verb reuses the standalone first-page renderers rather than re-implementing them.
        Assert.Contains("PdfPreviewRenderer.TryRenderFirstPage", program);
        Assert.Contains("StaticDocumentPreviewRenderer.TryRenderFirstPageOnStaThread", program);
        Assert.Contains("ImageFormat.Png", program);
    }

    [Fact]
    public void PaletteThumbnailHelperDelegatesToWatcherAndCachesUnderTempByPathAndMtime()
    {
        var helper = File.ReadAllText(RepoPath("src", "Clip.CommandPalette", "ClipDocumentThumbnail.cs"));

        // Rendering is delegated to the Watcher helper verb (no heavy PDF/Office deps in net9).
        Assert.Contains("ClipExecutableLocator.Resolve(\"Clip.Watcher.exe\")", helper);
        Assert.Contains("preview-thumb", helper);

        // Supported document kinds: PDF + Office + Visio.
        Assert.Contains("\".pdf\"", helper);
        Assert.Contains("\".docx\"", helper);
        Assert.Contains("\".xlsx\"", helper);
        Assert.Contains("\".pptx\"", helper);
        Assert.Contains("\".vsdx\"", helper);

        // Cached under %TEMP%\Clip\PaletteThumbs keyed by path + mtime + size.
        Assert.Contains("Path.GetTempPath()", helper);
        Assert.Contains("\"PaletteThumbs\"", helper);
        Assert.Contains("info.LastWriteTimeUtc.Ticks", helper);

        // A render-free cached lookup exists for the list/details path, and the rendering call is
        // separate (so the list never launches the helper).
        Assert.Contains("TryGetCachedThumbnailPng", helper);
        Assert.Contains("TryGetThumbnailPng", helper);
    }

    [Fact]
    public void PreviewPageRendersDocumentThumbnailLazilyWhileDetailsPaneStaysRenderFree()
    {
        var previewPage = File.ReadAllText(RepoPath("src", "Clip.CommandPalette", "ClipItemPreviewPage.cs"));
        var imagePreview = File.ReadAllText(RepoPath("src", "Clip.CommandPalette", "ClipImagePreviewContent.cs"));
        var historyPage = File.ReadAllText(RepoPath("src", "Clip.CommandPalette", "ClipHistoryPage.cs"));

        // The heavyweight render happens only when the preview page's content is built (GetContent),
        // never during list render.
        Assert.Contains("TryCreateDocumentThumbnail(fullItem, out var documentPreview)", previewPage);
        Assert.Contains("ClipDocumentThumbnail.TryGetThumbnailPng", imagePreview);

        // The details pane (built per row on the list path) only ever uses the render-free cached
        // lookup and falls back to the text/path preview otherwise.
        Assert.Contains("ClipDocumentThumbnail.TryGetCachedThumbnailPng", historyPage);
        Assert.DoesNotContain("ClipDocumentThumbnail.TryGetThumbnailPng", historyPage);
        Assert.Contains("ImageMarkdown(cachedPng)", historyPage);
    }

    private static string RepoPath(params string[] parts)
    {
        var directory = AppContext.BaseDirectory;
        while (!string.IsNullOrWhiteSpace(directory))
        {
            var candidate = Path.Combine(new[] { directory }.Concat(parts).ToArray());
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = Directory.GetParent(directory)?.FullName;
        }

        throw new FileNotFoundException("Could not find repo file.", Path.Combine(parts));
    }
}
