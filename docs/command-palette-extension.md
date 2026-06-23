# Clip Command Palette Extension Path

Verified against current Microsoft docs on 2026-06-20.

## Current Recommendation

Build the Command Palette integration as a separate packaged .NET extension that references `Clip.Core`.
Do not launch or host the WPF shell from Command Palette.

The extension should read Clip history through `ClipboardHistoryStore.OpenForCommandSurface()` and `ClipboardHistoryListCommand.Create(store, query, limit)` so opening Command Palette does not load rich HTML/RTF payloads or any WPF UI. Full item hydration should happen only when the user invokes paste/copy with original formatting.
Paste payload rules are reusable from `Clip.Core` through `ClipboardPasteData` and `PasteFormatPreference`; the extension should not reference `Clip.Shell`.

## Why This Is Feasible

Microsoft's current Command Palette model is designed for this shape:

- Extensions are standalone .NET applications.
- Command Palette discovers installed extensions through the Windows Package Catalog.
- The package manifest declares `windows.appExtension` with `Name="com.microsoft.commandpalette"`.
- The manifest also declares an out-of-process COM server; the generated template handles the boilerplate.
- The extension process talks to Command Palette through `Microsoft.CommandPalette.Extensions` and the toolkit package.
- List pages are supported, which matches Clip's searchable clipboard list.
- Distribution can go through Microsoft Store, WinGet, and the Command Palette Extension Gallery.
- WinGet entries need the `windows-commandpalette-extension` tag so users can find and install the extension from Command Palette.

This gives us a no-Clip-sign-in path: the extension can read local Clip history from the user's own machine, and users can install/update it through Store, WinGet, or the Gallery.

Sources:

- https://learn.microsoft.com/en-us/windows/powertoys/command-palette/extensibility-overview
- https://learn.microsoft.com/en-us/windows/powertoys/command-palette/creating-an-extension
- https://learn.microsoft.com/en-us/windows/powertoys/command-palette/publish-extension
- https://learn.microsoft.com/en-us/windows/powertoys/command-palette/finding-and-installing-extensions
- https://github.com/microsoft/CmdPal-Extensions

## Local Tooling State

PowerToys Command Palette is installed locally under:

`%LOCALAPPDATA%\Microsoft\PowerToys\CmdPal`

The installed PowerToys version in settings is `v0.98.1`.

The repo now has an initial buildable extension project at `src/Clip.CommandPalette`.
It references `Clip.Core`, targets .NET 9, and uses `Microsoft.CommandPalette.Extensions` `0.9.260303001`.
The project builds locally and now has a minimal MSIX packaging scaffold:

- `tools/Build-ClipCommandPalettePackage.ps1` publishes and packages the extension.
- `tools/Install-ClipCommandPalettePackage.ps1` installs the signed MSIX.
- `tools/Install-ClipCommandPalettePackage.ps1 -Build -TrustDevCertificate -ElevateIfNeeded` is the local dev path. On this PC, Windows package deployment requires the local signing certificate in the `LocalMachine\Root` store, so the command must run elevated for local MSIX installs.
- The package script trims by default and strips `.pdb` files plus dump/debug helpers to keep the extension download smaller; pass `-NoTrim` or `-IncludeSymbols` only for debugging package builds.
- The GitHub release workflow now builds `Clip.CommandPalette_*.msix` and uploads it beside the app zip and installer so release builds do not silently omit the extension package.

Unsigned local install is not enough for this extension because the package declares executable activation for the out-of-process COM server. The GitHub release MSIX is useful as a build artifact, but public no-friction install still needs trusted signing through Store, WinGet, or another proper MSIX signing route. User-level cert trust is enough to verify the package signature, but it did not satisfy `Add-AppxPackage` on this machine. A direct non-admin install failed with `0x800B0109`, meaning Windows did not trust the package root for app deployment.

The Command Palette .NET template is not registered in `dotnet new list` on this machine. If the hand-built packaging scaffold drifts, the fastest recovery path is still the built-in Command Palette command:

