# Research Notes

## Findings

- PowerToys Command Palette extensions are standalone .NET apps that talk to Command Palette through a WinRT API.
- Extensions register through a packaged app manifest using `com.microsoft.commandpalette`.
- Supported UI surfaces include list pages, detail pages, form pages, markdown pages, grid pages, context menu items, settings pages, and top-level commands.
- The official setup path is the Command Palette command named `Create a new extension`.
- Visual Studio deployment is the normal test/install path; build alone is not enough to refresh an extension package.
- The PowerToys repo already includes `Microsoft.CmdPal.Ext.ClipboardHistory`.
- The built-in ClipboardHistory extension uses Windows clipboard history APIs, so it is tied to the Windows history feature and its limits.
- A Raycast-style tool needs its own local store so it can pin, reorder, edit, save, append, and open image items.

## Decision

Build this as two pieces:

1. Local clipboard engine: captures and owns history.
2. Command Palette extension: displays and acts on that owned history.

This keeps the app free and avoids paid tools like ClipboardFusion.

## Current local implementation

- `Clip.Core`: local JSON store and item actions for Clip.
- `Clip.Watcher`: Clip background clipboard watcher plus command-line actions.
- `Clip.CmdPal`: older Command Palette bridge kept as reference while Clip remains standalone.

## Follow-up UI Work

- Replace the basic `Open With...` file dialog with a searchable app picker.
- The picker should list installed apps, filter by typed app name, and launch the selected app with the selected clipboard file/image.
- Long term, prefer Command Palette's native UI for that picker when the extension wrapper is built.
- File previews for PDF, Word, and other document types need a separate preview-handler feature. The WinForms prototype can add file-type previews later, but the proper long-term home is the Command Palette UI integration.

## Verified

- .NET 8 SDK installed.
- Solution builds with no warnings or errors.
- Watcher captured a real copied text item during smoke testing.

## Sources

- https://learn.microsoft.com/en-us/windows/powertoys/command-palette/creating-an-extension
- https://learn.microsoft.com/en-us/windows/powertoys/command-palette/extensibility-overview
- https://learn.microsoft.com/en-us/windows/powertoys/command-palette/adding-commands
- https://learn.microsoft.com/en-us/windows/powertoys/command-palette/update-a-list-of-commands
- https://github.com/microsoft/PowerToys/tree/main/src/modules/cmdpal/ext/Microsoft.CmdPal.Ext.ClipboardHistory
