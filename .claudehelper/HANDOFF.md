# Clip — Command Palette removal → standalone-only — HANDOFF

_Last updated: 2026-06-30_

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
