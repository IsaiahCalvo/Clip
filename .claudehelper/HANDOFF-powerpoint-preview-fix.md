# Handoff — Office preview was closing the user's PowerPoint

Written 2026-08-03 by the `claude/hopeful-haslett-677c63` worktree session.
This file is a report for whoever owns the merge. Nothing here needs to be re-investigated.

## The bug

`StaticDocumentPreviewRenderer` in `src/Clip.Watcher/Program.cs` builds document thumbnails by
driving Office over COM. It created an application with
`Activator.CreateInstance(Type.GetTypeFromProgID("Word.Application"))` and then treated whatever
came back as private: set `Visible = false`, suppressed `DisplayAlerts`, and called `Quit()` when
the export finished.

The suspicion going in was that this was hijacking a running **Word**. Measured on this machine
with a user-launched instance already open, that turned out to be wrong:

| App | What COM hands back |
|---|---|
| Word | a separate WINWORD.EXE (its `Documents.Count` was 0 while the user's held 4) |
| Excel | a separate EXCEL.EXE |
| Visio | a separate VISIO.EXE |
| **PowerPoint** | **the user's own process** — same pid, their unsaved deck in `Presentations` |

So PowerPoint was the real victim, and it was never on the suspect list.

Reproduced through the shipping code path, not a script: with `deckA.pptx` open and edited but
unsaved, running `Clip.Watcher.exe preview-thumb` on an **uncached** `.pptx` attached to that same
POWERPNT.EXE (no second process appeared for the entire run), rendered the thumbnail, and then quit
the user's PowerPoint. The unsaved edit was discarded and no save prompt was ever shown.

## The fix — commit `85923fb` on `claude/hopeful-haslett-677c63` (pushed)

Every COM instance now carries whether *we* created it, decided at runtime by whether an Office
process appeared that was not running a moment before. `Visible`, `DisplayAlerts` and `Quit` are all
gated on that flag. Runtime detection rather than a hard-coded per-app list, because which
applications share an instance is a property of the installed Office, not something to bake in.

Two deliberate calls worth knowing about:

- **Previews still run on an attached instance.** Refusing to preview whenever PowerPoint is open
  would kill the feature for exactly the people most likely to use it. It just stops rearranging
  someone else's application around itself.
- **`Quit()` is unchanged for instances we started.** Releasing the COM object without it leaves the
  process running — one leaked EXE per preview. Skipping Quit outright would trade a data-loss bug
  for a process-leak bug.

It also stops closing a document that was **already open** in an attached instance: `Open()` there
hands back the user's own document rather than a second copy, so closing it would take their work
away mid-edit. Same root cause, silent, and would have survived the ownership fix on its own.

Diff: 210 lines in `Program.cs`, 65 in `tests/Clip.Tests/DocumentThumbnailPreviewTests.cs`.

## What was verified

- Before/after A/B on identical uncached scenarios: pre-fix binary killed the user's PowerPoint and
  lost the edit; post-fix binary rendered the thumbnail with PowerPoint and the unsaved edit intact.
- Same-file case: previewing the very `.pptx` the user had open and dirty — preview rendered, their
  presentation and window untouched.
- Word with an unsaved document open: automation instance was separate (`owned=True` in the debug
  log), user's Word survived visible with its edit, and no WINWORD.EXE was left behind afterward.
- `dotnet test .\Clip.sln` — 230 passed, 0 failed. **See the caveat below about that number.**

## Branch situation — read this before merging

- `main` is **0 ahead / 59 behind** `ui/grayscale-text-rendering`. The recent work is all on the
  latter; `main` is stale.
- This worktree was cut from stale `main`, so the fix sits on an old base.
- `claude/eloquent-bardeen-fac4a5` holds commit `bc39743`, the Office preview cold-start timeout
  (120s cold / 45s warm), also not merged anywhere.

**The two Office-preview changes do not overlap.** Verified mechanically: this diff does not touch a
single line `bc39743` changes. It altered the export helpers' signatures and bodies, not the
`export(app, ...)` call sites that commit patches. Either order works.

Suggested integration: bring both feature branches together on top of whichever branch is the real
trunk, and get `main` caught up to it, so there is one line again rather than three.

## What is left

1. **Re-run the full suite on the current trunk.** The 230 tests here are this stale base's whole
   suite; `ui/grayscale-text-rendering` has 16 more test files and ~376 tests. The fix has never been
   run against those.
2. **Re-check the `.docx` preview end to end on that trunk.** Here it stopped one step short: the
   Word export produced a valid 14KB PDF, but this base has no PDF rasterizer (`pdftoppm.exe` not
   present), so `preview-thumb` returned 3 instead of writing a PNG. That branch has the Windows PDF
   renderer, so the full path is testable there.
3. **Excel and Visio are untested against a running user instance.** They each got their own process
   here, so the ownership flag should read `owned=True` and behavior should be unchanged — but that
   was only confirmed for Word.

## Gotcha for whoever tests this next

Preview results are cached by path + mtime + size, so **re-previewing the same document never
touches COM at all**. A first "the old build doesn't reproduce it" result during this session was
purely that cache. Copy the test file to a fresh name for every A/B run.

## Machine state

All Office processes from testing are closed. One stuck test Word had to be force-terminated (it was
holding a modal save prompt over a throwaway test document, which blocked COM) — Word may show a
"didn't shut down properly" notice on its next launch. Nothing of yours was in it; its AutoRecover
file was checked for and none was left. A windowless EXCEL.EXE that appeared afterward was not from
this session and was left alone. Test files live in the session scratchpad, nothing in the repo.
