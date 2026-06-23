using Clip.Core;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace Clip.CommandPalette;

internal sealed partial class ClipImagePreviewContent : MarkdownContent
{
    private ClipImagePreviewContent(string body)
    {
        Body = body;
    }

    public static bool TryCreate(ClipboardHistoryItem fullItem, out ClipImagePreviewContent content)
    {
        content = null!;
        if (fullItem.Kind != ClipboardItemKind.Image ||
            string.IsNullOrWhiteSpace(fullItem.AssetPath) ||
            !File.Exists(fullItem.AssetPath))
        {
            return false;
        }

        content = new ClipImagePreviewContent(ImageMarkdown(fullItem.AssetPath));
        return true;
    }

    /// <summary>
    /// Lazily renders (and caches) a first-page thumbnail for a PDF / Office / Visio file item and
    /// returns it as an embedded image. Runs only when the preview page is opened (GetContent), so
    /// the heavyweight render never touches cold-open or the list-render path. Returns false for
    /// non-document items or when rendering is unavailable, so the caller falls back to the text
    /// excerpt card.
    /// </summary>
    public static bool TryCreateDocumentThumbnail(ClipboardHistoryItem fullItem, out ClipImagePreviewContent content)
    {
        content = null!;
        if (fullItem.Kind != ClipboardItemKind.Files ||
            fullItem.FilePaths.Count != 1 ||
            !ClipDocumentThumbnail.IsRenderableDocument(fullItem.FilePaths[0]))
        {
            return false;
        }

        var thumbnailPng = ClipDocumentThumbnail.TryGetThumbnailPng(fullItem.FilePaths[0]);
        if (thumbnailPng is null)
        {
            return false;
        }

        content = new ClipImagePreviewContent(ImageMarkdown(thumbnailPng));
        return true;
    }

    private static string ImageMarkdown(string path)
    {
        var uri = new Uri(path).AbsoluteUri;
        return $"![Clipboard image]({uri}?--x-cmdpal-fit=fit&--x-cmdpal-maxwidth=900&--x-cmdpal-maxheight=520)";
    }
}
