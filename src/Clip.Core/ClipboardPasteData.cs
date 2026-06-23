namespace Clip.Core;

public enum PasteFormatPreference
{
    PlainText,
    OriginalFormatting,
}

public sealed record ClipboardPastePayload(string Text, string? Html, string? Rtf);

public static class ClipboardPasteData
{
    public static bool HasOriginalFormatting(ClipboardHistoryItem item)
    {
        return item.HasOriginalFormatting ||
            !string.IsNullOrWhiteSpace(item.HtmlText) ||
            !string.IsNullOrWhiteSpace(item.RtfText);
    }

    public static ClipboardPastePayload Create(ClipboardHistoryItem item, PasteFormatPreference preference)
    {
        var text = item.Text ?? item.Preview ?? string.Empty;
        if (preference != PasteFormatPreference.OriginalFormatting)
        {
            return new ClipboardPastePayload(text, null, null);
        }

        return new ClipboardPastePayload(
            text,
            string.IsNullOrWhiteSpace(item.HtmlText) ? null : item.HtmlText,
            string.IsNullOrWhiteSpace(item.RtfText) ? null : item.RtfText);
    }
}
