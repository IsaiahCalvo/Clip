# Clip — Command Palette removal → standalone-only — HANDOFF

_Last updated: 2026-07-31_

---

## 2026-07-31 (latest) — icons, OCR, version fix; all pushed

Everything is on `origin/ui/grayscale-text-rendering` (10 commits). 262 tests pass. Version
1.1.9 published and installed to `%APPDATA%\Programs\Clip`.

- **Source-app icons** via `IShellItemImageFactory` (`src/Clip.Shell/SourceAppIcons.cs`).
  Two gotchas found the hard way: never request a 16/20 px asset (apps author those as
  separate simplified bitmaps and they come back as smudges — floor at 32 and downscale),
  and the returned data is **straight alpha, not premultiplied** despite the docs, verified
  against `Icon.ExtractAssociatedIcon` pixel-for-pixel. Read the DIB section's bits directly;
  `GetDIBits` drops alpha. A row badge was built and then **removed at the user's request** —
  he did not want an app icon on the list glyphs.
- **OCR** via `Windows.Media.Ocr` (`Clip.Core/OcrTextExtractor.cs` + `Clip.Shell/OcrQueue.cs`).
  Off by default, Settings → "Search text in images". Backfills history when enabled.
  Both suggested GitHub projects were rejected (6 GB GPU VLM; Docker service needing a cloud LLM).
- **Version fix:** `Publish-Clip.ps1` hardcoded a 1.1.0 local default while the newest release
  was 1.1.8, so every local install was "outdated" and the updater offered to downgrade it.
  Now derives from the newest tag + 1.

**Virtualization was measured and skipped.** Palette shows in 33 ms; 93 rows render in ~17 ms
via the existing deferred renderer. Rewriting `BuildRow` into a DataTemplate would be a large
risky refactor of an 11.8k-line file for no measurable gain at these list sizes.

**Still open:** accent overuse (one blue on search border + All pill + Open button + every
footer hotkey), single-line rows, favicons (decided: zero-network monogram tiles by default,
real fetching opt-in — contradicts PRIVACY.md otherwise). Inno Setup is not installed, so
local publishes produce the zip but no `Clip-Setup.exe`.

---

## 2026-07-31 (earlier) — SDK installed, capture bug fixed, UI pass shipped locally

.NET 8 SDK **8.0.423 is now installed** (winget `Microsoft.DotNet.SDK.8`). `dotnet build`
and `dotnet test` both work. **258 tests pass.** The fixed build has been published and
installed over `%APPDATA%\Programs\Clip` via `Publish-Clip.ps1` + `Install-ClipStartup.ps1`,
and is the live running Clip. Note `Publish-Clip.ps1` warns that Inno Setup (ISCC.exe) is
not installed, so no `Clip-Setup.exe` is produced locally — the zip is.

Commits on branch `ui/grayscale-text-rendering` (still local, **not pushed** — no `gh`, no
cached git credentials):

- `2679941` grayscale text AA instead of ClearType
- `393cf26` remove title bar, settings gear moved to footer bottom-right
- `286186a` drop the border box around the preview
- `0d88a81` color-capture regression + three capture-reliability defects
- `b8d549b` bounded PDF tool search + watcher mirror of the capture fixes

**Root cause of "colors don't get captured" (was a regression):** commit `e9036d1` added a
900 ms settle debounce. Pending text items are re-compared to the live clipboard before
saving with an ordinal `string.Equals` against `item.Text` — and Color is the only kind
whose capture rewrites `Text` to canonical `#RRGGBB` uppercase. So lowercase, shorthand,
bare, or newline-terminated hex failed the compare and the item was dropped **entirely**,
not even saved as text. History held 67 items and zero colors. Fixed via
`Clip.Core/ClipboardCaptureMatch.cs` (kind-aware matcher). Verified live: `#3b5bdb` →
`Kind=Color`, `#3B5BDB.png` swatch.

**Capture also lives in the SHELL, not the watcher** (since `45a0212`). `Clip.Watcher` holds
a dormant near-duplicate. Three reliability defects fixed in `MainWindow.xaml.cs`: no retry
on transient clipboard lock, sequence number consumed before the read succeeded (making one
failure permanent), and the single pending slot silently destroying an earlier copy.

Also added: per-item **Paste as Plain Text** (Ctrl+Shift+V) in the action menu. The global
Settings → Default paste format already existed; only the per-item override was missing.
Note this change got swept into commit `0d88a81` rather than its own commit.

**Correction to the earlier backlog below:** date grouping already exists (the "TODAY 35"
header). Only the two-line row shape differs from Raycast.

