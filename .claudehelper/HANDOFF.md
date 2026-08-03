# Clip — handoff

_Last updated 2026-08-03. Branch `ui/grayscale-text-rendering`, all work pushed._

## Where things stand

Picture-in-picture, video controls and audio controls are **done** and signed off.

- **Player stays in the browser.** A fully native mini window was built (WPF window, WPF
  controls, then Windows playback, then a bundled GPU decoder) and **rejected** — it fixed the
  resize lag but lost the interface, and both decoders were worse than the browser's. All of it
  was removed. Do not revisit without new information.
- **Resize jitter** is down to a 3–4px gap between frame and picture, flat across drag speeds
  (was 8–45px and grew with drag speed). Accepted as-is.
- **Word previews** now open the cached PDF in the browser viewer instead of rasterising page one,
  so all pages and the text layer survive — and they work at all, since the rasteriser this
  machine lacks was the reason they showed a placeholder.
- 375 tests pass.

## Verify off screen — never take over the display

Isaiah works on this machine and has escalated repeatedly about it. `--jank-test` does both jobs
without touching the screen:

```
Clip.exe --jank-test --shot=out.png --audio --show=speeds --w=550 --h=230   # picture of the player
Clip.exe --jank-test --steps=30 --step-px=16                                # resize smoothness
```

## Next steps

1. **Excel previews** — still not working. Needs `SaveAs xlHtml` plus a sheet-tab strip inside an
   `<iframe name="frSheet">` (the `name` is required or sheets redirect). Roughly 7× slower than
   the PDF export route, and the `_files` directories need cleaning up.
2. **First Word preview after a reboot** can still show a placeholder: Word takes ~25s to start
   cold and there is a 25s cutoff, measured right at the limit. The cutoff exists because Word
   used to hang forever, so raising it blindly is not safe. A warm-up task was started separately.
3. **Palette load time with thumbnails and favicons** — the 33ms open / 17ms for 93 rows
   measurement predates both, so it is stale and worth re-taking before deciding anything.

## Traps

- Never round-trip a `.cs` file through PowerShell `Get-Content`/`Set-Content` — it double-encodes
  the player's button glyphs into mojibake. Use Edit/Write.
- A media library compiled against one FFmpeg major version fails at *run* time with a mismatched
  one, so the app silently falls back and the decoder looks poor rather than absent.
