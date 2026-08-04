using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Xml.Linq;

namespace Clip.Shell;

/// <summary>One sheet of a workbook, as a grid of already-formatted cell text.</summary>
internal sealed record ExcelSheet(string Name, IReadOnlyList<IReadOnlyList<string>> Rows, bool Truncated);

/// <summary>
/// Reads a spreadsheet by opening the file, rather than by asking Excel to open it.
///
/// A .xlsx is a zip of XML, so the sheets can be read here in a few milliseconds. Driving Excel
/// over COM to export the same thing took the better part of twenty seconds the first time a
/// workbook was previewed, and started an Excel process to do it. Nothing about showing a grid of
/// values needs either.
///
/// The old binary format — .xls — is not a zip archive; ExcelDataReader parses it instead, into
/// the same grid. Only .xlsb still falls back to asking Excel.
/// </summary>
internal static class ExcelWorkbookReader
{
    /// <summary>Enough of a sheet to see what the file is, without pasting a whole database in.</summary>
    private const int MaxRows = 500;
    private const int MaxColumns = 50;

    static ExcelWorkbookReader() =>
        // Pre-Unicode .xls files name their text encoding by Windows codepage number.
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

    public static bool CanRead(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".xlsx" or ".xlsm" or ".xls";

    public static IReadOnlyList<ExcelSheet>? TryRead(string path)
    {
        try
        {
            if (Path.GetExtension(path).ToLowerInvariant() == ".xls")
            {
                return TryReadBinary(path);
            }

            using var archive = ZipFile.OpenRead(path);

            var shared = ReadSharedStrings(archive);
            var dateStyles = ReadDateStyles(archive);
            var sheets = new List<ExcelSheet>();

            foreach (var (name, part) in ReadSheetIndex(archive))
            {
                var entry = archive.GetEntry(part);
                if (entry is null)
                {
                    continue;
                }

                sheets.Add(ReadSheet(entry, name, shared, dateStyles));
            }

            return sheets.Count > 0 ? sheets : null;
        }
        catch
        {
            // A workbook that will not open here is one for Excel to deal with.
            return null;
        }
    }

    private static IReadOnlyList<ExcelSheet>? TryReadBinary(string path)
    {
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = ExcelDataReader.ExcelReaderFactory.CreateBinaryReader(stream);

        var sheets = new List<ExcelSheet>();
        do
        {
            var rows = new List<IReadOnlyList<string>>();
            var truncated = false;
            var widest = 0;

            while (reader.Read())
            {
                if (rows.Count >= MaxRows)
                {
                    truncated = true;
                    break;
                }

                var columns = reader.FieldCount;
                if (columns > MaxColumns)
                {
                    columns = MaxColumns;
                    truncated = true;
                }

                var cells = new List<string>(columns);
                for (var i = 0; i < columns; i++)
                {
                    cells.Add(BinaryCellText(reader, i));
                }

                widest = Math.Max(widest, cells.Count);
                rows.Add(cells);
            }

            foreach (var row in rows.OfType<List<string>>())
            {
                while (row.Count < widest)
                {
                    row.Add(string.Empty);
                }
            }

            sheets.Add(new ExcelSheet(reader.Name, rows, truncated));
        }
        while (reader.NextResult());

        return sheets.Count > 0 ? sheets : null;
    }

    private static string BinaryCellText(ExcelDataReader.IExcelDataReader reader, int index)
    {
        var value = reader.GetValue(index);
        return value switch
        {
            null => string.Empty,
            // The reader already turns date-formatted serials into DateTime where it can; a
            // date-formatted double that slips through is converted the same way the xlsx path
            // converts styled serials.
            DateTime date => date.TimeOfDay == TimeSpan.Zero
                ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                : date.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
            double serial when LooksLikeADate(reader.GetNumberFormatString(index)) => FromSerialDate(serial),
            double number => number.ToString(CultureInfo.InvariantCulture),
            bool flag => flag ? "TRUE" : "FALSE",
            _ => value.ToString() ?? string.Empty,
        };
    }

    private static readonly XNamespace Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace Rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace Package = "http://schemas.openxmlformats.org/package/2006/relationships";

    /// <summary>Sheet names in tab order, paired with the part each one lives in.</summary>
    private static IEnumerable<(string Name, string Part)> ReadSheetIndex(ZipArchive archive)
    {
        var workbook = Load(archive, "xl/workbook.xml");
        var rels = Load(archive, "xl/_rels/workbook.xml.rels");
        if (workbook is null || rels is null)
        {
            yield break;
        }

        var targets = rels.Root!
            .Elements(Package + "Relationship")
            .ToDictionary(r => (string)r.Attribute("Id")!, r => (string)r.Attribute("Target")!);

        foreach (var sheet in workbook.Root!.Element(Main + "sheets")?.Elements(Main + "sheet") ?? [])
        {
            var id = (string?)sheet.Attribute(Rel + "id");
            if (id is null || !targets.TryGetValue(id, out var target))
            {
                continue;
            }

            yield return ((string)sheet.Attribute("name")!, "xl/" + target.TrimStart('/').Replace("xl/", string.Empty));
        }
    }

    private static ExcelSheet ReadSheet(
        ZipArchiveEntry entry,
        string name,
        IReadOnlyList<string> shared,
        IReadOnlySet<int> dateStyles)
    {
        using var stream = entry.Open();
        var sheet = XDocument.Load(stream);

        var rows = new List<IReadOnlyList<string>>();
        var truncated = false;
        var widest = 0;

        foreach (var row in sheet.Root!.Element(Main + "sheetData")?.Elements(Main + "row") ?? [])
        {
            if (rows.Count >= MaxRows)
            {
                truncated = true;
                break;
            }

            var cells = new List<string>();
            foreach (var cell in row.Elements(Main + "c"))
            {
                // Cells carry their own address, and empty ones are simply absent, so the column
                // has to be read rather than counted or the row shifts left around every gap.
                var column = ColumnOf((string?)cell.Attribute("r"));
                if (column >= MaxColumns)
                {
                    truncated = true;
                    continue;
                }

                while (cells.Count <= column)
                {
                    cells.Add(string.Empty);
                }

                cells[column] = ValueOf(cell, shared, dateStyles);
            }

            widest = Math.Max(widest, cells.Count);
            rows.Add(cells);
        }

        // Ragged rows would draw a ragged table, so every row is padded to the widest one.
        foreach (var row in rows.OfType<List<string>>())
        {
            while (row.Count < widest)
            {
                row.Add(string.Empty);
            }
        }

        return new ExcelSheet(name, rows, truncated);
    }

    private static string ValueOf(XElement cell, IReadOnlyList<string> shared, IReadOnlySet<int> dateStyles)
    {
        var type = (string?)cell.Attribute("t");

        if (type == "inlineStr")
        {
            return Text(cell.Element(Main + "is"));
        }

        var raw = cell.Element(Main + "v")?.Value;
        if (string.IsNullOrEmpty(raw))
        {
            return string.Empty;
        }

        switch (type)
        {
            case "s" when int.TryParse(raw, out var index) && index >= 0 && index < shared.Count:
                return shared[index];
            case "b":
                return raw == "1" ? "TRUE" : "FALSE";
            case "e":
                return raw;
        }

        // A date is stored as a number and only its format says otherwise, so without the style
        // the cell would read as a five-digit serial.
        var style = (int?)cell.Attribute("s") ?? -1;
        if (dateStyles.Contains(style) && double.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var serial))
        {
            return FromSerialDate(serial);
        }

        return raw;
    }

