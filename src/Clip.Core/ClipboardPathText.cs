namespace Clip.Core;

public static class ClipboardPathText
{
    public static bool TryParseExistingFilePaths(string? text, out List<string> paths)
    {
        paths = [];
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        foreach (var rawLine in text.Split(["\r\n", "\n", "\r"], StringSplitOptions.RemoveEmptyEntries))
        {
            var path = rawLine.Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(path) || (!File.Exists(path) && !Directory.Exists(path)))
            {
                paths = [];
                return false;
            }

            paths.Add(path);
        }

        return paths.Count > 0;
    }
}
