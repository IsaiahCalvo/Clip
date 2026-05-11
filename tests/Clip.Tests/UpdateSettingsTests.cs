using Clip.Shell;

namespace Clip.Tests;

public sealed class UpdateSettingsTests
{
    [Fact]
    public void ResetToDefaultsRestoresUpdateSettings()
    {
        var settings = new ClipShellSettings
        {
            CheckForUpdatesOnStartup = false,
            InstallUpdatesAutomatically = true,
        };

        settings.ResetToDefaults();

        Assert.True(settings.CheckForUpdatesOnStartup);
        Assert.True(settings.InstallUpdatesAutomatically);
    }

    [Theory]
    [InlineData("1.2.4", "1.2.3", true)]
    [InlineData("v1.2.4", "1.2.3", true)]
    [InlineData("v1.2.4", "1.2.3+abc123", true)]
    [InlineData("1.2.3", "1.2.3", false)]
    [InlineData("v1.2.3", "1.2.3+abc123", false)]
    [InlineData("1.2.2", "1.2.3", false)]
    public void UpdateComparisonDetectsNewerVersions(string latest, string current, bool expected)
    {
        Assert.Equal(expected, ClipUpdateService.IsNewerVersion(latest, current));
    }
}
