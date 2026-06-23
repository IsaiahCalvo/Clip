namespace Clip.Tests;

public sealed class CommandPaletteProgramTests
{
    [Fact]
    public void ProgramUsesCsWinRtRegistrationShapeWithInstalledSdkMetadata()
    {
        var program = File.ReadAllText(RepoPath("src", "Clip.CommandPalette", "Program.cs"));
        var project = File.ReadAllText(RepoPath("src", "Clip.CommandPalette", "Clip.CommandPalette.csproj"));

        Assert.Contains("using Shmuelie.WinRTServer.CsWinRT;", program);
        Assert.Contains("RegisterClass<ClipCommandPaletteExtension, IExtension>(() => extensionInstance)", program);
        Assert.Contains("<TargetFramework>net9.0-windows10.0.22000.0</TargetFramework>", project);
        Assert.Contains("Microsoft.CommandPalette.Extensions\" Version=\"0.9.260303001\"", project);
        Assert.Contains("Shmuelie.WinRTServer\" Version=\"2.1.1\"", project);
        Assert.Contains("<AssetTargetFallback>net8.0-windows10.0.22000.0</AssetTargetFallback>", project);
        Assert.Contains("<CsWinRTWindowsMetadata>10.0.19041.0</CsWinRTWindowsMetadata>", project);
        Assert.Contains("<WindowsSdkPackageVersion>10.0.26100.57</WindowsSdkPackageVersion>", project);
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
