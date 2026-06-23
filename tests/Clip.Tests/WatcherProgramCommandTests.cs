namespace Clip.Tests;

public sealed class WatcherProgramCommandTests
{
    [Fact]
    public void WatcherHelperSupportsCommandPaletteHistoryActions()
    {
        var program = File.ReadAllText(RepoPath("src", "Clip.Watcher", "Program.cs"));

        Assert.Contains("case \"rename\":", program);
        Assert.Contains("Store.Rename", program);
        Assert.Contains("case \"copy-path\":", program);
        Assert.Contains("CopyPath(id)", program);
        Assert.Contains("rename <id> <title>", program);
        Assert.Contains("copy-path <id>", program);
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
