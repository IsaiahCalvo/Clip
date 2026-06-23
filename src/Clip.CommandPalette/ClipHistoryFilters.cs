using Clip.Core;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace Clip.CommandPalette;

internal sealed partial class ClipHistoryFilters : Filters
{
    // File sub-kinds present in the current result set, supplied by ClipHistoryPage after each
    // query. The list is rebuilt from whatever Files items are visible, mirroring the standalone
    // app's File pill whose dropdown is built dynamically from the kinds present in history.
    private IReadOnlyList<string> _fileKinds = [];

    public ClipHistoryFilters()
    {
        CurrentFilterId = ClipboardHistoryListFilter.All;
    }

    /// <summary>
    /// Replaces the dynamic file sub-kind entries with the kinds discovered in the latest
    /// result set. When the active filter is a now-absent file sub-kind it falls back to the
    /// Files filter so the dropdown selection stays valid. Returns true when the offered
    /// filters changed so the caller can raise an items-changed notification.
    /// </summary>
    public bool SetFileKinds(IReadOnlyList<string> fileKinds)
    {
        var next = fileKinds ?? [];
        if (_fileKinds.SequenceEqual(next, StringComparer.Ordinal))
        {
            return false;
        }

        _fileKinds = next;

        // If the current selection is a file sub-kind that no longer exists, drop back to Files.
        if (ClipboardHistoryFileKindFilter.IsFileKindFilter(CurrentFilterId) &&
            !string.Equals(CurrentFilterId, ClipboardHistoryFileKindFilter.All, StringComparison.Ordinal) &&
            !next.Any(kind => string.Equals(ClipboardHistoryFileKindFilter.FilterIdFor(kind), CurrentFilterId, StringComparison.Ordinal)))
        {
            CurrentFilterId = ClipboardHistoryListFilter.Files;
        }

        return true;
    }

    // Command Palette surfaces exactly one Filters dropdown per list page (IListPage exposes a
    // single IFilters with a single CurrentFilterId). The SDK has no second-dropdown affordance,
    // so kind/pinned, date-recency, and (when Files is active) file sub-kind entries all share
    // this one dropdown, visually grouped with Separators. A single selection therefore means a
    // kind/pinned filter OR a date-recency window OR a file sub-kind, mirroring the standalone
    // app where the date dropdown hangs off "All" and the sub-kind dropdown hangs off "Files".
    public override IFilterItem[] GetFilters()
    {
        var items = new List<IFilterItem>
        {
            new Filter() { Id = ClipboardHistoryListFilter.All, Name = "All" },
            new Filter() { Id = ClipboardHistoryListFilter.Pinned, Name = "Pinned", Icon = new IconInfo("\uE718") },
            new Filter() { Id = ClipboardHistoryListFilter.Text, Name = "Text", Icon = new IconInfo("\uE8D2") },
            new Filter() { Id = ClipboardHistoryListFilter.Links, Name = "Links", Icon = new IconInfo("\uE71B") },
            new Filter() { Id = ClipboardHistoryListFilter.Files, Name = "Files", Icon = new IconInfo("\uE8B7") },
            new Filter() { Id = ClipboardHistoryListFilter.Images, Name = "Images", Icon = new IconInfo("\uEB9F") },
            new Filter() { Id = ClipboardHistoryListFilter.Colors, Name = "Colors", Icon = new IconInfo("\uE790") },
        };

        // File sub-kind entries appear only when the Files filter (or a file sub-kind) is the
        // active selection, matching the standalone where the sub-kind dropdown is scoped to the
        // File pill. They are built dynamically from the kinds present in the current results.
        var filesActive =
            string.Equals(CurrentFilterId, ClipboardHistoryListFilter.Files, StringComparison.Ordinal) ||
            ClipboardHistoryFileKindFilter.IsFileKindFilter(CurrentFilterId);
        if (filesActive && _fileKinds.Count > 0)
        {
            items.Add(new Separator());
            items.Add(new Filter()
            {
                Id = ClipboardHistoryFileKindFilter.All,
                Name = "All files",
                Icon = new IconInfo("\uE8B7"),
            });
            foreach (var kind in _fileKinds)
            {
                items.Add(new Filter()
                {
                    Id = ClipboardHistoryFileKindFilter.FilterIdFor(kind),
                    Name = ClipboardHistoryFileKindFilter.LabelFor(kind),
                    Icon = new IconInfo(IconForFileKind(kind)),
                });
            }
        }

        items.Add(new Separator());
        items.Add(new Filter() { Id = ClipboardHistoryDateFilter.Today, Name = "Today", Icon = new IconInfo("\uE787") });
        items.Add(new Filter() { Id = ClipboardHistoryDateFilter.Yesterday, Name = "Yesterday", Icon = new IconInfo("\uE787") });
        items.Add(new Filter() { Id = ClipboardHistoryDateFilter.Week, Name = "This week", Icon = new IconInfo("\uE787") });
        items.Add(new Filter() { Id = ClipboardHistoryDateFilter.Month, Name = "This month", Icon = new IconInfo("\uE787") });
        items.Add(new Filter() { Id = ClipboardHistoryDateFilter.Year, Name = "This year", Icon = new IconInfo("\uE787") });
        items.Add(new Filter() { Id = ClipboardHistoryDateFilter.Older, Name = "Older", Icon = new IconInfo("\uEC92") });

        return items.ToArray();
    }

    // Segoe MDL2 Assets glyphs roughly matching each file kind; falls back to the generic
    // document glyph for discovered/unknown extensions.
    private static string IconForFileKind(string kind) => kind switch
    {
        ClipboardHistoryFileKindFilter.Folder => "\uE8B7",
        ClipboardHistoryFileKindFilter.Pdf => "\uEA90",
        ClipboardHistoryFileKindFilter.Word => "\uE8A5",
        ClipboardHistoryFileKindFilter.Excel => "\uE9F9",
        ClipboardHistoryFileKindFilter.PowerPoint => "\uE8FD",
        ClipboardHistoryFileKindFilter.Visio => "\uE80A",
        ClipboardHistoryFileKindFilter.Html => "\uE12B",
        ClipboardHistoryFileKindFilter.Image => "\uEB9F",
        ClipboardHistoryFileKindFilter.Text => "\uE8D2",
        _ => "\uE8A5",
    };
}