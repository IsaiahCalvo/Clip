using System.Diagnostics;

namespace Clip.Core;

public static class FileExplorerReveal
{
    public static ProcessStartInfo? CreateStartInfo(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        if (File.Exists(path))
        {
            return new ProcessStartInfo("explorer.exe", $"/select,{Quote(path)}")
            {
                UseShellExecute = true,
            };
        }

        if (Directory.Exists(path))
        {
            return new ProcessStartInfo("explorer.exe", Quote(path))
            {
                UseShellExecute = true,
            };
        }

        return null;
    }

    public static bool TryReveal(string? path)
    {
        var startInfo = CreateStartInfo(path);
        if (startInfo is null)
        {
            return false;
        }

        Process.Start(startInfo);
        return true;
    }

    private static string Quote(string value)
    {
        return "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }
}
