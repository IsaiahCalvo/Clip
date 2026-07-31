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
    public static string Build(string filePath, string mediaUrl, bool isVideo, string backgroundHex, string textHex, string accentHex)
    {
        var uri = mediaUrl;
        var mime = MimeFor(Path.GetExtension(filePath));
        var element = isVideo ? "video" : "audio";
        var name = System.Net.WebUtility.HtmlEncode(Path.GetFileName(filePath));

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
              .name { font-size: 12px; opacity: .72; text-align: center; word-break: break-all; }
              .speed { display: flex; gap: 6px; align-items: center; font-size: 12px; opacity: .85; }
              .speed button {
                background: transparent;
                color: inherit;
                border: 1px solid rgba(255,255,255,.22);
                border-radius: 5px;
                padding: 2px 8px;
                font: inherit;
                cursor: pointer;
              }
              .speed button.on { border-color: {{accentHex}}; color: {{accentHex}}; }
              .missing { font-size: 13px; opacity: .7; }
            </style>
            </head>
            <body>
              <div class="wrap">
                <{{element}} id="player" src="{{uri}}" type="{{mime}}" controls preload="metadata"></{{element}}>
                <div class="speed">
                  <span>Speed</span>
                  <button data-rate="0.5">0.5x</button>
                  <button data-rate="1" class="on">1x</button>
                  <button data-rate="1.5">1.5x</button>
                  <button data-rate="2">2x</button>
                </div>
                <div class="name">{{name}}</div>
              </div>
              <script>
                const player = document.getElementById('player');
                const buttons = [...document.querySelectorAll('.speed button')];
                buttons.forEach(b => b.addEventListener('click', () => {
                  player.playbackRate = parseFloat(b.dataset.rate);
                  buttons.forEach(o => o.classList.toggle('on', o === b));
                }));
                player.addEventListener('error', () => {
                  document.querySelector('.wrap').innerHTML =
                    '<div class="missing">This format cannot be played here. Use Open to play it in your default app.</div>';
                });
                // Space toggles playback the way a media viewer should.
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
