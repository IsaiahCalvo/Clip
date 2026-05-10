using Clip.Core;

namespace Clip.Tests;

public sealed class BlipShareLaunchPlanTests
{
    [Fact]
    public void IsInstalledFindsBlipInPath()
    {
        var path = string.Join(Path.PathSeparator, @"C:\Other", @"C:\Users\me\AppData\Local\Microsoft\WindowsApps");

        var installed = BlipShareLaunchPlan.IsInstalled(path, null, candidate => candidate.EndsWith(@"WindowsApps\blip.exe", StringComparison.OrdinalIgnoreCase));

        Assert.True(installed);
    }

    [Fact]
    public void IsInstalledReturnsFalseWhenBlipAliasIsMissing()
    {
        var installed = BlipShareLaunchPlan.IsInstalled(@"C:\Other", null, _ => false);

        Assert.False(installed);
    }

    [Fact]
    public void CreateUsesBlipExecutionAliasAndPayloadFiles()
    {
        var item = new ClipboardHistoryItem
        {
            Kind = ClipboardItemKind.Files,
            FilePaths = [Path.GetTempFileName()],
            Preview = "file",
        };

        try
        {
            var payload = ClipboardSharePayload.Create(item);
            var plan = BlipShareLaunchPlan.Create(payload);

            Assert.Equal("blip.exe", BlipShareLaunchPlan.ExecutableName);
            Assert.Equal(item.FilePaths, plan.FilePaths);
            Assert.Equal(["--file", item.FilePaths[0]], plan.LaunchArguments);
        }
        finally
        {
            File.Delete(item.FilePaths[0]);
        }
    }

}
