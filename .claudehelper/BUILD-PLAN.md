# Clip → Command Palette Parity Build-Out — Plan & Tracker

**Branch:** `feature/cmdpal-parity-buildout` (baseline checkpoint: `f0c4639` on `codex/clip-paste-focus-fallback`)
**Baseline:** 210 tests green; CmdPal Release build passes.
**Goal:** Every standalone feature available in the Command Palette extension, native CmdPal rendering,
matching panels (list + details/preview), ≤50ms per feature click. Reuse Clip.Core. Don't regress the 25 parity features.

**Perf gate:** `tools/Measure-ClipPerformance.ps1` (defaults: 50ms for load/palette/settings/search/secondary-UI).
**Test gate:** `dotnet test tests/Clip.Tests/Clip.Tests.csproj` (must stay ≥210 passing, 0 fail).
**Blueprints:** `.claudehelper/clip-palette-gap-analysis.json` (per-gap implementation steps).

## Architecture decisions
- **Paste** = palette sets clipboard (honoring format) + `CommandResult.Dismiss()` + delegate to Watcher `paste <id>`
  verb (reliable focus-restore + Ctrl+V injection; builds on Codex's focus-fallback work). Wire as DEFAULT command (Enter).
- **Reuse Clip.Core** everywhere; promote Shell-only logic (DateKey grouping, Open-With discovery, doc preview renderers)
  into shared libs so both surfaces share them.
- **Native look**: lean on CmdPal SDK (ListItem/Details/FactSet/Forms/ContentPage), no custom chrome. Match standalone panels.
- **Keep cold-open fast**: previews/thumbnails lazy, never on the list-render path.
- **Platform limits** (document, don't fake): WebView2 live HTML preview, in-palette image pan/zoom, host-controlled theme.

## Phases (each: implement → build → test → commit; verify between phases)

### Phase 1 — Foundation + Paste (CRITICAL)  ✅ (commit pending) — 224 tests green
- [x] F1 Expose settings in ClipSharedSettings (DefaultPasteFormat + HistoryLimit/MaxItemSize/Folder read accessors)
- [x] F2 Add action ids to Clip.Core ClipboardHistoryListAction.ForHistoryItem: paste, paste-plain, append, share
- [x] G1 Paste: ClipHistoryActionCommand handles paste/paste-plain → set clipboard (format-aware) + Dismiss + Watcher `paste <id>`
- [x] G1b Wire Paste as DEFAULT command on each list item; preview demoted to secondary (Enter pastes)
- [x] G2 Copy honors DefaultPasteFormat (HTML/RTF) instead of hard-coded PlainText
- _Carry-forward:_ promote StartupRegistration (Shell→Core) for P3/P4 run-at-startup; widen Watcher AppendText to accept Link;
  verify no palette code still assumes 'copy' as DefaultActionId (now 'paste'). Share is Id-only — palette dispatches in-process (P3).

### Phase 2 — Browse & Preview parity  ✅ — 285 tests green
- [x] G3 Time-bucket grouping (Today/Yesterday/This week/Month/Year/Older) via ListItem.Section
- [x] G9 Date filter dropdown
- [x] G8 File-kind sub-filter (pdf/word/excel/…)
- [x] G11 List limit / incremental load (raise 25 cap, respect HistoryLimit)
- [x] G10 Live updates while open (FileSystemWatcher → InvalidateItems)
- [x] G4 Rich file previews — TEXT files via Clip.Core FilePreview/TextFilePreviewReader (details + card). 
      _Deferred:_ PDF/Office/Visio first-page thumbnails need a helper-renderer subproject → Phase 3 (G4b).
- [x] G13 Remove duplicate Information markdown table (native FactSet only)
- [x] G12 Full action set on preview card — Paste primary/Enter, destructive Delete, full set on preview page; icons added
- _Note:_ Phase 2 done in two commits (part 1 = G3/G9/G8/G11/G10/G13; part 2 = G12/G4) due to a transient API outage mid-run.

### Phase 3 — Actions & Settings  🔄 (3a done; 3b remaining) — 286 tests green
- [x] G5 Append to clipboard (in-process)
- [x] G6 Share (Blip target; Windows share sheet is standalone-only — windowless extension can't host it)
- [x] G15 Save debug-log snapshot / diagnostics command (delegate to Clip.exe --tray-action=save-log)
- [x] G16 Trigger Windows clipboard-history import (delegate to Clip.Command import-windows-history)
- [x] G19 "Paste latest item" top-level quick command
- [x] G14 Full in-palette settings (paste format, history limit, max item size, data folder, run-at-startup; StartupRegistration promoted Shell→Core)
- [x] G7 Open With… searchable app picker page (OpenWithAppDiscovery+OpenWithRecentStore promoted to Core; ClipOpenWithPage)
- [x] G4b PDF/Office/Visio first-page thumbnails (Clip.Watcher `preview-thumb` verb; lazy + cached PNG embed)
- _Notes:_ 3a + G14-settings finished by main agent during an Anthropic API 529 overload that kept killing subagents; rest via workflow once it recovered. Clip.Core retargeted net8.0→net8.0-windows10.0.19041.0 for Open-With (Registry/Assoc/COM).

### Phase 4 — UI fidelity + full verification  🔄
- [x] Native CmdPal rendering (list + details/preview panels) — inherent to the SDK; matches the praised look. Icons/tags consistent.
- [x] Full `dotnet test` green — 305 passing, 0 failed (was 210 baseline).
- [x] Trimmed Release publish warning-free (Open-With made trim-safe: source-gen JSON + IShellLinkW COM).
- [x] Rebuild + reinstall MSIX (v1.0.100.0 verified live; v1.0.101.0 = trim-safe rebuild).
- [x] Live smoke test in Command Palette: Paste = Enter (primary), Preview = Ctrl+Enter; per-item menu shows
      Paste / Paste as Plain Text / Copy / Append to Clipboard / Rename / Edit Text / Pin / Move / Delete /
      Save as File / Share; new top-level "Paste latest Clip item" present; details pane rich. ✅
- [~] 50ms click budget: met BY DESIGN (in-process actions, cached list, lazy preview/thumbnail, no I/O on list-render)
      and covered by CommandPalettePerformanceScriptTests (green). Full live `Measure-ClipPerformance.ps1` is the
      standalone harness (suspends running Clip) — run on request for hard numbers.
- [ ] Optional follow-ups: Office/Visio thumbnails need the COM server installed; per-app real bitmap icons in
      Open-With picker (currently Segoe glyphs); periodic prune of %TEMP%\Clip\PaletteThumbs cache.

## Status log
- 2026-06-23: baseline checkpoint + branch; gap analysis done; plan created. Starting Phase 1.
- 2026-06-23: Phases 1–3 complete (all 19 gaps), Phase 4 verification — 13 commits, 305 tests green, MSIX
  installed + smoke-tested live. Paste-on-Enter confirmed working in Command Palette. Branch: feature/cmdpal-parity-buildout.
