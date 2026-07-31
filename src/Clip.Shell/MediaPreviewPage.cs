using System;
using System.IO;
using System.Text;

namespace Clip.Shell;

/// <summary>
/// Builds the little player page shown for video and audio files.
///
/// Clip already hosts a WebView2 for HTML previews, so the browser's own media element gives real
/// playback — seeking, speed, volume — without adding a media stack to the app. Everything is
/// local: the page is generated here and the file is loaded straight off disk.
/// </summary>
internal static class MediaPreviewPage
{
    /// <param name="mediaUrl">
    /// URL the player loads from. This is a virtual-host address rather than a file:// path,
    /// because a generated page has no file-system origin and the browser would refuse it.
    /// </param>
    public static string Build(string filePath, string mediaUrl, bool isVideo, string backgroundHex, string textHex)
    {
        var uri = mediaUrl;
        var mime = MimeFor(Path.GetExtension(filePath));
        var element = isVideo ? "video" : "audio";

        var page = new StringBuilder();
        page.Append(
            $$"""
            <!doctype html>
            <html>
            <head>
            <meta charset="utf-8">
            <style>
              :root { color-scheme: dark; }
              html, body {
                margin: 0;
                height: 100%;
                background: {{backgroundHex}};
                color: {{textHex}};
                font-family: "Segoe UI Variable Text", "Segoe UI", sans-serif;
                overflow: hidden;
              }
              .wrap {
                height: 100%;
                display: flex;
                flex-direction: column;
                align-items: center;
                justify-content: center;
                gap: 14px;
                padding: 12px;
                box-sizing: border-box;
              }
              video { max-width: 100%; max-height: calc(100% - 46px); border-radius: 6px; background: #000; }
              audio { width: min(560px, 100%); }
              .missing { font-size: 13px; opacity: .7; text-align: center; padding: 0 24px; }
            </style>
            </head>
            <body>
              <div class="wrap">
                <!-- The browser's own controls already cover play/pause, seeking, volume and
                     playback speed, so no extra controls are drawn under them. -->
                <{{element}} id="player" src="{{uri}}" type="{{mime}}" controls preload="metadata"></{{element}}>
              </div>
              <script>
                const player = document.getElementById('player');
                player.addEventListener('error', () => {
                  document.querySelector('.wrap').innerHTML =
                    '<div class="missing">This format cannot be played here. Use Open to play it in your default app.</div>';
                });
                document.addEventListener('keydown', e => {
                  if (e.code === 'Space') { e.preventDefault(); player.paused ? player.play() : player.pause(); }
                });
              </script>
            </body>
            </html>
            """);

        return page.ToString();
    }

    private static string MimeFor(string extension) => extension.ToLowerInvariant() switch
    {
        ".mp4" or ".m4v" => "video/mp4",
        ".webm" => "video/webm",
        ".ogv" => "video/ogg",
        ".mov" => "video/quicktime",
        ".mkv" => "video/x-matroska",
        ".avi" => "video/x-msvideo",
        ".mp3" => "audio/mpeg",
        ".m4a" => "audio/mp4",
        ".wav" => "audio/wav",
        ".ogg" or ".oga" => "audio/ogg",
        ".flac" => "audio/flac",
        ".aac" => "audio/aac",
        ".wma" => "audio/x-ms-wma",
        _ => "application/octet-stream",
    };
}
