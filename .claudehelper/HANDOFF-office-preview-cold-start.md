# Handoff — Office preview cold-start timeout

_Written 2026-08-03. Standalone: assumes no context on this work._

## The problem

Clip's file preview pane can show Word, Excel, PowerPoint and Visio documents. It does that by
driving the relevant Office application over COM automation to export the document (Word and Excel
export to PDF, PowerPoint and Visio export a PNG of the first page), then either shows that PDF in
the WebView2 viewer or rasterises it for a thumbnail.

All of that lives in `StaticDocumentPreviewRenderer` in `src/Clip.Watcher/Program.cs`. COM
automation needs a single-threaded apartment, so every call is marshalled onto a dedicated STA
thread by the private helper `RunOnStaThread<T>`, and that helper waited on the thread with a
bounded `thread.Join(...)`.

The bound was a single flat `PreviewTimeout = TimeSpan.FromSeconds(25)`.

That number was sized against a *warm* Office. The first export after a reboot also has to pay for
the Office COM server starting from cold, which dwarfs the export itself. Measured on Isaiah's
machine against a ~180KB multi-page `.docx` with no `WINWORD.EXE` running, the cold export ran
**22.7s–26.2s** across repeated runs — straddling the 25s cap. When it lost that coin flip
`RunOnStaThread` returned `null`, and the preview pane silently fell back to the generic
placeholder, even though the export was working fine and would have finished seconds later.

Net user-visible symptom: **the first Word preview after a reboot often showed a placeholder
instead of the document.** Every preview after that worked, which made it look intermittent.

## Root cause, and the reframing that made the fix safe

The obvious objection to raising the timeout is that the timeout was introduced deliberately —
Word had previously been measured hanging indefinitely, and the comment in the code says an
unbounded `Join` "leaks the thread and the Office process permanently, once per preview."

That framing is wrong, and checking it is what unblocked this:

- On timeout, `RunOnStaThread` **abandons** the STA thread and returns `null`. It does not kill
  anything. The thread keeps running, Word keeps running, and `QuitAndRelease(app)` still fires
  from the `finally` block whenever the export eventually completes.
- So if Office genuinely hangs, the process leaks *regardless of what the number is*. The timeout
  never prevented that.

What the timeout actually controls is **how long the user stares at "Loading preview..." before
the pane gives up**. Nothing else. And the preview pane is already fully async behind that
placeholder with a `_previewToken` guard (`src/Clip.Shell/MainWindow.xaml.cs`), so a longer wait
costs the user nothing but the wait itself.

Once that is clear, the fix is just: stop using one number for two very different situations.

## What changed

**Commit `bc39743` — "Give the first Office preview room for the COM cold start"**
**Branch `claude/eloquent-bardeen-fac4a5`**

Two files, +61 / −3.

### `src/Clip.Watcher/Program.cs`

Replaced the single constant with two, plus a flag:

```csharp
private static readonly TimeSpan ColdPreviewTimeout = TimeSpan.FromSeconds(120);
private static readonly TimeSpan WarmPreviewTimeout = TimeSpan.FromSeconds(45);

// Set once an Office COM server has actually driven an export to completion in this process.
private static volatile bool _officeComWarm;
```

`RunOnStaThread` now picks between them:

```csharp
var timeout = _officeComWarm ? WarmPreviewTimeout : ColdPreviewTimeout;
if (!thread.Join(timeout))
{
    Program.LogDebug($"Static preview timed out after {timeout.TotalSeconds}s cold={!_officeComWarm} path={path}");
    return null;
}
```

And `_officeComWarm = true;` was added at exactly two places — immediately after the
`export(app, sourcePath, ...)` call inside `TryExportOfficePdf` (the Word/Excel route) and inside
`TryRenderImage` (the PowerPoint/Visio route).

Three design points worth not re-litigating:

1. **The flag is set where COM actually completed an export, not where `RunOnStaThread` returns.**
   Both `TryExportOfficePdf` and `TryRenderImage` short-circuit on a cached export and return
   without touching Office at all. Setting the flag on a fast return would mark the process "warm"
   without Office ever having started, and the *next* genuinely cold export would then get the
   45s budget and fail — exactly the bug being fixed.
2. **One flag covers all four applications.** Cold start is dominated by the shared Office runtime
   (`mso.dll` and friends) rather than by any one app, so warming up via Word legitimately counts
   for Excel, PowerPoint and Visio. This was confirmed by measurement, see below.
3. **The warm budget is 45s, not the old 25s.** A warm Visio floor plan was measured at 26.5s. The
   old cap would have killed that one too, so this bug was never Word-only.

### `tests/Clip.Tests/DocumentThumbnailPreviewTests.cs`