1. Open Command Palette.
2. Run `Create a new extension` or `Create extension`.
3. Create `ClipCommandPalette` outside the current app first so the generated package files stay intact.
4. Compare the generated package files against `src/Clip.CommandPalette`.
5. Keep generated `launchSettings.json`, assets, and `*.pubxml` files tracked; Microsoft notes these are needed for package deployability.
6. Deploy the package and confirm Command Palette sees the `com.microsoft.commandpalette` extension.

Build alone is not enough for a Command Palette extension. It has to be deployed as a signed package, then Command Palette may need its `Reload Command Palette Extension` command.

Current local tooling check:

- `dotnet new list` shows no Command Palette template.
- `dotnet new search commandpalette`, `dotnet new search CommandPalette`, and `dotnet new search CmdPal` return no NuGet templates.
- Microsoft's docs still describe the supported scaffold path as Command Palette's own `Create a new extension` command.
- NuGet has `Microsoft.CommandPalette.Extensions` version `0.9.260303001`, checked on 2026-06-20.
- The toolkit assembly requires .NET 9, so the .NET 9 SDK was installed user-locally at `%USERPROFILE%\.dotnet` and that folder was added to the user PATH.
- The extension uses `Shmuelie.WinRTServer` `2.1.1`, matching the current PowerToys sample pin.
- The project pins `WindowsSdkPackageVersion` to `10.0.26100.57`. This is required because `Microsoft.CommandPalette.Extensions.Toolkit` `0.9.260303001` references `Microsoft.Windows.SDK.NET` assembly version `10.0.26100.38`. Without that pin, the default `10.0.22000.38` projection made COM activation fail with `0x80070002` before Command Palette could call `GetProvider`.

Native AOT for `Clip.Command` and the tiny `Clip.Launcher` shortcut helper is not currently available on this machine because the Visual Studio C++ linker workload is missing. A test publish reached the native compilation step cleanly after source-generated JSON cleanup, then stopped with the missing platform linker. Installing the Desktop development with C++ workload is the next tooling step before using AOT to reduce command/helper startup.

`Publish-Clip.ps1` now attempts a Native AOT launcher automatically when the linker toolchain is detected, then falls back to the current .NET Framework launcher if it is missing. Use `-RequireNativeLauncher` for release/test runs where fallback should be treated as a failure, or `-NoNativeLauncher` to force the fallback path.

Trimming is viable for the Command Palette package: a local trimmed publish reduced the extension payload from about 125 MB to about 32 MB before debug-helper cleanup, and the current cleaned MSIX measures about 10.82 MB. The trimmed COM-server process stayed alive under `-RegisterProcessAsComServer`, and the perf gate now measures that idle process so the extension path cannot quietly turn into another heavy background app.

Local verification on 2026-06-20:

- Installed signed local MSIX: `Clip.CommandPalette_1.0.0.6`.
- Direct COM activation from `WindowsApps` succeeded.
- Command Palette provider cache included `Clip.CommandPalette_ktxc7nhkbz5yw!App!Clip`.
- UI Automation saw the visible `Clip History` command in Command Palette.
- Opening Command Palette did not start `Clip.Shell`; only `Clip.Watcher.exe` and `Clip.CommandPalette.exe` were present for Clip.
- Final trimmed MSIX size: 10.78 MB.
- Extension idle: 11.9 MB private memory, 0 CPU over 5 seconds.
- Watcher idle during the same sample: 16.6 MB private memory, 0 CPU over 5 seconds.
- Cold direct COM activation: 2691 ms. Full Command Palette UI discovery was slower because it includes the Microsoft Command Palette app startup/cache path, not just Clip extension work.

Local verification on 2026-06-22:

- Installed signed local MSIX: `Clip.CommandPalette_1.0.99.6`.
- Command Palette settings bind `Alt+V` to `clip.history`, not the built-in `Windows.ClipboardHistory` provider.
- Provider cache includes `Clip.CommandPalette_ktxc7nhkbz5yw!App!Clip`.
- Live Command Palette inspection showed Clip's native page with the native content-type filter, pinned/recent item sections, details, item actions, and a native item preview page.
- Full test suite passed: `dotnet test .\Clip.sln --no-restore` (`206 passed`).
- Current signed MSIX size: 10.82 MB.

