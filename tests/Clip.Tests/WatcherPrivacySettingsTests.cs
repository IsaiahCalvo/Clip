using System.Text.Json;
using Clip.Watcher;

namespace Clip.Tests;

public class WatcherPrivacySettingsTests
{
    [Fact]
    public void NameOnlyRulesDoNotRequireSourcePath()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "Privacy": {
                "ExcludedApps": [
                  { "Name": "Chrome" }
                ]
              }
            }
            """);

        var privacy = WatcherPrivacySettings.FromJson(document.RootElement);

        Assert.False(privacy.RequiresSourcePath);
        Assert.True(privacy.IsExcluded("chrome", null));
    }

    [Fact]
    public void PathRulesRequireSourcePath()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "Privacy": {
                "ExcludedApps": [
                  { "Name": "Bank", "ExecutablePath": "C:\\Apps\\Bank.exe" }
                ]
              }
            }
            """);

        var privacy = WatcherPrivacySettings.FromJson(document.RootElement);

        Assert.True(privacy.RequiresSourcePath);
        Assert.True(privacy.IsExcluded(null, @"C:\Apps\Bank.exe"));
    }
}
