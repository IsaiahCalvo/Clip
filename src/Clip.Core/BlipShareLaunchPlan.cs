using System.Diagnostics;

namespace Clip.Core;

public sealed class BlipShareLaunchPlan
{
    public const string ExecutableName = "blip.exe";
    public const string ProcessName = "Blip";

    private BlipShareLaunchPlan(IReadOnlyList<string> filePaths)
    {
        FilePaths = filePaths;
        LaunchArguments = filePaths.SelectMany(static path => new[] { "--file", path }).ToArray();
    }

    public IReadOnlyList<string> FilePaths { get; }
    public IReadOnlyList<string> LaunchArguments { get; }

    public static bool IsInstalled()
    {
        return IsInstalled(
            Environment.GetEnvironmentVariable("PATH"),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            File.Exists);
    }

    public static bool IsInstalled(string? pathValue, string? localAppData, Func<string, bool> fileExists)
    {
        foreach (var directory in SearchDirectories(pathValue, localAppData))
        {
            if (fileExists(Path.Combine(directory, ExecutableName)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True when Blip is running but cannot be handed anything.
    ///
    /// Blip is single-instance: launching <c>blip.exe --file ...</c> starts a second process that
    /// passes its arguments to the running one over a unix socket at
    /// <c>%TEMP%\net.blip.desktop\ui.sock</c> and exits. The lock file beside it is held open for
    /// the life of the primary instance, so it survives; the socket file is not, and Windows temp
    /// cleanup eventually deletes it out from under a long-running Blip. After that every launch —
    /// from Clip, from Explorer, from the Start menu — finds the lock (so it must be the second
    /// instance), fails to connect to a socket that is gone, and dies with Blip's own
    /// "SingleInstance failure" dialog. Seen on 2026-08-21 with Blip up since 08-13; restarting
    /// Blip recreated the socket and sharing worked again.
    ///
    /// Clip cannot repair that from outside Blip, but it can refuse to launch into it and say why.
    /// </summary>
    public static bool IsRunningWithBrokenHandoff()
    {
        return IsRunningWithBrokenHandoff(
            Process.GetProcessesByName(ProcessName).Length > 0,
            HandoffSocketPath(),
            File.Exists);
    }

    public static bool IsRunningWithBrokenHandoff(bool blipIsRunning, string socketPath, Func<string, bool> fileExists)
    {
        return blipIsRunning && !fileExists(socketPath);
    }

    public static string HandoffSocketPath()
    {
        return Path.Combine(Path.GetTempPath(), "net.blip.desktop", "ui.sock");
    }

    public static BlipShareLaunchPlan Create(ClipboardSharePayload payload)
    {
        if (payload.FilePaths.Count == 0)
        {
            throw new InvalidOperationException("Blip needs at least one file.");
        }

        return new BlipShareLaunchPlan(payload.FilePaths);
    }

    private static IEnumerable<string> SearchDirectories(string? pathValue, string? localAppData)
    {
        if (!string.IsNullOrWhiteSpace(pathValue))
        {
            foreach (var directory in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                yield return directory;
            }
        }

        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            yield return Path.Combine(localAppData, "Microsoft", "WindowsApps");
        }
    }
}