## First Extension Scope

Keep the extension focused on fast Command Palette-native clipboard history:

- One top-level command: `Clip History`.
- One searchable list page backed by `ClipboardHistoryListCommand.Create(store, query, limit)`.
- Reference `Clip.Core` and keep the extension process hot for list/search.
- Invoke helper commands only as a fallback.
- Copy, open, and reveal actions now run directly inside the extension through WinRT clipboard APIs, `ClipboardPasteData`, and `ClipboardItemLaunchCommand`, avoiding extra `Clip.Watcher.exe` or `Clip.Command.exe` process starts for normal actions.
- Pin, unpin, move pin up/down, and delete actions run in-process through `ClipboardHistoryActionExecutor`.
- Rename, edit text, and save-as-file use native Command Palette form content backed by the same `ClipboardHistoryStore` methods as `Clip.exe`.
- Copy path is available for file items through a native Command Palette clipboard command.
- Native filters cover All, Pinned, Text, Links, Files, Images, and Colors.
- The history page uses Command Palette's native filter dropdown for `All`, `Pinned`, `Text`, `Links`, `Files`, `Images`, and `Colors`.
- Clipboard items are listed first so the selected item owns the details pane immediately instead of a category/header row.
- Pinned and recent history rows use native `Pinned Items` and `Recent Items` sections.
- The history details pane has a `Preview` section and an `Information` table, then repeats the same metadata through native details rows where Command Palette supports it.
- The history command's context menu exposes settings, open `Clip.exe`, open `Clip.exe` settings, check for updates, clear unpinned history, and clear all history without adding extra noisy root search results.
- The Command Palette settings page writes the same shared settings JSON used by `Clip.exe` and the watcher for open mode, tray icon, and update check preference.
- For a prototype that should avoid referencing Clip assemblies, call `Clip.Command.exe list --json --limit 25` for the first page and `Clip.Command.exe list --json --query "<text>" --limit 25` for search. This returns summary metadata only and does not start `Clip.Shell`, `Clip.Watcher`, WinForms, WebView2, or the tray host.
- Use each JSON item's `defaultActionId` and `actions[]` fields to render Command Palette actions. Do not infer actions by item kind in the extension.
- Action hints are intentionally cheap: list rendering must not check the disk for file existence. Final validation happens only when the user invokes an action.
- Actions:
  - Copy item to clipboard directly in the extension with WinRT `DataPackage`.
  - Open item/link/file directly with `ClipboardItemLaunchCommand`.
  - Reveal files or file-path text directly with `ClipboardItemLaunchCommand`.
  - Pin/unpin/move/delete directly against the local Clip history store.
  - Fall back to helper executables only if the extension cannot handle an action in-process.
  - Paste item only if the Command Palette invocation model can do it without focus hacks.
- No WPF hosting, no WebView, and no tray inside the extension process.

## WPF Feature Migration Matrix

Status as of 2026-06-22:

