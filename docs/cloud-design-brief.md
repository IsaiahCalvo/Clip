# Clip Cloud Design Brief

## Product

Clip is a free Windows clipboard history app inspired by Raycast clipboard history.

The first design target is the standalone `Alt+V` clipboard window, not the older PowerToys Command Palette experiment.

## Main Screen

The main screen has three areas:

- Left: searchable clipboard list with pinned items at the top.
- Top right: preview pane for selected text, link, image, file, or folder.
- Bottom right: frozen `Information` header with scrollable metadata rows.

## Current Features

- `Alt+V` opens the clipboard window.
- Clipboard history updates live while the window is open.
- Text, links, images, files, and folders are captured.
- Images show thumbnails and a larger preview.
- Files and folders show file/folder icons and metadata.
- Items can be pinned, unpinned, moved, edited, deleted, saved, copied, pasted, or opened.
- `Ctrl+Shift+L` saves debug logs and shows a small toast.

## Design Direction Needed

- Modern, minimal, polished desktop UI.
- Light and dark themes.
- Dark mode should follow the Windows system theme.
- Content icons should feel like UI icons, not pasted image previews.
- The preview pane and information pane should feel separate and stable.
- Search and Open With should feel like a fast command palette inside Clip.

## Important Files

- `src\Clip.Watcher\Program.cs`: current WinForms UI, clipboard watcher, hotkey, context menu, preview, information panel.
- `src\Clip.Core`: clipboard item model and local JSON history store.
- `assets\icons`: current SVG icons for text, link, file, and folder thumbnails.
- `README.md`: current app overview and run instructions.

## Data

Clip stores history here:

```text
%LOCALAPPDATA%\Clip\history.json
```

On first run, it migrates old clipboard history from the previous app data folder.