**Next up, not started:** source-app icons (needs data capture first — exe path is
privacy-gated off by default and stripped from list summaries, and MSIX apps like Claude /
Raycast / Codex need an AUMID that is never captured; `IShellItemImageFactory` is the right
API and machinery already exists in `Clip.Watcher/Program.cs:2262`; existing
`BitmapFromDrawingImage` drops the alpha channel). Then list virtualization, OCR
(`Windows.Media.Ocr` — free, offline, already available on the current TFM; the two GitHub
projects the user linked are both disqualified: one is a 6 GB GPU VLM, the other is Docker
server middleware requiring a cloud LLM), accent discipline, single-line rows.

Favicon decision made: default to a zero-network monogram tile; real favicon fetching stays
an off-by-default opt-in because fetching per copied link contradicts `PRIVACY.md`.

---

## 2026-07-31 — Raycast UI comparison + text-rendering fix

Isaiah asked how Clip's palette compares to Raycast's Clipboard History (which now
ships on Windows) and how to improve the look/color/rendering. Both were captured
live and compared pixel-for-pixel on the same 150%-DPI display.

**Done (committed, local branch `ui/grayscale-text-rendering`, commit `2679941`, NOT pushed):**
- Switched all palette text rendering from ClearType to Grayscale AA, with
  `TextFormattingMode=Ideal` and `TextHintingMode=Auto`, in `MainWindow.xaml`
  (window + shell border) and `MainWindow.xaml.cs:4350-4352, 11721-11723, 11738-11740`.
- Dropped the forced `RenderOptions.SetClearTypeHint(Shell, ClearTypeHint.Enabled)`
  at `MainWindow.xaml.cs:762` → `ClearTypeHint.Auto`.
- Reason: 5x crops proved Clip's glyphs carry orange/blue subpixel fringing on the
  near-black surface while Raycast's are clean grayscale. This is the single biggest
  cause of "Raycast renders a little better."

**NOT VERIFIED — blocker:** this machine has .NET **runtimes only**, no SDK and no
Visual Studio MSBuild, so `dotnet build` fails ("No .NET SDKs were found"). The change
compiles-by-inspection but has not been built or smoke-tested. `gh` is also absent and
git has no cached GitHub credentials, so the branch could not be pushed.

**Next steps:**
1. Install the .NET 8 SDK (or push the branch and let `.github/workflows/ci.yml` build it),
   then run Clip and confirm the fringing is gone at Alt+V.
