# Clip — Command Palette removal → standalone-only — HANDOFF

_Last updated: 2026-06-24 (overnight build)_

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
