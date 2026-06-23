namespace Clip.Tests;

public sealed class CommandProgramTests
{
    [Fact]
    public void CommandHelperExposesCommandPaletteConfiguration()
    {
        var program = File.ReadAllText(RepoPath("src", "Clip.Command", "Program.cs"));

        Assert.Contains("configure-command-palette", program);
        Assert.Contains("CommandPaletteSettings.ConfigureClipHistoryHotkey(enableExternalReloadForApply: true)", program);
        Assert.Contains("CommandPaletteSettings.RequestExternalReload()", program);
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
