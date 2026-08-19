using System.Reflection;
using System.Text.Json;
using Clip.Core;

namespace Clip.Tests;

// The RayClipboard -> Clip legacy-store migration (via the LegacyRootOverrideForTests seam and
// reflection on the private static MigrateLegacyStore), plus the small private static helpers
// whose defensive branches are unreachable through the public API.
public sealed class ClipboardHistoryStoreMigrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Clip.Tests", Guid.NewGuid().ToString("N"));

    public ClipboardHistoryStoreMigrationTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            TestTemp.Delete(_root);
        }
        catch
        {
        }
    }

    private string Sub(string name)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private static MethodInfo PrivateStatic(string name) =>
        typeof(ClipboardHistoryStore).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!;

    private static void Migrate(string rootPath) => PrivateStatic("MigrateLegacyStore").Invoke(null, [rootPath]);

    // ---------- MigrateLegacyStore ----------

    [Fact]
    public void MigrationSkipsWhenRootAlreadyHasHistory()
    {
        var root = Sub("has-history");
        File.WriteAllText(Path.Combine(root, "history.json"), "[]");
        var legacy = Sub("legacy-unused");
        File.WriteAllText(Path.Combine(legacy, "history.json"), "[{\"Id\":\"legacy\"}]");
        ClipboardHistoryStore.LegacyRootOverrideForTests.Value = legacy;

        Migrate(root);

        Assert.Equal("[]", File.ReadAllText(Path.Combine(root, "history.json")));
    }

    [Fact]
    public void MigrationSkipsWhenLegacyStoreIsMissing()
    {
        var root = Path.Combine(_root, "fresh-no-legacy");
        ClipboardHistoryStore.LegacyRootOverrideForTests.Value = Sub("legacy-empty");

        Migrate(root);

        Assert.False(File.Exists(Path.Combine(root, "history.json")));
    }

    [Fact]
    public void MigrationCopiesHistoryAssetsAndRewritesAssetPaths()
    {
        var legacy = Sub("legacy-full");
        var legacyAssets = Path.Combine(legacy, "assets");
        Directory.CreateDirectory(Path.Combine(legacyAssets, "image"));
        File.WriteAllText(Path.Combine(legacyAssets, "note.txt"), "note");
        File.WriteAllText(Path.Combine(legacyAssets, "image", "shot.png"), "png");

        var migrated = new ClipboardHistoryItem
        {
            Kind = ClipboardItemKind.Text,
            Text = "m",
            Preview = "m",
            AssetPath = Path.Combine(legacyAssets, "note.txt"),
        };
        var external = new ClipboardHistoryItem
        {
            Kind = ClipboardItemKind.Text,
            Text = "e",
            Preview = "e",
            AssetPath = @"C:\elsewhere\thing.txt",
        };
        var noAsset = new ClipboardHistoryItem { Kind = ClipboardItemKind.Text, Text = "n", Preview = "n" };
        File.WriteAllText(
            Path.Combine(legacy, "history.json"),
            JsonSerializer.Serialize(new List<ClipboardHistoryItem> { migrated, external, noAsset }));

        var root = Path.Combine(_root, "fresh-full");
        ClipboardHistoryStore.LegacyRootOverrideForTests.Value = legacy;
        Migrate(root);

        Assert.True(File.Exists(Path.Combine(root, "assets", "note.txt")));
        Assert.True(File.Exists(Path.Combine(root, "assets", "image", "shot.png"))); // recursive copy

        var items = JsonSerializer.Deserialize<List<ClipboardHistoryItem>>(
            File.ReadAllText(Path.Combine(root, "history.json")))!;
        Assert.Equal(Path.Combine(root, "assets", "note.txt"), items.Single(i => i.Id == migrated.Id).AssetPath);
        Assert.Equal(@"C:\elsewhere\thing.txt", items.Single(i => i.Id == external.Id).AssetPath);
    }

    [Fact]
    public void MigrationWithoutLegacyAssetsCopiesHistoryOnly()
    {
        var legacy = Sub("legacy-noassets");
        File.WriteAllText(Path.Combine(legacy, "history.json"), "[]");

        var root = Path.Combine(_root, "fresh-noassets");
        ClipboardHistoryStore.LegacyRootOverrideForTests.Value = legacy;
        Migrate(root);

        Assert.True(File.Exists(Path.Combine(root, "history.json")));
        Assert.False(Directory.Exists(Path.Combine(root, "assets")));
    }

    [Fact]
    public void MigrationLeavesHistoryUntouchedWhenNoPathsNeedRewriting()
    {
        var legacy = Sub("legacy-noop");
        Directory.CreateDirectory(Path.Combine(legacy, "assets"));
        var item = new ClipboardHistoryItem
        {
            Kind = ClipboardItemKind.Text,
            Text = "keep",
            Preview = "keep",
            AssetPath = @"C:\elsewhere\keep.txt",
        };
        var original = JsonSerializer.Serialize(new List<ClipboardHistoryItem> { item });
        File.WriteAllText(Path.Combine(legacy, "history.json"), original);

        var root = Path.Combine(_root, "fresh-noop");
        ClipboardHistoryStore.LegacyRootOverrideForTests.Value = legacy;
        Migrate(root);

        // No asset path started with the legacy assets prefix, so the file is byte-identical.
        Assert.Equal(original, File.ReadAllText(Path.Combine(root, "history.json")));
    }

    [Fact]
    public void MigrationToleratesNullHistoryJson()
    {
        var legacy = Sub("legacy-null");
        Directory.CreateDirectory(Path.Combine(legacy, "assets"));
        File.WriteAllText(Path.Combine(legacy, "assets", "stray.txt"), "x");
        File.WriteAllText(Path.Combine(legacy, "history.json"), "null");

        var root = Path.Combine(_root, "fresh-null");
        ClipboardHistoryStore.LegacyRootOverrideForTests.Value = legacy;
        Migrate(root);

        Assert.Equal("null", File.ReadAllText(Path.Combine(root, "history.json")));
        Assert.True(File.Exists(Path.Combine(root, "assets", "stray.txt")));
    }

    // ---------- private static helpers ----------

    [Fact]
    public void TryDeleteDirectoryHandlesMissingAndReadOnlyContent()
    {
        var method = PrivateStatic("TryDeleteDirectory");

        method.Invoke(null, [Path.Combine(_root, "no-such-dir")]); // missing-dir early return

        var dir = Sub("delete-me");
        var file = Path.Combine(dir, "readonly.txt");
        File.WriteAllText(file, "x");
        File.SetAttributes(file, FileAttributes.ReadOnly);
        method.Invoke(null, [dir]);

        Assert.False(Directory.Exists(dir));
    }

    [Fact]
    public void DeleteTempHistoryFileDeletesAndSwallowsFailures()
    {
        var method = PrivateStatic("DeleteTempHistoryFile");
        var temp = Path.Combine(_root, "hist.tmp");

        File.WriteAllText(temp, "x");
        method.Invoke(null, [temp]);
        Assert.False(File.Exists(temp));

        File.WriteAllText(temp, "x");
        using (File.Open(temp, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            method.Invoke(null, [temp]); // sharing violation lands in the catch
        }

        Assert.True(File.Exists(temp));
    }

    [Fact]
    public void PrivateNameHelpersCoverDefensiveBranches()
    {
        Assert.Equal("Clipboard item", PrivateStatic("TrimFileName").Invoke(null, ["   ", 90]));

        Assert.Equal(string.Empty, PrivateStatic("ReadLinkAssetText").Invoke(null, [Path.Combine(_root, "missing.url")]));

        var defaultFileName = PrivateStatic("DefaultFileName");
        Assert.EndsWith(".png", (string)defaultFileName.Invoke(null, [new ClipboardHistoryItem { Kind = ClipboardItemKind.Image }])!);
        Assert.EndsWith(".txt", (string)defaultFileName.Invoke(null, [new ClipboardHistoryItem { Kind = ClipboardItemKind.Text }])!);

        var parsed = PrivateStatic("ParseHexColor").Invoke(null, ["#abc"])!;
        Assert.Equal("(170, 187, 204)", parsed.ToString()); // #abc expands to AA BB CC
    }

    [Fact]
    public void UniqueAssetPathFallsBackToGuidWhenAllNumberedNamesAreTaken()
    {
        var folder = Sub("crowded");
        File.WriteAllText(Path.Combine(folder, "n.txt"), "");
        for (var i = 2; i < 1000; i++)
        {
            File.WriteAllText(Path.Combine(folder, $"n ({i}).txt"), "");
        }

        var result = (string)PrivateStatic("UniqueAssetPath").Invoke(null, [folder, "n.txt", null])!;

        Assert.Matches(@"^n \([0-9a-fA-F]{32}\)\.txt$", Path.GetFileName(result));
    }

    [Fact]
    public void UniqueAssetPathForItemFallsBackToGuidWhenAllNamesAreReserved()
    {
        var store = new ClipboardHistoryStore(Sub("reserved"));
        var textFolder = Path.Combine(store.ContentRootPath, "text");
        var item = new ClipboardHistoryItem { Kind = ClipboardItemKind.Text, Preview = "Z", Text = "z" };
        var reserved = new List<ClipboardHistoryItem>
        {
            new() { Kind = ClipboardItemKind.Text, Preview = "r", AssetPath = Path.Combine(textFolder, "Z.txt") },
        };
        for (var i = 2; i < 1000; i++)
        {
            reserved.Add(new ClipboardHistoryItem
            {
                Kind = ClipboardItemKind.Text,
                Preview = "r",
                AssetPath = Path.Combine(textFolder, $"Z ({i}).txt"),
            });
        }

        var method = typeof(ClipboardHistoryStore).GetMethod(
            "UniqueAssetPathForItem", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var result = (string)method.Invoke(store, [item, reserved])!;

        Assert.Matches(@"^Z \([0-9a-fA-F]{32}\)\.txt$", Path.GetFileName(result));
    }
}
