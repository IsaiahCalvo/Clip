namespace Clip.Tests;

public sealed class CommandPaletteHistoryPageSourceTests
{
    [Fact]
    public void HistoryActionCommandUsesCoreExecutorAndRefreshCallback()
    {
        var command = File.ReadAllText(RepoPath("src", "Clip.CommandPalette", "ClipHistoryActionCommand.cs"));
        var page = File.ReadAllText(RepoPath("src", "Clip.CommandPalette", "ClipHistoryPage.cs"));

        Assert.Contains("ClipboardHistoryActionExecutor.Execute", command);
        Assert.Contains("_afterHistoryMutation", command);
        Assert.Contains("InvalidateItems", page);
        Assert.Contains("new ClipHistoryActionCommand(action, store, InvalidateItems)", page);
    }

    [Fact]
    public void HistoryActionCommandRunsManagementActionsBeforeCopyOpenRevealHydration()
    {
        var command = File.ReadAllText(RepoPath("src", "Clip.CommandPalette", "ClipHistoryActionCommand.cs"));

        Assert.True(
            command.IndexOf("ClipboardHistoryActionExecutor.Execute", StringComparison.Ordinal) <
            command.IndexOf("command.Equals(\"copy\"", StringComparison.Ordinal));
    }

    [Fact]
    public void HistoryPageDefinesNativeFiltersAndAppliesSelectedFilter()
    {
        var page = File.ReadAllText(RepoPath("src", "Clip.CommandPalette", "ClipHistoryPage.cs"));
        var filters = File.ReadAllText(RepoPath("src", "Clip.CommandPalette", "ClipHistoryFilters.cs"));

        Assert.Contains("filters.PropChanged +=", page);
        Assert.Contains("Filters = _filters", page);
        Assert.Contains("ClipboardHistoryListFilter.Matches(Filters?.CurrentFilterId, item)", page);
        Assert.Contains("internal sealed partial class ClipHistoryFilters : Filters", filters);
        Assert.Contains("new Filter() { Id = ClipboardHistoryListFilter.Pinned", filters);
    }

    [Fact]
    public void HistoryFiltersIntegrateDateEntriesIntoTheSingleNativeDropdown()
    {
        var page = File.ReadAllText(RepoPath("src", "Clip.CommandPalette", "ClipHistoryPage.cs"));
        var filters = File.ReadAllText(RepoPath("src", "Clip.CommandPalette", "ClipHistoryFilters.cs"));

        // Command Palette exposes one Filters dropdown, so date entries are grouped into it with a
        // Separator rather than introducing a second dropdown.
        Assert.Contains("new Separator()", filters);
        Assert.Contains("Id = ClipboardHistoryDateFilter.Today", filters);
        Assert.Contains("Id = ClipboardHistoryDateFilter.Week", filters);
        Assert.Contains("Id = ClipboardHistoryDateFilter.Older", filters);

        // The date predicate is applied alongside the kind predicate in CreateItems.
        Assert.Contains("ClipboardHistoryDateFilter.Matches(Filters?.CurrentFilterId, item)", page);
    }

    [Fact]
    public void HistoryPageExposesQuickActionsWithoutAddingRootNoise()
    {
        var page = File.ReadAllText(RepoPath("src", "Clip.CommandPalette", "ClipHistoryPage.cs"));
        var provider = File.ReadAllText(RepoPath("src", "Clip.CommandPalette", "ClipCommandsProvider.cs"));
        var clear = File.ReadAllText(RepoPath("src", "Clip.CommandPalette", "ClipClearHistoryCommand.cs"));
        var shell = File.ReadAllText(RepoPath("src", "Clip.CommandPalette", "ClipShellActionCommand.cs"));

        Assert.Contains("MoreCommands = historyPage.CreateContextCommands()", provider);
        Assert.Contains("new ClipSettingsPage()", page);
        Assert.Contains("new ClipClearHistoryCommand(_store, includePinned: false, InvalidateItems)", page);
        Assert.Contains("new ClipClearHistoryCommand(_store, includePinned: true, InvalidateItems)", page);
        Assert.Contains("ClearHistory(includePinned)", clear);
        Assert.Contains("--tray-action=settings", shell);
        Assert.Contains("--tray-action=check-updates", shell);
        Assert.DoesNotContain("new CommandItem(new ClipSettingsPage())", provider);
    }

