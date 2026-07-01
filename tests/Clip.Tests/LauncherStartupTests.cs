namespace Clip.Tests;

public sealed class LauncherStartupTests
{
    [Fact]
    public void ManagedLauncherStartsWatcherWithShowWhenOpeningClip()
    {
        var source = File.ReadAllText(RepoPath("src", "Clip.Launcher", "Program.cs"));

        Assert.Contains("return StartWatcher(show);", source);
        Assert.Contains("commandLine.Append(\" watch\")", source);
        Assert.Contains("commandLine.Append(\" --show\")", source);
        Assert.DoesNotContain("commandLine.Append(\" --palette-session\")", source);
    }

    [Fact]
    public void PowerShellLauncherShowsThroughWatcher()
    {
        var source = File.ReadAllText(RepoPath("Start-Clip.ps1"));
        var watcherBranch = source[
            source.IndexOf("if ($watcherExe -and $exe)", StringComparison.Ordinal)..
            source.IndexOf("if ($exe) {", StringComparison.Ordinal)];

        Assert.Contains("$watcherArgs += \"--show\"", watcherBranch);
        Assert.DoesNotContain("Start-Process -FilePath $exe -ArgumentList @(\"--palette-session\")", watcherBranch);
    }

    private static string RepoPath(params string[] parts)
    {
        var directory = AppContext.BaseDirectory;
        while (directory is not null && !File.Exists(Path.Combine(directory, "Clip.sln")))
        {
            directory = Directory.GetParent(directory)?.FullName;
        }

        return Path.Combine(directory ?? AppContext.BaseDirectory, Path.Combine(parts));
    }
}