Added `OfficePreviewGivesTheColdStartItsOwnBudget`, plus a small `TimeoutSeconds` regex helper.
It follows the source-text assertion idiom already used in that file (e.g.
`WatcherHelperExposesPreviewThumbVerbReusingFirstPageRenderers`), because a real behavioural test
would require Office installed on the build agent.

It asserts: both constants exist; the ternary in `RunOnStaThread` is present; cold ≥ 60s; warm ≥
35s (so it clears the 26.5s Visio measurement); warm < cold; and that `_officeComWarm = true;`
appears exactly twice, so the flag cannot drift onto the cached-return path.

## Approaches considered and rejected

- **Keep a `Word.Application` alive across previews.** Rejected. `RunOnStaThread` creates a *new*
  STA thread per call, and a COM pointer does not outlive its apartment — the cached instance
  would be unusable from the next call's thread. Making it work needs a long-lived dedicated STA
  pump thread with its own message loop and lifetime/idle-quit management. Large change, and it
  deliberately keeps an Office process resident.
- **Background warm-up export when a `.docx` first lands in history.** Rejected. Launches Office
  speculatively for documents the user may never preview.
- **Just raise the flat number.** Rejected as the primary fix because it makes every genuine hang
  strand the pane for the full duration. The cold/warm split gets the cold path what it needs
  without paying for it on the other 99% of previews.

## How it was verified

Verification used a **throwaway xunit probe** in `tests/Clip.Tests/` that called the real
production entry points (`StaticDocumentPreviewRenderer.TryExportWordPdfOnStaThread` and
`.TryRenderFirstPageOnStaThread`), not a reimplementation. It deleted the fingerprint cache before
each measurement, timed each call, and threw the timings out as an exception message so the test
runner would print them. **Both probe files were deleted before the commit** — they are not in the
tree. An earlier attempt to probe this from PowerShell was abandoned: PowerShell's IDispatch late
binding added ~10s of its own to a simple property set, which made the numbers meaningless.

`WINWORD.EXE` / `EXCEL.EXE` / `POWERPNT.EXE` / `VISIO.EXE` were force-killed before each cold run.

Results, before the fix:

```
export1 = 25.1s  ok=False   <-- cold, killed by the 25s cap
export2 =  6.6s  ok=True    <-- warm, same document
thumb   =  9.9s  ok=True    <-- warm export + pdftoppm rasterise
```

After the fix, Word, repeated cold runs:

```
export1 = 23.8s ok=True | 22.7s ok=True | 26.2s ok=True | 25.1s ok=True
export2 =  5.0s .. 13.3s ok=True
```

After the fix, the other three formats (cold process, Excel first so it absorbed the cold start):

```
xlsx = 22.5s ok=True  2040x2640
pptx =  9.9s ok=True  1400x1000
vsdx = 22.0s ok=True  1444x1106
```

An earlier run of that same set produced `xlsx 21.8s | pptx 14.7s | vsdx 26.5s` — that 26.5s Visio
is the measurement the 45s warm budget is sized against.

### Which branch, and how many tests

**Tested on `claude/eloquent-bardeen-fac4a5`, whose base is `f362556` — the tip of
`ui/grayscale-text-rendering` at the time this session started. That is current trunk for this
code, not the older `main`.**

`dotnet test .\Clip.sln` → **376 passed, 0 failed** (375 pre-existing + the 1 new gate).

Trunk has since moved to `5e5b286`, three commits ahead of `f362556`. Those three touch only
`src/Clip.Shell/JankHarness.cs`, `src/Clip.Shell/MediaPreviewPage.cs` and `.claudehelper/HANDOFF.md`
— nothing in the Office preview path — so the drift does not affect this work or invalidate the
376-test result. It has **not** been re-run on `5e5b286` itself.

## Branch situation — read before merging anything

Four branches matter, as of 2026-08-03:

| Branch | Commit | Base | What it is |
|---|---|---|---|
| `main` | `57e1498` | — | **59 commits behind `ui/grayscale-text-rendering`.** Effectively dead. |
| `ui/grayscale-text-rendering` | `5e5b286` | — | Where the real work lives. Treat as trunk. |
| `claude/eloquent-bardeen-fac4a5` | `bc39743` | `f362556` (on `ui/...`) | **This work.** The cold-start fix. |
| `claude/hopeful-haslett-677c63` | `85923fb` | `57e1498` (`main`) | The Office-instance-ownership fix. |

**Do not merge any of this to `main`.** It would land on a branch that is 59 commits behind and go
nowhere. The integration target is `ui/grayscale-text-rendering`.

### Interaction with `claude/hopeful-haslett-677c63` (`85923fb`)

