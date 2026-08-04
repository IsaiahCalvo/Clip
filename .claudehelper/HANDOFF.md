# Clip — handoff

_Last updated 2026-08-04. **`main` is the trunk and the only branch.** All work pushed._

## Branches

There is one branch now. `main` was 59 commits behind while the real work sat on
`ui/grayscale-text-rendering`, which is why two worktree sessions picked bases and one picked
wrong. `main` was fast-forwarded onto that work, and `ui/grayscale-text-rendering`, both
`claude/*` fix branches and two long-dead `codex/*` branches were deleted after confirming each
had nothing `main` lacked. Cut new work from `main`.

## Where things stand

Picture-in-picture, video controls and audio controls are **done** and signed off.

- **Player stays in the browser.** A fully native mini window was built and **rejected** — it
  fixed the resize lag but lost the interface, and both decoders tried were worse than the
  browser's. Do not revisit without new information.
- **Resize jitter** is a 3–4px gap between frame and picture, flat across drag speeds. Accepted.
- **Word previews** open the cached PDF in the browser viewer — all pages, real text layer.
- **Office previews no longer close the user's PowerPoint.** COM hands PowerPoint callers the
  instance the user is already working in, and the preview used to hide it and `Quit()` it,
  discarding unsaved work with no prompt. Ownership is now decided at runtime from whether a
  process appeared, and `Visible`, `DisplayAlerts` and `Quit` all sit behind it.
- **The first Office preview after a reboot no longer times out.** One flat 25s budget had to
  cover both a cold COM start (measured 22.7–26.2s) and a warm export; it is now 120s cold /
  45s warm, with the flag set only where COM actually completed an export.
- **The Word flicker is fixed at its root** (commit d81f7f1, 2026-08-04, after a 30-agent audit
  of every preview path). `await Dispatcher.InvokeAsync(async ...)` completes at the lambda's
  FIRST await and discards the inner task, so `shown` in the pdf/office branches was always
  false — the raster fallback ran on top of every live viewer, and the orphaned reveal then
  resurrected a blank pane. `LoadFilePreviewAsync` is now flat (it already resumes on the UI
  thread), `RevealWhenLoadedAsync` only accepts its own navigation's completion (NavigationId +
  IsSuccess) and re-checks `_previewToken` before revealing, and the placeholder is swapped for
  the pane in one dispatcher frame. Every browser-backed preview (media included) goes through
  that reveal; the loading placeholder masks every transition. `BlankHtmlPreview` is gone — its
  blank navigation was what raced the reveals — replaced by a script that pauses `video`/`audio`
  when the pane hides or the palette conceals (also fixes Esc/pip leaving audio playing).
  Image decode, code highlighting and workbook HTML now build off the UI thread. Office exports
  are serialized per cache file, written to a temp name and moved into place, and a cache hit
  requires the PDF `%%EOF` / PNG `IEND` tail marker (verified against all 25 real cache files).
- **Excel and Visio ownership is settled by experiment** (2026-08-04). With a COM-launched
  `Visible=false` instance already running, a second Clip-style `CoCreateInstance` landed in a
  new process both times — Excel user=33940 clip=10588, Visio user=55680 clip=41544; independent
  repro Excel 38336→47592, Visio 43628→63216 — so both read `owned=True` and the user's instance
  is untouched. Caveat: verified against COM-launched instances (which reproduce the attach
  case), not an interactively launched one; the runtime gate in `CreateComApplication` covers
  any future attach case regardless.
- 412 tests pass on `main`.

## Verify off screen — never take over the display

Isaiah works on this machine and has escalated about this repeatedly.

```
Clip.exe --jank-test --shot=out.png --audio --show=speeds --w=550 --h=230   # picture of the player
Clip.exe --jank-test --steps=30 --step-px=16                                # resize smoothness
Clip.exe --open-test                                # palette open timings, cold + warm, stderr + shell.log
```

For Office work, drive real instances over COM with `Visible = $false` — that reproduces the
attach case without putting a window on screen.

## Next steps

1. **Old-format spreadsheets still go through Excel.** `.xlsx` and `.xlsm` are read straight out of
   the file by `ExcelWorkbookReader` in about 25ms, which is what made them instant and gave them a
   real tab strip. `.xls` and `.xlsb` are not zip archives, so they still start Excel and take
   tens of seconds the first time. Worth doing only if such a file ever actually turns up — and
   as of 2026-08-04 zero `.xls`/`.xlsb` have appeared in 492 history entries.
2. **RESOLVED 2026-08-04 — Excel and Visio ownership verified by experiment.** Both landed in a
   fresh process (`owned=True`); PIDs and the COM-launched caveat are in "Where things stand".
3. **The 120s cold budget is deliberately above the measurements**, because killing `WINWORD.EXE`
   leaves Word's binaries in the OS file cache, so every "cold" number is a floor rather than a
   worst case. If a real post-reboot preview is ever seen timing out, raise it; the debug log
   prints which budget was in force.
4. **Palette load time with thumbnails and favicons** — measured 2026-08-04 (Release build,
   off-screen `--open-test`, real history: 374 items = 378 render entries with date headers).
   Cold: palette shown 80–88ms, first rows painted ~560–580ms, recent set of 8 rows complete
   ~655–670ms, background full-history load done at 1.1–1.35s (the queries themselves are
   20–33ms). Warm re-open: 45–68ms, rows already rendered, no reload. Visible rows render
   eagerly and the rest append on scroll, so a full render of all 378 never happens without
   scrolling. The old 33ms open / 17ms for 93 rows figure predated thumbnails and favicons and
   is retired.
5. **Word and PowerPoint are only instant if the palette was open first.** They still need their
   application, which takes tens of seconds cold, so the export is done in the background while the
   palette is being read. A document copied and previewed within a few seconds of each other can
   still wait once. Doing the export at copy time instead would fix that at the cost of starting
   Office for documents nobody ever looks at, which was considered and rejected.

## Traps

- **The preview cache hides everything.** Results are keyed by path + mtime + size under
  `%LOCALAPPDATA%\Clip\document-previews`, so re-previewing the same document never touches COM.
  Copy the file to a fresh name for every A/B run or you will measure nothing.
- **Never wrap preview work in `await Dispatcher.InvokeAsync(async ...)`.** It returns at the
  lambda's first await and discards the inner task — results read too early, exceptions vanish.
  `LoadFilePreviewAsync` already resumes on the UI thread; write straight-line awaits with
  `if (token != _previewToken) return;` after each one.
- **The timeout is not a leak guard**, whatever the code comment implies. On timeout the STA
  thread is abandoned, not killed, so a genuine Office hang leaks regardless of the number. What
  the timeout controls is how long the pane waits before falling back.
- **Never round-trip a `.cs` file through PowerShell `Get-Content`/`Set-Content`** — it
  double-encodes the media player's button glyphs into mojibake. Use Edit/Write.
- **A clean auto-merge is not a correct merge.** Both Office branches were reported as
  conflict-free; the ownership branch's stale base meant git happily produced a tree that did not
  compile, and the failure was the only thing standing between that and Word and Excel silently
  keeping the ungated COM path.
