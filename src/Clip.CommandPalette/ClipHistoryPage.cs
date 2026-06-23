using Clip.Core;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using System.Text;

namespace Clip.CommandPalette;

internal sealed partial class ClipHistoryPage : DynamicListPage, IDisposable
{
    // Cold-open paints only the first page so the list appears instantly; the page grows by
    // PageIncrement as the user scrolls (LoadMore) or keeps searching, up to the user's
    // configured HistoryLimit. The query stays native against the Core index and never loads
    // item bodies, so a larger page is cheap.
    private const int InitialPageSize = 25;
    private const int PageIncrement = 25;
    private readonly Lazy<ClipboardHistoryStore> _store = new(() => ClipboardHistoryStore.OpenForCommandSurface());
    private readonly ClipHistoryFilters _filters;
    private string? _cachedSearchText;
    private string? _cachedFilterId;
    private DateTime _cachedIndexStampUtc;
    private int _cachedPageSize;
    private IListItem[]? _cachedItems;
    private int _pageSize = InitialPageSize;
    private ClipHistoryWatcher? _watcher;
    // Last store stamp the watcher acted on, so background noise (asset cleanup, swatch writes)
    // that doesn't move the index stamp causes no visible list churn or selection loss while the
    // user is reading.
    private DateTime _watcherSeenStampUtc = DateTime.MinValue;

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
        new CommandContextItem(new ClipImportWindowsHistoryCommand(InvalidateItems))
        {
            Title = "Import Windows clipboard history",
            Icon = new IconInfo("\uE896"),
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
            // A new query starts cold: shrink back to the first page so search stays fast and
            // the user pages into the (now re-filtered) results from the top.
            _pageSize = InitialPageSize;
            _cachedItems = null;
        }

