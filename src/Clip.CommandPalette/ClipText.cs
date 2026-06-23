namespace Clip.CommandPalette;

internal static class ClipText
{
    public static string TrimForDisplay(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        value = value.ReplaceLineEndings(" ").Trim();
        if (value.Length <= maxLength)
        {
            return value;
        }

        return value[..Math.Max(0, maxLength - 3)] + "...";
    }
}
