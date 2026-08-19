using System.IO.Compression;
using System.Text;
using Clip.Shell;

namespace Clip.Tests;

/// <summary>
/// The xlsx path reads the zip directly instead of driving Excel, so it has to get every cell
/// representation right on its own: shared strings, inline strings, booleans, errors, styled date
/// serials, gap columns and the truncation limits. Workbooks are built in a temp dir at test time.
/// </summary>
public sealed class ExcelWorkbookReaderCoverageTests : IDisposable
{
    private const string MainNs = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string RelNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string PackageNs = "http://schemas.openxmlformats.org/package/2006/relationships";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "Clip.Tests", Guid.NewGuid().ToString("N"));

    public ExcelWorkbookReaderCoverageTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            TestTemp.Delete(_root);
        }
        catch
        {
            // Temp cleanup only.
        }
    }

    [Fact]
    public void EveryCellRepresentationComesBackAsFormattedText()
    {
        var path = WriteXlsx(
            ("xl/workbook.xml", $"""
                <workbook xmlns="{MainNs}" xmlns:r="{RelNs}">
                  <sheets>
                    <sheet name="First" sheetId="1" r:id="rId1"/>
                    <sheet name="Second" sheetId="2" r:id="rId2"/>
                  </sheets>
                </workbook>
                """),
            ("xl/_rels/workbook.xml.rels", $"""
                <Relationships xmlns="{PackageNs}">
                  <Relationship Id="rId1" Type="t" Target="/xl/worksheets/sheet1.xml"/>
                  <Relationship Id="rId2" Type="t" Target="worksheets/sheet2.xml"/>
                </Relationships>
                """),
            ("xl/sharedStrings.xml", $"""
                <sst xmlns="{MainNs}"><si><t>Alpha</t></si><si><r><t>Be</t></r><r><t>ta</t></r></si></sst>
                """),
            ("xl/styles.xml", $"""
                <styleSheet xmlns="{MainNs}">
                  <numFmts count="1"><numFmt numFmtId="164" formatCode="yyyy-mm-dd"/></numFmts>
                  <cellXfs count="4">
                    <xf numFmtId="0"/>
                    <xf numFmtId="14"/>
                    <xf numFmtId="164"/>
                    <xf numFmtId="4"/>
                  </cellXfs>
                </styleSheet>
                """),
            ("xl/worksheets/sheet1.xml", $"""
                <worksheet xmlns="{MainNs}"><sheetData>
                  <row r="1">
                    <c r="A1" t="s"><v>0</v></c>
                    <c r="B1" t="s"><v>1</v></c>
                    <c r="C1" t="inlineStr"><is><t>Inline</t></is></c>
                    <c r="D1" t="b"><v>1</v></c>
                    <c r="E1" t="b"><v>0</v></c>
                    <c r="F1" t="e"><v>#DIV/0!</v></c>
                  </row>
                  <row r="2">
                    <c r="A2" s="1"><v>45000</v></c>
                    <c r="B2" s="2"><v>45000.5</v></c>
                    <c r="C2" s="3"><v>12.5</v></c>
                    <c r="E2"><v>7</v></c>
                    <c r="F2"><v></v></c>
                  </row>
                  <row r="3">
                    <c r="A3" t="s"><v>99</v></c>
                  </row>
                </sheetData></worksheet>
                """),
            ("xl/worksheets/sheet2.xml", $"""
                <worksheet xmlns="{MainNs}"><sheetData>
                  <row r="1"><c r="A1" t="inlineStr"><is><t>second</t></is></c></row>
                </sheetData></worksheet>
                """));

        var sheets = ExcelWorkbookReader.TryRead(path);

        Assert.NotNull(sheets);
        Assert.Equal(2, sheets!.Count);
        Assert.Equal("First", sheets[0].Name);
        Assert.Equal("Second", sheets[1].Name);
        Assert.False(sheets[0].Truncated);

        var rows = sheets[0].Rows;
        Assert.Equal(3, rows.Count);

        // Shared strings (including a rich-text run), inline strings, booleans and errors.
        Assert.Equal(new[] { "Alpha", "Beta", "Inline", "TRUE", "FALSE", "#DIV/0!" }, rows[0]);

        // Date styles turn serials into dates (built-in format 14 and the custom yyyy-mm-dd),
        // an undated style leaves the number alone, the gap at D stays empty, and an empty
        // <v/> reads as nothing.
        Assert.Equal(new[] { "2023-03-15", "2023-03-15 12:00", "12.5", "", "7", "" }, rows[1]);

        // An out-of-range shared-string index falls back to the raw value, and the short row is
        // padded out to the widest one so the grid stays rectangular.
        Assert.Equal(new[] { "99", "", "", "", "", "" }, rows[2]);

        Assert.Equal("second", sheets[1].Rows[0][0]);
    }

    [Fact]
    public void OversizedSheetsAreCutAtTheRowAndColumnLimits()
    {
        // 502 rows and a cell out at column BZ (77): the preview keeps 500 rows and 50 columns.
        var sheet = new StringBuilder($"<worksheet xmlns=\"{MainNs}\"><sheetData>");
        sheet.Append("<row r=\"1\"><c r=\"A1\"><v>1</v></c><c r=\"BZ1\"><v>far</v></c></row>");
        for (var row = 2; row <= 502; row++)
        {
            sheet.Append($"<row r=\"{row}\"><c r=\"A{row}\"><v>{row}</v></c></row>");
        }

        sheet.Append("</sheetData></worksheet>");

        var path = WriteXlsx(
            ("xl/workbook.xml", $"""
                <workbook xmlns="{MainNs}" xmlns:r="{RelNs}">
                  <sheets><sheet name="Big" sheetId="1" r:id="rId1"/></sheets>
                </workbook>
                """),
            ("xl/_rels/workbook.xml.rels", $"""
                <Relationships xmlns="{PackageNs}">
                  <Relationship Id="rId1" Type="t" Target="worksheets/sheet1.xml"/>
                </Relationships>
                """),
            ("xl/worksheets/sheet1.xml", sheet.ToString()));

        var sheets = ExcelWorkbookReader.TryRead(path);

        Assert.NotNull(sheets);
        var big = Assert.Single(sheets!);
        Assert.True(big.Truncated);
        Assert.Equal(500, big.Rows.Count);
        Assert.Equal(new[] { "1" }, big.Rows[0]); // the BZ cell was dropped, not shifted left
    }

    /// <summary>
    /// A workbook without shared strings or styles is legal — both parts are optional — so the
    /// reader must fall back to no strings and no date styles rather than failing the file.
    /// </summary>
    [Fact]
    public void MissingOptionalPartsDoNotFailTheWorkbook()
    {
        var path = WriteXlsx(
            ("xl/workbook.xml", $"""
                <workbook xmlns="{MainNs}" xmlns:r="{RelNs}">
                  <sheets><sheet name="Bare" sheetId="1" r:id="rId1"/></sheets>
                </workbook>
                """),
            ("xl/_rels/workbook.xml.rels", $"""
                <Relationships xmlns="{PackageNs}">
                  <Relationship Id="rId1" Type="t" Target="worksheets/sheet1.xml"/>
                </Relationships>
                """),
            ("xl/worksheets/sheet1.xml", $"""
                <worksheet xmlns="{MainNs}"><sheetData>
                  <row r="1"><c r="A1"><v>5</v></c></row>
                </sheetData></worksheet>
                """));

        var sheets = ExcelWorkbookReader.TryRead(path);

        Assert.NotNull(sheets);
        Assert.Equal("5", Assert.Single(sheets!).Rows[0][0]);
    }

    /// <summary>
    /// A serial no real spreadsheet can hold (beyond year 9999) must degrade to the raw number,
    /// not throw the whole preview away.
    /// </summary>
    [Fact]
    public void AnImpossibleDateSerialFallsBackToTheNumber()
    {
        var path = WriteXlsx(
            ("xl/workbook.xml", $"""
                <workbook xmlns="{MainNs}" xmlns:r="{RelNs}">
                  <sheets><sheet name="Odd" sheetId="1" r:id="rId1"/></sheets>
                </workbook>
                """),
            ("xl/_rels/workbook.xml.rels", $"""
                <Relationships xmlns="{PackageNs}">
                  <Relationship Id="rId1" Type="t" Target="worksheets/sheet1.xml"/>
                </Relationships>
                """),
            ("xl/styles.xml", $"""
                <styleSheet xmlns="{MainNs}">
                  <cellXfs count="1"><xf numFmtId="14"/></cellXfs>
                </styleSheet>
                """),
            ("xl/worksheets/sheet1.xml", $"""
                <worksheet xmlns="{MainNs}"><sheetData>
                  <row r="1"><c r="A1" s="0"><v>100000000</v></c></row>
                </sheetData></worksheet>
                """));

        var sheets = ExcelWorkbookReader.TryRead(path);

        Assert.NotNull(sheets);
        Assert.Equal("100000000", Assert.Single(sheets!).Rows[0][0]);
    }

    [Fact]
    public void SheetsWhosePartsCannotBeResolvedAreSkipped()
    {
        // One sheet points at a part that is not in the archive, the other at a relationship id
        // that does not exist. Neither can be read, so the workbook yields nothing.
        var path = WriteXlsx(
            ("xl/workbook.xml", $"""
                <workbook xmlns="{MainNs}" xmlns:r="{RelNs}">
                  <sheets>
                    <sheet name="Gone" sheetId="1" r:id="rId1"/>
                    <sheet name="Orphan" sheetId="2" r:id="rId9"/>
                  </sheets>
                </workbook>
                """),
            ("xl/_rels/workbook.xml.rels", $"""
                <Relationships xmlns="{PackageNs}">
                  <Relationship Id="rId1" Type="t" Target="worksheets/missing.xml"/>
                </Relationships>
                """));

        Assert.Null(ExcelWorkbookReader.TryRead(path));
    }

    [Fact]
    public void AZipThatIsNotAWorkbookIsLeftToExcel()
    {
        // Valid zip, no workbook.xml at all.
        var path = WriteXlsx(("readme.txt", "not a spreadsheet"));

        Assert.Null(ExcelWorkbookReader.TryRead(path));
    }

    private string WriteXlsx(params (string Entry, string Content)[] entries)
    {
        var path = Path.Combine(_root, $"{Guid.NewGuid():N}.xlsx");
        using var stream = File.Create(path);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
        foreach (var (entry, content) in entries)
        {
            using var writer = new StreamWriter(zip.CreateEntry(entry).Open());
            writer.Write(content);
        }

        return path;
    }
}
