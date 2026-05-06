using System.IO.Compression;
using Clip.Watcher;

namespace Clip.Tests;

public sealed class ExcelPreviewReaderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Clip.ExcelPreview", Guid.NewGuid().ToString("N"));

    public ExcelPreviewReaderTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void TryReadReturnsFirstSheetCellsWithinPreviewRange()
    {
        var path = Path.Combine(_root, "sample.xlsx");
        CreateWorkbook(path);

        var ok = ExcelPreviewReader.TryRead(path, out var cells);

        Assert.True(ok);
        Assert.Equal("Hello", cells[0, 0].Value);
        Assert.True(cells[0, 0].Bold);
        Assert.Equal(System.Drawing.Color.FromArgb(255, 242, 204), cells[0, 0].FillColor);
        Assert.Equal("World", cells[1, 1].Value);
        Assert.Equal("19", cells[34, 18].Value);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static void CreateWorkbook(string path)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        WriteEntry(archive, "xl/sharedStrings.xml",
            """<?xml version="1.0" encoding="UTF-8"?><sst xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><si><t>Hello</t></si><si><t>World</t></si></sst>""");
        WriteEntry(archive, "xl/styles.xml",
            """<?xml version="1.0" encoding="UTF-8"?><styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><fonts count="2"><font/><font><b/></font></fonts><fills count="3"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill><fill><patternFill patternType="solid"><fgColor rgb="FFFFF2CC"/></patternFill></fill></fills><cellXfs count="2"><xf fontId="0" fillId="0"/><xf fontId="1" fillId="2"/></cellXfs></styleSheet>""");
        WriteEntry(archive, "xl/worksheets/sheet1.xml",
            """<?xml version="1.0" encoding="UTF-8"?><worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData><row r="1"><c r="A1" t="s" s="1"><v>0</v></c></row><row r="2"><c r="B2" t="s"><v>1</v></c></row><row r="35"><c r="S35"><v>19</v></c><c r="T35"><v>hidden</v></c></row><row r="36"><c r="A36"><v>hidden</v></c></row></sheetData></worksheet>""");
    }

    private static void WriteEntry(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(content);
    }
}
