using Clip.Core;

namespace Clip.Tests;

// OpenWithRecentStore has no path override hook — it always targets the user's real
// %LocalAppData%\Clip\open-with-recent.json. These tests therefore stick to the read-only
// Load contract (a made-up extension can never have recents), the Save guard clauses
// (which must return before any file I/O), and the internal RecentApp key logic.
public sealed class OpenWithRecentStoreCoverageTests : IDisposable
{
    private static readonly string StorePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Clip",
        "open-with-recent.json");

    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), "Clip.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void LoadReturnsEmptyForUnknownExtension()
    {
        var target = Path.Combine(_tempRoot, "file.zz" + Guid.NewGuid().ToString("N"));

        Assert.Empty(OpenWithRecentStore.Load(target));
    }

    [Fact]
    public void LoadForFolderYieldsOnlyRecentFlaggedChoices()
    {
        Directory.CreateDirectory(_tempRoot);

        var apps = OpenWithRecentStore.Load(_tempRoot);

        Assert.All(apps, app =>
        {
            Assert.True(app.IsRecent);
            Assert.Equal("Recent", app.Source);
        });
    }

    [Theory]
    [InlineData(true, "C:\\apps\\tool.exe", null)] // default app is never persisted
    [InlineData(false, null, null)]                // no executable and no AUMID: nothing to persist
    [InlineData(false, "   ", null)]               // whitespace executable counts as absent
    public void SaveGuardsReturnWithoutTouchingStore(bool isDefault, string? exePath, string? aumid)
    {
        var before = SnapshotStore();

        OpenWithRecentStore.Save(
            Path.Combine(_tempRoot, "file.txt"),
            new AppChoice("Tool", exePath, "Test", IsDefault: isDefault, AppUserModelId: aumid));

        Assert.Equal(before, SnapshotStore());
    }

    [Fact]
    public void RecentAppKeyPrefersAppUserModelIdOverExecutable()
    {
        var app = new OpenWithRecentStore.RecentApp("Paint", @"C:\apps\paint.exe", "Microsoft.Paint_8wekyb3d8bbwe!App");

        Assert.Equal("Microsoft.Paint_8wekyb3d8bbwe!App", app.AppKey);
    }

    [Fact]
    public void RecentAppKeyFallsBackToExecutableThenEmpty()
    {
        Assert.Equal(@"C:\apps\tool.exe", new OpenWithRecentStore.RecentApp("Tool", @"C:\apps\tool.exe", null).AppKey);
        Assert.Equal(string.Empty, new OpenWithRecentStore.RecentApp("Tool", null, null).AppKey);
    }

    private static (bool Exists, long Length, DateTime LastWriteUtc) SnapshotStore()
    {
        var info = new FileInfo(StorePath);
        return info.Exists ? (true, info.Length, info.LastWriteTimeUtc) : (false, 0L, DateTime.MinValue);
    }

    public void Dispose()
    {
        try
        {
            TestTemp.Delete(_tempRoot);
        }
        catch
        {
        }
    }
}
