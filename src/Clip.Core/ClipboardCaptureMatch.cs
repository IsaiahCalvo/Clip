namespace Clip.Core;

/// <summary>
/// Answers "is this pending capture still the thing sitting on the clipboard?".
///
/// Capture paths debounce text copies and re-check the clipboard before saving, so a
/// transient copy (an app that stages the clipboard then overwrites it) is not stored.
/// That re-check cannot be a raw string comparison: some kinds rewrite <see cref="ClipboardHistoryItem.Text"/>
/// while capturing. Colors are normalized to "#RRGGBB" uppercase, so a copied "#3b5bdb"
/// no longer equals the pending item's text and the item is wrongly dropped.
/// </summary>
public static class ClipboardCaptureMatch
{
    public static bool MatchesClipboardText(ClipboardHistoryItem? pending, string? clipboardText)
    {
        if (pending is null || clipboardText is null)
        {
            return false;
        }

        if (string.Equals(clipboardText, pending.Text, StringComparison.Ordinal))
        {
            return true;
        }

        if (pending.Kind == ClipboardItemKind.Color &&
            ClipboardColorDetector.TryNormalize(clipboardText, pending.SourceApplication, out var hex))
        {
            return string.Equals(hex, pending.Text, StringComparison.OrdinalIgnoreCase);
        }

        // Trailing whitespace is not a content change; a copy that only differs by the
        // newline the source app appended is still the same clipboard payload.
        return pending.Text is not null &&
            string.Equals(clipboardText.Trim(), pending.Text.Trim(), StringComparison.Ordinal) &&
            !string.IsNullOrWhiteSpace(pending.Text);
    }
}
