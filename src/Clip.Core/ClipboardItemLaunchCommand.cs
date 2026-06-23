using System.Diagnostics;

namespace Clip.Core;

public static class ClipboardItemLaunchCommand
{
    public static ProcessStartInfo? CreateOpenStartInfo(ClipboardHistoryItem item, string? appPath = null)
    {
        var target = OpenTarget(item);
        if (string.IsNullOrWhiteSpace(target))
        {
            return null;
        }

        return string.IsNullOrWhiteSpace(appPath)
            ? new ProcessStartInfo(target) { UseShellExecute = true }
            : new ProcessStartInfo(appPath, QuoteArgument(target)) { UseShellExecute = true };
    }

    public static ProcessStartInfo? CreateRevealStartInfo(ClipboardHistoryItem item) =>
        FileExplorerReveal.CreateStartInfo(ClipboardItemRevealTarget.GetPath(item));

    private static string? OpenTarget(ClipboardHistoryItem item)
    {
        if (item.Kind == ClipboardItemKind.Link &&
            ClipboardLinkDetector.TryNormalize(SafeTextPayload(item), out var normalized))
        {
            return normalized;
        }

        if (item.Kind == ClipboardItemKind.Image &&
            !string.IsNullOrWhiteSpace(item.AssetPath) &&
            File.Exists(item.AssetPath))
        {
            return item.AssetPath;
        }

        return ClipboardItemRevealTarget.GetPath(item);
    }

    private static string SafeTextPayload(ClipboardHistoryItem item)
    {
        if (!string.IsNullOrEmpty(item.Text))
        {
            return item.Text;
        }

        if (!string.IsNullOrEmpty(item.Preview))
        {
            return item.Preview;
        }

        return " ";
    }

    private static string QuoteArgument(string value) =>
        "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
}
