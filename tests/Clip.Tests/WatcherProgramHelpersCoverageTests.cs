using System.Diagnostics;
using Clip.Core;
using Clip.Watcher;

namespace Clip.Tests;

public sealed class WatcherProgramHelpersCoverageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Clip.Tests", Guid.NewGuid().ToString("N"));

    public WatcherProgramHelpersCoverageTests()
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

    [Fact]
    public void TrayActionArgumentMapsEveryAction()
    {
        Assert.Equal("open", WatcherTrayMenu.TrayActionArgument(WatcherTrayAction.OpenClip));
        Assert.Equal("settings", WatcherTrayMenu.TrayActionArgument(WatcherTrayAction.OpenSettings));
        Assert.Equal("check-updates", WatcherTrayMenu.TrayActionArgument(WatcherTrayAction.CheckForUpdates));
        Assert.Equal("save-log", WatcherTrayMenu.TrayActionArgument(WatcherTrayAction.SaveLogSnapshot));
        Assert.Null(WatcherTrayMenu.TrayActionArgument(WatcherTrayAction.PasteLatest));
        Assert.Null(WatcherTrayMenu.TrayActionArgument(WatcherTrayAction.Exit));
    }

    [Fact]
    public void RichPaletteStartInfoAddsKeepWarmFlag()
    {
        var startInfo = Program.CreateRichPaletteStartInfo(@"C:\Tools\Clip.exe", WatcherTrayAction.OpenClip, keepWarm: true);

        Assert.Equal(@"C:\Tools", startInfo.WorkingDirectory);
        Assert.Contains("--keep-warm", startInfo.ArgumentList);
        Assert.Contains("--tray-action=open", startInfo.ArgumentList);
    }

    [Fact]
    public void RichPaletteStartInfoHiddenLaunchPrewarmsWithoutTrayAction()
    {
        var startInfo = Program.CreateRichPaletteStartInfo(@"C:\Tools\Clip.exe", WatcherTrayAction.OpenSettings, startHidden: true);

        Assert.Contains("--prewarm", startInfo.ArgumentList);
        Assert.DoesNotContain(startInfo.ArgumentList, arg => arg.StartsWith("--tray-action", StringComparison.Ordinal));
    }

    [Fact]
    public void IsRichPaletteRunningReflectsMutexExistence()
    {
        var name = @"Local\ClipTests-" + Guid.NewGuid().ToString("N");

        Assert.False(Program.IsRichPaletteRunning(name));

        using var mutex = new Mutex(false, name);
        Assert.True(Program.IsRichPaletteRunning(name));
    }

    [Fact]
    public void IsRichPaletteRunningSwallowsInvalidMutexNames()
    {
        Assert.False(Program.IsRichPaletteRunning(string.Empty));
    }

    [Fact]
    public void TrySignalWatcherPaletteSetsExistingEvent()
    {
        var name = @"Local\ClipTests-" + Guid.NewGuid().ToString("N");
        Assert.False(Program.TrySignalWatcherPalette(name));

        using var signal = new EventWaitHandle(false, EventResetMode.AutoReset, name);
        Assert.True(Program.TrySignalWatcherPalette(name));
        Assert.True(signal.WaitOne(0));
    }

    [Fact]
    public void TrySignalRichPaletteSetsExistingEvent()
    {
        var name = @"Local\ClipTests-" + Guid.NewGuid().ToString("N");
        Assert.False(Program.TrySignalRichPalette(name));

        using var signal = new EventWaitHandle(false, EventResetMode.AutoReset, name);
        Assert.True(Program.TrySignalRichPalette(name));
        Assert.True(signal.WaitOne(0));
    }

    [Fact]
    public void SignalHelpersReturnFalseOnInvalidEventNames()
    {
        Assert.False(Program.TrySignalRichPalette(string.Empty));
        Assert.False(Program.TrySignalWatcherPalette(string.Empty));
    }

    [Fact]
    public void LauncherToNowMsIsNullWithoutLauncherTicks()
    {
        Assert.Null(Program.LauncherToNowMs());
    }

    [Fact]
    public void ParseImportCountHandlesSignsAndTrailingNoise()
    {
        Assert.Equal(-4, Program.ParseImportCount("-4\r\n"));
        Assert.Equal(5, Program.ParseImportCount("5\r\nnot-a-number"));
        Assert.Equal(0, Program.ParseImportCount(string.Empty));
    }

    [Fact]
    public void FindWindowsHistoryExecutableReturnsNullOrExistingHelper()
    {
        var exe = Program.FindWindowsHistoryExecutable();

        Assert.True(
            exe is null || (File.Exists(exe) && Path.GetFileName(exe) == "Clip.WindowsHistory.exe"),
            $"Unexpected helper path: {exe}");
    }

    [Fact]
    public void WithArgumentsPopulatesArgumentListAndReturnsSameInstance()
    {
        var info = new ProcessStartInfo();

        var result = info.WithArguments(args =>
        {
            args.Add("-r");
            args.Add("120");
        });

        Assert.Same(info, result);
        Assert.Equal(["-r", "120"], result.ArgumentList.ToArray());
    }

    [Fact]
    public void SettingsProviderReloadIfChangedReturnsCurrentSettings()
    {
        var provider = new WatcherSettingsProvider();

        var reloaded = provider.ReloadIfChanged();

        Assert.NotNull(reloaded);
        Assert.Same(provider.Current, reloaded);
        Assert.Same(provider.Current, provider.ReloadIfChanged());
    }

    [Fact]
    public void TrySetClipboardItemReturnsFalseForUnknownIdWithoutTouchingClipboard()
    {
        var store = new ClipboardHistoryStore(_root, enableLoadMaintenance: false, retainLoadedItems: false);

        Assert.False(Program.TrySetClipboardItem(store, "no-such-id", paste: false));
    }
}
