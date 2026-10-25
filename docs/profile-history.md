# Autosave, profile history and the last profile at start

JoyPro saves every change by itself, keeps every save as a version you can go back to, and opens the profile you had
open last. Nothing to press, nothing lost on a crash, and the history never fills the disk.

- GUI: top bar (`Saving…` / `Saved ✓` chip, `Undo`, `Redo`, `History`, `More > Save as... / Rename...`), Settings >
  Startup ("Open the last profile at start"). User guide: [GUI wiki, Top Bar](../gui/docs/GUI_USER_WIKI.md#top-bar-always-visible).
- CLI: `engine --profile <file> profile history list | show | diff | restore | undo | redo | keep | checkpoint | prune
  | usage` and the global `--history-note <text>` ([CLI wiki](../engine/docs/ENGINE_CLI_WIKI.md)).
- Code: `engine/ProfileHistory.cs` (the store, plain C# 7.3, compiled into the engine and linked into the GUI),
  `engine/EngineProfileHistory.cs` (what a profile's items are, labels, checkpoints, the CLI),
  `engine/CoreInternalDataManagement.cs` (`SaveProfileTo`: atomic write + version),
  `gui/JoyPro.Gui/Services/AutosaveScheduler.cs`, `gui/JoyPro.Gui/ViewModels/MainWindowViewModel.ProfileHistory.cs`.

## Saving

- **The engine writes the profile atomically.** `SaveProfileTo` serializes into memory, writes
  `<profile>.tmp-<pid>-<id>` next to the profile, flushes it to the disk and swaps it in with `File.Replace` (40 tries
  while a sync client or virus scanner holds the file). A crash, a full disk or a locked file leaves the old profile
  whole; temp files older than 10 minutes are removed on the next save. Before, a crash during a save could leave a
  half-written `.pr0file`.
- **Every engine command that changes the profile saves it** (as before) and records a version. The GUI passes the
  words of its busy line as the version's name (`--history-note "Adding relation"`); without it the engine names the
  command ("Import from DCS", "Set bind").
- **What the GUI holds before it is in the profile** is saved by the autosave, 2 s after the last change
  (`AutosaveScheduler`, debounced; saves never overlap, a change during a save is saved right after it):
  - axis tuning edits of the shown scope (as `engine binds tuning set`, the same as `Save for this scope`),
  - marker positions and callout tables of the Layout page's device pictures (the layout undo stays),
  - modifiers that are not on a bind yet: a profile cannot hold a modifier without a bind (the engine rebuilds the
    modifier list from the binds), so they go into `drafts.json` in the profile's history folder and come back when
    the profile is opened.
  The old layout-file editor (JoyPro 1 style `.layout` with a chosen output file) keeps its `Save`: its save writes a
  template and reloads it.
- **Flush**: the pending edits are saved at once when JoyPro closes (the window waits for it), before another profile
  opens (Open, New, Open last, Close profile, the tray's "open last"), before every export or import (any engine
  command `<area> export|import`), before Undo / Redo / Restore, Rename and Save as.
- **Top bar**: `Saving…` while an engine change runs or edits wait for the autosave, `Saved ✓` afterwards (tooltip:
  the time), `Not saved` with the reason when a save failed (the edits stay pending and are tried again). `Ctrl+S`
  still saves at once.
- **New profile** has no dialog: `Documents\JoyPro2\profiles\profile-<date-time>.pr0file` (`JOYPRO_PROFILES_DIR`
  replaces the folder; tests). `More > Rename...` renames the file (its history goes along), `More > Save as...`
  writes a copy and goes on with it (the copy starts its own history, "Saved as a copy of ...").
  Renaming onto the name of another profile asks first ("Replace the profile ...?"); yes keeps the replaced file as
  `<name>.pr0file.bak` next to it (`<name>.pr0file.<date-time>.bak` when a .bak is there already) together with its
  history, and the renamed profile, with its history, is the open one. Renaming the .bak back undoes it. No changes
  nothing; a replaced file that is open in another program or read-only stops the rename with a message (nothing is
  changed then).

## The last profile at start

Settings > Startup > "Open the last profile at start", on by default (`ui-settings.json`: `autoOpenLastProfile`; the
old `loadLastProfileOnStartup`, off by default, is no longer read). The profile is opened after the window has drawn
itself (`MainWindow.OnWindowOpened` posts `AutoOpenLastProfileOnStartupAsync` at background priority), so the start is
as fast as without a profile; the busy area shows "Loading profile...". When the file is gone the Start page stays at
"No profile open" with a note in the status line.

## Versions

Where: `<data>\ProfileHistory\<profile id>\`, data = `JOYPRO_DATA_DIR` or `%LOCALAPPDATA%\JoyPro2` (the engine's data
folder, next to `GameBackups`). The profile id is `<file name, 32 characters>-<10 hex of the full path>`.

| File | What |
|---|---|
| `history.json` | the versions (id, time, kind, label, summary, content hash, size, kept, parent, from), the undo cursor, the redo stack, the time of the last clean-up |
| `blobs\<hash>.gz` | one gzip copy per distinct profile content; versions with the same content share it (undo, redo, restore and an unchanged checkpoint cost nothing) |
| `blobs\<hash>.items.gz` | the profile's items, one line per relation (name, action count, groups, hash of the actions, hash of kind + groups), bind (device and input, hashes of input, modifiers, tuning), modifier and settings section |
| `drafts.json` | modifiers that are not on a bind yet (see above) |
| `.lock` | held while a process changes the history (GUI, its engine processes and the tray host may run at the same time) |

Kinds: `change` (a save), `open` (the file as it was when the history started), `external` (the file was changed
outside JoyPro: another program, an older JoyPro, a copy put back), `checkpoint` (before an export or import:
"Before export to DCS", "Before import from IL-2"; or `profile history checkpoint`), `restore`, `undo`, `redo`.

- **What changed** comes from the items of the version and the one it was made on: "Relation 'Throttle ALL' created,
  42 actions added", "Relation 'Gear' renamed to 'Landing gear'", "'Flaps' moved from Stick JOY_BTN2 to Stick
  JOY_BTN3", "'Pitch': axis tuning changed", "Modifier 'Shift' created on Stick JOY_BTN5", "Force feedback settings
  changed". More than two changes are counted ("12 relations created, 380 binds set"). `Details` / `show` lists every
  change of a version, `Compare` / `diff` the changes between two versions.
- **A burst is one version**: saves within 2 s of the last one (one click that runs several engine commands) replace
  it; the labels are joined ("Adding relation + Setting bind").
- **Nothing is lost when the file changed outside JoyPro**: before a save, an undo or a restore the file on disk is
  compared with the version the history is at; a different file is kept as an `external` version first (the engine
  also reads its items, so it is described; in the engine server from the graph it already keeps of that file). A profile moved or renamed in Explorer finds its history by its content
  (the newest version's hash, profile file gone).
- **Unchanged saves** (same content as the version the profile is at) make no version.

## Undo, redo, restore

Undo / Redo (Ctrl+Z, Ctrl+Y or Ctrl+Shift+Z) walk the versions like an editor's undo, also after a restart:

- a change's `parent` is the version it was made on; Undo writes the parent's content back and records an `undo`
  version (`from` = the parent), the undone version goes on the redo stack;
- Redo writes the newest entry of the redo stack back (`redo` version); any new change clears the redo stack;
- `Restore` of an older version is a change of its own (parent = the state before it), so Undo returns to the state
  before the restore;
- a checkpoint of the content the profile is at does not move the undo cursor, so an undo after an export takes back
  the last change, not the export;
- versions removed by the clean-up are skipped: a parent that is gone is replaced by its own parent.

The GUI's former undo (copies of the profile in `%TEMP%\JoyPro2\undo\<pid>`, lost on exit, 20 steps) is replaced by
this. The Layout page's marker undo stays separate (Ctrl+Z / Ctrl+Y there while its editor is in front).

## Clean-up (retention)

So the history never fills the disk (`ProfileHistory.Prune`, `ProfileHistoryPolicy`):

| Age | Kept |
|---|---|
| last 24 hours | every version |
| up to 7 days | the newest version of every hour |
| up to 60 days | the newest version of every day |
| older | the newest version of every week |

Then, per profile, the oldest versions go until its history is under **200 MB**, and across all profiles the oldest
versions of all go until all histories are under **1 GB**. Never removed: versions marked `Keep`, the newest version,
the version the profile is at (and the one it stands for after an undo) and the redo stack. Blobs no version uses any
more are deleted.

It runs: when a save finds the last clean-up of the profile older than 6 hours; in the GUI a minute after start and
every 6 hours in the background at the lowest thread priority; on `Clean up` in the History panel (all profiles); on
`engine profile history prune [--all] [--dry-run]`.

Sizes: the user's 3 MB profile (≈450 relations) compresses to ≈0.55 MB per distinct version, so 200 MB hold ≈360
distinct versions of it; a 450-relation test profile of 4.2 MB took ≈1.2 MB for two versions. A change on that profile
costs ≈0.13 s more with the history (engine smoke, `smoke\history.ps1`; the engine run itself takes ≈1.2 s).

## Engine commands

```
engine --profile <file> profile history [list]            versions, newest first (JSON, or --text)
engine --profile <file> profile history show <id>         one version and every change it made
engine --profile <file> profile history diff <id> [<id>|current]
engine --profile <file> profile history restore <id>      | undo | redo
engine --profile <file> profile history keep <id> [on|off]
engine --profile <file> profile history checkpoint [--label <text>]
engine [--profile <file>] profile history prune [--all] [--dry-run]
engine [--profile <file>] profile history usage [--all]
engine ... --history-note <text> <any command that changes the profile>
```

`JOYPRO_PROFILE_HISTORY=off` switches the recording off (saves stay atomic). History commands do not change the
profile through the engine's own save, so they never write the loaded state over a restored file.

## Tests

- `gui\JoyPro.Gui.Tests\ProfileHistoryTests.cs`: debounce, flush, a change during a save, a failed save; atomic writes
  (whole file, a crash's temp file cleaned up, a locked file keeps the old content); versions with what changed,
  renames / binds / modifiers / settings in the description; undo / redo / restore and undo of a restore; a change
  outside JoyPro kept before an undo; a burst as one version; a checkpoint before an export; rename and a moved file;
  retention with simulated timestamps (every hour / day / week bucket, kept version, removed blobs); the size cap and
  the global cap; a 3 MB profile with 450 relations (15 saves, read, diff, undo); auto-open (opens, missing file, off);
  the top bar state and `--history-note`; undo / redo, the History panel (details, compare, keep, restore, clean up),
  pending modifiers kept and restored, switching profile saves into the old one, the export / import flush rule, New
  profile with an automatic path and Rename.
- `engine\tests\smoke\history.ps1` (part of `engine_smoke_test.ps1`): the engine end to end on temp profiles.
