using System.IO;

namespace Clip.Shell;

internal static class TrayActionRequest
{
    private static readonly string RequestPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Clip",
        "tray-action.request");

    public static void Save(string action)
    {
        if (string.IsNullOrWhiteSpace(action))
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(RequestPath)!);
        File.WriteAllText(RequestPath, action.Trim());
    }

    public static string? Consume()
    {
        try
        {
            if (!File.Exists(RequestPath))
            {
                return null;
            }

            var action = File.ReadAllText(RequestPath).Trim();
            File.Delete(RequestPath);
            return string.IsNullOrWhiteSpace(action) ? null : action;
        }
        catch
        {
            return null;
        }
    }
}
