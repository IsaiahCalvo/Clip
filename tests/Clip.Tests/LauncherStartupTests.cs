namespace Clip.Tests;

public sealed class LauncherStartupTests
{
    [Fact]
    public void ManagedLauncherStartsShellAsResidentWhenOpeningClip()
    {
        // The launcher must start the single-process shell (Clip.exe) as the resident host so it
        // owns the Alt+V toggle — NOT "Clip.Watcher.exe watch", the legacy host whose hotkey only
        // ever re-shows (which broke closing the palette with Alt+V).
        var source = File.ReadAllText(RepoPath("src", "Clip.Launcher", "Program.cs"));

        Assert.Contains("return StartShell(show);", source);
        Assert.Contains("\"Clip.exe\"", source);
        Assert.Contains("--tray-action", source);
        Assert.DoesNotContain("commandLine.Append(\" watch\")", source);
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
