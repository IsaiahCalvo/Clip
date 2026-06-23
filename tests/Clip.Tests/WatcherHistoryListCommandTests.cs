using Clip.Core;
using Clip.Watcher;

namespace Clip.Tests;

public sealed class WatcherHistoryListCommandTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Clip.WatcherCommand.Tests", Guid.NewGuid().ToString("N"));
    private readonly ClipboardHistoryStore _store;

    public WatcherHistoryListCommandTests()
    {
        _store = new ClipboardHistoryStore(_root);
    }

    [Fact]
    public void JsonListUsesRecentSummaryByDefault()
    {
        var older = TextItem("older note");
        older.LastUsedAt = DateTimeOffset.Now.AddMinutes(-10);
        _store.AddOrUpdate(older);

        var newer = TextItem("newer invoice");
        newer.CustomTitle = "Invoice";
        newer.HtmlText = new string('h', 10_000);
        newer.RtfText = new string('r', 10_000);
        _store.AddOrUpdate(newer);

        var result = ClipboardHistoryListCommand.Create(_store, ["list", "--json", "--limit", "1"]);
        var json = ClipboardHistoryListCommand.Serialize(result);

        var item = Assert.Single(result.Items);
        Assert.Equal("recent-summary", result.Source);
        Assert.Equal(newer.Id, item.Id);
        Assert.Equal("Invoice", item.Title);
        Assert.True(item.HasOriginalFormatting);
        Assert.Equal("paste", item.DefaultActionId);
        var paste = item.Actions.First(action => action.Id == "paste");
        Assert.Equal("Paste", paste.Label);
        Assert.Equal("Clip.Watcher.exe", paste.Executable);
        Assert.Equal(["paste", newer.Id], paste.Arguments);
        Assert.True(paste.RequiresFullItem);
        var copy = item.Actions.First(action => action.Id == "copy");
        Assert.Equal("Copy", copy.Label);
        Assert.Equal("Clip.Watcher.exe", copy.Executable);
        Assert.Equal(["copy", newer.Id], copy.Arguments);
        Assert.True(copy.RequiresFullItem);
        Assert.Contains("\"actions\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("htmlText", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("rtfText", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("assetPath", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void JsonListSearchesSummaryIndexWhenQueryIsProvided()
    {
        var invoice = TextItem("alpha invoice");
        var proposal = TextItem("beta proposal");
        _store.AddOrUpdate(invoice);
        _store.AddOrUpdate(proposal);

        var result = ClipboardHistoryListCommand.Create(_store, ["list", "--json", "--query", "invoice", "--limit", "5"]);

        var item = Assert.Single(result.Items);
        Assert.Equal("summary-search", result.Source);
        Assert.Equal("invoice", result.Query);
        Assert.Equal(5, result.Limit);
        Assert.Equal(invoice.Id, item.Id);
        Assert.Equal("Text", item.Kind);
    }

    [Fact]
    public void DirectListApiSupportsExtensionStyleQueryWithoutCommandArgs()
    {
        var invoice = TextItem("extension invoice");
        _store.AddOrUpdate(invoice);

        var result = ClipboardHistoryListCommand.Create(_store, query: "invoice", limit: ClipboardHistoryListCommand.MaximumLimit + 5_000);

        var item = Assert.Single(result.Items);
        Assert.Equal("summary-search", result.Source);
        Assert.Equal(ClipboardHistoryListCommand.MaximumLimit, result.Limit);
        Assert.Equal(invoice.Id, item.Id);
    }

    [Theory]
    [InlineData(null, 25, 25)]              // Unlimited setting: page size honored as-is.
    [InlineData(null, 9_999, 1_000)]       // Unlimited setting: still capped at MaximumLimit per query.
    [InlineData(500, 25, 25)]              // Default setting, small page: honored.
    [InlineData(500, 999, 500)]           // Default setting: page cannot exceed the user's HistoryLimit.
    [InlineData(100, 250, 100)]           // Smaller setting clamps the page to the user's ceiling.
    [InlineData(1000, 5000, 1000)]        // Largest finite setting clamps to MaximumLimit.
    [InlineData(500, 0, 1)]               // Never returns less than 1.
    public void ResolveLimitClampsRequestedPageToHistoryLimitCeiling(int? historyLimit, int requested, int expected)
    {
        Assert.Equal(expected, ClipboardHistoryListCommand.ResolveLimit(historyLimit, requested));
    }

    [Fact]
    public void JsonListIncludesOpenAndRevealActionsForFileItems()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "invoice.pdf");
        File.WriteAllText(path, "pdf");
        var file = new ClipboardHistoryItem
        {
            Kind = ClipboardItemKind.Files,
            FilePaths = [path],
            Preview = "invoice.pdf",
            SourceApplication = "Test"
        };
        _store.AddOrUpdate(file);

        var result = ClipboardHistoryListCommand.Create(_store, ["list", "--json", "--limit", "1"]);

        var item = Assert.Single(result.Items);
        Assert.Equal("paste", item.DefaultActionId);
        Assert.Contains(item.Actions, action =>
            action.Id == "paste" &&
            action.Executable == "Clip.Watcher.exe" &&
            action.Arguments.SequenceEqual(["paste", file.Id]) &&
            action.RequiresFullItem);
        Assert.Contains(item.Actions, action =>
            action.Id == "share" &&
            action.RequiresFullItem);
        Assert.DoesNotContain(item.Actions, action => action.Id == "paste-plain");
        Assert.DoesNotContain(item.Actions, action => action.Id == "append");
        Assert.Contains(item.Actions, action =>
            action.Id == "copy" &&
            action.Executable == "Clip.Watcher.exe" &&
            action.Arguments.SequenceEqual(["copy", file.Id]));
        Assert.Contains(item.Actions, action =>
            action.Id == "open" &&
            action.Executable == "Clip.Command.exe" &&
            action.Arguments.SequenceEqual(["open", file.Id]));
        Assert.Contains(item.Actions, action =>
            action.Id == "reveal" &&
            action.Executable == "Clip.Command.exe" &&
            action.Arguments.SequenceEqual(["reveal", file.Id]));
    }

    [Fact]
    public void JsonListIncludesImageAssetPathForCommandPalettePreview()
    {
        Directory.CreateDirectory(_root);
        var imagePath = Path.Combine(_root, "preview.png");
        File.WriteAllBytes(imagePath, [0x89, 0x50, 0x4E, 0x47]);
        var image = new ClipboardHistoryItem
        {
            Kind = ClipboardItemKind.Image,
            Preview = "preview.png",
            AssetPath = imagePath,
            SourceApplication = "Test"
        };
        _store.Save([image]);

        var result = ClipboardHistoryListCommand.Create(_store, ["list", "--json", "--limit", "1"]);
        var json = ClipboardHistoryListCommand.Serialize(result);

        var listed = Assert.Single(result.Items);
        Assert.Equal(imagePath, listed.AssetPath);
        Assert.Contains("assetPath", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("preview.png", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void JsonListIncludesManagementActionsForUnpinnedItems()
    {
        var item = TextItem("manage me");
        _store.AddOrUpdate(item);

        var result = ClipboardHistoryListCommand.Create(_store, ["list", "--json", "--limit", "1"]);

        var listed = Assert.Single(result.Items);
        Assert.Contains(listed.Actions, action =>
            action.Id == "pin" &&
            action.Label == "Pin" &&
            action.Arguments.SequenceEqual(["pin", item.Id]) &&
            action.RequiresFullItem == false);
        Assert.Contains(listed.Actions, action =>
            action.Id == "delete" &&
            action.Label == "Delete" &&
            action.Arguments.SequenceEqual(["delete", item.Id]) &&
            action.RequiresFullItem == false);
    }

    [Fact]
    public void JsonListIncludesFormActionsForTextItems()
    {
        var item = TextItem("form me");
        _store.AddOrUpdate(item);

        var result = ClipboardHistoryListCommand.Create(_store, ["list", "--json", "--limit", "1"]);

        var listed = Assert.Single(result.Items);
        Assert.Contains(listed.Actions, action =>
            action.Id == "rename" &&
            action.Label == "Rename" &&
            action.Arguments.SequenceEqual(["rename", item.Id]) &&
            action.RequiresFullItem == false);
        Assert.Contains(listed.Actions, action =>
            action.Id == "edit-text" &&
            action.Label == "Edit Text" &&
            action.Arguments.SequenceEqual(["edit", item.Id]) &&
            action.RequiresFullItem == true);
        Assert.Contains(listed.Actions, action =>
            action.Id == "save-as-file" &&
            action.Label == "Save as File" &&
            action.Arguments.SequenceEqual(["save", item.Id]) &&
            action.RequiresFullItem == true);
    }

    [Fact]
    public void JsonListIncludesPasteAppendAndShareActionsForTextItems()
    {
        var item = TextItem("paste me");
        _store.AddOrUpdate(item);

        var result = ClipboardHistoryListCommand.Create(_store, ["list", "--json", "--limit", "1"]);

        var listed = Assert.Single(result.Items);
        Assert.Equal("paste", listed.DefaultActionId);
        Assert.Contains(listed.Actions, action =>
            action.Id == "paste" &&
            action.Label == "Paste" &&
            action.Executable == "Clip.Watcher.exe" &&
            action.Arguments.SequenceEqual(["paste", item.Id]) &&
            action.RequiresFullItem);
        Assert.Contains(listed.Actions, action =>
            action.Id == "paste-plain" &&
            action.Label == "Paste as Plain Text" &&
            action.Executable == "Clip.Watcher.exe" &&
            action.Arguments.SequenceEqual(["paste", item.Id]) &&
            action.RequiresFullItem);
        Assert.Contains(listed.Actions, action =>
            action.Id == "append" &&
            action.Label == "Append to Clipboard" &&
            action.Executable == "Clip.Watcher.exe" &&
            action.Arguments.SequenceEqual(["append", item.Id]) &&
            action.RequiresFullItem);
        Assert.Contains(listed.Actions, action =>
            action.Id == "share" &&
            action.Label == "Share" &&
            action.Arguments.SequenceEqual(["share", item.Id]) &&
            action.RequiresFullItem);
    }

    [Fact]
    public void JsonListIncludesPastePlainButNotAppendForColorItems()
    {
        var item = new ClipboardHistoryItem
        {
            Kind = ClipboardItemKind.Color,
            Preview = "#FF0000",
            Text = "#FF0000",
            SourceApplication = "Test"
        };
        _store.AddOrUpdate(item);

        var result = ClipboardHistoryListCommand.Create(_store, ["list", "--json", "--limit", "1"]);

        var listed = Assert.Single(result.Items);
        Assert.Contains(listed.Actions, action => action.Id == "paste");
        Assert.Contains(listed.Actions, action => action.Id == "paste-plain");
        Assert.DoesNotContain(listed.Actions, action => action.Id == "append");
        Assert.DoesNotContain(listed.Actions, action => action.Id == "share");
    }

    [Fact]
    public void JsonListIncludesShareButNotPastePlainOrAppendForImageItems()
    {
        Directory.CreateDirectory(_root);
        var imagePath = Path.Combine(_root, "share.png");
        File.WriteAllBytes(imagePath, [0x89, 0x50, 0x4E, 0x47]);
        var image = new ClipboardHistoryItem
        {
            Kind = ClipboardItemKind.Image,
            Preview = "share.png",
            AssetPath = imagePath,
            SourceApplication = "Test"
        };
        _store.Save([image]);

        var result = ClipboardHistoryListCommand.Create(_store, ["list", "--json", "--limit", "1"]);

        var listed = Assert.Single(result.Items);
        Assert.Contains(listed.Actions, action => action.Id == "paste");
        Assert.Contains(listed.Actions, action =>
            action.Id == "share" &&
            action.Executable == string.Empty &&
            action.Arguments.SequenceEqual(["share", listed.Id]));
        Assert.DoesNotContain(listed.Actions, action => action.Id == "paste-plain");
        Assert.DoesNotContain(listed.Actions, action => action.Id == "append");
    }

    [Fact]
    public void JsonListIncludesCopyPathForFileItems()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "invoice.pdf");
        File.WriteAllText(path, "pdf");
        var file = new ClipboardHistoryItem
        {
            Kind = ClipboardItemKind.Files,
            FilePaths = [path],
            Preview = "invoice.pdf",
            SourceApplication = "Test"
        };
        _store.AddOrUpdate(file);

        var result = ClipboardHistoryListCommand.Create(_store, ["list", "--json", "--limit", "1"]);

        var listed = Assert.Single(result.Items);
        Assert.Contains(listed.Actions, action =>
            action.Id == "copy-path" &&
            action.Label == "Copy path" &&
            action.Arguments.SequenceEqual(["copy-path", file.Id]) &&
            action.RequiresFullItem == false);
    }

    [Fact]
    public void JsonListIncludesPinManagementActionsForPinnedItems()
    {
        var first = TextItem("first pin");
        var second = TextItem("second pin");
        _store.AddOrUpdate(first);
        _store.AddOrUpdate(second);
        _store.SetPinned(first.Id, true);
        _store.SetPinned(second.Id, true);

        var result = ClipboardHistoryListCommand.Create(_store, ["list", "--json", "--limit", "2"]);

        var listed = result.Items.First(item => item.Id == second.Id);
        Assert.Contains(listed.Actions, action =>
            action.Id == "unpin" &&
            action.Label == "Unpin" &&
            action.Arguments.SequenceEqual(["unpin", second.Id]) &&
            action.RequiresFullItem == false);
        Assert.Contains(listed.Actions, action =>
            action.Id == "move-pin-up" &&
            action.Label == "Move Pin Up" &&
            action.Arguments.SequenceEqual(["up", second.Id]) &&
            action.RequiresFullItem == false);
        Assert.Contains(listed.Actions, action =>
            action.Id == "move-pin-down" &&
            action.Label == "Move Pin Down" &&
            action.Arguments.SequenceEqual(["down", second.Id]) &&
            action.RequiresFullItem == false);
    }

    [Fact]
    public void JsonListIncludesDetailsMetadata()
    {
        var copiedAt = new DateTimeOffset(2026, 6, 22, 9, 30, 0, TimeSpan.Zero);
        var item = TextItem("alpha beta");
        item.LastCopiedAt = copiedAt;
        item.CopyCount = 3;
        item.CharacterCount = 10;
        item.WordCount = 2;
        _store.Save([item]);

        var result = ClipboardHistoryListCommand.Create(_store, ["list", "--json", "--limit", "1"]);

        var listed = Assert.Single(result.Items);
        Assert.Equal(copiedAt, listed.LastCopiedAt);
        Assert.Equal(3, listed.CopyCount);
        Assert.Equal(10, listed.CharacterCount);
        Assert.Equal(2, listed.WordCount);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static ClipboardHistoryItem TextItem(string text) => new()
    {
        Kind = ClipboardItemKind.Text,
        Preview = ClipboardHistoryStore.PreviewText(text),
        Text = text,
        SourceApplication = "Test"
    };
}
