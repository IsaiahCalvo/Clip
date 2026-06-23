using System.Text.Json;
using Clip.Core;

namespace Clip.Tests;

public sealed class ClipSharedSettingsTests
{
    [Fact]
    public void SetOpenModeJsonPreservesOtherSettings()
    {
        var json = ClipSharedSettings.SetOpenModeJson("""{ "AppIcon": 1, "HistoryLimit": 25 }""", ClipSharedOpenMode.CommandPalette);
        using var document = JsonDocument.Parse(json);

        Assert.Equal((int)ClipSharedOpenMode.CommandPalette, document.RootElement.GetProperty("OpenMode").GetInt32());
        Assert.Equal(1, document.RootElement.GetProperty("AppIcon").GetInt32());
        Assert.Equal(25, document.RootElement.GetProperty("HistoryLimit").GetInt32());
    }

    [Fact]
    public void LoadFromJsonReadsSharedSettings()
    {
        var settings = ClipSharedSettings.LoadFromJson("""{ "OpenMode": 1, "AppIcon": 1, "CheckForUpdatesOnStartup": false }""");

        Assert.Equal(ClipSharedOpenMode.CommandPalette, settings.OpenMode);
        Assert.Equal(ClipSharedAppIcon.Dark, settings.AppIcon);
        Assert.False(settings.CheckForUpdatesOnStartup);
    }
}
