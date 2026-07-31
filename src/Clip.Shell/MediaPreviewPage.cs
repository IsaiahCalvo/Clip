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
                position: relative;
                height: 100%;
                display: flex;
                flex-direction: column;
                align-items: center;
                justify-content: center;
                gap: 14px;
                padding: 12px;
                box-sizing: border-box;
              }
              /* Video fills the pane by default rather than sitting small in the middle. */
              video { width: 100%; height: 100%; object-fit: contain; border-radius: 6px; background: #000; }
              /* The audio player should use the pane rather than sit in a narrow strip. */
              audio { width: 94%; }
              .missing { font-size: 13px; opacity: .7; text-align: center; padding: 0 24px; }

              /* Only the speed list is replaced. Chrome draws it as widely spaced rows that need
                 scrolling in a pane this size and cannot be restyled from the page. Download and
                 picture-in-picture stay in the native overflow menu. */
              .rate { position: absolute; right: 16px; bottom: 14px; z-index: 5; }
              .rate-btn {
                background: rgba(0,0,0,.66);
                color: #fff;
                border: 1px solid rgba(255,255,255,.3);
                border-radius: 4px;
                padding: 1px 7px;
                font: 600 11px/1.5 "Segoe UI Variable Text", "Segoe UI", sans-serif;
                cursor: pointer;
              }
              .rate-menu {
                display: none;
                position: absolute;
                right: 0;
                bottom: 24px;
                background: rgba(30,30,32,.98);
                border: 1px solid rgba(255,255,255,.18);
                border-radius: 5px;
                padding: 2px;
                box-shadow: 0 4px 14px rgba(0,0,0,.55);
              }
              .rate-menu.open { display: block; }
              .rate-menu button {
                display: block;
                width: 100%;
                text-align: left;
                background: transparent;
                color: #fff;
                border: 0;
                border-radius: 3px;
                padding: 2px 10px 2px 8px;
                font: 11px/1.45 "Segoe UI Variable Text", "Segoe UI", sans-serif;
                white-space: nowrap;
                cursor: pointer;
              }
              .rate-menu button:hover { background: rgba(255,255,255,.14); }
              .rate-menu button.on { color: #8ab4ff; font-weight: 600; }
            </style>
            </head>
            <body>
              <div class="wrap">
                <{{element}} id="player" src="{{uri}}" type="{{mime}}" controls controlsList="noplaybackrate" preload="metadata"></{{element}}>
                <div class="rate">
                  <button class="rate-btn" id="rateBtn">1x</button>
                  <div class="rate-menu" id="rateMenu">
                    <button data-rate="0.25">0.25x</button>
                    <button data-rate="0.5">0.5x</button>
                    <button data-rate="0.75">0.75x</button>
                    <button data-rate="1" class="on">1x</button>
                    <button data-rate="1.25">1.25x</button>
                    <button data-rate="1.5">1.5x</button>
                    <button data-rate="2">2x</button>
                  </div>
                </div>
              </div>
              <script>
                const player = document.getElementById('player');
                const rateBtn = document.getElementById('rateBtn');
                const rateMenu = document.getElementById('rateMenu');

                rateBtn.addEventListener('click', e => {
                  e.stopPropagation();
                  rateMenu.classList.toggle('open');
                });

                rateMenu.addEventListener('click', e => {
                  const hit = e.target.closest('button[data-rate]');
                  if (!hit) return;
                  e.stopPropagation();
                  player.playbackRate = parseFloat(hit.dataset.rate);
                  rateBtn.textContent = hit.textContent;
                  [...rateMenu.querySelectorAll('button')].forEach(b => b.classList.toggle('on', b === hit));
                  rateMenu.classList.remove('open');
                });

                document.addEventListener('click', () => rateMenu.classList.remove('open'));
                document.addEventListener('keydown', e => {
                  if (e.code === 'Escape') rateMenu.classList.remove('open');
                  if (e.code === 'Space') { e.preventDefault(); player.paused ? player.play() : player.pause(); }
                });
                player.addEventListener('error', () => {
                  document.querySelector('.wrap').innerHTML =
                    '<div class="missing">This format cannot be played here. Use Open to play it in your default app.</div>';
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
