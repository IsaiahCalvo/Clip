using Clip.Watcher;

namespace Clip.Tests;

public sealed class WatcherAppIconTests
{
    [Fact]
    public void WatcherSettingsReadsAppIconPreference()
    {
        var settings = WatcherSettings.LoadFromJson("""{ "AppIcon": 1 }""");

        Assert.Equal(WatcherAppIconPreference.Dark, settings.AppIcon);
    }

    [Fact]
    public void WatcherSettingsDefaultsToLightAppIcon()
    {
        var settings = WatcherSettings.LoadFromJson("""{}""");

        Assert.Equal(WatcherAppIconPreference.Light, settings.AppIcon);
    }

    [Fact]
    public void WatcherTrayIconUsesConfiguredAppIconFile()
    {
        var darkPath = WatcherTrayIcon.IconPath(WatcherAppIconPreference.Dark, @"C:\Clip");
        var lightPath = WatcherTrayIcon.IconPath(WatcherAppIconPreference.Light, @"C:\Clip");

        Assert.EndsWith(@"assets\app-icons\clip-tile-dark.ico", darkPath);
        Assert.EndsWith(@"assets\app-icons\clip-tile-light.ico", lightPath);
    }
}
