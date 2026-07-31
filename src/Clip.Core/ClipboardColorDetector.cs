using System.Text.RegularExpressions;

namespace Clip.Core;

/// <summary>
/// Detects and normalizes hex color text (for example "#3b5bdb", "3B5BDB", "#abc").
/// Shared by the capture paths and the history store so every caller agrees on
/// what a color is and what its canonical text looks like.
/// </summary>
public static partial class ClipboardColorDetector
{
    /// <summary>
    /// Returns true when <paramref name="text"/> is a hex color. <paramref name="hex"/>
    /// receives the canonical form: "#" + six uppercase hex digits.
    /// Bare hex (no leading "#") is only treated as a color when it came from a
    /// known color picker, so ordinary six-character hex-looking words stay text.
    /// </summary>
    public static bool TryNormalize(string? text, string? source, out string hex)
    {
        hex = string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var trimmed = text.Trim();
        var match = HexColorRegex().Match(trimmed);
        if (!match.Success)
        {
            return false;
        }

        if (!trimmed.StartsWith('#') && !LooksLikeColorPicker(source))
        {
            return false;
        }

        var value = match.Groups[1].Value;
        if (value.Length == 3)
        {
            value = string.Concat(value.Select(ch => $"{ch}{ch}"));
        }

        hex = "#" + value.ToUpperInvariant();
        return true;
    }

    private static bool LooksLikeColorPicker(string? source) =>
        source?.Contains("ColorPicker", StringComparison.OrdinalIgnoreCase) == true ||
        source?.Contains("PowerToys", StringComparison.OrdinalIgnoreCase) == true ||
        source?.Equals("Clip", StringComparison.OrdinalIgnoreCase) == true ||
        source?.Equals("Clip.Shell", StringComparison.OrdinalIgnoreCase) == true;

    [GeneratedRegex(@"^#?([0-9a-fA-F]{3}|[0-9a-fA-F]{6})$", RegexOptions.CultureInvariant)]
    private static partial Regex HexColorRegex();
}
