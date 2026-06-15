namespace Clip.Core;

public static class ClipboardItemRevealTarget
{
    public static string? GetPath(ClipboardHistoryItem item)
    {
        if (item.Kind == ClipboardItemKind.Files)
        {
            return item.FilePaths.FirstOrDefault(path => File.Exists(path) || Directory.Exists(path));
        }

        if (item.Kind == ClipboardItemKind.Image &&
            !string.IsNullOrWhiteSpace(item.AssetPath) &&
            File.Exists(item.AssetPath))
        {
            return item.AssetPath;
        }

        if (item.Kind is ClipboardItemKind.Text or ClipboardItemKind.Link &&
            ClipboardPathText.TryParseExistingFilePaths(item.Text ?? item.Preview, out var paths))
        {
            return paths.FirstOrDefault();
        }

        return null;
    }
}
