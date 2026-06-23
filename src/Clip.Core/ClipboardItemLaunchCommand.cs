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

    // The path/URL this item would open. Surfaces are expected to use this to decide whether to
    // offer an "Open with…" action (non-null) and to feed app discovery / recent-app tracking.
    public static string? GetOpenTarget(ClipboardHistoryItem item) => OpenTarget(item);

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
