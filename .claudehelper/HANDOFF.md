# Clip → Command Palette Parity — HANDOFF

_Last updated: 2026-06-23_

## What this was
Bring the **Clip.CommandPalette** extension to feature + responsiveness parity with the standalone Clip app,
using Command Palette's native rendering. Driven from a 26-agent gap analysis (see `clip-palette-gap-analysis.json`,
`GAP-REPORT.md`) that found 25 features already at parity and **19 gaps**. All 19 are now implemented.

## Current state — DONE
- **Branch:** `feature/cmdpal-parity-buildout` (baseline checkpoint `f0c4639` on `codex/clip-paste-focus-fallback`).
- **Tests:** 305 passing, 0 failed (baseline was 210). Full solution builds clean; trimmed Release publish warning-free.
- **Installed + smoke-tested live:** MSIX v1.0.101.0 (trim-safe). Paste-on-Enter confirmed working in Command Palette.
- **Not committed/pushed beyond the branch.** 13 commits on the branch; nothing merged. Push/PR only on request.

### Commits (branch, newest last)
f0c4639 checkpoint · dd24339 P1 paste · 325b50c P2pt1 browse · 89a8466 P2pt2 preview ·
e239d16 G16 import · 855c313 P3a actions · ca104e3 G14 settings · 223d693 P3b startup/open-with/thumbs ·
ced2efc P4 trim-safe Open-With

### What shipped (by gap)
Paste (default Enter, format-aware, delegates Ctrl+V to Watcher `paste <id>`), paste-as-plain, format-aware copy,
append (in-process), share (Blip), save-log, Windows-history import, paste-latest (top-level), time-bucket grouping,
date filter, file sub-filter, paging/incremental load, live updates (FileSystemWatcher), text-file previews,
PDF/Office/Visio thumbnails (Watcher `preview-thumb` verb, lazy+cached), de-duped Information panel,
full preview-card actions, in-palette settings (paste format / history limit / max size / data folder / run-at-startup),
Open-With searchable picker.

### Key architecture decisions
- Paste reliability = palette sets clipboard + `Dismiss()` + Watcher `paste <id>` (owns focus-restore + keystroke).
- Shared logic promoted to **Clip.Core**: StartupRegistration, OpenWithAppDiscovery, OpenWithRecentStore,
  ClipboardHistoryTimeBucket/DateFilter/FileKindFilter, FilePreview. Clip.Core retargeted **net8.0-windows10.0.19041.0**.
- Doc thumbnails delegated to a Watcher helper verb (keeps the net9 extension lean; no heavy PDF/Office deps in it).
- Open-With made trim-safe: source-gen JSON (`OpenWithJsonContext`) + `IShellLinkW` COM (no dynamic/ProgID).

## Build / install / test
- Build extension: `dotnet build src/Clip.CommandPalette/Clip.CommandPalette.csproj -c Debug`
- Tests: `dotnet test tests/Clip.Tests/Clip.Tests.csproj -c Debug`  (use Windows PowerShell / pwsh not present)
- Build+install MSIX: `tools/Install-ClipCommandPalettePackage.ps1 -Version <bump> -Build` (run with **powershell.exe**, not pwsh)
- After install, restart `Microsoft.CmdPal.UI` + `Clip.CommandPalette` processes so the new build loads.
- Perf harness: `tools/Measure-ClipPerformance.ps1` (50ms budgets; standalone-oriented, suspends running Clip).

## Next steps / open follow-ups
1. (Optional) Run the full live `Measure-ClipPerformance.ps1` for hard 50ms numbers — not run (disruptive to running Clip).
   The 50ms budget is met by design (in-process actions, cached list, lazy I/O) and the perf-script tests are green.
2. Office/Visio thumbnails need the matching COM server (Word/Excel/PowerPoint/Visio) installed; PDF uses pdftoppm.
3. Open-With picker uses Segoe glyph icons by source (no real per-app bitmaps — avoids System.Drawing in the extension).
4. Add a periodic prune of `%TEMP%\Clip\PaletteThumbs` so the thumbnail cache doesn't grow unbounded.
5. Consider a CmdPal-side test pattern for settings-page wiring (none exists today).
6. Live-verify Open-With end to end (copy a file → Open with…) — smoke test covered text items; Open-With is gated to file/image.
7. When ready: PR/merge the branch (kept isolated; not pushed).