That branch fixed a genuinely nasty bug: COM was handing back the user's *own* running Office
instance rather than a private one, and the preview code then set `Visible = false`, suppressed
alerts and called `Quit()` on it — closing the user's application and losing unsaved work.
PowerPoint turned out to be the one that actually attaches. `85923fb` makes the code decide
ownership at runtime (from whether a new process appeared) and only drive/quit an instance it
created.

That work started from a suggestion raised at the end of this session, so the two are related but
independent.

**I checked the conflict question rather than assuming it.** Running
`git merge-tree --write-tree bc39743 85923fb`:

- `src/Clip.Watcher/Program.cs` — **auto-merges cleanly**, and the result is semantically coherent.
  The two changes are orthogonal: `85923fb` changed *who owns and quits* the COM application
  (`CreateComApplication` now returns an `OfficeApplication` wrapper, `QuitAndRelease` takes it),
  while this branch changed *how long we wait*. In the merged file the timeout split and
  `_officeComWarm` sites survive intact and sit inside the new ownership-aware call structure.
- `tests/Clip.Tests/DocumentThumbnailPreviewTests.cs` — **CONFLICTS.** Both branches appended new
  tests to the same region of the same file. This is a trivial, mechanical resolution (keep both
  sets of tests), not a semantic clash.

So: the user's expectation that they would not conflict is *almost* right — one mechanical test-file
conflict, no source conflict.

**The bigger integration issue is the base, not the conflict.** `85923fb`'s parent is `57e1498`
(`main`), so it was written against a version of `StaticDocumentPreviewRenderer` that predates
`f362556` ("Show Word documents as PDFs rather than pictures of their first page"). Its base file
contains **none** of `TryExportWordPdfOnStaThread`, `CachedExportPath`, the generic
`RunOnStaThread<T>` helper, or even `PreviewTimeout` — on `main` the STA helper did a plain
unbounded `thread.Join()`. Git's textual merge happens to reconcile this, but that ownership work
has never been compiled or run against the Word-PDF preview route it will govern once merged.
Whoever integrates should re-run the full suite on the merged tree and re-run a manual Office
check, not trust the clean auto-merge.

**No merge, rebase or push was performed. The dry run above wrote nothing to any branch.**

## What's left

1. **Integrate.** Merge `bc39743` and `85923fb` onto `ui/grayscale-text-rendering`, resolve the one
   test-file conflict, re-run `dotnet test .\Clip.sln`, and re-verify a real Office preview.
2. **Consider whether `ui/grayscale-text-rendering` should become `main`.** A 59-commit gap where
   all the work is on the side branch is the actual structural problem here, and it is why every
   new branch has to pick a base and can pick wrong (as `85923fb` did).
3. **Cold budget sizing is a guess above the measurements, deliberately.** 120s was chosen with
   headroom rather than snugly above 26.2s, because the measurements are a floor and not a worst
   case (see gotchas). If a real post-reboot preview is ever observed timing out, the number is the
   thing to raise, and the debug log line prints which budget was in force.
4. **Excel previews** are still not working generally (unrelated, pre-existing — see the main
   `HANDOFF.md`).

## Gotchas

- **The timeout is not a leak guard.** Covered above, but it bears repeating because the code
  comment implies otherwise. If Office hangs, the process leaks whatever the number is. Nothing in
  this change makes leaks better or worse.
- **Killing `WINWORD.EXE` does not give a true cold start.** Word's binaries stay in the OS file
  cache, so a genuine post-reboot first export is *slower* than anything measurable that way. Every
  cold number in this document is a floor, not a worst case. That is why the cold budget is 120s.
- **The fingerprint cache makes A/B testing invisible.** `TryRenderOfficePdf` and `TryRenderImage`
  short-circuit on a cached `.pdf`/`.png` keyed by path + mtime + size, under
  `%LOCALAPPDATA%\Clip\document-previews`. Re-running a preview on the same document never touches
  COM at all. Delete that directory, or copy the test file to a fresh name, or you will measure
  nothing and conclude the bug does not reproduce.
- **Never round-trip a `.cs` file through PowerShell `Get-Content`/`Set-Content`** — it
  double-encodes the media player's button glyphs into mojibake. Use the Edit/Write tools.
- **`.claudehelper/HANDOFF.md` in the main repo has uncommitted working-tree edits** made during
  this session (a summary of this fix, and a now-outdated note about the Office ownership bug that
  `85923fb` has since corrected and narrowed to PowerPoint). The main repo working tree also has an
  unrelated uncommitted change to `src/Clip.Shell/JankHarness.cs` that predates this session and
  was not touched.
- **Verify off screen.** Isaiah works on this machine and has escalated about this repeatedly. Use
  `Clip.exe --jank-test ...` rather than taking over the display.
