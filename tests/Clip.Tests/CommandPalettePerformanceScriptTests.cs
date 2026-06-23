namespace Clip.Tests;

public sealed class CommandPalettePerformanceScriptTests
{
    [Fact]
    public void PerformanceScriptValidatesCommandPaletteHotkeyForCommandPaletteMode()
    {
        var script = File.ReadAllText(RepoPath("tools", "Measure-ClipPerformance.ps1"));

        Assert.Contains("function Get-ClipOpenMode", script);
        Assert.Contains("function Measure-CommandPaletteHotkeyReadiness", script);
        Assert.Contains("function Test-CommandPaletteAltVHotkeyOwned", script);
        Assert.Contains("$openMode -eq \"CommandPalette\"", script);
        Assert.Contains("$watcherHotkeyShowSkipped = $true", script);
    }

    [Fact]
    public void PerformanceScriptFallsBackToExtensionProcessWhenAppxQueryFails()
    {
        var script = File.ReadAllText(RepoPath("tools", "Measure-ClipPerformance.ps1"));

        Assert.Contains("function Get-CommandPalettePackageFullNameFromPath", script);
        Assert.Contains("try {", script);
        Assert.Contains("Get-AppxPackage -Name Clip.CommandPalette", script);
        Assert.Contains("Get-CommandPalettePackageFullNameFromPath $extensionProcess.Path", script);
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
