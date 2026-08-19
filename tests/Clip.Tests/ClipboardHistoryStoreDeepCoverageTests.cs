using System.Reflection;
using System.Text.Json;
using Clip.Core;

namespace Clip.Tests;

// Second wave of ClipboardHistoryStore coverage: the summary/top-index query fallbacks,
// deterministic lock-based catch blocks (hydrate, delete, rename, touch, sidecars),
// reconcile decoys, and the load-time backfill edge cases the first wave missed.
public sealed class ClipboardHistoryStoreDeepCoverageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Clip.Tests", Guid.NewGuid().ToString("N"));

    public ClipboardHistoryStoreDeepCoverageTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                TestTemp.Delete(_root);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    // ---------- summary/top index query fallbacks ----------

    [Fact]
    public void QueryWithLimitRebuildsWhenBothIndexesAreMissing()
    {
        var store = NoRetainStore(Sub("no-indexes"));
        var match = store.AddOrUpdate(TextItem("needle in history"));
        File.Delete(store.HistoryIndexFilePath);
        File.Delete(store.HistoryTopIndexFilePath);

        var found = Assert.Single(store.QueryItemSummaries("needle", 5));
        Assert.Equal(match.Id, found.Id);
    }

    [Fact]
    public void QueryWithLimitFallsBackWhenCompactedSummaryIndexNeedsRebuild()
    {
        var store = NoRetainStore(Sub("rebuild-limit"));
        var match = store.AddOrUpdate(TextItem("needle real"));
        File.Delete(store.HistoryTopIndexFilePath);
        var huge = "[\n{\"Id\":\"ghost\",\"Kind\":0,\"Preview\":\"needle ghost\",\"Text\":\"" + new string('x', 20_000) + "\"}]";
        File.WriteAllText(store.HistoryIndexFilePath, huge);
        TouchNewerThanHistory(store, store.HistoryIndexFilePath);

        var found = Assert.Single(store.QueryItemSummaries("needle", 5));
        Assert.Equal(match.Id, found.Id);
    }

    [Fact]
    public void QueryWithLimitTrimsCompactedMatches()
    {
        var store = NoRetainStore(Sub("trim-limit"));
        store.AddOrUpdate(TextItem("seed"));
        File.Delete(store.HistoryTopIndexFilePath);
        // The newline forces the deserialize-and-compact path; three matches exceed the limit.
        File.WriteAllText(
            store.HistoryIndexFilePath,
            "[\n{\"Id\":\"a\",\"Kind\":0,\"Preview\":\"needle a\"},{\"Id\":\"b\",\"Kind\":0,\"Preview\":\"needle b\"},{\"Id\":\"c\",\"Kind\":0,\"Preview\":\"needle c\"}]");
        TouchNewerThanHistory(store, store.HistoryIndexFilePath);

        Assert.Equal(2, store.QueryItemSummaries("needle", 2).Count);
    }

    [Fact]
    public void QueryWithLimitCompactsVerboseTopIndex()
    {
        var store = NoRetainStore(Sub("top-compact-query"));
        var match = store.AddOrUpdate(TextItem("needle top"));
        store.AddOrUpdate(TextItem("other top"));
        MakeTopIndexVerbose(store);

        var found = Assert.Single(store.QueryItemSummaries("needle", 5));
        Assert.Equal(match.Id, found.Id);
        Assert.DoesNotContain('\n', File.ReadAllText(store.HistoryTopIndexFilePath));
    }

    [Fact]
    public void QueryWithLimitSkipsTopIndexNeedingRebuild()
    {
        var store = NoRetainStore(Sub("top-rebuild-query"));
        var match = store.AddOrUpdate(TextItem("needle deep"));
        var huge = "[\n{\"Id\":\"ghost\",\"Kind\":0,\"Preview\":\"needle ghost\",\"Text\":\"" + new string('x', 20_000) + "\"}]";
        File.WriteAllText(store.HistoryTopIndexFilePath, huge);
        TouchNewerThanHistory(store, store.HistoryTopIndexFilePath);

        var found = Assert.Single(store.QueryItemSummaries("needle", 5));
        Assert.Equal(match.Id, found.Id);
    }

    [Fact]
    public void QueryWithLimitSurvivesUnreadableTopIndex()
    {
        var store = NoRetainStore(Sub("top-locked"));
        var match = store.AddOrUpdate(TextItem("needle locked"));
        TouchNewerThanHistory(store, store.HistoryTopIndexFilePath);
        using (new FileStream(store.HistoryTopIndexFilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var found = Assert.Single(store.QueryItemSummaries("needle", 5));
            Assert.Equal(match.Id, found.Id);
        }
    }

    [Fact]
    public void SummaryScannerSkipsNonStringSearchableValues()
    {
        var store = NoRetainStore(Sub("scanner-array"));
        store.AddOrUpdate(TextItem("seed"));
        File.Delete(store.HistoryTopIndexFilePath);
        File.WriteAllText(
            store.HistoryIndexFilePath,
            "[{\"Id\":\"w1\",\"Kind\":0,\"Preview\":[\"needle\",5]},{\"Id\":\"w2\",\"Kind\":0,\"Preview\":\"needle two\"}]");
        TouchNewerThanHistory(store, store.HistoryIndexFilePath);

        var found = Assert.Single(store.QueryItemSummaries("needle", 5));
        Assert.Equal("w2", found.Id);
    }

    [Fact]
    public void NoRetainSummariesServeFromCurrentIndexWithoutRebuild()
    {
        var store = NoRetainStore(Sub("summaries-current"));
        var item = store.AddOrUpdate(TextItem("current index"));

        var summary = Assert.Single(store.QueryItemSummaries());
        Assert.Equal(item.Id, summary.Id);
    }

    [Fact]
    public void WarmHotIndexesTreatsIndexesAsCurrentWhenHistoryIsGone()
    {
        var store = NoRetainStore(Sub("keys-no-history"));
        store.AddOrUpdate(TextItem("keys"));
        File.Delete(store.HistoryFilePath);

        store.WarmHotIndexes();

        Assert.True(store.HasCurrentRecentSummaryIndex());
    }

    // ---------- append planning ----------

    [Fact]
    public void UnknownKindWithMatchingHashIsNeverADuplicate()
    {
        var store = NoRetainStore(Sub("unknown-hash"));
        store.AddOrUpdate(new ClipboardHistoryItem { Kind = (ClipboardItemKind)77, Preview = "w1", ContentHash = "SAME" });
        store.AddOrUpdate(new ClipboardHistoryItem { Kind = (ClipboardItemKind)77, Preview = "w2", ContentHash = "SAME" });

        // The append path must not merge them (load-time normalization may, so read the raw file).
        var stored = JsonSerializer.Deserialize<List<ClipboardHistoryItem>>(File.ReadAllText(store.HistoryFilePath))!;
        Assert.Equal(2, stored.Count);
    }

    [Fact]
    public void PinnedAndUnlimitedAppendsSkipTrimPlanning()
    {
        var store = NoRetainStore(Sub("fast-append"));
        store.AddOrUpdate(TextItem("base"));

        var pinned = TextItem("pinned fast");
        pinned.IsPinned = true;
        store.AddOrUpdate(pinned, maxItems: 1);
        store.AddOrUpdate(TextItem("unlimited fast"), maxItems: -1);

        Assert.Equal(3, store.GetItems().Count);
    }

    [Fact]
    public void AppendSurvivesUnwritableTopIndex()
    {
        var store = NoRetainStore(Sub("top-unwritable"));
        store.AddOrUpdate(TextItem("first"));

        using (new FileStream(store.HistoryTopIndexFilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            store.AddOrUpdate(TextItem("second"));
        }

        Assert.Equal(2, store.GetItems().Count);
    }

    // ---------- lock-based catch blocks ----------

    [Fact]
    public void GetItemKeepsPreviewWhenAssetIsUnreadable()
    {
        var root = Sub("hydrate-locked");
        var creator = NoRetainStore(root);
        var longText = new string('q', 400) + " tail";
        var item = creator.AddOrUpdate(TextItem(longText));

        var reader = NoRetainStore(root);
        using (new FileStream(item.AssetPath!, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var loaded = reader.GetItem(item.Id);

            Assert.NotNull(loaded);
            Assert.True(loaded!.Text!.Length < longText.Length);
        }
    }

    [Fact]
    public void GetItemSkipsRichPayloadWhenFileIsUnreadable()
    {
        var root = Sub("rich-locked");
        var creator = NoRetainStore(root);
        var item = TextItem("rich text locked");
        item.HtmlText = "<b>rich</b>";
        creator.AddOrUpdate(item);
        var htmlPath = item.AssetPath + ".html";
        Assert.True(File.Exists(htmlPath));

        var reader = NoRetainStore(root);
        using (new FileStream(htmlPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var loaded = reader.GetItem(item.Id);

            Assert.NotNull(loaded);
            Assert.Null(loaded!.HtmlText);
        }
    }

    [Fact]
    public void DeleteSurvivesLockedAssetAndLockedRichPayload()
    {
        var store = new ClipboardHistoryStore(Sub("delete-locked"));

        var lockedAsset = store.AddOrUpdate(TextItem("locked asset"));
        using (new FileStream(lockedAsset.AssetPath!, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.True(store.Delete(lockedAsset.Id));
        }

        var rich = TextItem("locked html");
        rich.HtmlText = "<i>x</i>";
        store.AddOrUpdate(rich);
        using (new FileStream(rich.AssetPath + ".html", FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.True(store.Delete(rich.Id));
        }
    }

    [Fact]
    public void RenameKeepsGoingWhenAssetOrSidecarIsLocked()
    {
        var store = new ClipboardHistoryStore(Sub("rename-locked"));

        var lockedAsset = store.AddOrUpdate(TextItem("asset lock"));
        using (new FileStream(lockedAsset.AssetPath!, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            _ = store.Rename(lockedAsset.Id, "Blocked Asset");
        }

        Assert.Equal("Blocked Asset", store.GetItems().Single(i => i.Id == lockedAsset.Id).CustomTitle);

        var lockedSidecar = store.AddOrUpdate(TextItem("sidecar lock"));
        using (new FileStream(lockedSidecar.AssetPath + ".clip.json", FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            _ = store.Rename(lockedSidecar.Id, "Fresh Sidecar Name");
        }

        Assert.Equal(
            "Fresh Sidecar Name.txt",
            Path.GetFileName(store.GetItems().Single(i => i.Id == lockedSidecar.Id).AssetPath!));
    }

    [Fact]
    public void DuplicateCopySurvivesUntouchableAsset()
    {
        var store = new ClipboardHistoryStore(Sub("touch-denied"));
        var item = store.AddOrUpdate(TextItem("touch me"));

        // Denying WriteAttributes makes SetLastWriteTime fail, landing in the TouchAsset catch.
        var fileInfo = new FileInfo(item.AssetPath!);
        var user = System.Security.Principal.WindowsIdentity.GetCurrent().User!;
        var deny = new System.Security.AccessControl.FileSystemAccessRule(
            user,
            System.Security.AccessControl.FileSystemRights.WriteAttributes,
            System.Security.AccessControl.AccessControlType.Deny);
        var security = System.IO.FileSystemAclExtensions.GetAccessControl(fileInfo);
        security.AddAccessRule(deny);
        System.IO.FileSystemAclExtensions.SetAccessControl(fileInfo, security);
        try
        {
            store.AddOrUpdate(TextItem("touch me"));
        }
        finally
        {
            security = System.IO.FileSystemAclExtensions.GetAccessControl(fileInfo);
            security.RemoveAccessRule(deny);
            System.IO.FileSystemAclExtensions.SetAccessControl(fileInfo, security);
        }

        Assert.Equal(2, store.GetItems().Single(i => i.Id == item.Id).CopyCount);
    }

    [Fact]
    public void WriteSidecarReturnsFalseWhenSidecarIsUnreadable()
    {
        var store = new ClipboardHistoryStore(Sub("sidecar-locked"));
        var item = store.AddOrUpdate(TextItem("sidecar catch"));
        var method = typeof(ClipboardHistoryStore).GetMethod("WriteSidecar", BindingFlags.NonPublic | BindingFlags.Static)!;

        using (new FileStream(item.AssetPath + ".clip.json", FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.False((bool)method.Invoke(null, [item])!);
        }
    }

    [Fact]
    public void CleanupSkipsCategoryFolderItCannotDelete()
    {
        var root = Sub("cleanup-cwd");
        var store = new ClipboardHistoryStore(root);
        var stuck = Path.Combine(store.ContentRootPath, "file", "stuckcat");
        Directory.CreateDirectory(stuck);
        var priorCwd = Directory.GetCurrentDirectory();
        try
        {
            // A process's current directory cannot be deleted, which lands in the cleanup catch.
            Directory.SetCurrentDirectory(stuck);
            _ = new ClipboardHistoryStore(root);
        }
        finally
        {
            Directory.SetCurrentDirectory(priorCwd);
        }

        Assert.True(Directory.Exists(stuck));
    }

    // ---------- reconcile decoys ----------

    [Fact]
    public void ReconcileSkipsIgnoredLockedAndNonMatchingCandidates()
    {
        var root = Sub("reconcile-decoys");
        var external = Sub("reconcile-decoys-src");
        var store = new ClipboardHistoryStore(root);

        // Text item whose asset vanished; the folder holds an ignored sidecar-named file
        // and a candidate that cannot be read.
        var text = store.AddOrUpdate(TextItem("decoy target text"));
        var textFolder = Path.GetDirectoryName(text.AssetPath!)!;
        File.Delete(text.AssetPath + ".clip.json");
        File.Delete(text.AssetPath!);
        File.WriteAllText(Path.Combine(textFolder, "aaa.clip.json"), "not a sidecar");
        var lockedCandidate = Path.Combine(textFolder, "aab.txt");
        File.WriteAllText(lockedCandidate, "unreadable");

        // Link item whose asset vanished; the only candidate .url has no URL= line.
        var link = store.AddOrUpdate(TextItem("https://example.com/decoy1"));
        Assert.Equal(ClipboardItemKind.Link, link.Kind);
        var linkFolder = Path.GetDirectoryName(link.AssetPath!)!;
        File.Delete(link.AssetPath + ".clip.json");
        File.Delete(link.AssetPath!);
        File.WriteAllText(Path.Combine(linkFolder, "AAA.url"), "[InternetShortcut]\r\nIconIndex=0\r\n");

        // Files item whose asset vanished; the category folder holds a manifest-less decoy directory.
        var source = WriteFile(external, "doc-decoy.txt", "content");
        var files = store.AddOrUpdate(FilesItem(source));
        var filesFolder = Path.GetDirectoryName(files.AssetPath!)!;
        File.Delete(files.AssetPath + ".clip.json");
        File.Delete(files.AssetPath!);
        Directory.CreateDirectory(Path.Combine(filesFolder, "decoy-dir"));

        using (new FileStream(lockedCandidate, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var reloaded = new ClipboardHistoryStore(root);
            var items = reloaded.GetItems();

            // Nothing matched the decoys; every item was backfilled with a fresh asset instead.
            Assert.True(File.Exists(items.Single(i => i.Id == text.Id).AssetPath));
            Assert.True(File.Exists(items.Single(i => i.Id == link.Id).AssetPath));
            var reloadedFiles = items.Single(i => i.Id == files.Id);
            Assert.True(File.Exists(reloadedFiles.AssetPath) || Directory.Exists(reloadedFiles.AssetPath));
        }
    }

    // ---------- load-time backfill edge cases ----------

    [Fact]
    public void LoadBackfillsShortHexColorsBadPathsAndUnknownKinds()
    {
        var root = Sub("load-mixed");
        var contentRoot = Path.Combine(root, "Clipboard History");
        Directory.CreateDirectory(contentRoot);
        var weirdAsset = Path.Combine(contentRoot, "weird.bin");
        File.WriteAllText(weirdAsset, "blob");
        var external = Sub("load-mixed-src");
        var noExt = WriteFile(external, "noextension", "raw");

        var shortColor = new ClipboardHistoryItem { Kind = ClipboardItemKind.Color, Text = "#abc", Preview = "#abc" };
        var badPathColor = new ClipboardHistoryItem { Kind = ClipboardItemKind.Color, Text = "#123456", Preview = "#123456", AssetPath = "bad\0path" };
        var weird = new ClipboardHistoryItem { Kind = (ClipboardItemKind)99, Preview = "weird thing", AssetPath = weirdAsset, ContentHash = "AA11" };
        var extless = new ClipboardHistoryItem { Kind = ClipboardItemKind.Files, Preview = "noextension", FilePaths = [noExt] };

        File.WriteAllText(
            Path.Combine(root, "history.json"),
            JsonSerializer.Serialize(new List<ClipboardHistoryItem> { shortColor, badPathColor, weird, extless }));

        var store = new ClipboardHistoryStore(root);
        var items = store.GetItems();

        var color = items.Single(i => i.Id == shortColor.Id);
        Assert.True(File.Exists(color.AssetPath)); // #abc expanded to a swatch

        Assert.True(File.Exists(items.Single(i => i.Id == badPathColor.Id).AssetPath)); // survived the invalid old path

        var weirdLoaded = items.Single(i => i.Id == weird.Id);
        Assert.Equal("weird thing.txt", Path.GetFileName(weirdLoaded.AssetPath!)); // default asset name for unknown kinds

        var extLoaded = items.Single(i => i.Id == extless.Id);
        Assert.Equal("noextension", Path.GetFileName(extLoaded.AssetPath!)); // no extension appended

        // A second maintenance load finds the color asset already in place and leaves it alone.
        var second = new ClipboardHistoryStore(root);
        Assert.Equal(color.AssetPath, second.GetItems().Single(i => i.Id == shortColor.Id).AssetPath);
    }

    // ---------- helpers ----------

    private string Sub(string name)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private static ClipboardHistoryStore NoRetainStore(string root) =>
        new(root, enableLoadMaintenance: false, retainLoadedItems: false);

    private static void TouchNewerThanHistory(ClipboardHistoryStore store, string path)
    {
        File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(store.HistoryFilePath).AddSeconds(5));
    }

    private static void MakeTopIndexVerbose(ClipboardHistoryStore store)
    {
        var items = JsonSerializer.Deserialize<List<ClipboardHistoryItem>>(File.ReadAllText(store.HistoryTopIndexFilePath))!;
        File.WriteAllText(
            store.HistoryTopIndexFilePath,
            JsonSerializer.Serialize(items, new JsonSerializerOptions { WriteIndented = true }));
        TouchNewerThanHistory(store, store.HistoryTopIndexFilePath);
    }

    private static string WriteFile(string folder, string name, string content)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name);
        File.WriteAllText(path, content);
        return path;
    }

    private static ClipboardHistoryItem TextItem(string text) => new()
    {
        Kind = ClipboardItemKind.Text,
        Text = text,
        Preview = ClipboardHistoryStore.PreviewText(text),
    };

    private static ClipboardHistoryItem FilesItem(string path) => new()
    {
        Kind = ClipboardItemKind.Files,
        Preview = Path.GetFileName(path),
        FilePaths = [path],
    };
}
