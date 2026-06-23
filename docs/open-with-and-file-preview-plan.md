# Open With And File Preview Plan

## Status

Implemented in the standalone Clip app.

## Best Open With Direction

Build the picker inside Clip instead of trying to use Command Palette as the picker.

Reason: Command Palette extensions are meant to show commands inside Command Palette. They are not a clean reusable modal picker that can return a selected application back into our standalone WinForms app.

## Open With Picker

The picker should feel like Command Palette:

- Opens from the item context menu.
- Has a focused search box immediately.
- Shows app rows with icon, app name, and source.
- Filters instantly as the user types.
- Prioritizes the default app and recently used apps.
- Remembers recently used apps per file extension.
- Pressing Enter opens the selected app with the selected file.
- Escape cancels and returns to Clip.

## App Sources

Use multiple local sources:

- Windows file associations for the selected file extension.
- Start Menu shortcuts from user and system Start Menu folders.
- Registered app paths from Windows registry.
- Recently used Open With choices saved by Clip.
- Packaged/Microsoft Store apps where possible.

## Launching

- Desktop apps: launch the selected `.exe` with the file path as an argument.
- Default app: use normal shell open.
- Packaged apps: use Windows app activation only when we can reliably resolve the app identity.
- Fallback: keep Windows `SHOpenWithDialog` available if Clip cannot launch a target cleanly.

## File Previews

Use a layered preview approach:

1. Images: keep Clip's native image preview.
2. Text/code files: show a fast read-only text preview.
3. PDFs: use a local embedded preview if available, otherwise Windows shell preview handler.
4. Word/Excel/PowerPoint/other document types: use Windows shell preview handlers when installed.
5. Unsupported files: show icon, file name, size, type, modified date, and path.

## Windows Preview Handler Notes

Windows Explorer-style previews come from preview handlers. Clip can host those handlers through `IPreviewHandler` when a handler exists on the machine. PDF and Word preview support depends on installed software such as Microsoft Office, Edge, Adobe, or other apps that register preview handlers.

## Risk

Shell preview handlers are COM-based and can be fragile. They need careful cleanup when the selected item changes, when the preview pane resizes, and when Clip closes.

## Implemented Build

1. Searchable Open With picker for desktop apps.
2. Default app and recommended file association support.
3. Recent app memory per extension.
4. Text-file previews.
5. Windows shell preview handler host for PDF/Word/Office-style previews when handlers exist.
6. Fallback metadata previews for unsupported file types.
7. Microsoft Store packaged app discovery through Windows Start apps, including apps like Drawboard PDF.
8. Packaged app file activation for opening selected files in Store apps when Windows supports that app/file combination.
9. Custom `.xlsx` preview grid for columns `A:S` and rows `1:35`.
10. File/folder/icon fallback preview instead of metadata text in the preview pane.

## Command Palette First-Page Thumbnails

The Command Palette extension shows first-page IMAGE thumbnails for PDF / Office / Visio file
items without taking any heavy PDF/Office dependency in the lean net9 extension:

- `Clip.Watcher.exe preview-thumb <srcPath> <outPng>` renders the first page to a PNG, reusing the
  standalone `PdfPreviewRenderer` (PDF via `pdftoppm`) and `StaticDocumentPreviewRenderer`
  (Word/Excel/PowerPoint/Visio via Office/Visio COM). Exit codes: 0 rendered, 2 bad args /
  unsupported type, 3 nothing rendered (tool/COM server unavailable).
- `ClipDocumentThumbnail` (extension) calls that verb lazily on preview, caches the PNG under
  `%TEMP%\Clip\PaletteThumbs` keyed by source path + last-write-time + size, and embeds it via the
  same `file://` cmdpal image markdown used for image items.
- The render runs only when the preview page is opened (`ClipItemPreviewPage.GetContent`), never on
  cold-open or list render. The list details pane uses a render-free cached lookup so a thumbnail
  produced by a prior preview is reused without ever launching the helper.
- PDF thumbnails work end-to-end wherever `pdftoppm` is on PATH/LocalAppData. Office/Visio
  thumbnails depend on the corresponding COM server (Word/Excel/PowerPoint/Visio) being installed;
  when it is absent the helper returns 3 and the palette falls back to the text/path preview.
- WebView2 live HTML preview is intentionally NOT hosted in the palette; HTML stays a text-excerpt
  fallback.

## Remaining Polish

- The picker UI works now, but it should be visually redesigned with the rest of Clip.
- Legacy `.xls` previews still use shell preview/fallback instead of the custom Excel grid.
