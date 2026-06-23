using Clip.Core;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using System.Text;

namespace Clip.CommandPalette;

internal sealed partial class ClipHistoryPage : DynamicListPage
{
    private const int Limit = 25;
    private readonly Lazy<ClipboardHistoryStore> _store = new(() => ClipboardHistoryStore.OpenForCommandSurface());
    private readonly ClipHistoryFilters _filters;
    private string? _cachedSearchText;
    private string? _cachedFilterId;
    private DateTime _cachedIndexStampUtc;
    private IListItem[]? _cachedItems;

    public ClipHistoryPage()
    {
        Id = CommandPaletteSettings.ClipHistoryCommandId;
        Icon = new IconInfo("\uE8C8");
        Name = Title = CommandPaletteSettings.ClipHistoryTitle;
        ShowDetails = true;
        _filters = new ClipHistoryFilters();
        _filters.PropChanged += Filters_PropChanged;
        Filters = _filters;
    }

    public IContextItem[] CreateContextCommands() =>
    [
        new CommandContextItem(new ClipSettingsPage())
        {
            Title = "Settings",
            Icon = new IconInfo("\uE713"),
        },
        new CommandContextItem(ClipShellActionCommand.OpenClip())
        {
            Title = "Open Clip.exe",
            Icon = new IconInfo("\uE8A7"),
        },
        new CommandContextItem(ClipShellActionCommand.OpenSettings())
        {
            Title = "Open Clip.exe settings",
            Icon = new IconInfo("\uE713"),
        },
        new CommandContextItem(ClipShellActionCommand.CheckForUpdates())
        {
            Title = "Check for Clip.exe updates",
            Icon = new IconInfo("\uE895"),
        },
        new CommandContextItem(new ClipClearHistoryCommand(_store, includePinned: false, InvalidateItems))
        {
            Title = "Clear unpinned history",
            Icon = new IconInfo("\uE74D"),
        },
        new CommandContextItem(new ClipClearHistoryCommand(_store, includePinned: true, InvalidateItems))
        {
            Title = "Clear all history",
            Icon = new IconInfo("\uE74D"),
        },
    ];

    public override void UpdateSearchText(string oldSearch, string newSearch)
    {
        if (!string.Equals(oldSearch, newSearch, StringComparison.Ordinal))
        {
            _cachedItems = null;
        }

        RaiseItemsChanged();
    }

    public override IListItem[] GetItems()
    {
        try
        {
            var searchText = SearchText ?? string.Empty;
            var filterId = Filters?.CurrentFilterId ?? ClipboardHistoryListFilter.All;
            var store = _store.Value;
            var indexStampUtc = CurrentIndexStampUtc(store, searchText);
            if (_cachedItems is not null &&
                string.Equals(_cachedSearchText, searchText, StringComparison.Ordinal) &&
                string.Equals(_cachedFilterId, filterId, StringComparison.Ordinal) &&
                _cachedIndexStampUtc == indexStampUtc)
            {
                return _cachedItems;
            }

            var result = ClipboardHistoryListCommand.Create(store, searchText, Limit);
            var items = CreateItems(result, store);
            _cachedSearchText = searchText;
            _cachedFilterId = filterId;
            _cachedIndexStampUtc = CurrentIndexStampUtc(store, searchText);
            _cachedItems = items;
            return items;
        }
        catch (Exception ex)
        {
            return
            [
                new ListItem(new NoOpCommand())
                {
                    Title = "Clip history is unavailable",
                    Subtitle = ex.Message,
                },
            ];
        }
    }

    private void InvalidateItems()
    {
        _cachedItems = null;
        RaiseItemsChanged();
    }

    private void Filters_PropChanged(object sender, IPropChangedEventArgs args)
    {
        InvalidateItems();
    }

    private IListItem[] CreateItems(ClipboardHistoryListResult result, ClipboardHistoryStore store)
    {
        var visibleItems = result.Items
            .Where(item => ClipboardHistoryListFilter.Matches(Filters?.CurrentFilterId, item))
            .ToArray();
        var items = new List<IListItem>();

        if (visibleItems.Length == 0)
        {
            items.Add(
                new ListItem(new NoOpCommand())
                {
                    Title = EmptyTitle(result.Items.Count),
                    Subtitle = "Clip will show items here after you copy something.",
                    Section = "History",
                });
            return items.ToArray();
        }

        var pinnedItems = visibleItems.Where(item => item.IsPinned).ToArray();
        var recentItems = visibleItems.Where(item => !item.IsPinned).ToArray();
        if (pinnedItems.Length > 0)
        {
            items.AddRange(pinnedItems.Select(item => CreateListItem(item, store)));
        }

        if (recentItems.Length > 0)
        {
            items.AddRange(recentItems.Select(item => CreateListItem(item, store)));
        }

        return items.ToArray();
    }

