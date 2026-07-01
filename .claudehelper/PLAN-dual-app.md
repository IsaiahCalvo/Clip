# Dual-App Improvement Plan: Clip + WinShot

**Status: evidence-verified build plan** (20-agent deep audit + 12-claim adversarial verification, 2026-06-30). Every item re-confirmed against source AND installed runtime artifacts. Tags: **MEASURED** = read directly. **INFERRED** = derived. **UNCONFIRMED** = flagged, not proven.

### IMPLEMENTATION STATUS (2026-06-30, working tree — NOT committed, NOT published/installed)
- ✅ **Option A chosen + built.** Clip: removed the watcher-redirect (`App.xaml.cs`) so `Clip.exe` no-args is the single warm standalone shell that owns Alt+V + tray + window in one process; removed the 4 dead redirect methods + unused `using System.Diagnostics;`.
- ✅ **Alt+V toggle** (`MainWindow.xaml.cs` WM_HOTKEY handler: `if (_paletteOpen) ConcealPalette("hotkey-toggle") else ShowPalette()`).
- ✅ **WebView2 disposes on conceal** (earlier this session).
- ✅ **WinShot W1 ReadyToRun** added to `tools/install.ps1` publish.
- ✅ **WinShot W2 selector force-foreground** (`FastRegionSelectorDialog.ForceForeground()`, called in `ShowAsync` when not prewarming; follow-timer kept as fallback).
- Verified: Clip 265 tests pass, WinShot 290 tests pass; both build clean. Nothing committed/published — live installs still run the OLD watcher-owned Clip until a rebuild+reinstall.
- ⏸ **Deferred (deliberate):** C5 idle working-set trim (risks a reveal hitch → conflicts with the instant priority; revisit only if idle RAM is a real complaint); tray-icon-only-when-ready (polish; needs care around the standalone startup show sequence); C2 installer alignment (`installer/Clip.iss:104` still launches `Clip.Watcher.exe watch` — fix so fresh Inno installs match the single-process model).

---

## 1. Ground Truth (resolved)

### 1.1 Clip — launch path & Alt+V ownership
- **Live autostart = `Clip.exe` no args** (MEASURED: HKCU Run = `…\Programs\Clip\Clip.exe`).
- **Today the WATCHER owns Alt+V cross-process** (MEASURED: watcher `debug.log` 2026-06-30 shows `Open hotkey registered=True key=Alt+V` + `Rich palette signaled existing shell`; shell.log frozen at 2026-06-29).
- **This is driven by an UNCOMMITTED redirect baked into the installed binary** (MEASURED: redirect symbol present in installed `Clip.dll`, absent from git HEAD `6b506ac`). So: a clean rebuild from HEAD silently reverts to single-process shell-owned. **git and the running binary disagree — the #1 landmine (item C1).**

### 1.2 Clip — R2R / self-contained (corrects my earlier error)
- **Self-contained: YES** (MEASURED: `includedFrameworks` + bundled coreclr/clrjit present). Set on the publish CLI, not the csproj (csproj flags are a red herring).
- **R2R: PARTIAL — only `Clip.Watcher.dll` + `Clip.Core.dll`; the WPF shell `Clip.dll` is IL** (MEASURED: PE CLI-header flags). Deliberate, for package size (`Publish-Clip.ps1` sets `=false` for the shell). The login-resident hot path is already R2R; the shell is IL but launches lazily.
- ⇒ **"Add R2R to Clip" is MOOT for the hot path.**

### 1.3 Clip — behavior
- **Alt+V is open-only** (MEASURED). Toggle is feasible — `_paletteOpen` + `ConcealPalette` exist.
- **Foreground workaround ALREADY present in the shell** (MEASURED: `MainWindow.xaml.cs:1349-1386` does AttachThreadInput + SetForegroundWindow + TOPMOST). ⇒ **"Fix Clip foreground" is MOOT. It's WINSHOT that lacks it.**
- **No memory-trim anywhere in Clip** (MEASURED: 0 grep hits for EmptyWorkingSet/SetProcessWorkingSetSize/GC.Collect/MemoryCleanup).

### 1.4 WinShot
- **Self-contained, NOT R2R, no AOT/Trim/SingleFile** (MEASURED: `tools/install.ps1:27`, 0 R2R markers).
- **Zero foreground/activation P/Invokes**; selector only `Show()/Activate()/Focus()` (MEASURED) — the code's own comment (`FastRegionSelectorDialog.cs:60-62`) admits the overlay "isn't the foreground window … doesn't reliably across multiple monitors."
- Autostart enabled; `MemoryCleanup` gated 220MB/600MB; in-process hotkey; idle-evicting capture (MEASURED).

### 1.5 Already done / MOOT — do NOT re-do
1. Add R2R to Clip — moot for hot path (watcher + Core already R2R).
2. Add foreground handling to Clip — moot (shell already has it).
3. Make Clip self-contained — already is.
4. Make WinShot self-contained — already is.

