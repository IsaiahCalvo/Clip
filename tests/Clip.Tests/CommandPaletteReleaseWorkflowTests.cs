namespace Clip.Tests;

public sealed class CommandPaletteReleaseWorkflowTests
{
    [Fact]
    public void ReleaseWorkflowBuildsAndUploadsCommandPalettePackage()
    {
        var workflow = File.ReadAllText(RepoPath(".github", "workflows", "release.yml"));

        Assert.Contains("9.0.x", workflow);
        Assert.Contains("Build Command Palette extension package", workflow);
        Assert.Contains("Build-ClipCommandPalettePackage.ps1", workflow);
        Assert.Contains("Clip.CommandPalette_*.msix", workflow);
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