    [Fact]
    public void HistoryCommandUsesDistinctClipNameInsteadOfBuiltinClipboardHistoryName()
    {
        var settings = File.ReadAllText(RepoPath("src", "Clip.Core", "CommandPaletteSettings.cs"));
        var provider = File.ReadAllText(RepoPath("src", "Clip.CommandPalette", "ClipCommandsProvider.cs"));

        Assert.Contains("ClipHistoryTitle = \"Clip History\"", settings);
        Assert.Contains("Subtitle = \"Search Clip clipboard history\"", provider);
    }

    [Fact]
    public void SettingsPageUsesSharedClipSettings()
    {
        var settings = File.ReadAllText(RepoPath("src", "Clip.CommandPalette", "ClipSettingsPage.cs"));

        Assert.Contains("internal sealed partial class ClipSettingsPage : ContentPage", settings);
        Assert.Contains("\"openMode\"", settings);
        Assert.Contains("ClipSharedSettings.SetOpenMode", settings);
        Assert.Contains("CommandPaletteSettings.ConfigureClipHistoryHotkey", settings);
        Assert.Contains("\"appIcon\"", settings);
        Assert.Contains("ClipSharedSettings.SetAppIcon", settings);
        Assert.Contains("\"checkForUpdatesOnStartup\"", settings);
        Assert.Contains("ClipSharedSettings.SetCheckForUpdatesOnStartup", settings);
        Assert.Contains("_settings.ToContent()", settings);
    }

    [Fact]
    public void HistoryDetailsRenderWpfMetadataRows()
    {
        var page = File.ReadAllText(RepoPath("src", "Clip.CommandPalette", "ClipHistoryPage.cs"));

        Assert.Contains("new(\"Source\"", page);
        Assert.Contains("new DetailRow(\"Saved format\"", page);
        Assert.Contains("new(\"Copied\"", page);
        Assert.Contains("new(\"Times copied\"", page);
        Assert.Contains("new DetailRow(\"Words\"", page);
        Assert.Contains("new DetailRow(\"Hex\"", page);
        Assert.Contains("new DetailRow(\"Dimensions\"", page);
        Assert.Contains("\"File path\"", page);
    }

    [Fact]
    public void HistoryPageMapsFormActionsToNativeCommandPalettePages()
    {
        var page = File.ReadAllText(RepoPath("src", "Clip.CommandPalette", "ClipHistoryPage.cs"));
        var form = File.ReadAllText(RepoPath("src", "Clip.CommandPalette", "ClipItemTextFormPage.cs"));
        var copyPath = File.ReadAllText(RepoPath("src", "Clip.CommandPalette", "ClipCopyPathCommand.cs"));

        Assert.Contains("CreateContextCommand(action, item, store)", page);
        Assert.Contains("ClipItemTextFormPage.Rename", page);
        Assert.Contains("ClipItemTextFormPage.EditText", page);
        Assert.Contains("ClipItemTextFormPage.SaveAsFile", page);
        Assert.Contains("new ClipCopyPathCommand", page);
        Assert.Contains("internal sealed partial class ClipItemTextFormPage : ContentPage", form);
        Assert.Contains("TextSetting(ValueKey", form);
        Assert.Contains("Multiline = multiline", form);
        Assert.Contains("_settings.SettingsChanged +=", form);
        Assert.Contains("_settings.ToContent()", form);
        Assert.Contains("store.Rename", form);
        Assert.Contains("store.EditText", form);
        Assert.Contains("store.SaveAsFile", form);
        Assert.Contains("Clipboard.SetContent", copyPath);
    }

    [Fact]
    public void HistoryItemsOpenNativePreviewPageByDefault()
    {
        var page = File.ReadAllText(RepoPath("src", "Clip.CommandPalette", "ClipHistoryPage.cs"));
        var preview = File.ReadAllText(RepoPath("src", "Clip.CommandPalette", "ClipItemPreviewPage.cs"));
        var card = File.ReadAllText(RepoPath("src", "Clip.CommandPalette", "ClipItemPreviewCard.cs"));
        var imagePreview = File.ReadAllText(RepoPath("src", "Clip.CommandPalette", "ClipImagePreviewContent.cs"));

        Assert.Contains("new ClipItemPreviewPage(item, store, InvalidateItems)", page);
        Assert.DoesNotContain("ICommand command = defaultAction", page);
        Assert.Contains("internal sealed partial class ClipItemPreviewPage : ContentPage", preview);
        Assert.Contains("new ClipItemPreviewCard(fullItem, _item, _store, _afterHistoryMutation)", preview);
        Assert.Contains("ClipImagePreviewContent.TryCreate(fullItem, out var imagePreview)", preview);
        Assert.Contains("internal sealed partial class ClipItemPreviewCard : FormContent", card);
        Assert.Contains("internal sealed partial class ClipImagePreviewContent : MarkdownContent", imagePreview);
        Assert.Contains("\"type\": \"AdaptiveCard\"", card);
        Assert.Contains("Commands =", preview);
    }

