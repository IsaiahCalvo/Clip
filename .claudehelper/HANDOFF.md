# Clip — handoff

_Last updated 2026-08-05. **`main` is the trunk.** All work pushed. The installed copy has **not**
been updated — see "Open latency" for the one command that does it._

## Open latency (2026-08-05)

**Cold open is 69–76% faster on every page. Warm open is 6–29% faster. The comparison against
Raycast could not be taken — the session was locked all night, and that measurement needs real
keystrokes on an unlocked desktop. The command is written and ready; it takes about a minute.**

### The numbers

Five cold and ten warm samples per page, off screen, against a frozen fixture history of 146 items.
Cold is the first open in a fresh process; warm is a reopen with the list marked stale as though a
clip had arrived, which is why the palette is usually being opened at all.

| page | cold median | cold p95 | warm median | warm p95 | preview ready |
|---|---|---|---|---|---|
| palette | 518 → **159** | 534 → **164** | 100 → **94** | 128 → **104** | — |
| preview-text | 567 → **160** | 877 → **165** | 121 → **102** | 150 → **116** | 217 → **178** |
| preview-image | 583 → **156** | 619 → **173** | 112 → **95** | 153 → **110** | 397 → 433 |
| preview-code | 608 → **150** | 724 → **160** | 190 → **135** | 248 → **148** | 1028 → **771** |
| preview-html | 555 → **144** | 571 → **148** | 173 → **145** | 197 → **171** | 678 → 683 |
| preview-textfile | 533 → **160** | 688 → **168** | 117 → **87** | 143 → **97** | — |
| settings | 541 → **167** | 624 → **181** | 117 → **87** | 138 → **102** | 276 → **225** |

All milliseconds. Raw samples are in `.claudehelper/perf/{baseline,round1,round2,round3}.json`.

The p95 column is the bigger story than the median: preview-text's worst case went from 877ms to
165ms. Cold opens are now boringly consistent (145–180ms across every page and every sample),
where before they ranged 484–929ms.

### What "open" means here

The clock starts when the hotkey message arrives and stops when **a keystroke would be handled and
find the window usable**: shown and opaque, search box focused, a screenful of rows present. Rows
past the fold are deliberately not waited for — nobody can see them and deferring them is correct.

Readiness is checked at **Input** dispatcher priority, because that is where a key press sits in the
queue. This detail is worth about 60ms: checking at Background (the obvious first choice) waits
behind the deferred rows and the preview and reports the window as unusable while it would happily
accept typing.

### What got faster, and why

1. **Rows no longer render in one uninterruptible cascade.** Adding rows re-lays out, layout raises
   `ScrollChanged`, and the handler appended the next batch straight from that event. Because layout
   runs at Render priority — above Input — the whole 146-row list rendered without yielding, and the
   search box did not get focus until it finished: 377ms into a cold open. Batches now go back
   through the dispatcher, which is what batching was for.
2. **The list is rendered while the window is still hidden at startup.** The startup pre-render built
   the frame but left the list empty, so the first Alt+V paid for the query and for building every
   visible row. Rows are expensive on a cold process — the first file row measured **193ms** and the
   first image row **49ms**, because a row resolves its icon by asking the shell for the file type's
   icon or by decoding the picture itself. That was most of a cold open. This is the single biggest
   win.
3. **The first TextBox focus is paid at startup.** Focusing a TextBox the first time costs ~100ms
   while WPF brings up the text services behind it. It now happens during the one moment at startup
   when the window is really shown (off screen, about to be concealed). It has to be there: focusing
   a control in a *hidden* window returns immediately and initialises nothing — an earlier attempt
   measured `focusMs=0` and changed nothing.

### What did not get faster, and why

- **Warm open barely moved (100 → 94ms on the palette).** It never paid the costs above. What is
  left, measured: `Show()` itself ~19ms, ~39ms laying out the first screenful before the Input queue
  is reached, ~15ms in the focus call. The `Show()` cost is the deliberate `Hide()` in
  `ConcealPalette` — it exists to avoid a stale black surface (a DWM glitch), so it is not free to
  remove.
