# Clip — Command Palette vs Standalone App: Gap Report

_Generated 2026-06-23 from a 26-agent code analysis (Clip.Shell + Clip.Core + Clip.Watcher/Command + docs vs Clip.CommandPalette)._
_Full machine-readable data (incl. per-gap implementation blueprints): `clip-palette-gap-analysis.json`._

**Score: 25 features already at parity, 19 gaps (1 critical, 4 high, 8 medium, 6 low).**

The palette is already a strong native surface: searchable list, kind/pinned filter, pinned/recent sections,
copy/pin/unpin/move/delete/rename/edit-text/save-as-file/copy-path/open/reveal all in-process, details pane with
metadata, image preview, settings page, and fast cold-open. The gaps below are what's missing or weaker vs the exe.

---

## CRITICAL

### 1. Paste into the previously-focused app (paste-on-Enter)
- **Now:** Palette has NO paste at all. Enter opens a preview; you can only *Copy*, then paste manually.
- **Exe:** Enter restores focus to the prior app, sets the clipboard, and fires Ctrl+V (with retry, per-app override hotkeys, Alt+V for Claude CLI, direct UIA paste into Explorer search).
- **Fix:** New `ClipPasteCommand` as the **default** command on each row → set clipboard (reuse `ClipboardPasteData`), `CommandResult.Dismiss()`, synthesize Ctrl+V after a focus-settle delay. Fallback: delegate to the watcher's `paste <id>` verb.
- **Refs:** MainWindow.xaml.cs:3841 PasteSelected; Clip.Watcher/Program.cs:184 paste verb; ClipboardPasteData.cs:20

---

## HIGH

### 2. Paste/copy honors formatting preference (original vs plain)
- Copy path hard-codes PlainText, so "Formatted" items lose their formatting even on copy. Wire `DefaultPasteFormat` from settings into `ClipboardPasteData.Create` (HTML/RTF). Add "Paste as plain text" override. — ClipHistoryActionCommand.cs:174

### 3. Time-bucket grouping (Today / Yesterday / This week / Month / Year / Older)
- Palette only shows "Pinned" + one flat "Recent". Compute a DateKey per item and set `ListItem.Section` to the bucket. — MainWindow.xaml.cs:3726; ClipHistoryPage.cs:212

### 4. Rich file previews (PDF / Office / Visio / HTML / text files)
- Exe renders first-page thumbnails + text excerpts; palette shows only the file path for non-images. Render first-page PNG via the existing watcher renderers (move to shared lib) + embed; text via `TextFilePreviewReader`. Lazy, off the list-render path. — MainWindow.xaml.cs:3533; TextFilePreviewReader.cs:9

### 5. Fuller in-palette Settings
- Palette settings = 3 controls (open-mode, tray icon, update-check). Add the non-capture ones backed by Clip.Core: history limit, max item size, default paste format, run-at-startup, clipboard data folder. Leave capture-hotkey/privacy/theme to the exe. — ClipSettingsPage.cs:19; ClipboardHistoryStore.cs:1037,1575

---

## MEDIUM

6. **Append to clipboard** (accumulate text for Text/Link items) — MainWindow.xaml.cs:4827
7. **Share** (Windows share sheet + optional Blip target); Clip.Core payload already exists — ClipboardSharePayload.cs:19
8. **Open With…** searchable app picker for file/image items (palette is an *ideal* host for this) — MainWindow.xaml.cs:3182; docs/open-with-and-file-preview-plan.md
9. **Date filter** dropdown (All/Today/…/Older) — MainWindow.xaml.cs:6986
10. **Live updates while open** — new copies don't appear until you re-search; add a FileSystemWatcher → InvalidateItems — ClipHistoryPage.cs:80
11. **More than 25 items / paging** — palette hard-caps at 25; raise limit / incremental load, respect HistoryLimit — ClipHistoryPage.cs:10
12. **Full action set on the preview card** — inline buttons capped at 4 (copy/pin/open/reveal); rename/edit/delete/save/append/share/paste unreachable inline — ClipItemPreviewCard.cs:208

---

## LOW

13. File-kind sub-filter (pdf/excel/word/… under Files) — MainWindow.xaml.cs:6998
14. Rich-text (RTF/HTML) preview fidelity — platform-limited; keep plain text — ClipItemPreviewCard.cs:178
15. Expand image to full overlay w/ pan-zoom — not feasible in palette; rely on Open — MainWindow.xaml.cs:5444
16. Remove duplicate "Information" markdown table (renders twice) — ClipHistoryPage.cs:300
17. Save debug-log snapshot / diagnostics command — MainWindow.xaml.cs:7025
18. Trigger Windows clipboard-history import from palette — ClipboardHistoryImportService.cs:24
19. "Paste latest item" quick command — MainWindow.xaml.cs:5887

---

## Cross-cutting notes
- **Reuse Clip.Core**: most fixes are wiring, not new logic (ClipboardPasteData, ClipboardSharePayload, BlipShareLaunchPlan, TextFilePreviewReader, ClipboardHistoryImportService, ApplyHistoryLimit/SetContentRootPath already exist).
- **Promote to shared lib**: DateKey/grouping, Open-With app discovery, and the PDF/Office preview renderers currently live in Clip.Shell/Clip.Watcher — extract so both surfaces share them.
- **Keep cold-open fast**: do previews/thumbnails lazily, never during list render.
- **Platform limits** (document, don't fight): WebView2 live HTML, image pan/zoom, host-controlled theme.
- **Hotkey note (observed live):** settings `OpenMode=1` makes the standalone Watcher defer Alt+V to Command Palette — palette is already the primary surface.
