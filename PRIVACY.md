# Privacy

Clip is a local clipboard history app.

## What Clip Stores

Clip stores clipboard history on your own Windows device under:

```text
%LOCALAPPDATA%\Clip\Clipboard History
```

That folder can contain copied text, image files, saved file copies, links, color swatches, file paths, and hidden metadata files used by Clip.

Clip also stores settings, app cache data, and logs under:

```text
%LOCALAPPDATA%\Clip
```

Clip can exclude selected apps from clipboard history. This is intended for sensitive apps such as password managers, banking apps, and private browsers.
Excluded apps apply to future clipboard captures; they do not automatically remove items that were already saved.

## What Clip Sends

Clip does not intentionally send clipboard history to a server.

Clip checks GitHub Releases for app updates. That update check sends a normal request to GitHub for release information, but it does not send clipboard history.

## Before Publishing or Sharing Logs

Clipboard history, file names, source app names, file paths, item previews, screenshots, and logs can contain private content. Do not upload your local `%LOCALAPPDATA%\Clip` folder, `history.json`, logs, or screenshots unless you have reviewed them first.