- **Preview-ready for code and HTML is still 680–770ms.** The palette is interactive at ~145ms and
  the preview fills in after, so this is a separate problem from open latency. Partly diagnosed
  (`--open-bench --page=preview-code --runs=5 --stages` splits it):

  - **The first browser-backed preview in a process costs ~810ms creating the WebView2**
    (`code-view-created=221` → `code-webview-ready=1031` on run 0; instant on every run after). That
    is the first code, HTML, PDF, video or audio preview after launch, and it is **pre-warmable the
    same way the rows and the focus now are** — but it means a browser process resident from login.
    That is a memory-versus-speed call, so it is left for Isaiah rather than assumed. **Biggest
    single remaining win if the memory is acceptable.**
  - After that, ~300–480ms building the page HTML and ~200–515ms navigating, both with high
    variance, on a **5.8KB** source file — far too slow for the input size and **not root-caused**.
    The regex highlighter is already cached per language with `RegexOptions.Compiled`, and
    counter-intuitively the *first* build is the fast one (23ms), so the cost is not regex
    construction. Start here.
  - Caveat on the number: the harness selects the item *after* the open, so a preview-page run
    renders two previews (the auto-selected first item, then the page's item). A user opening onto
    that item renders once. Treat `preview ready` as an upper bound.
- **Two attempts produced no gain and were reverted.** Dropping the first render batch to 8 to match
  the query limit *doubled* the open — date headers consume entries, so it built only 7 rows and the
  list waited for the next batch. Requesting a 1ms timer resolution to sharpen the poll did nothing,
  because the ~20ms it was aimed at is real dispatcher work, not timer granularity.

### Raycast — not measured, and why

The real goal was matching or beating Raycast, not the 50ms proxy. That number was **not obtained**:

- The session was locked all night. Synthetic keystrokes go to the input desktop, which while locked
  is the secure desktop, so neither app would have received them. Reporting silence as a slow
  application would have been worse than reporting nothing.
- Raycast's `raycast://` deeplink *does* work locked, but it costs **1438ms** end to end (384ms of
  that inside `ShellExecute` before the process is even reached). That is MSIX activation, not its
  hotkey path, so it is not a usable comparison — it would flatter Clip enormously.
- Its hotkey could not be driven by message instead. Raycast owns Alt+Shift+V through
  `RegisterHotKey` (confirmed: registering it returns `ERROR_HOTKEY_ALREADY_REGISTERED`), but
  posting `WM_HOTKEY` to every one of its windows and threads with ids 0–15, across both its
  processes, never opened it.

**Useful thing found while trying:** Raycast hides its window by keeping it mapped at layered
**alpha 0** and flipping it to 255 in one step, no fade. That is a precise, cheap, poll-able "it is
on screen now" signal, and it is what `Measure-VsRaycast.ps1` uses.

To take the measurement, on an unlocked session, keyboard free for ~30 seconds:

```bash
pwsh -File tools/Measure-VsRaycast.ps1 -Runs 10
```

It presses the real Alt+V and Alt+Shift+V and watches each window by its own hiding mechanism. The
comparison is deliberately generous to Raycast: its alpha flip is stamped when it *decides* to show,
while Clip must additionally have painted — and Clip's own harness is stricter still, waiting for
the search box to take focus. If Clip wins there, it wins while being judged more harshly.

### Re-running the harness

```bash
dotnet build Clip.sln -c Release
pwsh -File tools/New-BenchFixture.ps1              # once; -Force to rebuild
pwsh -File tools/Measure-OpenLatency.ps1 -Label whatever
```

Do not rebuild the fixture between an optimization and its re-measurement — that invalidates the
comparison. Everything runs off screen; nothing takes the display.

### Two traps this work walked into

- **The fixture builder seeded 145 items into the real clipboard history** on its first run, because
  `CLIP_ROOT` silently did nothing: the shell, the watcher and the store each rebuilt
  `%LocalAppData%\Clip` themselves rather than asking `ClipStoragePaths`. All 145 were removed and
  Isaiah's 139 items were untouched, but the lesson stands: **prove a redirect seam before bulk
  writes**. The builder now writes one probe item and aborts if it lands in the wrong place.
  (`Environment.GetFolderPath(LocalApplicationData)` also ignores the `LOCALAPPDATA` env var — it
  asks the shell — so redirecting via the environment alone is impossible.)
- **Pre-warming the rows quietly broke selection.** With rows already present the first open skipped
  the reload, and the reload was what selected the first item — so the palette opened with nothing
  selected and a blank preview. The bench did not catch it (the palette page ignores selection);
  running the real shell through `--open-test` did. Fixed in b04e4e9. **Off-screen benchmarks measure
  time, not correctness — run `--open-test` and read the trace after any change to the open path.**

## Branches

