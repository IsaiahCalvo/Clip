using Clip.Shell;

namespace Clip.Tests;

public sealed class PrivacySettingsTests
{
    [Fact]
    public void PrivacySettingsExcludeSourceByAppName()
    {
        var privacy = new ClipPrivacySettings();
        privacy.AddExcludedApp("1Password", null);

        Assert.True(privacy.IsExcluded("1Password", null));
        Assert.True(privacy.IsExcluded("1password", null));
    }

    [Fact]
    public void PrivacySettingsExcludeSourceByExecutablePath()
    {
        var privacy = new ClipPrivacySettings();
        privacy.AddExcludedApp("Bank", @"C:\Apps\Bank.exe");

        Assert.True(privacy.IsExcluded("Bank", @"C:\Apps\Bank.exe"));
        Assert.True(privacy.IsExcluded("bank", @"C:\Apps\BANK.EXE"));
    }

    [Fact]
    public void PrivacySettingsIgnoreEmptyAndDuplicateApps()
    {
        var privacy = new ClipPrivacySettings();

        privacy.AddExcludedApp("", null);
        privacy.AddExcludedApp("Chrome", null);
        privacy.AddExcludedApp(" chrome ", null);

        Assert.Single(privacy.ExcludedApps);
        Assert.Equal("Chrome", privacy.ExcludedApps[0].Name);
    }

    [Fact]
    public void PrivacySettingsStoreSelectedAppNameAndPath()
    {
        var privacy = new ClipPrivacySettings();

        privacy.AddExcludedApp("Chrome", @"C:\Program Files\Google\Chrome\Application\chrome.exe");

        var app = Assert.Single(privacy.ExcludedApps);
        Assert.Equal("Chrome", app.Name);
        Assert.Equal(@"C:\Program Files\Google\Chrome\Application\chrome.exe", app.ExecutablePath);
    }
}
