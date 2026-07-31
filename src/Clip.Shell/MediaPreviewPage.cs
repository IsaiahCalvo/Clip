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
        var fullscreenItem = isVideo ? "<button id=\"fs\">Full screen</button>" : string.Empty;

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
              video { width: 100%; flex: 1; min-height: 0; object-fit: contain; border-radius: 6px; background: #000; }
              /* The audio player should use the pane rather than sit in a narrow strip. */
              audio { display: none; }
              .missing { font-size: 13px; opacity: .7; text-align: center; padding: 0 24px; }

              /* Chromium no longer exposes its media-control internals to page CSS, so the speed
                 list could not be resized. The whole bar is drawn here instead — same controls,
                 sized to fit a preview pane. */
              .bar {
                position: relative;
                display: flex;
                align-items: center;
                gap: 10px;
                width: 94%;
                padding: 7px 12px;
                box-sizing: border-box;
                background: rgba(0,0,0,.55);
                border: 1px solid rgba(255,255,255,.13);
                border-radius: 8px;
                font: 12px/1.4 "Segoe UI Variable Text", "Segoe UI", sans-serif;
                color: #fff;
              }
              .bar button {
                background: transparent;
                border: 0;
                color: #fff;
                cursor: pointer;
                padding: 2px 5px;
                border-radius: 4px;
                font: inherit;
                line-height: 1;
              }
              .bar button:hover { background: rgba(255,255,255,.14); }
              .icon { font-size: 14px; }
              .time { font-variant-numeric: tabular-nums; opacity: .88; white-space: nowrap; }
              input[type=range] {
                flex: 1;
                accent-color: #8ab4ff;
                height: 3px;
                cursor: pointer;
              }
              .vol { width: 66px; flex: none; }
              .menu {
                display: none;
                position: absolute;
                right: 8px;
                bottom: 38px;
                background: rgba(32,32,34,.98);
                border: 1px solid rgba(255,255,255,.16);
                border-radius: 6px;
                padding: 3px;
                box-shadow: 0 6px 18px rgba(0,0,0,.55);
                min-width: 96px;
              }
              .menu.open { display: block; }
              .menu button {
                display: block;
                width: 100%;
                text-align: left;
                /* The whole point: rows this tight fit every option with no scrolling. */
                padding: 3px 9px;
                white-space: nowrap;
              }
              .menu button.on { color: #8ab4ff; font-weight: 600; }
              .menu .sep { height: 1px; background: rgba(255,255,255,.12); margin: 3px 2px; }
              .missing { font-size: 13px; opacity: .7; text-align: center; padding: 0 24px; }
            </style>
            </head>
            <body>
              <div class="wrap">
                <{{element}} id="player" src="{{uri}}" type="{{mime}}" preload="metadata"></{{element}}>
                <div class="bar">
                  <button id="play" class="icon" title="Play/pause">▶</button>
                  <span class="time" id="time">0:00 / 0:00</span>
                  <input type="range" id="seek" value="0" min="0" max="1000" step="1">
                  <button id="mute" class="icon" title="Mute">🔊</button>
                  <input type="range" class="vol" id="vol" min="0" max="1" step="0.01" value="1">
                  <button id="more" class="icon" title="More">⋮</button>
                  <div class="menu" id="menu">
                    <button data-rate="0.25">0.25x</button>
                    <button data-rate="0.5">0.5x</button>
                    <button data-rate="0.75">0.75x</button>
                    <button data-rate="1" class="on">Normal</button>
                    <button data-rate="1.25">1.25x</button>
                    <button data-rate="1.5">1.5x</button>
                    <button data-rate="2">2x</button>
                    <div class="sep"></div>
                    <button id="pip">Picture in picture</button>
                    <button id="dl">Download</button>
                    {{fullscreenItem}}
                  </div>
                </div>
              </div>
              <script>
                const p = document.getElementById('player');
                const play = document.getElementById('play');
                const seek = document.getElementById('seek');
                const time = document.getElementById('time');
                const vol = document.getElementById('vol');
                const mute = document.getElementById('mute');
                const more = document.getElementById('more');
                const menu = document.getElementById('menu');

                const fmt = s => {
                  if (!isFinite(s)) return '0:00';
                  const m = Math.floor(s / 60), r = Math.floor(s % 60);
                  return m + ':' + String(r).padStart(2, '0');
                };
                const sync = () => {
                  time.textContent = fmt(p.currentTime) + ' / ' + fmt(p.duration);
                  if (p.duration) seek.value = (p.currentTime / p.duration) * 1000;
                };

                play.addEventListener('click', () => p.paused ? p.play() : p.pause());
                p.addEventListener('play', () => play.textContent = '❚❚');
                p.addEventListener('pause', () => play.textContent = '▶');
                p.addEventListener('timeupdate', sync);
                p.addEventListener('loadedmetadata', sync);
                seek.addEventListener('input', () => { if (p.duration) p.currentTime = (seek.value / 1000) * p.duration; });
                vol.addEventListener('input', () => { p.volume = vol.value; p.muted = vol.value == 0; });
                mute.addEventListener('click', () => { p.muted = !p.muted; mute.textContent = p.muted ? '🔇' : '🔊'; });

                more.addEventListener('click', e => { e.stopPropagation(); menu.classList.toggle('open'); });
                menu.addEventListener('click', e => e.stopPropagation());
                document.addEventListener('click', () => menu.classList.remove('open'));

                menu.querySelectorAll('button[data-rate]').forEach(b => b.addEventListener('click', () => {
                  p.playbackRate = parseFloat(b.dataset.rate);
                  menu.querySelectorAll('button[data-rate]').forEach(o => o.classList.toggle('on', o === b));
                  menu.classList.remove('open');
                }));

                document.getElementById('pip').addEventListener('click', async () => {
                  menu.classList.remove('open');
                  try { await (document.pictureInPictureElement ? document.exitPictureInPicture() : p.requestPictureInPicture()); } catch {}
                });
                document.getElementById('dl').addEventListener('click', () => {
                  menu.classList.remove('open');
                  const a = document.createElement('a');
                  a.href = p.currentSrc; a.download = '';
                  a.click();
                });
                const fs = document.getElementById('fs');
                if (fs) fs.addEventListener('click', () => {
                  menu.classList.remove('open');
                  if (document.fullscreenElement) document.exitFullscreen(); else p.requestFullscreen();
                });

                document.addEventListener('keydown', e => {
                  if (e.code === 'Escape') menu.classList.remove('open');
                  if (e.code === 'Space') { e.preventDefault(); p.paused ? p.play() : p.pause(); }
                });
                p.addEventListener('error', () => {
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
