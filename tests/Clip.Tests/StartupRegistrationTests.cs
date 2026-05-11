using Clip.Shell;

namespace Clip.Tests;

public sealed class StartupRegistrationTests : IDisposable
{
    private readonly string _valueName = "Clip.Tests." + Guid.NewGuid().ToString("N");

    [Fact]
    public void DefaultStartupPreferenceIsEnabled()
    {
        Assert.True(StartupRegistration.DefaultEnabled);
    }

    [Fact]
    public void SetEnabledWritesAndRemovesStartupValue()
    {
        var fakeExe = Path.Combine(Path.GetTempPath(), "Clip.Tests", Guid.NewGuid().ToString("N"), "Clip.Shell.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(fakeExe)!);
        File.WriteAllText(fakeExe, "");

        StartupRegistration.SetEnabled(true, _valueName, fakeExe);

        Assert.True(StartupRegistration.IsEnabled(_valueName));
        Assert.Equal($"\"{fakeExe}\"", StartupRegistration.CurrentValue(_valueName));

        StartupRegistration.SetEnabled(false, _valueName, fakeExe);

        Assert.False(StartupRegistration.IsEnabled(_valueName));
        Assert.Null(StartupRegistration.CurrentValue(_valueName));
    }

    public void Dispose()
    {
        try
        {
            StartupRegistration.SetEnabled(false, _valueName, "unused");
        }
        catch
        {
        }
    }
}
