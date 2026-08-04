using System.Reflection;
using Clip.Core;

namespace Clip.Tests;

public sealed class BlipShareLaunchPlanCoverageTests
{
    [Fact]
    public void ParameterlessIsInstalledMatchesExplicitOverloadWithRealEnvironment()
    {
        var expected = BlipShareLaunchPlan.IsInstalled(
            Environment.GetEnvironmentVariable("PATH"),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            File.Exists);

        Assert.Equal(expected, BlipShareLaunchPlan.IsInstalled());
    }

    [Fact]
    public void IsInstalledFallsBackToWindowsAppsUnderLocalAppData()
    {
        var localAppData = @"C:\Users\me\AppData\Local";
        var expectedProbe = Path.Combine(localAppData, "Microsoft", "WindowsApps", BlipShareLaunchPlan.ExecutableName);

        var installed = BlipShareLaunchPlan.IsInstalled(
            null,
            localAppData,
            candidate => string.Equals(candidate, expectedProbe, StringComparison.OrdinalIgnoreCase));

        Assert.True(installed);
    }

    [Fact]
    public void IsInstalledIgnoresWindowsAppsWhenLocalAppDataIsMissing()
    {
        // With no PATH and no local app data there is nowhere to probe, even when every
        // candidate would match.
        Assert.False(BlipShareLaunchPlan.IsInstalled(null, null, _ => true));
        Assert.False(BlipShareLaunchPlan.IsInstalled(null, "   ", _ => true));
    }

    [Fact]
    public void CreateRejectsPayloadWithoutFiles()
    {
        // The payload factory never returns an empty file list, so the guard is only reachable
        // through the private constructor.
        var ctor = typeof(ClipboardSharePayload).GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance).Single();
        var emptyPayload = (ClipboardSharePayload)ctor.Invoke([Array.Empty<string>(), Array.Empty<string>()]);

        Assert.Throws<InvalidOperationException>(() => BlipShareLaunchPlan.Create(emptyPayload));
    }
}