    [Fact]
    public void PreviewPageUsesFullStoredItemForNativeImageAndTextPreview()
    {
        var preview = File.ReadAllText(RepoPath("src", "Clip.CommandPalette", "ClipItemPreviewPage.cs"));
        var card = File.ReadAllText(RepoPath("src", "Clip.CommandPalette", "ClipItemPreviewCard.cs"));
        var imagePreview = File.ReadAllText(RepoPath("src", "Clip.CommandPalette", "ClipImagePreviewContent.cs"));

        Assert.Contains("_store.GetItem(_item.Id)", preview);
        Assert.Contains("fullItem.AssetPath", imagePreview);
        Assert.Contains("fullItem.Text", card);
        Assert.Contains("new Uri(path).AbsoluteUri", imagePreview);
        Assert.Contains("![Clipboard image]", imagePreview);
        Assert.Contains("--x-cmdpal-fit=fit", imagePreview);
        Assert.DoesNotContain("\"type\": \"Image\"", card);
        Assert.Contains("\"fontType\": \"Monospace\"", card);
        Assert.Contains("\"type\": \"FactSet\"", card);
        Assert.Contains("\"type\": \"ColumnSet\"", card);
        Assert.Contains("\"Action.Submit\"", card);
        Assert.Contains("SubmitForm(string payload)", card);
        Assert.Contains("new ClipHistoryActionCommand(action, _store, _afterHistoryMutation).Invoke()", card);
    }

    [Fact]
    public void HistoryPageSeparatesPinnedItemsAndStructuresPreviewDetails()
    {
        var page = File.ReadAllText(RepoPath("src", "Clip.CommandPalette", "ClipHistoryPage.cs"));

        Assert.Contains("SectionFor(item)", page);
        Assert.Contains("\"Pinned Items\"", page);
        Assert.Contains("ClipboardHistoryTimeBucket.LabelFor(item)", page);
        Assert.Contains("ClipboardHistoryTimeBucket.OrderedKeys", page);
        Assert.DoesNotContain("CreateSectionHeader", page);
        Assert.Contains("## Preview", page);
        Assert.Contains("ImageMarkdown(item.AssetPath!)", page);
        // The Information fields render solely via the native Metadata FactSet; the
        // duplicate markdown "## Information" table was dropped (mirrors the standalone
        // app's single Information panel).
        Assert.DoesNotContain("## Information", page);
        Assert.DoesNotContain("| Field | Value |", page);
        Assert.Contains("DetailsBody(item)", page);
        Assert.Contains("Metadata = metadata.ToArray()", page);
    }

    [Fact]
    public void HistoryPageLoadsIncrementallyCappedByUserHistoryLimit()
    {
        var page = File.ReadAllText(RepoPath("src", "Clip.CommandPalette", "ClipHistoryPage.cs"));

        // No hard-coded 25-item cap; the page grows via LoadMore toward the user's HistoryLimit.
        Assert.DoesNotContain("const int Limit = 25", page);
        Assert.Contains("public override void LoadMore()", page);
        Assert.Contains("HasMoreItems", page);

        // The ceiling comes from the F1 ClipSharedSettings HistoryLimit accessor, applied via
        // the Core ResolveLimit helper, and the query stays native against the Core index.
        Assert.Contains("ClipSharedSettings.Load().HistoryLimit", page);
        Assert.Contains("ClipboardHistoryListCommand.ResolveLimit(EffectiveHistoryLimit()", page);
        Assert.Contains("ClipboardHistoryListCommand.Create(store, searchText, limit)", page);

        // Cold-open paints only the first page.
        Assert.Contains("InitialPageSize = 25", page);
    }

