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

    private static string ImageMarkdown(string path)
    {
        var uri = new Uri(path).AbsoluteUri;
        return $"![Clipboard image]({uri}?--x-cmdpal-fit=fit&--x-cmdpal-maxwidth=900&--x-cmdpal-maxheight=520)";
    }
}
