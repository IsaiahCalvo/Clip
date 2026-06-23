using System.Text;

namespace Clip.Core;

public static class TextFilePreviewReader
{
    public const string TruncatedMarker = "... preview truncated ...";

    public static string Read(string path, int maxChars)
    {
        ValidateMaxChars(maxChars);
        using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var buffer = new char[maxChars + 1];
        var read = reader.ReadBlock(buffer, 0, buffer.Length);
        return FormatPreview(buffer, read, maxChars);
    }

    public static async Task<string> ReadAsync(string path, int maxChars, CancellationToken cancellationToken = default)
    {
        ValidateMaxChars(maxChars);
        using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var buffer = new char[maxChars + 1];
        var read = await reader.ReadBlockAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
        return FormatPreview(buffer, read, maxChars);
    }

    public static string Format(string text, int maxChars)
    {
        ValidateMaxChars(maxChars);
        return text.Length > maxChars
            ? AppendTruncatedMarker(text[..maxChars])
            : text;
    }

    private static string FormatPreview(char[] buffer, int read, int maxChars)
    {
        var text = new string(buffer, 0, Math.Min(read, maxChars));
        return read > maxChars ? AppendTruncatedMarker(text) : text;
    }

    private static string AppendTruncatedMarker(string text)
    {
        return text + $"{Environment.NewLine}{Environment.NewLine}{TruncatedMarker}";
    }

    private static void ValidateMaxChars(int maxChars)
    {
        if (maxChars <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxChars), "Preview length must be positive.");
        }
    }
}