    [Fact]
    public void HistoryPageKeepsHistoryItemsFirstAndUsesNativeFilters()
    {
        var page = File.ReadAllText(RepoPath("src", "Clip.CommandPalette", "ClipHistoryPage.cs"));
        var filters = File.ReadAllText(RepoPath("src", "Clip.CommandPalette", "ClipHistoryFilters.cs"));

        Assert.DoesNotContain("CreateFilterShortcutItems", page);
        Assert.DoesNotContain("Section = \"Categories\"", page);
        Assert.Contains("Filters = _filters", page);
        Assert.Contains("ClipboardHistoryListFilter.Pinned", filters);
        Assert.Contains("ClipboardHistoryListFilter.Text", filters);
        Assert.Contains("ClipboardHistoryListFilter.Links", filters);
        Assert.Contains("ClipboardHistoryListFilter.Files", filters);
        Assert.Contains("ClipboardHistoryListFilter.Images", filters);
        Assert.Contains("ClipboardHistoryListFilter.Colors", filters);
    }

    [Fact]
    public void OpenClipCommandForcesStandalonePaletteSession()
    {
        var shell = File.ReadAllText(RepoPath("src", "Clip.CommandPalette", "ClipShellActionCommand.cs"));

        Assert.Contains("OpenClip() => new(\"Open Clip.exe\",", shell);
        Assert.Contains("forcePaletteSession: true", shell);
        Assert.Contains("if (_forcePaletteSession || _trayAction is not null)", shell);
        Assert.Contains("startInfo.ArgumentList.Add(\"--palette-session\")", shell);
    }

    [Fact]
    public void HistoryPagePushesLiveUpdatesViaDebouncedFileSystemWatcher()
    {
        var page = File.ReadAllText(RepoPath("src", "Clip.CommandPalette", "ClipHistoryPage.cs"));
        var watcher = File.ReadAllText(RepoPath("src", "Clip.CommandPalette", "ClipHistoryWatcher.cs"));

        // The page owns a watcher, lazily started from GetItems (cold-open stays untouched), and
        // disposes it.
        Assert.Contains("ClipHistoryWatcher? _watcher", page);
        Assert.Contains("EnsureWatcher(store)", page);
        Assert.Contains("class ClipHistoryPage : DynamicListPage, IDisposable", page);
        Assert.Contains("public void Dispose()", page);
        Assert.Contains("_watcher?.Dispose()", page);

        // Watcher-driven refresh is gated on the existing index stamp so background noise does not
        // churn the list; explicit mutations still hard-refresh via InvalidateItems.
        Assert.Contains("InvalidateIfStoreChanged", page);
        Assert.Contains("CurrentIndexStampUtc(_store.Value, string.Empty)", page);
        Assert.Contains("_watcherSeenStampUtc", page);

        // The watcher watches the store's own file paths (not hardcoded), debounces the burst write
        // sequence, and signals only (no file I/O on the callback thread).
        Assert.Contains("store.HistoryFilePath", page);
        Assert.Contains("store.HistoryIndexFilePath", page);
        Assert.Contains("store.HistoryTopIndexFilePath", page);
        Assert.Contains("new FileSystemWatcher", watcher);
        Assert.Contains("DebounceMilliseconds", watcher);
        Assert.Contains("_debounceTimer.Change(DebounceMilliseconds", watcher);
        Assert.Contains("_watcher.EnableRaisingEvents = false", watcher);
        Assert.Contains(": IDisposable", watcher);
    }

    [Fact]
    public void HistoryPageExposesWindowsClipboardHistoryImportDelegatingToClipCommand()
    {
        var page = File.ReadAllText(RepoPath("src", "Clip.CommandPalette", "ClipHistoryPage.cs"));
        var import = File.ReadAllText(RepoPath("src", "Clip.CommandPalette", "ClipImportWindowsHistoryCommand.cs"));

        // The page surfaces the import as a page-level context command and refreshes the list after.
        Assert.Contains("new ClipImportWindowsHistoryCommand(InvalidateItems)", page);

        // The reader is the net8.0-windows WinRT helper the extension cannot load in-process, so the
        // command delegates to Clip.Command.exe import-windows-history via ClipExecutableLocator,
        // then invalidates the list and toasts the imported count.
        Assert.Contains("ClipExecutableLocator.Resolve(\"Clip.Command.exe\")", import);
        Assert.Contains("import-windows-history", import);
        Assert.Contains("_afterImport()", import);
        Assert.Contains("ToastStatusMessage", import);
    }

    private static string RepoPath(params string[] parts)
    {
        var directory = AppContext.BaseDirectory;
        while (!string.IsNullOrWhiteSpace(directory))
        {
            var candidate = Path.Combine(new[] { directory }.Concat(parts).ToArray());
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = Directory.GetParent(directory)?.FullName;
        }

        throw new FileNotFoundException("Could not find repo file.", Path.Combine(parts));
    }
}
