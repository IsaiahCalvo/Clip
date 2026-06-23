namespace Clip.Core;

/// <summary>
/// Shared, allocation-light helpers for previewing the contents of a copied file.
/// Used by both the standalone preview pane and the Command Palette details/preview.
/// Only the head of the file is read (via <see cref="TextFilePreviewReader"/>), so this
/// stays fast and bounded regardless of file size and is safe to call on item selection.
/// </summary>
public static class FilePreview
{
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".text", ".md", ".markdown", ".rst", ".log", ".csv", ".tsv",
        ".json", ".jsonc", ".xml", ".yaml", ".yml", ".toml", ".ini", ".cfg", ".conf", ".config", ".env",
        ".cs", ".vb", ".fs", ".js", ".jsx", ".ts", ".tsx", ".py", ".rb", ".go", ".rs", ".java", ".kt",
        ".c", ".h", ".cpp", ".hpp", ".cc", ".m", ".swift", ".php", ".pl", ".lua", ".r", ".scala", ".dart",
        ".html", ".htm", ".css", ".scss", ".sass", ".less", ".sql", ".graphql",
        ".ps1", ".psm1", ".psd1", ".sh", ".bash", ".zsh", ".bat", ".cmd",
        ".gitignore", ".gitattributes", ".editorconfig", ".dockerfile", ".props", ".targets",
        ".csproj", ".vbproj", ".fsproj", ".sln", ".gradle", ".properties", ".manifest",
    };

    /// <summary>True when the path has a known text-like extension that is safe to render inline.</summary>
    public static bool IsTextFile(string? path) =>
        !string.IsNullOrWhiteSpace(path) && TextExtensions.Contains(Path.GetExtension(path!));

    /// <summary>
    /// Reads a bounded text excerpt for a single copied text file. Returns false (and an empty
    /// excerpt) for non-text files, multi-file selections, missing files, or read errors —
    /// callers should fall back to showing the path(s).
    /// </summary>
    public static bool TryReadTextExcerpt(IReadOnlyList<string>? filePaths, int maxChars, out string excerpt)
    {
        excerpt = string.Empty;
        if (filePaths is null || filePaths.Count != 1)
        {
            return false;
        }

        var path = filePaths[0];
        if (!IsTextFile(path) || !File.Exists(path))
        {
            return false;
        }

        try
        {
            excerpt = TextFilePreviewReader.Read(path, maxChars);
            return !string.IsNullOrWhiteSpace(excerpt);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
