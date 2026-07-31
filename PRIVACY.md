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

Clip does not send clipboard history to a server.

Clip makes network requests in exactly two cases:

1. **Update checks.** Clip asks GitHub Releases for the latest version. This sends no clipboard data.

2. **Site icons for links.** When you copy a link, Clip shows that site's real icon in the list.

   Clip looks in two places, in order:

   - **Your browser's own favicon database first.** Chrome and Edge already store the icon for every site you have visited. Clip reads a copy of that file locally. **No network request is made**, and for most links this is where the icon comes from.
   - **The website itself, only if the first step found nothing.** Clip requests the site's home page and icon, the same way a browser does when you visit it.

   When the second step happens, the website learns your IP address and that someone looked up its icon, at roughly the time you copied the link — including for links you copy but never open. Clip sends only the site name, never the full link, never its path or query, and never any other clipboard content. No cookies are sent, and only public websites are contacted; addresses on your own network are skipped.

   Icons are cached on your PC under hashed filenames, so the cache is not a readable list of sites you copied.

   Note that reading your browser's favicon database means Clip reads which sites you have visited, though only their icons and addresses — never history entries, passwords, or cookies. The copy Clip makes is temporary and deleted immediately.

## Text in Images

If "Search text in images" is on, Clip reads text out of images you copy so screenshots can be found by what they say. This runs entirely on your PC using the text recognition built into Windows; nothing is uploaded.

Recognized text is stored alongside the item in your local history as plain text. That means text visible in a screenshot — including anything sensitive that happened to be on screen — becomes searchable text on disk rather than only pixels. This setting is off by default, and it respects your excluded apps list.

## Before Publishing or Sharing Logs

Clipboard history, file names, source app names, file paths, item previews, screenshots, and logs can contain private content. Do not upload your local `%LOCALAPPDATA%\Clip` folder, `history.json`, logs, or screenshots unless you have reviewed them first.