        RaiseItemsChanged();
    }

    public override void LoadMore()
    {
        // Grow the requested page toward the user's HistoryLimit ceiling. If we are already at
        // the ceiling, there is nothing more to load.
        var ceiling = ClipboardHistoryListCommand.ResolveLimit(EffectiveHistoryLimit(), int.MaxValue);
        if (_pageSize >= ceiling)
        {
            HasMoreItems = false;
            return;
        }

        _pageSize = Math.Min(ceiling, _pageSize + PageIncrement);
        _cachedItems = null;
        RaiseItemsChanged();
    }

    public override IListItem[] GetItems()
    {
        try
        {
            var searchText = SearchText ?? string.Empty;
            var filterId = Filters?.CurrentFilterId ?? ClipboardHistoryListFilter.All;
            var store = _store.Value;
            // Lazily start live updates only once the user actually opens the page, so the extension
            // process stays idle until then and cold-open is never burdened by watcher setup.
            EnsureWatcher(store);
            var indexStampUtc = CurrentIndexStampUtc(store, searchText);
            var limit = ClipboardHistoryListCommand.ResolveLimit(EffectiveHistoryLimit(), _pageSize);
            if (_cachedItems is not null &&
                string.Equals(_cachedSearchText, searchText, StringComparison.Ordinal) &&
                string.Equals(_cachedFilterId, filterId, StringComparison.Ordinal) &&
                _cachedPageSize == limit &&
                _cachedIndexStampUtc == indexStampUtc)
            {
                return _cachedItems;
            }

            var result = ClipboardHistoryListCommand.Create(store, searchText, limit);

            // There may be more history beyond this page only when the store returned a full
            // page (Count == limit) and we have not yet hit the user's HistoryLimit ceiling.
            var ceiling = ClipboardHistoryListCommand.ResolveLimit(EffectiveHistoryLimit(), int.MaxValue);
            HasMoreItems = result.Count >= limit && limit < ceiling;

            var items = CreateItems(result, store);
            _cachedSearchText = searchText;
            _cachedFilterId = filterId;
            _cachedPageSize = limit;
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
        // Mutations (pin/delete/clear/rename) and filter changes re-query from the first page so
        // the refresh stays fast; the user can page back down with the scroll gesture.
        _pageSize = InitialPageSize;
        _cachedItems = null;
        RaiseItemsChanged();
    }

    // Creates the single FileSystemWatcher on first page query (when the store is materialized so
    // its content root + file paths are known). The watcher pushes store changes back as a
    // stamp-gated invalidation so newly-copied items appear live. If the content root did not exist
    // yet (first run before any capture), Rearm() re-attempts on subsequent queries.
    private void EnsureWatcher(ClipboardHistoryStore store)
    {
        if (_watcher is null)
        {
            _watcher = new ClipHistoryWatcher(
                store.ContentRootPath,
                [store.HistoryFilePath, store.HistoryIndexFilePath, store.HistoryTopIndexFilePath],
                InvalidateIfStoreChanged);
        }
        else
        {
            _watcher.Rearm();
        }
    }

    // The watcher-driven refresh path. Unlike InvalidateItems (which hard-refreshes for explicit
    // mutations), this only re-queries when the store's index/history stamp actually moved, so
    // background writes that don't change visible history don't churn the list or drop the user's
    // selection while they read. Uses the empty search text stamp (the top-index fast path) as the
    // liveness signal; the next GetItems re-checks the real per-query stamp anyway.
    private void InvalidateIfStoreChanged()
    {
        try
        {
            var stamp = CurrentIndexStampUtc(_store.Value, string.Empty);
            if (stamp == _watcherSeenStampUtc)
            {
                return;
            }

            _watcherSeenStampUtc = stamp;
        }
        catch
        {
            // If the stamp can't be read (mid-write), fall through and let GetItems' own cache
            // guard settle it on the next query.
        }

        InvalidateItems();
    }

    // Reads the user's configured history limit (the F1 ClipSharedSettings HistoryLimit; null =
    // Unlimited) as the ceiling for incremental loading. The settings file is tiny and this is
    // only consulted on GetItems/LoadMore, never on the per-item render path. Cached by the
    // settings-file mtime so repeated paging does not re-read JSON.
    private int? _cachedHistoryLimit = ClipSharedSettings.DefaultHistoryLimit;
    private DateTime _cachedSettingsStampUtc = DateTime.MinValue;

    private int? EffectiveHistoryLimit()
    {
        var stamp = LastWriteTimeUtcOrMin(ClipStoragePaths.SettingsPath);
        if (stamp != _cachedSettingsStampUtc)
        {
            _cachedHistoryLimit = ClipSharedSettings.Load().HistoryLimit;
            _cachedSettingsStampUtc = stamp;
        }

        return _cachedHistoryLimit;
    }

    private void Filters_PropChanged(object sender, IPropChangedEventArgs args)
    {
        InvalidateItems();
    }

    private IListItem[] CreateItems(ClipboardHistoryListResult result, ClipboardHistoryStore store)
    {
        // Refresh the dynamic file sub-kind entries from the Files items in this result set so
        // the dropdown only offers kinds actually present, mirroring the standalone File pill.
        var fileItems = result.Items
            .Where(item => string.Equals(item.Kind, "Files", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        _filters.SetFileKinds(ClipboardHistoryFileKindFilter.DiscoverKinds(fileItems));

        // The single shared dropdown carries one selection: a kind/pinned id, a date-recency id,
        // or (when Files is active) a file sub-kind id. Every predicate short-circuits to true for
        // the other dimensions' ids, so applying them together yields exactly the selected filter
        // without needing multiple dropdowns.
        var visibleItems = result.Items
            .Where(item => ClipboardHistoryListFilter.Matches(Filters?.CurrentFilterId, item)
                && ClipboardHistoryDateFilter.Matches(Filters?.CurrentFilterId, item)
                && ClipboardHistoryFileKindFilter.Matches(Filters?.CurrentFilterId, item))
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
            // Group non-pinned items into time buckets (Today / Yesterday / This week / ...)
            // and emit sections most-recent first, mirroring the standalone app.
            var today = DateTime.Today;
            var byBucket = recentItems
                .GroupBy(item => ClipboardHistoryTimeBucket.KeyFor(item, today))
                .ToDictionary(group => group.Key, group => group.ToArray());

            foreach (var bucketKey in ClipboardHistoryTimeBucket.OrderedKeys)
            {
                if (byBucket.TryGetValue(bucketKey, out var bucketItems))
                {
                    items.AddRange(bucketItems.Select(item => CreateListItem(item, store)));
                }
            }
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
        // Primary command (Enter) is Paste, mirroring the standalone app. When the item
        // cannot be pasted (no paste action), fall back to opening the Preview page.
        var pasteAction = item.Actions.FirstOrDefault(action =>
            string.Equals(action.Id, "paste", StringComparison.Ordinal));
        var previewPage = new ClipItemPreviewPage(item, store, InvalidateItems);
        ICommand primaryCommand = pasteAction is not null
            ? new ClipHistoryActionCommand(pasteAction, store, InvalidateItems)
            : previewPage;

        var listItem = new ListItem(primaryCommand)
        {
            Title = ClipText.TrimForDisplay(item.Title, 96),
            Subtitle = SubtitleFor(item),
            Icon = new IconInfo(IconFor(item.Kind)),
            Section = SectionFor(item),
            Details = CreateDetails(item),
            Tags = CreateTags(item),
        };

        var moreCommands = new List<IContextItem>();

        // Demote the full-screen Preview page to a secondary command. Skip it when it is
        // already the primary command (i.e. no paste action available).
        if (pasteAction is not null)
        {
            moreCommands.Add(new CommandContextItem(previewPage)
            {
                Title = "Preview",
                Icon = new IconInfo(""),
            });
        }

        // Keep every item action (paste-plain, copy, open, reveal, pin, etc.) as a context
        // command. The paste action that became the primary command is excluded to avoid a
        // duplicate entry.
        moreCommands.AddRange(item.Actions
            .Where(action => !ReferenceEquals(action, pasteAction))
            .Select(action => CreateContextCommand(action, item, store)));

        if (moreCommands.Count > 0)
        {
            listItem.MoreCommands = moreCommands.ToArray();
        }

        return listItem;
    }

    private static string SectionFor(ClipboardHistoryListItem item) =>
        item.IsPinned ? "Pinned Items" : ClipboardHistoryTimeBucket.LabelFor(item);

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
            var paths = string.Join(Environment.NewLine, item.FilePaths.Select(path => ClipText.TrimForDisplay(path, 160)));
            if (FilePreview.TryReadTextExcerpt(item.FilePaths, 2_000, out var excerpt))
            {
                // Show the path plus a fenced excerpt of the file's contents (markdown body).
                return $"{paths}{Environment.NewLine}{Environment.NewLine}```{Environment.NewLine}{excerpt}{Environment.NewLine}```";
            }

            return paths;
        }

        var preview = ClipText.TrimForDisplay(item.Preview, 2_000);
        return string.IsNullOrWhiteSpace(preview) ? "(No preview available)" : preview;
    }

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

    public void Dispose()
    {
        // FileSystemWatcher holds an OS handle. The SDK does not guarantee a Dispose call on the
        // page (it is a long-lived COM singleton), but disposing here is correct hygiene and lets
        // shutdown chain-dispose the watcher.
        _watcher?.Dispose();
        _watcher = null;
    }

    private readonly record struct DetailRow(string Key, string Value);
}
