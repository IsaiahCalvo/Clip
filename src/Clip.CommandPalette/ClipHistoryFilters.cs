using Clip.Core;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace Clip.CommandPalette;

internal sealed partial class ClipHistoryFilters : Filters
{
    public ClipHistoryFilters()
    {
        CurrentFilterId = ClipboardHistoryListFilter.All;
    }

    public override IFilterItem[] GetFilters() =>
    [
        new Filter() { Id = ClipboardHistoryListFilter.All, Name = "All" },
        new Filter() { Id = ClipboardHistoryListFilter.Pinned, Name = "Pinned", Icon = new IconInfo("\uE718") },
        new Filter() { Id = ClipboardHistoryListFilter.Text, Name = "Text", Icon = new IconInfo("\uE8D2") },
        new Filter() { Id = ClipboardHistoryListFilter.Links, Name = "Links", Icon = new IconInfo("\uE71B") },
        new Filter() { Id = ClipboardHistoryListFilter.Files, Name = "Files", Icon = new IconInfo("\uE8B7") },
        new Filter() { Id = ClipboardHistoryListFilter.Images, Name = "Images", Icon = new IconInfo("\uEB9F") },
        new Filter() { Id = ClipboardHistoryListFilter.Colors, Name = "Colors", Icon = new IconInfo("\uE790") },
    ];
}