| Clip.exe WPF surface | Command Palette extension status |
| --- | --- |
| `Alt+V` clipboard history | Native `Clip History` command/page using Clip's local store. |
| Search history | Native Command Palette search via `DynamicListPage`. |
| Type filters | Native filter dropdown: All, Pinned, Text, Links, Files, Images, Colors. |
| Visible filter bar | Command Palette does not expose WPF-style horizontal filter chips, so the extension uses the native filter dropdown. |
| Pinned section | Native `Pinned Items` and `Recent Items` sections split pinned rows from recent rows without placing fake rows above history items. |
| Item preview/body | Enter opens a native Command Palette preview page. Text and file lists render as fenced Markdown; image items render from the stored image file using Command Palette's Markdown image support. Rich WebView/PDF/document preview stays WPF-only for now. |
| Details pane | Migrated metadata into an `Information` table plus native metadata rows: source, type, saved format, copied, times copied, characters, words, hex, dimensions, size, file path. |
| Copy item | Native in-process WinRT clipboard action. |
| Paste into previous app | WPF-only until Command Palette can provide a reliable focus handoff that does not depend on keyboard injection. |
| Pin/unpin/move pin/delete | Native in-process history actions. |
| Rename item | Native Command Palette text form backed by `ClipboardHistoryStore.Rename`. |
| Edit text | Native Command Palette multiline text form backed by `ClipboardHistoryStore.EditText`. |
| Save as file | Native Command Palette path form backed by `ClipboardHistoryStore.SaveAsFile`; blank path uses Clip's desktop default. |
| Copy path | Native action for file items. |
| Open/reveal files, links, images | Native in-process launch/reveal actions. |
| Clear history | Native context actions: clear unpinned or clear all. |
| Open Clip.exe | Context action from the `Clip History` command. |
| Open Clip.exe settings | Context action routed through the same `--palette-session --tray-action=settings` path as the tray. |
| Check for updates | Context action routed through `--palette-session --tray-action=check-updates`; update UI remains in Clip.exe. |
| Open mode setting | Native Command Palette settings page writes shared Clip settings and configures Command Palette `Alt+V` when Command Palette mode is selected. |
| Tray icon setting | Native Command Palette settings page writes the shared icon preference used by the watcher/tray. |
| Update check setting | Native Command Palette settings page writes the shared update-check preference used by Clip.exe. |
| Theme setting | WPF-only. Command Palette visual theme is host-controlled. |
| Clipboard folder, history limit, max item size | WPF-only settings for now; the extension respects the current stored values through `Clip.Core`. |
| Privacy/app exclusions and per-app hotkey overrides | WPF/watcher-only for now. The extension reads the resulting local history but does not manage capture rules. |
| Windows clipboard history import | WPF/watcher-only for now. |
| Open-with picker | WPF-only for now. It depends on Clip's app-discovery picker and recent-app UI. |
| Startup/tray/listener | Standalone `Clip.exe` and `Clip.Watcher.exe` responsibility. The Command Palette extension does not own a tray item. |
| App updates/install flow | Release builds include the MSIX; public low-friction install still needs Store/WinGet/gallery signing. |

## Performance Rules

- Use `history.index.json` for list/search.
- Use `history.top.index.json` for the first visible page before search so Command Palette does not deserialize the full history on initial open.
- Do not load `history.json` during extension startup.
- Do not decode image previews in the extension list.
- Do not probe file paths while rendering list items.
- Create the store with `ClipboardHistoryStore.OpenForCommandSurface()` so load maintenance and item retention stay disabled.
- Keep full payload hydration behind explicit invocation only.
- Do not start `Clip.Shell` from the extension. The rich WPF shell is the expensive path; the extension should stay closer to the lightweight watcher model.
- Keep JSON serialization source-generated in `Clip.Core`; dynamic JSON calls block trim/AOT readiness for the extension and command helper.
- Keep the packaged extension COM-server idle process under the perf gate's lightweight budget. Local measurement on 2026-06-20 was 11.9 MB private memory and 0 CPU over 5 seconds; the signed local MSIX measured 10.82 MB on 2026-06-22.

## Done Criteria For The First Prototype

- `ClipCommandPalette` builds and deploys as a packaged extension.
- Command Palette discovers it through `com.microsoft.commandpalette`.
- Opening Command Palette does not start `Clip.Shell`.
- The `Clip History` page renders from `QueryItemSummaries()` only.
- Copy/reveal actions work for text, links, images, and files.
- Hidden idle cost is measured separately from the standalone app.
- The MSIX size is tracked by `tools/Measure-ClipPerformance.ps1` so the plugin path cannot quietly grow past the current lightweight budget.
- The packaged extension COM-server idle process is tracked by `tools/Measure-ClipPerformance.ps1` so the plugin path stays low-cost even before it is deployed through Command Palette.
