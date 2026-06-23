using Clip.Shell;
using Clip.Watcher;

namespace Clip.Tests;

public sealed class OpenModeSettingsTests
{
    [Fact]
    public void ShellSettingsDefaultToStandaloneOpenMode()
    {
        var settings = new ClipShellSettings();

        Assert.Equal(ClipOpenMode.Standalone, settings.OpenMode);
    }

    [Fact]
    public void ResetRestoresStandaloneOpenMode()
    {
        var settings = new ClipShellSettings
        {
            OpenMode = ClipOpenMode.CommandPalette,
        };

        settings.ResetToDefaults();

        Assert.Equal(ClipOpenMode.Standalone, settings.OpenMode);
    }

    [Fact]
    public void WatcherSettingsReadsCommandPaletteOpenMode()
    {
        var settings = WatcherSettings.LoadFromJson("""{ "OpenMode": 1 }""");

        Assert.Equal(WatcherOpenModePreference.CommandPalette, settings.OpenMode);
    }

    [Fact]
    public void WatcherSettingsDefaultsToStandaloneOpenMode()
    {
        var settings = WatcherSettings.LoadFromJson("""{}""");

        Assert.Equal(WatcherOpenModePreference.Standalone, settings.OpenMode);
    }

    [Fact]
    public void WatcherOnlyRegistersOpenHotkeyForStandaloneMode()
    {
        Assert.True(WatcherSettings.ShouldRegisterOpenHotkey(WatcherOpenModePreference.Standalone));
        Assert.False(WatcherSettings.ShouldRegisterOpenHotkey(WatcherOpenModePreference.CommandPalette));
    }

    [Fact]
    public void RichPalettePrewarmStartInfoKeepsStandaloneShellWarmAndHidden()
    {
        var startInfo = Program.CreateRichPaletteStartInfo(
            @"C:\Tools\Clip.exe",
            WatcherTrayAction.OpenClip,
            keepWarm: true,
            startHidden: true);

        Assert.Equal(@"C:\Tools\Clip.exe", startInfo.FileName);
        Assert.Equal(
            ["--palette-session", "--keep-warm", "--prewarm"],
            startInfo.ArgumentList.ToArray());
    }

    [Fact]
    public void WatcherShowPathOpensStandaloneClipEvenInCommandPaletteMode()
    {
        var source = File.ReadAllText(RepoPath("src", "Clip.Watcher", "Program.cs"));
        var showPalette = source[
            source.IndexOf("    private void ShowPalette()", StringComparison.Ordinal)..
            source.IndexOf("    private void QueueWindowsHistoryImport", StringComparison.Ordinal)];

        Assert.Contains("Program.TryLaunchRichPalette", showPalette);
        Assert.DoesNotContain("Program.TryLaunchCommandPalette", showPalette);
    }

    [Fact]
    public void WatcherWarmsCommandPaletteProviderWhenCommandPaletteModeIsSelected()
    {
        var source = File.ReadAllText(RepoPath("src", "Clip.Watcher", "Program.cs"));
        var applyOpenMode = source[
            source.IndexOf("    private void ApplyOpenMode", StringComparison.Ordinal)..
            source.IndexOf("    private void EnsureStandaloneShellWarm", StringComparison.Ordinal)];

        Assert.Contains("EnsureCommandPaletteWarm()", applyOpenMode);
        Assert.Contains("CommandPaletteSettings.ConfigureClipHistoryHotkey()", source);
        Assert.Contains("CommandPaletteSettings.RequestExternalReload()", source);
    }

    [Fact]
    public void WatcherOnlyReloadsCommandPaletteWhenHotkeySettingsChanged()
    {
        var source = File.ReadAllText(RepoPath("src", "Clip.Watcher", "Program.cs"));
        var warmCommandPalette = source[
            source.IndexOf("    private void EnsureCommandPaletteWarm", StringComparison.Ordinal)..
            source.IndexOf("    private ContextMenuStrip CreateTrayMenu", StringComparison.Ordinal)];

        Assert.Contains("if (result.Changed)", warmCommandPalette);
        Assert.Contains("CommandPaletteSettings.SetExternalReloadAllowed(true)", warmCommandPalette);
        Assert.Contains("CommandPaletteSettings.RequestExternalReload()", warmCommandPalette);
        Assert.Contains("CommandPaletteSettings.SetExternalReloadAllowed(false)", warmCommandPalette);
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
