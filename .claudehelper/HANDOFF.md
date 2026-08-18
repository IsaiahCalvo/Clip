# Clip — handoff

_Last updated 2026-08-17. **`main` is the trunk.** All work pushed, and **installed** — the copy in
`%APPDATA%\Programs\Clip` is this build._

## Split-pill unification + accent glow retired (2026-08-17, commit faf44f3)

Isaiah flagged two things: the Media split pill missed the border/fill that wraps All and
File when active (root cause: All/File had hand-attached shell MouseEnter glow handlers that
Media never got), and the app-wide blue "glow" looked immature. Root cause of the glow:
`ApplySystemAccentBrushes` poured the full-strength Windows accent into
`Selected`/`SelectedBorder`/`AccentSoft`, so every selected row, hover fill, and 1px border
in the app was accent-colored. Design reference he supplied: OriginUI's button-with-dropdown
(21st.dev) — one rounded container, flat halves, 1px seam, neutral fills.

Fix: shell `Border` now carries the whole selected look for every split pill (halves stay
transparent, painted only by `SetFilterVisual` — they cannot drift apart);
Selected/SelectedBorder/AccentSoft are now fixed neutrals in both themes; the Windows accent
survives only in `Accent` (search focus ring, toggles), `TextCursor`, and a new
`TextSelection` brush that all four text editors use (a neutral selection highlight would
have been invisible). Dead `FilterChevronButton` style deleted. Net −53 lines.

Verified off-screen by rendering the real MainWindow.xaml resources through a scratch
RenderTargetBitmap harness (no window shown): all three split pills identical per state,
dark + light. 877 tests pass. Pushed, installed (hash-verified all four exes), restarted.

**Verify:** Alt+V — active filter pill is neutral gray with a single border around BOTH the
label and the chevron, identical for All/Media/File; selected list row no longer has the
blue ring; text selection in previews/editors is still clearly visible.

### Follow-up (commit 1a59fc2): Raycast palette adopted. He asked to tone the focus ring and
cyan caret too and "match Raycast and their color palette". Accent + TextCursor are now
fixed Raycast red (#FF6363 dark / #D64545 light); ALL Windows-accent plumbing is deleted
(GetWindowsAccentColor/BlendColors/SetBrushColor/ApplySystemAccentBrushes) — the palette no
longer follows the system accent at all. Search box has NO focus ring anymore (border always
Line2; OnSearchFocusChanged deleted) since the palette autofocuses its only text field.
TextSelection is plain neutral (#414141/#C8C8C8). Red now appears in: caret, title hover,
settings toggles ON, active option labels. 877 tests pass, off-screen render verified,
pushed, installed (hash-verified), restarted.

### Follow-up (commit a28841d): settings UI audit. He flagged the app-icon picker clipping
(30px host for 32px of content) and off-center hotkey text (Padding 10,5,10,0). A sub-agent
audit of all five procedural windows found 16 defects; 14 fixed: dotted focus rectangles
killed app-wide (App.xaml implicit Button/TextBox styles, FocusVisualStyle=null with
BasedOn); theme change now REBUILDS the settings page (RefreshTheme(rebuildPage:true))
instead of the blanket repaint walker that erased state colors and missed popups — walker
deleted; OS highlight blue removed from all three app-picker ListBoxes via shared
MainWindow.PaletteListItemStyle; clear-history segment 1px overpaint, update-button clip,
hotkey error text clip ("Needs modifier"/"Invalid"), hotkey-help chip centering,
ActionOverDetailRow label alignment, app-icon hover on inactive button, sticky dropdown
hover outline, asymmetric search margins. Deliberately skipped: white toggle knob
(intentional convention), icon-warm hex literals. 877 tests pass, pushed, installed,
restarted.

**Next steps:** broader UI pass continues if he flags more. No release cut yet — hold until
the UI pass settles, then ship one release.

## Smaller window + info panel right-margin fix (2026-08-17, commit 2813af0)

Isaiah compared Clip to Raycast: window felt big, and the INFORMATION section had a visibly
bigger right margin than left. Root cause of the margin: any item with enough info rows to
scroll (every file item — 9 rows in a 132px viewport) made the ScrollViewer reserve a **17px
scrollbar gutter that rendered invisibly** (the window's implicit ThinScrollBar style never
reached inside the ScrollViewer template, so the default-width bar was reserved but drawn
transparent). Fix: `OverlayScrollViewer` template on `InfoScroll` — the 6px thin bar now
floats in the 24px outer margin (negative right margin) and content keeps full width, 24/24
symmetric. Window shrunk 880x560 → 800x520 (Raycast is ~750x475); the hosted settings
overlay (fixed 720x500) still fits, verified by film. 877 tests pass, `--open-test` clean,
built, pushed, **installed and restarted**.

His right-click-menu comparison was checked too: Clip's context menu already covers
everything Raycast's does (paste/copy/plain-paste/rename/pin+reorder/edit/append/open/
open-with/explorer/copy-path/share/save-as/delete). No change made there.

**Verify:** Alt+V — window is smaller; select a copied *file* and look at the INFORMATION
section: values reach the same margin on the right as labels do on the left, thin scrollbar
floats outside the text when it scrolls.

### Follow-up same day (commit b81f323): Isaiah rejected the always-visible thumb ("thick as
hell"). It now starts at opacity 0, fades in only on a real user scroll (offset change with
zero extent change, so selecting another item never flashes it), and fades out 800ms after
scrolling stops — `OnInfoScrollChanged` in MainWindow.xaml.cs. Not hit-testable. Installed
and restarted.

