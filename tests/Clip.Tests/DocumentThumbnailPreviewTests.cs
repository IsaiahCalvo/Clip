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
    public void OfficePreviewGivesTheColdStartItsOwnBudget()
    {
        // A single flat budget cannot cover both an Office cold start and a warm export: measured
        // against a 178KB multi-page .docx with no WINWORD.EXE running, the cold export ran 22.7s
        // to 26.2s while the next one took 5.0s to 13.3s. The cold budget has to leave real room
        // above those cold measurements, the warm one has to clear the slowest warm render seen
        // here -- a Visio floor plan at 26.5s -- while staying short enough that a genuine Office
        // hang does not strand the preview pane, and the two must not collapse back into one
        // number.
        var program = File.ReadAllText(RepoPath("src", "Clip.Watcher", "Program.cs"));

        Assert.Contains("ColdPreviewTimeout", program);
        Assert.Contains("WarmPreviewTimeout", program);
        Assert.Contains("_officeComWarm ? WarmPreviewTimeout : ColdPreviewTimeout", program);

        var cold = TimeoutSeconds(program, "ColdPreviewTimeout");
        var warm = TimeoutSeconds(program, "WarmPreviewTimeout");

        Assert.True(cold >= 60, $"cold budget {cold}s leaves no room above the measured cold export");
        Assert.True(warm >= 35, $"warm budget {warm}s does not clear the slowest measured warm render");
        Assert.True(warm < cold, "the cold path must get the larger budget");

        // The flag turns on at both COM export sites -- the PDF one Word and Excel use and the
        // image one PowerPoint and Visio use -- and only there, so a call that returned fast off
        // the fingerprint cache without ever starting Office never counts as a warm-up.
        Assert.Equal(2, program.Split("_officeComWarm = true;").Length - 1);
    }

    private static double TimeoutSeconds(string program, string name)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            program,
            name + @"\s*=\s*TimeSpan\.From(?<unit>Seconds|Minutes)\((?<value>[\d.]+)\)");
        Assert.True(match.Success, $"could not read {name}");
        var value = double.Parse(match.Groups["value"].Value, System.Globalization.CultureInfo.InvariantCulture);
        return match.Groups["unit"].Value == "Minutes" ? value * 60 : value;
    }

    [Theory]
    [InlineData(".xlsx")]
    [InlineData(".pptx")]
    [InlineData(".vsdx")]
    [InlineData(".pdf")]
    [InlineData(".zip")]
    public void WordPdfExportRefusesEveryFormatThatIsNotWord(string extension)
    {
        // The Word preview route hands its cached PDF straight to the browser viewer. Letting any
        // other format in would mean driving Word against a file it cannot open, so the guard is
        // checked before COM is ever touched and this test never starts an Office process.
        var source = Path.Combine(Path.GetTempPath(), $"clip-word-{Guid.NewGuid():N}{extension}");
        File.WriteAllText(source, "not a word document");
        try
        {
            Assert.Null(StaticDocumentPreviewRenderer.TryExportWordPdfOnStaThread(source));
        }
        finally
        {
            File.Delete(source);
        }
    }

    [Fact]
    public void WordPdfExportRefusesAMissingFile()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"clip-missing-{Guid.NewGuid():N}.docx");
        Assert.Null(StaticDocumentPreviewRenderer.TryExportWordPdfOnStaThread(missing));
    }

    [Fact]
    public void OfficeInstanceIsOwnedOnlyWhenComStartedANewProcess()
    {
        // COM hands PowerPoint callers the instance the user is already working in, so "did a
        // process appear that was not running a moment ago" is what separates an instance we may
        // hide and quit from one we must leave alone.
        Assert.True(StaticDocumentPreviewRenderer.CreatedNewProcess([], [7]));
        Assert.True(StaticDocumentPreviewRenderer.CreatedNewProcess([1, 2], [1, 2, 3]));
        Assert.False(StaticDocumentPreviewRenderer.CreatedNewProcess([1, 2], [1, 2]));

        // An instance that quit on its own while we were attaching is still not one we created.
        Assert.False(StaticDocumentPreviewRenderer.CreatedNewProcess([1, 2], [1]));

        // Unknown snapshots mean we could not tell, which must not be read as ownership.
        Assert.False(StaticDocumentPreviewRenderer.CreatedNewProcess(null, [7]));
        Assert.False(StaticDocumentPreviewRenderer.CreatedNewProcess([1], null));
    }

    [Fact]
    public void OfficeProgIdsMapToTheProcessesTheyStart()
    {
        Assert.Equal("WINWORD", StaticDocumentPreviewRenderer.OfficeProcessName("Word.Application"));
        Assert.Equal("EXCEL", StaticDocumentPreviewRenderer.OfficeProcessName("Excel.Application"));
        Assert.Equal("POWERPNT", StaticDocumentPreviewRenderer.OfficeProcessName("PowerPoint.Application"));
        Assert.Equal("VISIO", StaticDocumentPreviewRenderer.OfficeProcessName("Visio.Application"));

        // Every progId the renderer drives must be mapped, or its instances are never owned and its
        // processes leak one per preview.
        var program = File.ReadAllText(RepoPath("src", "Clip.Watcher", "Program.cs"));
        foreach (var progId in new[] { "Word.Application", "Excel.Application", "PowerPoint.Application", "Visio.Application" })
        {
            Assert.Contains($"\"{progId}\"", program);
        }

        Assert.Null(StaticDocumentPreviewRenderer.OfficeProcessName("Access.Application"));
    }

    [Fact]
    public void OfficePreviewNeverTouchesGlobalStateOfAnInstanceItDidNotCreate()
    {
        var program = File.ReadAllText(RepoPath("src", "Clip.Watcher", "Program.cs"));

        // Quit, Visible and DisplayAlerts are global to the Office process. On an instance COM
        // attached us to they belong to the user, so every one of them sits behind an ownership
        // check -- not just the ones that happen to be written today.
        Assert.Contains("if (app.Owned)", program);
        Assert.Contains("app.Application.Quit();", program);

        foreach (var mutation in new[] { "app.Visible = false;", "app.DisplayAlerts = 0;", "app.DisplayAlerts = false;" })
        {
            var index = program.IndexOf(mutation, StringComparison.Ordinal);
            Assert.True(index >= 0, $"expected the renderer to still set {mutation}");
            while (index >= 0)
            {
                Assert.Contains("if (office.Owned)", program[Math.Max(0, index - 120)..index]);
                index = program.IndexOf(mutation, index + 1, StringComparison.Ordinal);
            }
        }

        // And a document that was already open in the user's instance is theirs to close, not ours.
        Assert.Contains("FindOpenDocument", program);
        Assert.Contains("ReleaseOrClose(", program);
        Assert.DoesNotContain("CloseAndRelease(document, false);", program);
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
