using Clip.Shell;

namespace Clip.Tests;

public class ExcelPreviewTests
{
    [Theory]
    [InlineData("A1", 0)]
    [InlineData("B2", 1)]
    [InlineData("Z9", 25)]
    [InlineData("AA1", 26)]
    [InlineData("AB7", 27)]
    [InlineData("BA1", 52)]
    public void CellAddressesResolveToTheirColumn(string reference, int expected) =>
        Assert.Equal(expected, ExcelWorkbookReader.ColumnOf(reference));

    [Theory]
    [InlineData(0, "A")]
    [InlineData(25, "Z")]
    [InlineData(26, "AA")]
    [InlineData(27, "AB")]
    [InlineData(52, "BA")]
    public void ColumnHeadingsCountLikeASpreadsheet(int index, string expected) =>
        Assert.Equal(expected, ExcelPreviewPage.ColumnName(index));

    /// <summary>
    /// Every address must survive the round trip, because a column read one place and written
    /// another that disagree would put values under the wrong heading.
    /// </summary>
    [Fact]
    public void ColumnNamesAndAddressesAgree()
    {
        for (var i = 0; i < 200; i++)
        {
            Assert.Equal(i, ExcelWorkbookReader.ColumnOf(ExcelPreviewPage.ColumnName(i) + "1"));
        }
    }

    [Theory]
    [InlineData("book.xlsx", true)]
    [InlineData("book.xlsm", true)]
    [InlineData("book.XLSX", true)]
    [InlineData("book.xls", false)]   // not a zip archive; Excel still has to open it
    [InlineData("book.csv", false)]
    [InlineData("deck.pptx", false)]
    public void OnlyTheZipBasedWorkbookFormatsAreReadDirectly(string name, bool expected) =>
        Assert.Equal(expected, ExcelWorkbookReader.CanRead(name));

    [Fact]
    public void AWorkbookThatIsNotAZipIsLeftToExcel()
    {
        var fake = Path.Combine(Path.GetTempPath(), $"clip-fake-{Guid.NewGuid():N}.xlsx");
        File.WriteAllText(fake, "this is not a zip archive");
        try
        {
            Assert.Null(ExcelWorkbookReader.TryRead(fake));
        }
        finally
        {
            File.Delete(fake);
        }
    }

    /// <summary>
    /// The tab strip is the whole point of showing a workbook rather than a picture of one sheet,
    /// so every sheet has to reach it and the first has to start open.
    /// </summary>
    [Fact]
    public void EverySheetGetsATabAndTheFirstIsOpen()
    {
        var sheets = new List<ExcelSheet>
        {
            new("Summary", [["a", "b"]], false),
            new("Detail", [["c"]], false),
            new("Notes & <odd>", [], false),
        };

        var html = ExcelPreviewPage.Build(sheets, "book.xlsx", "#1e1e1e", "#ffffff");

        Assert.Contains(">Summary<", html);
        Assert.Contains(">Detail<", html);
        Assert.Equal(3, html.Split("class=\"tab").Length - 1);
        Assert.Equal(1, html.Split("class=\"tab active\"").Length - 1);

        // A sheet name is user text and lands in markup, so it has to be escaped.
        Assert.Contains("Notes &amp; &lt;odd&gt;", html);
        Assert.DoesNotContain("<odd>", html);
    }

    [Fact]
    public void ATruncatedSheetSaysSoRatherThanJustEnding()
    {
        var html = ExcelPreviewPage.Build([new("Big", [["x"]], true)], "book.xlsx", "#1e1e1e", "#ffffff");

        Assert.Contains("first part of this sheet", html);
    }
}
