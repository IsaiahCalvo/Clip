namespace Clip.Tests;

public sealed class CommandPaletteInstallScriptTests
{
    [Fact]
    public void InstallScriptCanRelaunchElevatedWhenMachineRootTrustIsRequired()
    {
        var script = File.ReadAllText(RepoPath("tools", "Install-ClipCommandPalettePackage.ps1"));

        Assert.Contains("[switch]$ElevateIfNeeded", script);
        Assert.Contains("[string]$ElevatedLogPath", script);
        Assert.Contains("Start-Transcript", script);
        Assert.Contains("Get-Content -Tail", script);
        Assert.Contains("-Verb RunAs", script);
        Assert.Contains("-TrustDevCertificate", script);
        Assert.Contains("LocalMachine Root", script);
        Assert.Contains("Set-ClipCommandPaletteHotkey", script);
        Assert.Contains("configure-command-palette", script);
        Assert.Contains("clip.history", script);
        Assert.Contains("x-cmdpal://reload", script);
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
