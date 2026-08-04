using Clip.Core;

namespace Clip.Tests;

// ClipStoragePaths derives every path from %LocalAppData% (no override hook), so these
// tests pin the shape of the paths and the read-only EffectiveClipboardFolderPath contract
// without touching the user's real settings.json.
public sealed class ClipStoragePathsCoverageTests
{
    [Fact]
    public void SettingsPathIsRootedUnderClipFolder()
    {
        var path = ClipStoragePaths.SettingsPath;

        Assert.True(Path.IsPathRooted(path));
        Assert.Equal("settings.json", Path.GetFileName(path));
        Assert.Equal("Clip", new DirectoryInfo(Path.GetDirectoryName(path)!).Name);
    }

    [Fact]
    public void DefaultClipboardFolderPathIsSiblingOfSettings()
    {
        var path = ClipStoragePaths.DefaultClipboardFolderPath;

        Assert.True(Path.IsPathRooted(path));
        Assert.Equal("Clipboard History", new DirectoryInfo(path).Name);
        Assert.Equal(
            Path.GetDirectoryName(ClipStoragePaths.SettingsPath),
            Path.GetDirectoryName(path));
    }

    [Fact]
    public void WebView2UserDataFolderPathIsSiblingOfSettings()
    {
        var path = ClipStoragePaths.WebView2UserDataFolderPath;

        Assert.True(Path.IsPathRooted(path));
        Assert.Equal("WebView2", new DirectoryInfo(path).Name);
        Assert.Equal(
            Path.GetDirectoryName(ClipStoragePaths.SettingsPath),
            Path.GetDirectoryName(path));
    }

    [Fact]
    public void EffectiveClipboardFolderPathNeverReturnsEmptyAndIsRooted()
    {
        // Read-only: consults the real settings.json if present, else the default.
        var path = ClipStoragePaths.EffectiveClipboardFolderPath();

        Assert.False(string.IsNullOrWhiteSpace(path));
        Assert.True(Path.IsPathRooted(path));
    }
}
