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
