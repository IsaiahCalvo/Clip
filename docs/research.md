# Research Notes

Updated 2026-06-20.

## Findings

- PowerToys Command Palette extensions are standalone .NET apps that talk to Command Palette through a WinRT API.
- Extensions register through a packaged app manifest using `com.microsoft.commandpalette`.
- Supported UI surfaces include list pages, detail pages, form pages, markdown pages, grid pages, context menu items, settings pages, and top-level commands.
- The official setup path is the Command Palette command named `Create a new extension`.
- Visual Studio deployment is the normal test/install path; build alone is not enough to refresh an extension package.
- Extensions are installed as packaged apps and can be distributed through Microsoft Store, WinGet, and the Command Palette Extension Gallery.
- WinGet discovery requires the `windows-commandpalette-extension` tag.
- A Command Palette extension can be a no-Clip-sign-in path because it can read Clip's local store on the user's own machine.
- The PowerToys repo has Command Palette extension samples and is still useful as an implementation reference.
- A Raycast-style tool needs its own local store so it can pin, reorder, edit, save, append, and open image items.

## Decision

Build this as two pieces:

1. Local clipboard engine: captures and owns history.
2. Command Palette extension: displays and acts on that owned history.

This keeps the app free and avoids paid tools like ClipboardFusion.

## Current local implementation

- `Clip.Core`: local JSON store and item actions for Clip.
- `Clip.Watcher`: Clip background clipboard watcher plus command-line actions.
- `Clip.Shell`: current standalone WPF palette.
- Future `ClipCommandPalette`: separate packaged extension that references `Clip.Core` but not `Clip.Shell`.

## Idle Architecture Finding

- The current WPF shell still creates `MainWindow` at startup so it can own the tray, hotkeys, clipboard listener, update checks, and palette.
- The lightweight watcher can sit idle around 14 MB private memory, while the rich WPF shell is closer to 100 MB hidden idle.
- The next major memory win is to make a small background host own tray/hotkey/clipboard capture, then launch or create the rich WPF palette only when the user opens Clip.
- The Command Palette extension should follow the same rule: read `Clip.Core` summaries directly and never start `Clip.Shell`.
- `Clip.exe --palette-session` is the bridge mode for that split: it opens the rich WPF palette and exits when the palette is hidden.
- `Clip.Watcher.exe watch` is the low-idle host path: watcher owns clipboard capture and hotkey handling, then opens the WPF shell on demand.
- The watcher must stay headless. It should not pre-create, show, or fall back to a separate watcher palette.
- Full hidden list prewarm was rejected: it pushed watcher idle private memory to about 52.6 MB, over the 40 MB watcher budget. Keep background startup headless unless the memory budget changes.
- Publish uses ReadyToRun only for `Clip.Watcher` and `Clip.Core`, then overlays those binaries into the normal package. Full-app ReadyToRun pushed the package over the size budget, while the targeted watcher overlay added less than 1 MB and improved cold shortcut launch.
- `Publish-Clip.ps1` now uses a tiny .NET Framework launcher when the built-in .NET Framework compiler is present, then falls back to the ReadyToRun launcher. Local seven-sample testing measured cold shortcut average at 697.4 ms for the .NET Framework launcher versus 766.6 ms for the managed .NET launcher.
- `Publish-Clip.ps1` automatically replaces the launcher with a Native AOT launcher when the Visual Studio C++ linker toolchain is present; without that toolchain it falls back to the .NET Framework or ReadyToRun launcher. Use `-RequireNativeLauncher` when the build must fail instead of falling back, or `-NoNativeLauncher` to force the fallback path.
- Startup scripts, the installer, and the in-app Settings startup toggle now target the watcher host so normal installs can idle near the watcher memory profile instead of keeping WPF resident all day.
- Fresh palette opens render from `history.top.index.json`, a tiny recent-summary index capped at 64 items and 1,024 text characters per item, then refresh the full summary list after first paint.

## Follow-up UI Work

- Replace the basic `Open With...` file dialog with a searchable app picker.
- The picker should list installed apps, filter by typed app name, and launch the selected app with the selected clipboard file/image.
- Long term, prefer Command Palette's native UI for that picker when the extension wrapper is built.
- File previews for PDF, Word, and other document types need a separate preview-handler feature. The WinForms prototype can add file-type previews later, but the proper long-term home is the Command Palette UI integration.

## Verified

