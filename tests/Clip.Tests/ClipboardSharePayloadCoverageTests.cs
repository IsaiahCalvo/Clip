using Clip.Core;

namespace Clip.Tests;

public sealed class ClipboardSharePayloadCoverageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Clip.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void CreateThrowsWhenNoCopiedFileStillExists()
    {
        var item = new ClipboardHistoryItem
        {
            Kind = ClipboardItemKind.Files,
            FilePaths = [Path.Combine(_root, "gone.txt"), Path.Combine(_root, "also-gone.txt")],
            Preview = "2 files",
        };

        Assert.Throws<InvalidOperationException>(() => ClipboardSharePayload.Create(item, _root));
    }

    [Fact]
    public void CreateThrowsWhenImageAssetIsMissing()
    {
        var item = new ClipboardHistoryItem
        {
            Kind = ClipboardItemKind.Image,
            AssetPath = Path.Combine(_root, "missing.png"),
            Preview = "Image",
        };

        Assert.Throws<InvalidOperationException>(() => ClipboardSharePayload.Create(item, _root));
    }

    [Fact]
    public void CleanupStaleTemporaryFilesIsNoOpWhenRootIsMissing()
    {
        ClipboardSharePayload.CleanupStaleTemporaryFiles(_root);

        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public void CleanupToleratesTemporaryFileStillHeldByShareTarget()
    {
        var item = new ClipboardHistoryItem
        {
            Kind = ClipboardItemKind.Text,
            Text = "still being read",
            Preview = "still being read",
        };
        var payload = ClipboardSharePayload.Create(item, _root);
        var path = Assert.Single(payload.FilePaths);

        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            payload.Cleanup();

            // Delete failed silently; a later cleanup pass takes care of it.
            Assert.True(File.Exists(path));
        }
    }

    [Fact]
    public void CleanupIfDueToleratesUnwritableMarkerFile()
    {
        Directory.CreateDirectory(_root);
        var now = new DateTimeOffset(2026, 6, 19, 12, 0, 0, TimeSpan.Zero);
        var staleFile = Path.Combine(_root, "clip-stale.txt");
        File.WriteAllText(staleFile, "old");
        File.SetLastWriteTime(staleFile, now.AddDays(-2).LocalDateTime);
        var marker = Path.Combine(_root, ".clip-share-cleanup");
        File.WriteAllText(marker, "old-marker");
        File.SetLastWriteTime(marker, now.AddDays(-2).LocalDateTime);

        using (new FileStream(marker, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var ran = ClipboardSharePayload.CleanupStaleTemporaryFilesIfDue(_root, now: now);

            Assert.True(ran);
            Assert.False(File.Exists(staleFile));
            // The marker rewrite failed silently, so its content is untouched.
            Assert.Equal("old-marker", File.ReadAllText(marker));
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