2. Then work the ranked UI backlog below, highest payoff first:
   - Delete the 36px "Clip" title bar; move the settings gear into the header row next to
     the filters. Raycast has zero window chrome — this is pure reclaimed space.
   - Collapse the six filter buttons + two chevrons into one "All Types" dropdown
     (Raycast's pattern); the header currently reads as a toolbar, not a search bar.
   - Drop the always-on blue 1px border on the search field; make it borderless, larger
     type, focus-only affordance.
   - Stop spending the accent on four things at once (search border, All pill, Open
     button, every footer hotkey). Pick one primary.
   - List rows: single line, ~38px logical, rounded 8px inset selection with margin —
     not full-bleed two-line rows. Add "Today"/"Yesterday" section headers.
   - Show the real source-app icon and link favicons instead of generic type glyphs.
   - Remove the border box around the preview and the fixed 180px INFORMATION panel;
     let preview + info share one scroll like Raycast.
   - Add `VirtualizingStackPanel` to the list (`ItemsHost` is a plain StackPanel).
3. Feature gaps worth stealing from Raycast: OCR on copied images so screenshots become
   searchable, and paste-as-plain-text. Everything else Clip already matches or beats.

---

## 2026-06-30 — Lightweight + instant plan

> **SUPERSEDED by `PLAN-dual-app.md`** (evidence-verified, 20-agent deep audit). The draft below had two errors that the deep audit corrected: (1) "add ReadyToRun" — Clip's hot path is ALREADY R2R (watcher + Core); only the lazily-launched WPF shell is IL. (2) "fix Clip foreground" — the shell ALREADY has the AttachThreadInput+SetForegroundWindow+TOPMOST dance; it's WINSHOT that lacks it. Also resolved: the live app is WATCHER-owned via an UNCOMMITTED redirect baked into the installed binary (git ≠ binary). Use `PLAN-dual-app.md`.

Goal: make Clip (and WinShot) feel as instant as Command Palette while staying lightweight and reliable. Full audit on the Desktop: `Clip-WinShot-vs-CommandPalette-AUDIT.md`. Key reframing: CmdPal is NOT light on RAM (~150MB) — it feels instant only because it stays warm and reveals a pre-built window. "Instant" and "low RAM" trade off; we pick a happy medium.

**Done this session (working tree, NOT committed):**
- Clip: WebView2/Chromium now torn down when the palette closes — `DisposeHtmlPreview()` called from `ConcealPalette` in `src/Clip.Shell/MainWindow.xaml.cs`. Builds clean; 265 tests pass.
- Removed an unrelated UI mockup (`docs/native-ui-mockup.html`) and the startup-redirect experiment's 2 test files (`ShellStartupTests.cs` deleted; reverted additions in `WatcherTrayMenuTests.cs`).
- WinShot: a selector-prewarm change was made then **reverted** (kept WinShot's deliberate lean-idle design per user, enforced by `StartupWarmupPlanTests`). WinShot repo is clean/untouched.

**Agreed build list for Clip (not yet done):**
1. Keep Clip as ONE warm resident process (instant ~14ms open). Resolve against the uncommitted "redirect `Clip.exe` → watcher" experiment still in the working tree (`App.xaml.cs` + `Clip.Watcher/Program.cs`), which pushes the opposite (watcher-primary, on-demand, no warm window). Direction agreed with user = warm/instant → **likely revert that experiment.**
2. **Reliable foreground on open.** NOTE: prior handoff (below) says the live app has the **watcher** owning Alt+V and showing the shell = cross-process, which is exactly the Windows foreground-block / "stuck window" pattern. Fix = the process that shows the window owns the hotkey (single-process), or an `AttachThreadInput` foreground workaround. CONFIRM current hotkey ownership before building.
3. Tray icon appears only once Clip is actually ready ("icon present = usable"). No usable-but-not-ready window.
4. Alt+V **toggles** — press to open, press again to dismiss (like CmdPal's hold-space).
5. ReadyToRun: prior handoff says the self-contained Release is ALREADY R2R (~120MB warm). VERIFY; don't re-add if present.
6. Trim idle RAM: WebView2 dispose (done) + an idle memory-trim like WinShot's `MemoryCleanup`.
7. Auto-enable Windows clipboard history on first run via `HKCU\Software\Microsoft\Clipboard\EnableClipboardHistory = 1` (per-user, no admin) + a Clip settings toggle to control it + harden the Windows-history import (it silently returns nothing if the toggle is off — `WindowsClipboardHistorySource.cs:14`).

**Hard rules:** never break clipboard capture / text editing / cross-day persistence. The resident piece always owns the clipboard listener (no missed copies). Idle RAM-trim frees only display memory, never saved history or an open edit. No "retry later" on a missed Alt+V — if not instant, don't show it (no delayed surprise opens).

**Not doing:** NativeAOT (incompatible with WPF — can't be flipped on without a UI rewrite) and a WinUI 3 rewrite (parked; not a guaranteed RAM win — CmdPal is WinUI 3 and still ~150MB; only worth it to modernize the look later).

**Before building, grep to confirm:** (a) whether the live/installed model is watcher-owns-hotkey or shell-owns-hotkey, and (b) whether R2R is already enabled. Items 1, 2, and 5 hinge on this.

---

## What you asked for
Remove the Command Palette plugin/extension entirely and make the **original
standalone Clip app run off Alt+V**, just like before the Command Palette
experiment. Just the standalone `Clip.exe`.

## Status — DONE (on a branch, not pushed)
- **Branch:** `feature/remove-command-palette` (based on `feature/cmdpal-parity-buildout`).
- **Two commits**, both local only — nothing pushed, nothing merged, `main` untouched.
  - `f06c696` Remove Command Palette extension; make Clip standalone-only
  - `855570c` Drop Command Palette from publish, release, and startup scripts
- **Build:** full solution builds clean — **0 warnings, 0 errors**.
- **Tests:** **266 passed, 0 failed** (was 305; the ~39 removed tests were Command-Palette-only).
- **Publish:** `Publish-Clip.ps1 -FrameworkDependent` produces a package containing only
  `Clip.exe`, `Clip.Watcher.exe`, `Clip.Launcher.exe`, `Clip.WindowsHistory.exe` —
  **no `Clip.Command.exe`, no `Clip.CommandPalette`, no `.msix`.**
- Net change vs the parity branch: **60 files, +34 / −5375 lines** (almost entirely deletions).

## How Alt+V works now (this is the important part)
The parity work had added an `OpenMode` setting: in **CommandPalette** mode the
background watcher *handed Alt+V to Command Palette* instead of opening your app.
Your machine was almost certainly in that mode — that's why it stopped feeling
like the original.

That whole mode is gone. There is now only the standalone path:
`Clip.Launcher.exe` → `Clip.Watcher.exe watch` registers the **Alt+V** global
hotkey and shows the WPF `Clip.exe` window. A stale `"OpenMode": 1` left in your
old `settings.json` is now simply ignored (it reads as standalone), so no manual
cleanup is needed.

## What was removed
- Projects: `src/Clip.CommandPalette` (the extension) and `src/Clip.Command` (its helper CLI).
- `src/Clip.Core/CommandPaletteSettings.cs` (wrote into Command Palette's settings.json).
- The `OpenMode` "Open with" dropdown + all Command-Palette open-mode code in Shell/Watcher/Core.
- Packaging: `tools/Build-ClipCommandPalettePackage.ps1`, `tools/Install-ClipCommandPalettePackage.ps1`.
- `docs/command-palette-extension.md`, the `artifacts/command-palette*` build outputs.
- The CommandPalette build/MSIX steps in `.github/workflows/release.yml` and the
  `Clip.Command` publish in `Publish-Clip.ps1`.
- 9 Command-Palette-only test files (+ trimmed 2 mixed tests).

## What was kept (your standalone improvements were NOT thrown away)
All the standalone-side work from the parity era stays: paste reliability, the
prewarmed "rich palette" fast-open, time-bucket grouping, file/doc previews,
Open-With, in-app settings, Windows-history import, etc. The Watcher's history
import was re-pointed from the deleted `Clip.Command.exe` to `Clip.WindowsHistory.exe`
(same helper the Shell already used), so import still works.

## How to verify in the morning
1. `dotnet build .\Clip.sln`  → clean.
2. `dotnet test .\Clip.sln`    → 266 passing.
3. `.\Start-Clip.ps1` then press **Alt+V** → the standalone Clip window opens.
   (I could not press keys in a GUI session here, so this live keystroke check is
   the one thing left for you to eyeball. The code path + tests are green.)
4. To ship: `.\Publish-Clip.ps1` then `.\Install-ClipStartup.ps1`.

## Getting this onto your machine / into main
Nothing is pushed (the parity branch was never on origin, and I don't push to your
public repo unprompted). Your options:

**A. Use this cleaned branch (keeps all standalone improvements):**
```powershell
git checkout feature/remove-command-palette
.\Publish-Clip.ps1
.\Install-ClipStartup.ps1     # installs to %APPDATA%\Programs\Clip + Alt+V autostart
```
To open a PR for review:
```powershell
git push -u origin feature/cmdpal-parity-buildout    # base (only if you want the focused removal diff)
git push -u origin feature/remove-command-palette
gh pr create --base feature/cmdpal-parity-buildout --head feature/remove-command-palette `
  --title "Remove Command Palette; standalone-only" --body-file .claudehelper/PR-BODY.md
```
(Targeting the parity branch shows *only* the removal — the cleanest review. Targeting
`main` instead shows the whole parity era minus Command Palette.)

**B. Or just go back to the pristine pre-experiment app:** `main` already has zero
Command Palette code. `git checkout main` + publish gives you the original standalone
as it shipped (v1.0.x) — but you'd lose the parity-era standalone improvements listed above.
I chose A because it matches "remove the extension" while keeping your work; B is there
if you'd rather have the literal original.

## Known follow-up (not blocking, not shipped)
- `tools/Measure-ClipPerformance.ps1` is a local dev perf harness that still contains
  (now-inert, fully guarded) Command-Palette measurement helpers. It isn't run in CI and
  doesn't affect the app or build. Prune its `*CommandPalette*` functions when convenient.
- Historical planning docs (`.claudehelper/BUILD-PLAN.md`, `GAP-REPORT.md`,
  `clip-palette-gap-analysis.json`) are left as a record of the experiment.

## Fluidity & polish pass (2026-06-25) — installed live
Commits on `feature/remove-command-palette`:
- `0af78da` anti-flicker reveal: DWM cloak hides the window while it paints, uncloaks on first frame.
- `2fee92e` selection now uses the **live Windows accent color** (selected row + active filter +
  settings), falling back to the themed accent if the registry read fails.

Both are **built, tested (266/0), and installed live** — Alt+V verified (watcher owns the hotkey).
The pre-warmed window now sits ~120MB (was ~210MB) with the self-contained R2R Release.

**Instant revert** if you dislike the look: the prior (cloak-only) build is saved at
`%APPDATA%\Programs\Clip.backup-cloak`. To roll back:
```powershell
Get-Process Clip,Clip.Watcher | Stop-Process -Force
Remove-Item "$env:APPDATA\Programs\Clip\*" -Recurse -Force
Copy-Item "$env:APPDATA\Programs\Clip.backup-cloak\*" "$env:APPDATA\Programs\Clip" -Recurse -Force
Start-Process "$env:APPDATA\Programs\Clip\Clip.Watcher.exe" -ArgumentList watch -WindowStyle Hidden
```

### Deliberately deferred (need your eyes / risk)
- **Mica frosted backdrop** — requires `AllowsTransparency=True` + a transparent window/root, which
  reworks contrast everywhere and gives up the perf win of avoiding WPF's software-render path.
  Do it when you can see/approve it.
- **List virtualization** — the list is hand-built; real virtualization is a core rewrite verifiable
  only by GUI interaction. The app already lazy-loads rows + caps image memory, so low ROI for the risk.
- The keyboard-hint footer the mockup showed **already existed** (Enter/Copy/Pin chips).
