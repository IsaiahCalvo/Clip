using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;

namespace Clip.Shell;

/// <summary>
/// Draws a workbook as a workbook: a grid of cells with a tab along the bottom for each sheet,
/// the way a spreadsheet is expected to look.
///
/// Showing the exported PDF instead turned every sheet into a page of print output, chopped at the
/// paper margins and in whatever order the print settings happened to give — which is not how
/// anybody reads a spreadsheet.
/// </summary>
internal static class ExcelPreviewPage
{
    public static string Build(
        IReadOnlyList<ExcelSheet> sheets,
        string fileName,
        string backgroundHex,
        string textHex)
    {
        var tabs = new StringBuilder();
        var panes = new StringBuilder();

        for (var i = 0; i < sheets.Count; i++)
        {
            var sheet = sheets[i];
            var active = i == 0 ? " active" : string.Empty;

            tabs.Append($"""<button class="tab{active}" data-sheet="{i}">{Escape(sheet.Name)}</button>""");
            panes.Append($"""<div class="pane{active}" data-sheet="{i}">{Table(sheet)}</div>""");
        }

        return $$"""
            <!doctype html>
            <html>
            <head>
            <meta charset="utf-8">
            <style>
              html, body {
                margin: 0;
                height: 100%;
                background: {{backgroundHex}};
                color: {{textHex}};
                font-family: "Segoe UI Variable Text", "Segoe UI", sans-serif;
                font-size: 12px;
                overflow: hidden;
              }
              .book { display: flex; flex-direction: column; height: 100%; }

              /* The grid scrolls, the tabs do not: the tab strip is how you change sheet, so it has
                 to stay put however far down a long sheet you have gone. */
              .sheets { flex: 1; overflow: auto; }
              .pane { display: none; }
              .pane.active { display: block; }

              table { border-collapse: collapse; white-space: nowrap; }
              td, th {
                border: 1px solid rgba(128,128,128,.28);
                padding: 2px 7px;
                max-width: 320px;
                overflow: hidden;
                text-overflow: ellipsis;
              }
              /* Row and column headers stay in view, or a scrolled grid is unreadable. */
              th {
                background: rgba(128,128,128,.16);
                font-weight: 600;
                position: sticky;
                top: 0;
                z-index: 2;
              }
              th.row {
                left: 0;
                z-index: 1;
                text-align: right;
                font-variant-numeric: tabular-nums;
              }
              th.corner { left: 0; z-index: 3; }
              td { font-variant-numeric: tabular-nums; }

              .strip {
                display: flex;
                gap: 2px;
                padding: 5px 8px;
                overflow-x: auto;
                border-top: 1px solid rgba(128,128,128,.3);
                background: rgba(128,128,128,.1);
                flex: none;
              }
              .tab {
                background: transparent;
                color: inherit;
                border: 1px solid transparent;
                border-radius: 5px 5px 0 0;
                padding: 4px 12px;
                font: inherit;
                cursor: pointer;
                white-space: nowrap;
                opacity: .75;
              }
              .tab:hover { background: rgba(128,128,128,.18); }
              .tab.active {
                opacity: 1;
                font-weight: 600;
                background: rgba(128,128,128,.26);
                border-color: rgba(128,128,128,.4);
              }
              .more { padding: 4px 8px; opacity: .6; font-style: italic; }
            </style>
            </head>
            <body>
              <div class="book">
                <div class="sheets">{{panes}}</div>
                <div class="strip">{{tabs}}</div>
              </div>
              <script>
                document.querySelectorAll('.tab').forEach(tab => {
                  tab.addEventListener('click', () => {
                    const wanted = tab.dataset.sheet;
                    document.querySelectorAll('.tab').forEach(t => t.classList.toggle('active', t === tab));
                    document.querySelectorAll('.pane').forEach(p =>
                      p.classList.toggle('active', p.dataset.sheet === wanted));
                    document.querySelector('.sheets').scrollTo(0, 0);
                  });
                });
              </script>
            </body>
            </html>
            """;
    }

    private static string Table(ExcelSheet sheet)
    {
        if (sheet.Rows.Count == 0)
        {
            return """<div class="more">This sheet is empty.</div>""";
        }

        var columns = sheet.Rows.Max(r => r.Count);
        var html = new StringBuilder("<table><tr><th class=\"corner\"></th>");

        for (var c = 0; c < columns; c++)
        {
            html.Append($"<th>{ColumnName(c)}</th>");
        }

        html.Append("</tr>");

        for (var r = 0; r < sheet.Rows.Count; r++)
        {
            html.Append($"<tr><th class=\"row\">{r + 1}</th>");
            for (var c = 0; c < columns; c++)
            {
                html.Append($"<td>{Escape(c < sheet.Rows[r].Count ? sheet.Rows[r][c] : string.Empty)}</td>");
            }

            html.Append("</tr>");
        }

        html.Append("</table>");

        if (sheet.Truncated)
        {
            html.Append("""<div class="more">Showing the first part of this sheet. Open it to see the rest.</div>""");
        }

        return html.ToString();
    }

    /// <summary>Spreadsheet column names: 0 is A, 26 is AA.</summary>
    internal static string ColumnName(int index)
    {
        var name = string.Empty;
        for (var i = index; i >= 0; i = (i / 26) - 1)
        {
            name = (char)('A' + (i % 26)) + name;
        }

        return name;
    }

    private static string Escape(string value) => WebUtility.HtmlEncode(value);
}