    /// <summary>
    /// Spreadsheet day zero is 1899-12-30, not 1900-01-01: the format deliberately reproduces a
    /// 1900 leap year that never happened, and counting from the 30th absorbs it.
    /// </summary>
    private static string FromSerialDate(double serial)
    {
        try
        {
            var date = new DateTime(1899, 12, 30).AddDays(serial);
            return serial % 1 == 0
                ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                : date.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        }
        catch
        {
            return serial.ToString(CultureInfo.InvariantCulture);
        }
    }

    private static IReadOnlyList<string> ReadSharedStrings(ZipArchive archive)
    {
        var document = Load(archive, "xl/sharedStrings.xml");
        return document?.Root?.Elements(Main + "si").Select(Text).ToArray() ?? [];
    }

    /// <summary>
    /// Which cell styles mean "this number is a date". The built-in format numbers are fixed by the
    /// specification; a workbook may also define its own, which are recognised by their pattern.
    /// </summary>
    private static IReadOnlySet<int> ReadDateStyles(ZipArchive archive)
    {
        var styles = new HashSet<int>();
        var document = Load(archive, "xl/styles.xml");
        if (document is null)
        {
            return styles;
        }

        var custom = document.Root!
            .Element(Main + "numFmts")?
            .Elements(Main + "numFmt")
            .Where(f => LooksLikeADate((string?)f.Attribute("formatCode")))
            .Select(f => (int)f.Attribute("numFmtId")!)
            .ToHashSet() ?? [];

        var index = 0;
        foreach (var format in document.Root!.Element(Main + "cellXfs")?.Elements(Main + "xf") ?? [])
        {
            var id = (int?)format.Attribute("numFmtId") ?? 0;
            if ((id >= 14 && id <= 22) || (id >= 45 && id <= 47) || custom.Contains(id))
            {
                styles.Add(index);
            }

            index++;
        }

        return styles;
    }

    private static bool LooksLikeADate(string? formatCode) =>
        formatCode is not null
        && formatCode.Any(c => c is 'y' or 'd' or 'h' or 's')
        && !formatCode.Contains("General", StringComparison.OrdinalIgnoreCase);

    /// <summary>Zero-based column from a cell address: "A1" is 0, "AB7" is 27.</summary>
    internal static int ColumnOf(string? reference)
    {
        if (string.IsNullOrEmpty(reference))
        {
            return 0;
        }

        var column = 0;
        foreach (var c in reference)
        {
            if (c is < 'A' or > 'Z')
            {
                break;
            }

            column = (column * 26) + (c - 'A' + 1);
        }

        return Math.Max(0, column - 1);
    }

    private static string Text(XElement? element) =>
        element is null ? string.Empty : string.Concat(element.Descendants(Main + "t").Select(t => t.Value));

    private static XDocument? Load(ZipArchive archive, string part)
    {
        var entry = archive.GetEntry(part);
        if (entry is null)
        {
            return null;
        }

        using var stream = entry.Open();
        return XDocument.Load(stream);
    }
}