- The current standalone app has a perf gate in `tools/Measure-ClipPerformance.ps1`.
- The perf gate reports both rich shell hidden memory and lightweight watcher hidden memory so the idle gap stays visible.
- The current extension path is documented in `docs/command-palette-extension.md`.
- Published output keeps `Clip.Watcher.exe` so the low-idle host path can be tested from the same folder as `Clip.exe`.
- The perf gate measures the published watcher host in `watch` mode.
- Shortcut/show verification expects the visible window to be the WPF shell, not a watcher palette.
- The perf gate now separately measures the already-running watcher hotkey path, because this is the normal day-to-day open path and should stay under the native-feel budget.
- The installed shortcut should keep targeting `Clip.Launcher.exe`, which signals or starts `Clip.exe --palette-session`.
- The native-launcher publish path is guarded by linker detection so normal release builds stay reproducible on machines without the C++ workload.
- The .NET Framework launcher path is guarded by `csc.exe` detection and can be disabled with `Publish-Clip.ps1 -NoNetFxLauncher`.
- Publish trims unused legacy theme, printing, VisualBasic, service, web, and event-log assemblies; the perf gate now fails if the published folder exceeds 165 MB.
- Publish also removes `PresentationUI.dll` and `System.DirectoryServices.dll`; the real published folder now measures 150.7 MB, with the installed folder at 156 MB including installer metadata. Keep `System.Windows.Controls.Ribbon.dll` even though Clip does not directly use Ribbon controls, because WPF BAML command conversion can load it at runtime.
- `Publish-Clip.ps1 -FrameworkDependent -NoInstaller` creates a lightweight portable build for users who already have the .NET 8 Desktop Runtime. A local 2026-06-20 build measured 27.7 MB unpacked and 7.34 MB zipped, and the release workflow now uploads `Clip-win-x64-framework-dependent.zip` alongside the self-contained installer and zip.
- Whole-app ReadyToRun is intentionally not used: an experiment measured the package at about 187 MB, above the 165 MB budget.
- The perf gate expects the first palette-session list load to stay under 50 ms; the small top-summary index exists specifically to keep cold first paint from reading the full history summary file.
- The perf gate now measures the packaged Command Palette COM-server idle process. Local measurement on 2026-06-20 was about 10 MB private memory and 0 CPU over 5 seconds, which keeps the plugin path meaningfully lighter than the standalone WPF shell.
- The old lightweight watcher palette was removed after the shell path became the only visible UI. Keep watcher measurements focused on idle, capture, and hotkey-to-shell signaling.
- Shortcut cold-start reporting now separates window-visible time, launcher-to-watcher-main time, launcher-to-palette-shown time, and first-list-load time. On 2026-06-20, showing the palette immediately during `--show` startup cut one local internal cold sample from 1067 ms to 462 ms; repeat samples varied around 905-997 ms because first WinForms `Show()` can still block. The perf gate now keeps this cold launcher-to-show path under 1200 ms while the warm watcher UI stays under the 50 ms feel target.
- The watcher keeps the prewarmed lightweight palette parked offscreen instead of hiding it. The 2026-06-20 perf gate measured palette open at 14 ms, first list load at 27 ms, search at 19 ms, watcher hotkey at 16 ms, watcher settled idle at 18.9 MB with 0 CPU over 10 seconds, no startup WebView2 load, and watcher secondary surfaces at Settings 34 ms / Rename 21 ms / Edit Text 50 ms / Open With 21 ms.
- Frame prewarm no longer recursively creates every child handle before the offscreen show. This dropped measured warm-start prewarm from roughly 660-710 ms to about 99-113 ms while keeping hotkey and palette paths under the 50 ms feel target. The first installed start after reinstall can still be slower while Windows scans/caches the new binary.
- The shortcut signal perf gate now reports both shell-observed latency and watcher app latency, with a 250 ms outer limit and 50 ms watcher-render limit. The current package measured shortcut signal at 111 ms and watcher app show at 16 ms. The remaining time is the tiny .NET Framework launcher process starting, not watcher UI rendering.
- Repeated InfoRows layout diagnostics are now trace-only behind `CLIP_WATCHER_TRACE=1`, keeping normal palette opens from writing per-row debug lines to disk while preserving deeper layout diagnostics when explicitly needed.
- The 2026-06-20 installed perf gate passed with clipboard capture skipped because Windows clipboard access was blocked outside Clip too: `clip.exe`, `Get-Clipboard`, and WinForms clipboard writes all returned access denied / requested operation did not succeed, with no open clipboard owner reported.
- The lightweight watcher now trims `debug.log` once it grows past 2 MB, keeping only the latest 768 KB. Shell logs were already bounded; this keeps long-running watcher installs from accumulating large debug files.
- Windows history import now runs through the short-lived `Clip.Command.exe import-windows-history` helper instead of loading the WinRT clipboard-history stack inside the long-running watcher. A 2026-06-20 installed sample after startup import measured the watcher at 17.5 MB private memory with no resident `Microsoft.Windows.SDK.NET.dll`, `Windows.ApplicationModel.DataTransfer.dll`, or `WinRT.Runtime.dll` modules.
- The watcher now keeps one Settings form prebuilt after the first palette load, not during idle startup. The 2026-06-20 perf gate measured watcher Settings open at 24 ms, down from edge samples around 50 ms, while watcher idle stayed about 18.5 MB with 0 CPU over 10 seconds.

## Sources

- https://learn.microsoft.com/en-us/windows/powertoys/command-palette/creating-an-extension
- https://learn.microsoft.com/en-us/windows/powertoys/command-palette/extensibility-overview
- https://learn.microsoft.com/en-us/windows/powertoys/command-palette/adding-commands
- https://learn.microsoft.com/en-us/windows/powertoys/command-palette/update-a-list-of-commands
- https://learn.microsoft.com/en-us/windows/powertoys/command-palette/publish-extension
- https://learn.microsoft.com/en-us/windows/powertoys/command-palette/finding-and-installing-extensions
