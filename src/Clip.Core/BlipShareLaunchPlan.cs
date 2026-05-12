namespace Clip.Core;

public sealed class BlipShareLaunchPlan
{
    public const string ExecutableName = "blip.exe";

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
