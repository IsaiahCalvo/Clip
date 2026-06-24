## What this does
Removes the Microsoft Command Palette extension from Clip and restores the
original behavior: **Alt+V opens the standalone `Clip.exe`**. The shipped download
is now just the standalone app (no extension, no MSIX, no `Clip.Command.exe`).
All the standalone improvements made during the parity work are kept.

## Files changed
- **Deleted** the extension and its helper CLI: `src/Clip.CommandPalette/`, `src/Clip.Command/`.
- **Deleted** `src/Clip.Core/CommandPaletteSettings.cs` (configured Command Palette's settings.json).
- **Shell** (`MainWindow.xaml.cs`): removed `ClipOpenMode` and the "Open with" settings dropdown.
- **Watcher** (`Program.cs`): removed `WatcherOpenModePreference` / `EnsureCommandPaletteWarm` /
  `TryLaunchCommandPalette`; Alt+V is now always registered (standalone). Windows-history import
  re-pointed from the deleted `Clip.Command.exe` to `Clip.WindowsHistory.exe`.
- **Core** (`ClipSharedSettings.cs`): dropped the now-unused `OpenMode`; `ClipboardHistoryListCommand`
  open/reveal actions re-pointed to `Clip.Watcher.exe` (which already serves those verbs).
- **Scripts/CI**: removed the Command Palette steps from `Publish-Clip.ps1` and `.github/workflows/release.yml`;
  deleted `tools/*CommandPalette*.ps1`; tidied `Install-ClipStartup.ps1`.
- **Tests**: deleted 9 Command-Palette-only test files; fixed 2 mixed tests.
- Net: 60 files, +34 / −5375 lines.

## How to test
1. `dotnet build .\Clip.sln` → clean (0 warnings, 0 errors).
2. `dotnet test .\Clip.sln` → 266 passing, 0 failing.
3. `.\Start-Clip.ps1`, then press **Alt+V** → the standalone Clip window opens.
4. `.\Publish-Clip.ps1 -FrameworkDependent -NoInstaller` → output package contains
   `Clip.exe` and no `Clip.Command.exe` / `Clip.CommandPalette` / `.msix`.

## Edge cases handled
- A stale `"OpenMode": 1` in an existing `settings.json` is ignored and reads as standalone,
  so users previously in Command-Palette mode auto-revert with no manual cleanup.
- The Watcher's history import keeps working via `Clip.WindowsHistory.exe` (the Shell already used it).

## What's NOT included
- `tools/Measure-ClipPerformance.ps1` still contains inert, fully guarded Command-Palette
  measurement helpers (dev-only tool, not in CI, doesn't affect the app). Left for a later prune.

## Verification done by agent
- [x] Build passes (0/0)
- [x] Tests pass (266/0)
- [x] Standalone publish verified clean (no Command Palette / Clip.Command artifacts)
- [ ] Live Alt+V keystroke — needs a human (couldn't send keys in a headless session)
