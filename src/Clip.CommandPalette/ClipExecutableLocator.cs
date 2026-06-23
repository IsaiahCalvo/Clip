using Microsoft.Win32;

namespace Clip.CommandPalette;

internal static class ClipExecutableLocator
{
    private static readonly string[] InstallRoots =
    [
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Programs", "Clip"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Clip"),
    ];

    public static string? Resolve(string executableName)
    {
        if (string.IsNullOrWhiteSpace(executableName))
        {
            return null;
        }

        foreach (var directory in CandidateDirectories())
        {
            var candidate = Path.Combine(directory, executableName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static IEnumerable<string> CandidateDirectories()
    {
        var baseDirectory = AppContext.BaseDirectory;
        if (!string.IsNullOrWhiteSpace(baseDirectory))
        {
            yield return baseDirectory;
        }

        var startupExe = TryGetStartupExecutable();
        if (!string.IsNullOrWhiteSpace(startupExe))
        {
            var startupDirectory = Path.GetDirectoryName(startupExe);
            if (!string.IsNullOrWhiteSpace(startupDirectory))
            {
                yield return startupDirectory;
            }
        }

        foreach (var root in InstallRoots)
        {
            yield return root;
        }
    }

    private static string? TryGetStartupExecutable()
    {
        try
        {
            using var runKey = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            if (runKey is null)
            {
                return null;
            }

            foreach (var valueName in runKey.GetValueNames())
            {
                if (!valueName.Contains("Clip", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (runKey.GetValue(valueName) is string command &&
                    TryExtractExecutablePath(command, out var executable) &&
                    File.Exists(executable))
                {
                    return executable;
                }
            }
        }
        catch
        {
        }

        return null;
    }

    private static bool TryExtractExecutablePath(string command, out string executable)
    {
        executable = string.Empty;
        command = Environment.ExpandEnvironmentVariables(command).Trim();
        if (command.Length == 0)
        {
            return false;
        }

        if (command[0] == '"')
        {
            var endQuote = command.IndexOf('"', 1);
            if (endQuote > 1)
            {
                executable = command[1..endQuote];
                return true;
            }
        }

        var exeIndex = command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exeIndex >= 0)
        {
            executable = command[..(exeIndex + 4)].Trim();
            return true;
        }

        var firstSpace = command.IndexOf(' ');
        executable = firstSpace > 0 ? command[..firstSpace] : command;
        return executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
    }
}