---

## 2. Clip — Build List

### C1. Decide launch architecture: commit or revert the watcher-redirect — **BLOCKING, FIRST**
The installed app's launch model lives in **uncommitted** code; a clean rebuild silently changes runtime behavior. **Decision required (see two options below).** Then make git match the binary. Files: `App.xaml.cs` (redirect + stale comment ~137-141), `MainWindow.xaml.cs`, `Clip.Watcher/Program.cs`. Verify: fresh-built `Clip.dll` redirect-symbol count matches the chosen model; `git status` clean.

### C2. Make the two installers agree with the chosen model
`Install-ClipStartup.ps1:39-41` writes `Clip.exe` no-args; `installer/Clip.iss:104` writes `Clip.Watcher.exe watch`. Pick one target, make both write it. Config-only, LOW risk.

### C3. Alt+V toggle (press again to dismiss) — if wanted
Both handlers call `ShowPalette()` unconditionally. Single-process: one branch at `MainWindow.xaml.cs:1541` (`if (_paletteOpen) ConcealPalette() else ShowPalette()`). Cross-process: the toggle decision must live in the **shell** (it owns `_paletteOpen`), not the watcher — watcher signals "toggle intent." MEDIUM risk cross-process (signal race), LOW single-process.

### C4. (Optional, low value) R2R the WPF shell
Only if first-palette latency is a measured complaint. The login hot path is already R2R; shell launches lazily. Not recommended otherwise. NOT recommended at all: Trim (WPF reflection-fragile), AOT (WPF unsupported), GC server mode (raises idle RAM).

### C5. (Happy-medium option) Idle working-set trim on the warm shell
If we keep the shell warm for instant-always (Option A below), add an `EmptyWorkingSet` call on `ConcealPalette` so idle reported RAM drops (pages back in on reveal, sub-perceptible). Mirrors WinShot's MemoryCleanup. LOW risk.

---

## 3. WinShot — Build List

### W1. Enable ReadyToRun in publish — **highest ROI, zero idle/UI cost**
Add `-p:PublishReadyToRun=true` to `tools/install.ps1:27`. Self-contained means everything JITs cold on first capture after boot; R2R removes that. Does NOT touch lean-idle design. LOW risk. Verify: `WinShot.dll` CLI-header flag 0x4; first-capture-after-boot latency drops.

### W2. Reliable keyboard focus for the region selector from a global hotkey
Port Clip's proven foreground dance (AttachThreadInput + SetForegroundWindow + TOPMOST) into `FastRegionSelectorDialog.ShowAsync` after `Show()` (`:197-202`). Selector ONLY (quick-actions/self-timer intentionally `ShowWithoutActivation`). Keep the existing follow-timer as fallback. MEDIUM risk (depends on ForegroundLockTimeout). **UNCONFIRMED: needs a live multi-monitor repro of dead Esc/Enter before building — if keys already work in practice, drops to low priority.**

### W3. (Micro) Cache the 3 fixed fonts in `ThemePalette` (`:49-53`) as `static readonly`. LOW.
### W4. (Housekeeping) Remove the double `_gifRecorder.Stop()` in `RecordingController.Shutdown` (`:77` & `:89`). TRIVIAL.

---

## 4. Hard Constraints & Non-Goals

**Constraints:** never break Clip live capture (`AddClipboardFormatListener`→`AddOrUpdate`), edit/persistence (atomic `history.json`), no double-capture (don't run shell+watcher capture simultaneously), no retry-later on a missed hotkey, don't enable/prompt Windows clipboard history as part of THIS work (separate agreed feature), keep WinShot lean-idle (empty `LightweightStartupStages`).

**Non-goals:** NativeAOT (WPF-incompatible), WinUI3 migration (parked), GC server mode / aggressive trim for Clip beyond C5, WinShot startup warmups (breaks lean-idle + its test).

---

## 5. The one decision that gates everything (C1)

Two coherent models; pick by priority:

- **Option A — Always instant (single warm process).** Revert the redirect; the shell owns Alt+V + tray + window in one process. Instant every open (~14ms), foreground guaranteed, no cross-process race. Full WPF shell resident (~tens–120MB); add C5 idle trim to keep reported RAM low. Best if "instant every time" wins.
- **Option B — Lightest idle (watcher + on-demand shell).** Commit the redirect; tiny watcher (~16MB) resident, shell launches on Alt+V. Lowest idle RAM, but the first Alt+V after the shell exits is a cold open (not instant), even with C4 R2R. Best if minimal idle RAM wins.

---

## 6. Residual Uncertainty (honest)
1. WinShot W2 dead-keys symptom — cause confirmed, live repro not done.
2. C4/W1 latency payoff magnitude — asserted from "IL JITs cold," not stopwatched on this hardware.
3. Whether the install came from `Install-ClipStartup.ps1` vs the Inno installer — INFERRED; only matters for C2, resolved by the C1 choice.
