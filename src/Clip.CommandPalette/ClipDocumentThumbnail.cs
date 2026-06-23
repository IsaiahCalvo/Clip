using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Clip.Core;

namespace Clip.CommandPalette;

/// <summary>
/// Lazily produces a first-page PNG thumbnail for PDF / Office / Visio file items so the palette
/// preview can show a real rendered page instead of just the file path. The actual rendering is
/// delegated to the Clip.Watcher.exe "preview-thumb" verb, which already owns the heavyweight
/// first-page renderers (pdftoppm for PDF, Office/Visio COM for documents). The net9 extension never
/// references those dependencies itself, keeping cold-open fast and the package lean.
///
/// The PNG is cached under %TEMP%\Clip\PaletteThumbs keyed by source path + last-write time + size,
/// so an unchanged file renders at most once. Callers MUST invoke this off the list-render path
/// (e.g. on selection / preview), never while building list rows.
/// </summary>
internal static class ClipDocumentThumbnail
{
    // Document extensions that produce a first-page image. PDF is rendered via pdftoppm; the rest go
    // through the Office/Visio COM renderers in Clip.Watcher (StaticDocumentPreviewRenderer).
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf",
        ".docx", ".doc",
        ".xlsx", ".xlsm", ".xls",
        ".pptx", ".ppt",
        ".vsdx", ".vsd",
    };

    private static readonly TimeSpan RenderTimeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// True when the single file backing a Files item is a document type we can render a first-page
    /// thumbnail for (and is not already an image, which has its own inline preview path).
    /// </summary>
    public static bool IsRenderableDocument(ClipboardHistoryListItem item)
    {
        if (!string.Equals(item.Kind, "Files", StringComparison.OrdinalIgnoreCase) || item.FilePaths.Count != 1)
        {
            return false;
        }

        return IsRenderableDocument(item.FilePaths[0]);
    }

    public static bool IsRenderableDocument(string? path) =>
        !string.IsNullOrWhiteSpace(path) && SupportedExtensions.Contains(Path.GetExtension(path!));

    /// <summary>
    /// Cheap, render-free lookup: returns the cached first-page PNG for <paramref name="sourcePath"/>
    /// only if it has already been rendered, otherwise null. Safe to call on the list-render path
    /// (no process launch, no directory creation) — it is just a fingerprint hash plus a
    /// File.Exists check, so the details pane can show a thumbnail that a prior preview produced
    /// without ever blocking the list.
    /// </summary>
    public static string? TryGetCachedThumbnailPng(string? sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !IsRenderableDocument(sourcePath))
        {
            return null;
        }

        try
        {
            if (!File.Exists(sourcePath))
            {
                return null;
            }

            var cachedPng = CachePathFor(sourcePath!, createDirectory: false);
            return File.Exists(cachedPng) ? cachedPng : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Returns the path to a cached first-page PNG for <paramref name="sourcePath"/>, rendering it
    /// on demand via the Watcher helper if not already cached. Returns null when the source is not a
    /// renderable document, the file is missing, the helper is unavailable, or rendering fails (the
    /// caller should fall back to the text/path preview). Safe to call repeatedly: an unchanged file
    /// short-circuits to the cached PNG without launching the helper. This MAY launch the helper
    /// process, so call it only off the list-render path (e.g. when building the preview page).
    /// </summary>
    public static string? TryGetThumbnailPng(string? sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !IsRenderableDocument(sourcePath) || !File.Exists(sourcePath))
        {
            return null;
        }

        try
        {
            var cachedPng = CachePathFor(sourcePath!, createDirectory: true);
            if (File.Exists(cachedPng))
            {
                return cachedPng;
            }

            return TryRenderViaHelper(sourcePath!, cachedPng) ? cachedPng : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool TryRenderViaHelper(string sourcePath, string outPng)
    {
        var executable = ClipExecutableLocator.Resolve("Clip.Watcher.exe");
        if (executable is null)
        {
            return false;
        }

        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = executable,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    WorkingDirectory = Path.GetDirectoryName(executable) ?? AppContext.BaseDirectory,
                },
            };
            process.StartInfo.ArgumentList.Add("preview-thumb");
            process.StartInfo.ArgumentList.Add(sourcePath);
            process.StartInfo.ArgumentList.Add(outPng);

            if (!process.Start())
            {
                return false;
            }

            if (!process.WaitForExit((int)RenderTimeout.TotalMilliseconds))
            {
                TryKill(process);
                return false;
            }

            return process.ExitCode == 0 && File.Exists(outPng);
        }
        catch
        {
            return false;
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch
        {
        }
    }

    // Cache key = source path + last-write-time + size, mirroring the renderers' own fingerprinting,
    // so editing a file in place invalidates the thumbnail.
    private static string CachePathFor(string sourcePath, bool createDirectory)
    {
        var info = new FileInfo(sourcePath);
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            sourcePath + "|" + info.LastWriteTimeUtc.Ticks + "|" + info.Length)));
        var cacheRoot = Path.Combine(Path.GetTempPath(), "Clip", "PaletteThumbs");
        if (createDirectory)
        {
            Directory.CreateDirectory(cacheRoot);
        }

        return Path.Combine(cacheRoot, fingerprint + ".png");
    }
}