The 2026-08-05 open-latency work was done on `perf/open-latency` and merged into `main`. Four
commits, each revertable on its own: the harness, the baseline, and one per optimization round.

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
- **874 tests pass on `main`** (2026-08-04). A second push took Clip.Core to 98.84% of
  hand-written lines (3850/3895) by adding small internal test seams (ClipStoragePaths.RootOverride
  AsyncLocal redirects the whole %LocalAppData%\Clip tree; registry roots, launch and
  powershell-query hooks) plus 95 more tests. The 45 remaining Core lines are each analyzed:
  dead guards Utf8JsonReader can't reach, success-only launch returns (would open real apps),
  COM/WinRT branches that can't be forced, and race-only catches. Two dead private store
  overloads and other provably unreachable code were deleted outright. Earlier the same day, a
  coverage push added 364 tests
  across 35 new `*CoverageTests.cs` files: overall lines 33.8% → 42.0%, Clip.Core 77.8% → 87.0%,
  Clip.Shell 9.6% → 16.3%, Clip.Watcher 17.8% → 31.3%. Every pure-logic class is at or near
  100%; what remains uncovered is live-UI (MainWindow's 9.9k lines, App, PiP, JankHarness),
  network fetches, registry writes, Office COM and live-clipboard paths — those can't run
  hermetically, and 100% overall is not attainable via unit tests without refactoring seams
  into the product code. Two real finds fell out: the update checker's release-name fallback
  never bound (missing `[JsonPropertyName("name")]`, fixed), and `PdfPage.Size` returns DIPs
  (1/96") not points, with `RenderToStreamAsync` scaling output by display DPI — so PNG output
  size is machine-dependent (behavior kept, comment corrected).

## Verify off screen — never take over the display

Isaiah works on this machine and has escalated about this repeatedly.

```
Clip.exe --jank-test --shot=out.png --audio --show=speeds --w=550 --h=230   # picture of the player
Clip.exe --jank-test --steps=30 --step-px=16                                # resize smoothness
Clip.exe --open-test                                # one cold + one warm open, read the trace
Clip.exe --open-bench --page=palette --runs=10      # N opens, every sample + stage breakdown
```

`--open-test` is the correctness check (does it select, does the preview render); `--open-bench`
is the timing one. Use `tools/Measure-OpenLatency.ps1` rather than `--open-bench` directly unless
you want the raw stages — the script handles cold-vs-warm and the median/p95.

For Office work, drive real instances over COM with `Visible = $false` — that reproduces the
attach case without putting a window on screen.

## Next steps

1. **RESOLVED 2026-08-04 — `.xls` now reads natively.** Isaiah asked for it despite zero `.xls`
   ever appearing in history, so `ExcelWorkbookReader` parses the old binary format through
   ExcelDataReader (new package, read-only, no Excel process) into the same grid and tab strip
   the zip formats get, with the COM export kept as the fallback for a file that will not parse.
   Verified against a real xlExcel8 fixture written by Excel itself
   (`tests/Clip.Tests/Fixtures/legacy.xls` — dates, booleans, codepage text, two sheets).
   Only `.xlsb` still goes to Excel.
2. **RESOLVED 2026-08-04 — Excel and Visio ownership verified by experiment.** Both landed in a
   fresh process (`owned=True`); PIDs and the COM-launched caveat are in "Where things stand".
3. **The 120s cold budget is deliberately above the measurements**, because killing `WINWORD.EXE`
   leaves Word's binaries in the OS file cache, so every "cold" number is a floor rather than a
   worst case. If a real post-reboot preview is ever seen timing out, raise it; the debug log
   prints which budget was in force.
4. **RETIRED 2026-08-05 — the palette load figures here are superseded.** They were single runs of
   `--open-test` read off the trace, and the trace lines each start their own stopwatch, so they
   never added up to an open. See "Open latency" at the top for the replacement, which measures one
   clock end to end with a median and a p95. Left here only so the old numbers are not mistaken for
   current ones.

   Still open from that work, and now quantified: **preview-ready for code and HTML is 680–770ms**
   while the palette itself is interactive at ~145ms. Not root-caused; it is not WebView2 teardown
   (3-minute idle timer). Best next lead if open latency is revisited.
5. **Word and PowerPoint are only instant if the palette was open first.** They still need their
   application, which takes tens of seconds cold, so the export is done in the background while the
   palette is being read. A document copied and previewed within a few seconds of each other can
   still wait once. Doing the export at copy time instead would fix that at the cost of starting
   Office for documents nobody ever looks at, which was considered and rejected.

## Traps

- **Log lines mentioning `Clip.Tests` or `clip-thumb-` paths are test noise, not app failures.**
  One triage session chased "PDF preview skipped" errors that were the suite's corrupt-PDF
  fixtures. Since 8200d92 the tests redirect logging via `CLIP_LOG_ROOT` (module initializer in
  `TestLogRedirect.cs`), so new pollution can't happen — but older log history still has it.

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