    private string EmptyTitle(int unfilteredCount)
    {
        if (unfilteredCount > 0)
        {
            return "No matches for this filter";
        }

        return string.IsNullOrWhiteSpace(SearchText) ? "No clipboard history yet" : "No matches";
    }

    private static DateTime CurrentIndexStampUtc(ClipboardHistoryStore store, string searchText)
    {
        var path = string.IsNullOrWhiteSpace(searchText)
            ? store.HistoryTopIndexFilePath
            : store.HistoryIndexFilePath;

        var indexStamp = LastWriteTimeUtcOrMin(path);
        var historyStamp = LastWriteTimeUtcOrMin(store.HistoryFilePath);
        return indexStamp > historyStamp ? indexStamp : historyStamp;
    }

    private static DateTime LastWriteTimeUtcOrMin(string path)
    {
        try
        {
            return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
        }
        catch
        {
            return DateTime.MinValue;
        }
    }

    private ListItem CreateListItem(ClipboardHistoryListItem item, ClipboardHistoryStore store)
    {
        var listItem = new ListItem(new ClipItemPreviewPage(item, store, InvalidateItems))
        {
            Title = ClipText.TrimForDisplay(item.Title, 96),
            Subtitle = SubtitleFor(item),
            Icon = new IconInfo(IconFor(item.Kind)),
            Section = SectionFor(item),
            Details = CreateDetails(item),
            Tags = CreateTags(item),
        };

        var moreCommands = item.Actions
            .Select(action => CreateContextCommand(action, item, store))
            .ToArray();

        if (moreCommands.Length > 0)
        {
            listItem.MoreCommands = moreCommands;
        }

        return listItem;
    }

    private static string SectionFor(ClipboardHistoryListItem item) =>
        item.IsPinned ? "Pinned Items" : "Recent Items";

    private CommandContextItem CreateContextCommand(ClipboardHistoryListAction action, ClipboardHistoryListItem item, ClipboardHistoryStore store)
    {
        ICommand command = action.Id switch
        {
            "rename" => ClipItemTextFormPage.Rename(item, store, InvalidateItems),
            "edit-text" => ClipItemTextFormPage.EditText(item, store, InvalidateItems),
            "save-as-file" => ClipItemTextFormPage.SaveAsFile(item, store),
            "copy-path" => new ClipCopyPathCommand(item.FilePaths),
            _ => new ClipHistoryActionCommand(action, store, InvalidateItems),
        };

        return new CommandContextItem(command)
        {
            Title = action.Label,
            Icon = new IconInfo(IconForAction(action.Id)),
        };
    }

    private static Details CreateDetails(ClipboardHistoryListItem item)
    {
        var rows = DetailRows(item);
        var metadata = rows.Select(row => new DetailsElement
        {
            Key = row.Key,
            Data = new DetailsLink { Text = row.Value },
        }).ToList();

        return new Details
        {
            Title = ClipText.TrimForDisplay(item.Title, 120),
            Body = DetailsBody(item),
            Metadata = metadata.ToArray(),
        };
    }

    private static IReadOnlyList<DetailRow> DetailRows(ClipboardHistoryListItem item)
    {
        var rows = new List<DetailRow>
        {
            new("Type", item.Kind),
            new("Source", string.IsNullOrWhiteSpace(item.SourceApplication) ? "Unknown" : item.SourceApplication),
            new("Pinned", item.IsPinned ? "Yes" : "No"),
            new("Last used", item.LastUsedAt.LocalDateTime.ToString("g")),
            new("Copied", item.LastCopiedAt.LocalDateTime.ToString("g")),
            new("Times copied", item.CopyCount.ToString("N0")),
        };

        if (item.Kind is "Text" or "Link")
        {
            rows.Add(new DetailRow("Saved format", item.HasOriginalFormatting ? "Plain text + formatting" : "Plain text"));
        }

        if (item.FilePaths.Count > 0)
        {
            rows.Add(new DetailRow(item.FilePaths.Count == 1 ? "File path" : "Files", ClipText.TrimForDisplay(item.FilePaths[0], 120)));
        }

        if (item.CharacterCount is > 0)
        {
            rows.Add(new DetailRow("Characters", item.CharacterCount.Value.ToString("N0")));
        }

        if (item.WordCount is > 0)
        {
            rows.Add(new DetailRow("Words", item.WordCount.Value.ToString("N0")));
        }

        if (item.Kind == "Color")
        {
            rows.Add(new DetailRow("Hex", item.Preview));
        }

        if (item.ImageWidth is > 0 && item.ImageHeight is > 0)
        {
            rows.Add(new DetailRow("Dimensions", $"{item.ImageWidth} x {item.ImageHeight}"));
        }

        if (item.AssetSizeBytes is > 0)
        {
            rows.Add(new DetailRow(item.Kind == "Image" ? "Image size" : "Size", FormatBytes(item.AssetSizeBytes.Value)));
        }

        return rows;
    }

