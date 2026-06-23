using Clip.Core;

namespace Clip.Tests;

public sealed class ClipboardHistoryFileKindFilterTests
{
    [Theory]
    [InlineData("report.pdf", "pdf")]
    [InlineData("budget.xlsx", "excel")]
    [InlineData("budget.XLSM", "excel")]
    [InlineData("memo.docx", "word")]
    [InlineData("deck.pptx", "powerpoint")]
    [InlineData("diagram.vsdx", "visio")]
    [InlineData("page.html", "html")]
    [InlineData("photo.PNG", "image")]
    [InlineData("notes.txt", "text")]
    [InlineData("archive.zip", "zip")]
    [InlineData("no-extension", "")]
    public void KeyForCategorizesByExtension(string fileName, string expected)
    {
        // Use a path under a directory that does not exist so the Directory.Exists folder branch
        // never triggers; we are exercising the pure extension categorization.
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), fileName);
        Assert.Equal(expected, ClipboardHistoryFileKindFilter.KeyFor(path));
    }

    [Fact]
    public void KeyForReturnsFolderForExistingDirectory()
    {
        Assert.Equal("folder", ClipboardHistoryFileKindFilter.KeyFor(Path.GetTempPath()));
    }

    [Fact]
    public void IsFileKindFilterRecognizesFileIdsOnly()
    {
        Assert.True(ClipboardHistoryFileKindFilter.IsFileKindFilter(ClipboardHistoryFileKindFilter.All));
        Assert.True(ClipboardHistoryFileKindFilter.IsFileKindFilter(ClipboardHistoryFileKindFilter.FilterIdFor("pdf")));
        Assert.False(ClipboardHistoryFileKindFilter.IsFileKindFilter(ClipboardHistoryListFilter.Files));
        Assert.False(ClipboardHistoryFileKindFilter.IsFileKindFilter(ClipboardHistoryDateFilter.Today));
        Assert.False(ClipboardHistoryFileKindFilter.IsFileKindFilter(null));
        Assert.False(ClipboardHistoryFileKindFilter.IsFileKindFilter(""));
    }

    [Fact]
    public void NonFileKindAndFileAllFiltersMatchEverything()
    {
        var pdf = FilesItem("c:\\docs\\report.pdf");

        // Kind/date/pinned ids and file "All" must short-circuit to true so the predicate composes
        // with the other dimensions on the single shared dropdown.
        Assert.True(ClipboardHistoryFileKindFilter.Matches(null, pdf));
        Assert.True(ClipboardHistoryFileKindFilter.Matches(ClipboardHistoryListFilter.Files, pdf));
        Assert.True(ClipboardHistoryFileKindFilter.Matches(ClipboardHistoryDateFilter.Today, pdf));
        Assert.True(ClipboardHistoryFileKindFilter.Matches(ClipboardHistoryFileKindFilter.All, pdf));
    }

    [Fact]
    public void SubKindFilterMatchesOnlyItemsWithThatKind()
    {
        var pdf = FilesItem("c:\\docs\\report.pdf");
        var excel = FilesItem("c:\\docs\\budget.xlsx");

        var pdfFilter = ClipboardHistoryFileKindFilter.FilterIdFor("pdf");
        Assert.True(ClipboardHistoryFileKindFilter.Matches(pdfFilter, pdf));
        Assert.False(ClipboardHistoryFileKindFilter.Matches(pdfFilter, excel));
    }

    [Fact]
    public void SubKindFilterMatchesWhenAnyPathInAMultiFileItemHasTheKind()
    {
        var mixed = FilesItem("c:\\docs\\budget.xlsx", "c:\\docs\\report.pdf");
        Assert.True(ClipboardHistoryFileKindFilter.Matches(ClipboardHistoryFileKindFilter.FilterIdFor("pdf"), mixed));
        Assert.True(ClipboardHistoryFileKindFilter.Matches(ClipboardHistoryFileKindFilter.FilterIdFor("excel"), mixed));
        Assert.False(ClipboardHistoryFileKindFilter.Matches(ClipboardHistoryFileKindFilter.FilterIdFor("word"), mixed));
    }

    [Fact]
    public void SubKindFilterExcludesItemsWithoutFilePaths()
    {
        var textItem = new ClipboardHistoryListItemBuilder().WithKind("Text").Build();
        Assert.False(ClipboardHistoryFileKindFilter.Matches(ClipboardHistoryFileKindFilter.FilterIdFor("pdf"), textItem));
    }

    [Fact]
    public void DiscoverKindsReturnsWellKnownKindsFirstThenDiscoveredAlphabetical()
    {
        var items = new[]
        {
            FilesItem("c:\\docs\\archive.zip"),
            FilesItem("c:\\docs\\report.pdf"),
            FilesItem("c:\\docs\\data.rtf"),
            FilesItem("c:\\docs\\memo.docx"),
            FilesItem("c:\\docs\\dup.pdf"),
        };

        var kinds = ClipboardHistoryFileKindFilter.DiscoverKinds(items);

        // Canonical kinds (pdf, word) come first in canonical order; discovered extensions
        // (rtf, zip) are appended alphabetically. Duplicates collapse.
        Assert.Equal(["pdf", "word", "rtf", "zip"], kinds);
    }

    [Fact]
    public void DiscoverKindsIsEmptyWhenNoFilePaths()
    {
        var items = new[] { new ClipboardHistoryListItemBuilder().WithKind("Text").Build() };
        Assert.Empty(ClipboardHistoryFileKindFilter.DiscoverKinds(items));
    }

    [Fact]
    public void LabelForReturnsHumanReadableNames()
    {
        Assert.Equal("All files", ClipboardHistoryFileKindFilter.LabelFor(ClipboardHistoryFileKindFilter.All));
        Assert.Equal("Folders", ClipboardHistoryFileKindFilter.LabelFor("folder"));
        Assert.Equal("PDF", ClipboardHistoryFileKindFilter.LabelFor("pdf"));
        Assert.Equal("Word", ClipboardHistoryFileKindFilter.LabelFor(ClipboardHistoryFileKindFilter.FilterIdFor("word")));
        Assert.Equal("PowerPoint", ClipboardHistoryFileKindFilter.LabelFor("powerpoint"));
        Assert.Equal("Images", ClipboardHistoryFileKindFilter.LabelFor("image"));
        Assert.Equal("ZIP", ClipboardHistoryFileKindFilter.LabelFor("zip"));
    }

    private static ClipboardHistoryListItem FilesItem(params string[] paths) =>
        new ClipboardHistoryListItemBuilder()
            .WithKind("Files")
            .WithFilePaths(paths)
            .Build();

    private sealed class ClipboardHistoryListItemBuilder
    {
        private string _kind = "Files";
        private IReadOnlyList<string> _filePaths = [];

        public ClipboardHistoryListItemBuilder WithKind(string kind)
        {
            _kind = kind;
            return this;
        }

        public ClipboardHistoryListItemBuilder WithFilePaths(IReadOnlyList<string> paths)
        {
            _filePaths = paths;
            return this;
        }

        public ClipboardHistoryListItem Build() => new(
            Id: Guid.NewGuid().ToString("N"),
            Kind: _kind,
            Title: _kind,
            Preview: _kind,
            FilePaths: _filePaths,
            IsPinned: false,
            PinOrder: 0,
            HasOriginalFormatting: false,
            SourceApplication: "Test",
            AssetSizeBytes: null,
            CharacterCount: null,
            WordCount: null,
            LastUsedAt: DateTimeOffset.Now,
            LastCopiedAt: DateTimeOffset.Now,
            CopyCount: 1,
            ImageWidth: null,
            ImageHeight: null,
            DefaultActionId: null,
            Actions: []);
    }
}