### Second follow-up (commit b47e5b0): he then said to open the real Raycast and copy it, so
that's what happened — with his screen-control permission, Raycast's clipboard window was
opened on screen (its window hides at layered alpha 0; the computer-use `key` tool could not
reach it, but `SendInput` Alt+Shift+V from PowerShell works — the repo's Measure-VsRaycast
trick). Measured: ~4px rounded pill hugging the panel edge, no track, visible only while
scrolling, fully gone ~1.5s after. Clip's bar is now a 4px full-bleed pill (own template, no
inner thumb margin, CornerRadius 2) tucked 5px from the window edge, same fade envelope, and
`RenderInfo` now calls `InfoScroll.ScrollToTop()` so the info section never opens mid-scroll.
Verified live on screen against the real Raycast. Installed and restarted.

### Third follow-up (commit 48528ee): he called the 4px bar an ugly block vs Raycast's and
told Claude off for staying on his screen. One full-res `CopyFromScreen` capture of Raycast's
bar mid-scroll was pixel-scanned — 6px wide, core 113,113,118 (#717176), soft anti-aliased
pill — and everything after was done off-screen. Root cause of the ugliness: the window's
`UseLayoutRounding` snapped the small pill to hard whole-pixel edges; the bar template now
opts out of snapping/rounding, uses #717176 at width 6 / radius 3, and an offscreen
RenderTargetBitmap reproduces Raycast's exact core pixels. Installed and restarted.
**Rule reaffirmed: stay off his screen; one capture, then iterate off-screen.**

### Fourth follow-up (commit c7680b6) — THE ACTUAL ROOT CAUSE. He said it "still looks the
same big blocky thing" on the newest build (verified by hash he was running it). The blocky
bar was never the overlay pill — it was the **stock 17px Windows scrollbar in the text
preview**: window-level implicit ScrollBar styles and the per-dialog `Resources.Add` copies
NEVER reach scrollbars inside control templates (TextBox's PART_ContentHost above all), so
Clip's "thin scrollbar" styling had silently never applied anywhere templated. Fix: Raycast
pill as the app-level implicit style in App.xaml (app-level DOES penetrate templates);
TextPreview got an explicit TextBox template whose PART_ContentHost uses
`OverlayScrollViewerInset` (bar inside the pane edge — the outer-margin variant would be
clipped by the preview Border's ClipToBounds) with the same fade-on-scroll via the shared
`OverlayBarFader`; deleted the dead window ThinScrollBar style, the codegen
`ThinScrollBarStyle`, and its three dialog injections. 877 tests, `--open-test` clean, text
preview film-verified unchanged, installed (hash-verified) and restarted.

### Fifth follow-up (commit a41012b) — the 17px mechanism, finally. "Still there" after the
app-level style because the style's Width setter never stood a chance: the stock
ScrollViewer template sets the bar's width from SystemParameters scrollbar metrics as a
LOCAL value, and local beats style — so the restyled thumb just stretched into a 17px gray
block (that WAS the "big blocky thing", including in settings/dialogs where c7680b6's pill
style made it a filled gray block). Fix: `VerticalScrollBarWidthKey` /
`HorizontalScrollBarHeightKey` overridden to 6 in App.xaml. Proven with an offscreen
live-window control test (real Window at Left=-9000 + ContentRendered + RTB + pixel scan —
note a detached RTB render never draws scrollbars, false negative): before the override the
test rendered a 17px block, after it a 6px #717176 pill. Also injected ::-webkit-scrollbar
pill CSS into every WebView2 preview page (c7680b6/a41012b). Installed (hash-verified,
process 3:43 PM) and restarted.

**Litter noticed, not touched:** his real history has two dead pinned items from the 8/5
bench-fixture leak — `sample-video.mp4` / `sample-audio.wav` pointing into a deleted session
scratchpad. Every palette open logs a "file preview failed" for the selected one. They are
his (pinned) data, so they were left alone — unpinning/deleting them from the palette would
stop the noise.

## Paste crash fixed (2026-08-08, commit dacfb69)

Clicking a row to paste killed the whole app: `Clipboard.SetDataObject(copy: true)`
re-renders every format inside `OleFlushClipboard`, and WPF answers any failure there
with `Environment.FailFast` — uncatchable, process gone (Event Log 11:34, v1.1.10;
stack: `GetDataIntoOleStructsByTypeMedimHGlobal` → `FailFast`). The text/html/rtf
branch of `SetClipboard` now renders the bytes itself and hands Windows finished
HGLOBALs via `Clip.Core/Win32ClipboardWriter` (CF_UNICODETEXT + "HTML Format" UTF-8 +
"Rich Text Format" ANSI, owner hwnd passed so source attribution still sees Clip).
If the Win32 write fails after retries it falls back to WPF with `copy: false`, which
skips the flush. 877 tests pass (3 new for the byte encoders — deliberately not
touching the real clipboard). Built, pushed, installed, Clip restarted.

**Verify:** copy something formatted from Word/browser, paste it back out of Clip a
few times — the 1.1.10 build died on exactly that.

### Next steps
- Image and file paste still go through WPF (`SetImage`/`SetFileDropList`, which also
  flush). Same FailFast is theoretically reachable there; extend Win32ClipboardWriter
  if it ever fires.

## Raycast comparison research (2026-08-07, no code changes)

Compared Clip against Raycast's Clipboard History (macOS + Windows beta). Clip is ahead on
previews, paste verification/per-app overrides, CLI, and licensing. Gaps worth considering,
roughly by value:

1. **Respect Windows clipboard-exclusion formats** — password managers (1Password, Bitwarden,
   KeePass) set `ExcludeClipboardContentFromMonitorProcessing` / `CanIncludeInClipboardHistory`
   on the clipboard. Clip ignores these and records the secret unless the app is manually
   excluded. Raycast honors the macOS equivalent by default. Small change in the capture path,
   big privacy win.
2. Time-based retention option (Clip is count-only, 500) + bulk delete-by-time-window.
3. Fuzzy search (current search is case-insensitive substring across preview/text/OCR/paths).
4. Sequential paste (select several, paste in order).
5. Pause-capture / incognito toggle.
6. Code signing (already planned).

### Next steps
- If Isaiah wants any of the above, start with item 1 — it's the only real privacy hole.

## Five visual defects fixed (2026-08-07, commit dea8350)

All five reported 2026-08-06, all root-caused, fixed in one commit, 874 tests pass, `--open-test`
clean, installed and restarted:

- **Video bled through settings.** WebView2 is an HWND with its own airspace — the ZIndex-1000
  settings overlay never covered it. The browser pane is now collapsed (and its media paused) when
  hosted settings opens and restored on close. The reveal path (`RevealWhenLoadedAsync`) also
  refuses to show the browser while `_settingsOverlay` is up, because a theme change inside
  settings re-renders the preview and would have re-revealed it over the panel.
- **Settings corners missing their border.** A WPF Border does not clip its child to its rounded
  corners, so the square header/body painted over the 1px border along the curves. The content
  grid now carries a static rounded clip (panel is a fixed 720x500 everywhere).
- **Theme switch swapped list icons for "old" generic glyphs until restart.**
  `RefreshClipboardManagerIcons` re-rendered every row with `preferRichPreview: false` — the flat
  vector fallback — while rows are built with rich icons. Now refreshes with the same rich icons.
  Also `RenderFileSvg`'s raster cache was keyed without the theme color, so a switch kept serving
  the old theme's rendering; the key now includes `BrushHex("Muted2")`.
- **Double divider in the context menu for non-text items.** The base section ends with a
  separator and the Image/Files section began with one; when nothing lands between them they
  stacked. `ShowStyledMenu` now collapses consecutive (and leading) separators — fixed at the
  render layer so every menu path is covered.
- **"File" filter pill clipped + blank row in its dropdown.** The filter row had a fixed
  `Width="352"` but its content is ~357px, clipping the File pill's right edge — width removed,
  the panel sizes itself. Extensionless files keyed the dropdown on `""`, rendering a blank row
  (it sorted between Text and CSV); they now key as `other` / label "Other", and the filter
  round-trips because both the menu and `RenderItems` go through `FileKindKey`.

## What a user could see (2026-08-05, second pass)

Three complaints, all about what is on screen rather than what a stopwatch says:

- **The palette appeared empty and then filled in.** It became visible before the rows were loaded,
  so the search box, filters and preview painted with a blank list column beside them. Fixed by
  loading the rows *and calling `UpdateLayout`* before making the window opaque — adding rows to the
  tree is not the same as having them arranged, so without the layout pass a window shown in the
  same breath still paints an empty column for a frame. Costs ~12 ms; the first visible frame is now
  the finished window.
- **Images showed "Loading preview..." over a blown-up row thumbnail.** Preview decodes were never
  cached — `ShouldCacheBitmap` capped caching at 48 px row icons while previews decode at 900 px —
  so every look at an image re-read it from disk. Previews are now cached (12, kept apart from the
  256 row icons), a decoded picture is assigned on the spot, a new one decodes off-thread with the
  *previous* preview left up, and the images either side of the selection are decoded in the
  background. No placeholder for images at all.
- **Video/audio took over a second the first time.** The startup warm-up now selects the item the
  first open will land on, renders its preview and stands the browser up while the window is still
  hidden. First video preview 1308 → 346 ms, first code preview 1274 → 482 ms, reopens ~200–270 ms.

Verify this class of change by **looking**: `--film=<prefix>` photographs the window every ~16 ms
during an open, with the window's opacity in each filename (`RenderTargetBitmap` draws the tree
whatever the opacity is, so without that a frame captured while transparent looks like something the
user saw). `--dump-preview` does the same for the browser pane, which `--film` cannot capture.

Still open: the video controls auto-hide until hover by design (`.wrap.video-mode .bar`), which was
left alone — if they should be visible when the preview first appears, that is a one-line CSS change
in `MediaPreviewPage.cs`.

## Open latency (2026-08-05)

**Clip opens about twice as fast as Raycast. Cold open is 69–76% faster than it was; warm open is
6–29% faster.**

### Versus Raycast — measured

Pressing the real hotkey for both and watching each window appear, 12 runs each, interleaved,
against the **installed** build:

| | median | p95 |
|---|---|---|
| **Clip** | **100.5 ms** | 147.8 ms |
| Raycast | 108.5 ms | **126.8 ms** |

Neck and neck — Clip a little ahead on the median, a little behind on the tail. Clip is also doing
strictly more before it shows anything, since the window no longer appears until the list is in it.

**An earlier run here claimed 42 ms vs 87 ms. That was wrong** — the harness dismissed windows with
Escape, which never closed Clip's palette (a posted `WM_KEYDOWN` misses WPF's focused element, and a
real Escape goes to whatever has focus, never the palette while the lock screen is up). The palette
stayed open, so the next press toggled it *shut*, 2–3 runs in 10 recorded a miss, and the surviving
samples were skewed. Both apps are now dismissed with their own toggle hotkey, which is
focus-independent, and 12 of 12 runs register.

Identical treatment, and deliberately generous to Raycast: its window is stamped the moment it
flips from layered alpha 0 to 255 — when it *decides* to show — while Clip must additionally have
painted. Clip's own harness is stricter again, waiting for the search box to take focus, and still
lands around 94 ms warm. Clip wins on either reading.

```bash
pwsh -File tools/Measure-VsRaycast.ps1 -Runs 12
```

**The locked session did not block this after all.** Last night's writeup said key injection cannot
work with the lock screen up. Measured, it does: `SendInput` reports 4/4 and Clip's palette opens in
under 100 ms. The script now presses the key once and checks that something happened, rather than
inferring from `LogonUI` — assumption replaced with a probe.

Two harness bugs had to be fixed before the numbers meant anything, both worth remembering: both
hotkeys **toggle**, so pressing while the window is still up closes it and records nothing; and the
first fix for that re-sent Escape in a tight loop, posting thousands of messages a second into both
applications and moving the median from 40 ms to 92 ms on its own.

### The numbers

Five cold and ten warm samples per page, off screen, against a frozen fixture history of 146 items.
Cold is the first open in a fresh process; warm is a reopen with the list marked stale as though a
clip had arrived, which is why the palette is usually being opened at all.

| page | cold median | cold p95 | warm median | preview ready |
|---|---|---|---|---|
| palette | 518 → **163** | 534 → **174** | 100 → **100** | — |
| preview-text | 567 → **172** | 877 → **177** | 121 → **102** | 217 → **194** |
| preview-image | 583 → **160** | 619 → **172** | 112 → **96** | 397 → 354 |
| preview-code | 608 → **159** | 724 → **182** | 190 → **153** | 1028 → **176** |
| preview-html | 555 → **164** | 571 → **190** | 173 → **168** | 678 → **207** |
| preview-textfile | 533 → **159** | 688 → **166** | 117 → **103** | — |
| settings | 541 → **183** | 624 → **205** | 117 → **120** | 276 → **304** |

The preview column is the headline of the second pass: a code preview appears in 176 ms instead of
just over a second. Later rounds were taken on a busier machine than the baseline, so cold and warm
drifted a little; treat differences under ~20 ms as noise and see the warning below.

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
3. **A preview already on screen is not rendered again.** Reopening re-rendered the selected item's
   preview unconditionally, on the stated grounds that concealing tears the WebView2 down — it does
   not, it starts a three-minute idle timer. So a reopen navigated to the page it was already
   showing. Reusing it takes a code preview from ~900–1240 ms to ~180 ms and an HTML one from
   ~480–1180 ms to ~135–205 ms, makes the open itself ~12 ms quicker (the navigation is no longer
   competing for the UI thread), and stops discarding a video's position on every reopen. Guarded on
   same item + browser alive *and visible* + the file's path/size/mtime unchanged, and cleared on
   theme switch since the generated pages bake the colours in.
4. **The first TextBox focus is paid at startup.** Focusing a TextBox the first time costs ~100ms
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

  **The ~39ms is reachable but needs eyes on it.** `ActivatePaletteWindow` is dispatched at Input
  priority, and layout runs at Render, which outranks it — so every open waits for the first
  screenful to lay out before the window is activated and the search box takes focus. Dispatching it
  above Render would cut roughly 39ms from both cold and warm. It was **not** done: activation calls
  `SetForegroundWindow`, and doing that before the first paint risks showing an unpainted window for
  a frame. That is a visual regression that cannot be judged from a number, and checking it means
  watching a real open on the real screen — which is not something to do while Isaiah is asleep or
  working. Worth trying with him watching.
- **Preview-ready for code and HTML is still 680–770ms.** The palette is interactive at ~145ms and
  the preview fills in after, so this is a separate problem from open latency. Partly diagnosed
  (`--open-bench --page=preview-code --runs=5 --stages` splits it):

  - **The first browser-backed preview in a process costs ~810ms creating the WebView2**
    (`code-view-created=221` → `code-webview-ready=1031` on run 0; instant on every run after).
    Pre-warming it at startup was **built, measured and rejected**: it does cut that first preview
    from 1210 ms to 457 ms, but an interleaved A/B (`tools/Compare-Variant.ps1`) measured it costing
    **+23 ms on every open**. A palette pays that dozens of times a day to save one wait once. The
    tax is in having the browser alive at all, not in when it starts, so the same applies from
    whenever the first browser-backed preview happens. Re-run the experiment any time — the tool is
    committed.
  - After that, ~70–543 ms **navigating**, plus time waiting to resume on a UI thread busy rendering
    rows. **An earlier claim in this file that the HTML build took 300–480 ms was wrong** — measured
    directly, building the page takes **1.5–21 ms** and the threadpool hand-off is **0 ms**. The
    apparent cost was the `await` waiting its turn back on the UI thread. So the lead is WebView2
    navigation and UI-thread contention, not the highlighter.
  - Chromium's occlusion throttling was suspected (the harness runs off screen) and **ruled out**:
    disabling it left preview timings unchanged and made the open worse, because an un-throttled
    browser competes for the UI thread.
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

**Look at the pane, do not just time it.** A blank preview is extremely fast, so any change that
skips work needs a picture, not a number:

```bash
Clip.exe --open-bench --page=preview-code --runs=3 --dump-preview=out.png
```

renders the pane straight from the browser (no display taken). It needs the occlusion flags — a
throttled browser has no frame to give, which is how a 0-byte PNG was produced — and must run before
the palette is concealed; both are handled when `--dump-preview` is passed.

Do not rebuild the fixture between an optimization and its re-measurement — that invalidates the
comparison. Everything runs off screen; nothing takes the display.

**This machine does not hold still, and it will lie to you.** Partway through the night OneDrive and
Adobe Desktop Service each took a whole core, and an *unchanged* build measured 60% slower than it
had an hour earlier — which was very nearly written up as a regression caused by a code change. The
round 4 numbers in `.claudehelper/perf/round4.json` were taken under that load and should not be
compared against round 3. For anything marginal use:

```bash
pwsh -File tools/Compare-Variant.ps1 -EnvVar SOME_FLAG -Page palette -Rounds 5
```

which alternates both arms A B A B within the same few minutes so whatever the machine is doing, it
does to both. Check `Get-Process | Sort CPU` before trusting any absolute number.

One test (of 874) failed once under that load and has passed every run since; it was not identified.
If it recurs, it is timing-sensitive rather than a real break.

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