    private static string DetailsBody(ClipboardHistoryListItem item)
    {
        var body = new StringBuilder();
        body.AppendLine("## Preview");
        body.AppendLine();
        body.AppendLine(IsPreviewableImage(item) ? ImageMarkdown(item.AssetPath!) : PreviewText(item));
        body.AppendLine();
        body.AppendLine("## Information");
        body.AppendLine();
        body.AppendLine("| Field | Value |");
        body.AppendLine("| --- | --- |");
        foreach (var row in DetailRows(item))
        {
            body.AppendLine($"| {EscapeMarkdownCell(row.Key)} | {EscapeMarkdownCell(row.Value)} |");
        }

        return body.ToString().Trim();
    }

    private static bool IsPreviewableImage(ClipboardHistoryListItem item) =>
        item.Kind == "Image" &&
        !string.IsNullOrWhiteSpace(item.AssetPath) &&
        File.Exists(item.AssetPath);

    private static string ImageMarkdown(string path)
    {
        var uri = new Uri(path).AbsoluteUri;
        return $"![Clipboard image]({uri}?--x-cmdpal-fit=fit&--x-cmdpal-maxwidth=520&--x-cmdpal-maxheight=360)";
    }

    private static string PreviewText(ClipboardHistoryListItem item)
    {
        if (item.FilePaths.Count > 0)
        {
            return string.Join(Environment.NewLine, item.FilePaths.Select(path => ClipText.TrimForDisplay(path, 160)));
        }

        var preview = ClipText.TrimForDisplay(item.Preview, 2_000);
        return string.IsNullOrWhiteSpace(preview) ? "(No preview available)" : preview;
    }

    private static string EscapeMarkdownCell(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("|", "\\|", StringComparison.Ordinal)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal);

    private static ITag[] CreateTags(ClipboardHistoryListItem item)
    {
        var tags = new List<ITag> { new Tag(item.Kind) };
        if (item.IsPinned)
        {
            tags.Add(new Tag("Pinned"));
        }

        if (item.HasOriginalFormatting)
        {
            tags.Add(new Tag("Formatted"));
        }

        return tags.ToArray();
    }

    private static string SubtitleFor(ClipboardHistoryListItem item)
    {
        if (item.FilePaths.Count > 0)
        {
            return item.FilePaths.Count == 1
                ? ClipText.TrimForDisplay(item.FilePaths[0], 120)
                : $"{item.FilePaths.Count} files";
        }

        if (!string.IsNullOrWhiteSpace(item.SourceApplication))
        {
            return item.SourceApplication;
        }

        return ClipText.TrimForDisplay(item.Preview, 120);
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024)
        {
            return $"{bytes:N0} B";
        }

        if (bytes < 1024 * 1024)
        {
            return $"{bytes / 1024d:0.#} KB";
        }

        return $"{bytes / 1024d / 1024d:0.#} MB";
    }

    private static string IconFor(string kind) => kind switch
    {
        "Text" => "\uE8D2",
        "Link" => "\uE71B",
        "Files" => "\uE8B7",
        "Image" => "\uEB9F",
        "Color" => "\uE790",
        _ => "\uE8C8",
    };

    private static string IconForAction(string actionId) => actionId switch
    {
        "copy" => "\uE8C8",
        "rename" => "\uE8AC",
        "edit-text" => "\uE70F",
        "pin" => "\uE718",
        "unpin" => "\uE77A",
        "move-pin-up" => "\uE70E",
        "move-pin-down" => "\uE70D",
        "delete" => "\uE74D",
        "save-as-file" => "\uE792",
        "copy-path" => "\uE8C8",
        "open" => "\uE8A7",
        "reveal" => "\uEC50",
        _ => "\uE8A7",
    };

    private readonly record struct DetailRow(string Key, string Value);
}
