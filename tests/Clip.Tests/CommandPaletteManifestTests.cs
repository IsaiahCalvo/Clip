namespace Clip.Tests;

public sealed class CommandPaletteManifestTests
{
    [Fact]
    public void PackageManifestUsesConcreteResourceLanguage()
    {
        var manifest = File.ReadAllText(RepoPath("src", "Clip.CommandPalette", "Package.appxmanifest"));

        Assert.DoesNotContain("x-generate", manifest, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Resource Language=\"en-US\"", manifest);
        Assert.DoesNotContain("10.0.19041.0", manifest, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("MinVersion=\"10.0.22000.0\"", manifest);
        Assert.DoesNotContain(">Clip Command Palette<", manifest, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<DisplayName>Clip</DisplayName>", manifest);
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
