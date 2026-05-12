# Clip

Clip is a free Windows clipboard history app inspired by Raycast's clipboard history.

It runs locally, opens with `Alt+V`, and keeps clipboard history on your own device.

## Install

**[⬇ Download Clip-Setup.exe](https://github.com/IsaiahCalvo/Clip/releases/latest/download/Clip-Setup.exe)** — double-click and click through the wizard. Installs per-user with no admin prompt, adds a Start menu entry, and registers in Add/Remove Programs.

Prefer a portable copy with no installer? **[Download Clip-win-x64.zip](https://github.com/IsaiahCalvo/Clip/releases/latest/download/Clip-win-x64.zip)**, unzip anywhere, and run `Clip.exe`.

> **Heads up:** Windows SmartScreen will say "Windows protected your PC" the first time because Clip isn't code-signed yet. Click **More info → Run anyway**. The app is open source — you can read every line on this page.

After install, press `Alt+V` to open it.

### Code Signing

Clip uses free code signing provided by the [SignPath Foundation](https://signpath.org/) for open-source projects. Once signed, future releases will install without a SmartScreen warning. Certificates are issued in SignPath Foundation's name.

## Features

- Clipboard history for text, links, colors, images, files, and folders.
- Searchable `Alt+V` clipboard window.
- Pin, unpin, reorder, copy, paste, edit, delete, save, and open items.
- Image thumbnails and previews.
- File metadata and file previews where Windows or local renderers support it.
- Searchable `Open With` picker.
- Color swatches for copied hex colors.
- Source app metadata and item information panel.
- Local debug logs with `Ctrl+Shift+L`.
- Public settings for theme, startup, updates, history limits, storage, hotkeys, paste format, and excluded apps.

## Requirements

- Windows 11.
- .NET 8 SDK to build from source.
- Microsoft Edge WebView2 runtime for HTML previews.

## Run From Source

```powershell
dotnet build .\Clip.sln
.\Start-Clip.ps1
```

Then press `Alt+V`.

## Publish a Release Build

```powershell
.\Publish-Clip.ps1
```

The release files are created under:

```text
artifacts\publish\Clip-win-x64
```

Double-click this app file to start Clip:

```text
artifacts\publish\Clip-win-x64\Clip.exe
```

The zip file is created at:

```text
artifacts\publish\Clip-win-x64.zip
```

## Start With Windows

After publishing or building, run:

```powershell
.\Install-ClipStartup.ps1
```

This installs Clip under `%LOCALAPPDATA%\Programs\Clip`, creates Desktop and Start Menu shortcuts, and starts Clip automatically when your Windows user signs in.

## Privacy

Clip stores clipboard history locally under:

```text
%LOCALAPPDATA%\Clip\Clipboard History
```

App settings, update state, app cache data, and logs are stored under `%LOCALAPPDATA%\Clip`. Review `PRIVACY.md` before sharing logs or local app data.

## Development

```powershell
dotnet build .\Clip.sln
dotnet test .\Clip.sln
```

Main projects:

- `src\Clip.Shell`: WPF shell and main UI.
- `src\Clip.Core`: clipboard history model and local JSON store.
- `src\Clip.Watcher`: older watcher/utilities still used by the shell for some Windows integrations.
- `tests\Clip.Tests`: focused regression tests.
