Hand-written texts of the profile file commands, the DCS extras, the IL-2 and Star Citizen import side, the command lists (DB), the game detection, the watcher's sync and the helper commands. Each block below belongs to the command named in its marker; build.py puts it on the command's entry of the CLI reference.

<!-- entry: profile summary -->
:::layer beginner
Prints three counts of the profile: relations, binds and modifiers. Use it for a quick look whether the right profile
is open and how big it is. The GUI's **Profile Summary** (Start page and Advanced page) runs it, and **Reload** in the
profile bar runs it first.

```powershell
engine --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" profile summary
```
:::

:::layer advanced
- No options. `engine profile` without an action is the same command.
- Without `--profile` the engine opens the last profile it opened (see [[cli:profile load]]) and counts that one.
- Read-only: nothing is saved.
:::

:::layer technical
- Output: always one text line, `relations=<n>, binds=<n>, modifiers=<n>`, on stdout (no JSON; `--text` and `--out`
  change nothing).
- Exit code `0`.
- Related: [[cli:profile games]] (what an export writes per game), [[cli:profile validate]] (errors and warnings).
:::

<!-- entry: summary -->
:::layer beginner
Prints the three counts of the open profile: relations, binds and modifiers. It is the short form of
[[cli:profile summary]] and gives the same line.

```powershell
engine --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" summary
```
:::

:::layer advanced
- No options of its own.
- Without `--profile` the engine opens the last profile it opened and counts that one.
- Read-only: nothing is saved.
:::

:::layer technical
- Output: one text line, `relations=<n>, binds=<n>, modifiers=<n>`, on stdout (no JSON; `--text` and `--out` change
  nothing). Exit code `0`.
- Related: [[cli:profile summary]], [[cli:profile games]], [[cli:profile validate]].
:::

<!-- entry: version -->
:::layer beginner
Prints the version of the engine. Use it to see which JoyPro build answers on the command line, for example before you
send a support file. `engine --version` is the same.

```powershell
engine version
```
:::

:::layer advanced
- No options. As the first word (`engine version`) it answers at once, without opening a profile or reading devices.
- After global options (the GUI's start check runs `engine --status false --benchmark false version`) the engine
  starts as for any command first (it also opens the profile of `--profile`), then prints the version.
- Read-only.
:::

:::layer technical
- Output: one text line, the build's informational version (else the assembly version), on stdout; no JSON. Exit code
  `0`.
- Related: [[cli:support bundle]] (its `summary.txt` names the versions too).
:::

<!-- entry: profile load -->
:::layer beginner
Opens a `.pr0file`, applies the load corrections and prints the counts of relations, binds and modifiers. The GUI runs
it for **Open profile...** and **Open last profile** in the profile bar. The engine remembers the file, so a later
command without `--profile` works on it.

```powershell
engine profile load "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file"
```
:::

:::layer advanced
- The path is required (`profile load requires a .pr0file path.`).
- The load corrections run (global `--load-corrections`, default `true`); `--correction-report <file>` writes their
  report (`.txt` / `.log` as text, otherwise JSON).
- Actions of a relation that have no aircraft ticked leave the relation and become its dismissed suggestions. When
  there are any, the profile file is saved at once as a history version of its own ("Remove actions with no aircraft
  ticked"), so one undo brings them back.
- The path is stored as the last opened profile in `engine.meta.json` (`Documents\JoyPro`, or `JOYPRO_DATA_DIR`).
:::

:::layer technical
- Output: text lines `Loaded profile: <path>` and the counts as in [[cli:profile summary]]; no JSON.
- Exit code `0`; `1` when the path is missing or the file cannot be read.
- Related: [[cli:profile save]], [[cli:profile new]], [[cli:profile history]].
:::

<!-- entry: profile save -->
:::layer beginner
Writes the loaded profile to the given `.pr0file`. JoyPro saves every change by itself; **Save Profile** on the
Relations and Binds pages (and Ctrl+S) still runs this to write the file at once. The GUI also uses it to save a copy
under a new name.

```powershell
engine --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" profile save "C:\Users\Pilot\Documents\JoyPro\My Sticks copy.pr0file"
```
:::

:::layer advanced
- The target path is required (`profile save requires a target .pr0file path.`). A missing folder is created; an
  existing file is overwritten.
- The save is a version in the target's profile history, like every save.
- The target becomes the last opened profile (`engine.meta.json`).
:::

:::layer technical
- Output: `Saved profile: <path>` (text, no JSON). Exit code `0`, `1` on a missing path or a write error.
- Related: [[cli:profile save-last]], [[cli:profile load]], [[cli:profile history]].
:::

<!-- entry: profile save-last -->
:::layer beginner
Writes the loaded profile into JoyPro's own last-profile file. The GUI's **Save as last profile** in the profile bar's
**Options** panel runs it. Most people never need it: JoyPro saves your profile file by itself.

```powershell
engine --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" profile save-last
```
:::

:::layer advanced
- No options. The file is `last.pr0file` in `Documents\JoyPro` (or `JOYPRO_DATA_DIR`), together with
  `engine.meta.json`.
- `last.pr0file` has no profile history.
- A command without `--profile` opens the last opened profile; only when that file does not exist it opens
  `last.pr0file`.
- `dcs import`, `il2 import` and `starcitizen import` do the same with `--save-last`.
:::

:::layer technical
- Output: `Saved to last profile location.` (text). Exit code `0`.
- Related: [[cli:profile save]], [[cli:profile load]].
:::

<!-- entry: profile games -->
:::layer beginner
Lists the games, and for DCS the aircraft, that the profile has relations for, with how many of them are bound. That
is what an export to each game would write. The Export page builds its tiles from it (its **Refresh** runs it), and so
does the export from a Start page card.

```powershell
engine --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" profile games --text
```
:::

:::layer advanced
- No options. Read-only.
- A relation counts once per game; it counts as bound when its bind has a button or an axis (the same test the
  exporters use).
- DCS: per aircraft that is ticked in the relation's DCS actions; an action with no aircraft ticked counts under
  `Default`.
- Per game also whether it is installed, its installation folder and whether you hid it
  ([[cli:games install disable]]). DCS comes first, then the games by name.
:::

:::layer technical
- Output: JSON with one entry per game (`GameId`, `Name`, `Exporter`, `Relations`, `Bound`, `Planes`, `Installed`,
  `InstallPath`, `Disabled`), or a text table with `--text`; `--out` writes it to a file.
- Exit code `0`.
- Related: [[cli:dcs export]], [[cli:games installs]], [[cli:profile summary]].
:::

<!-- entry: profile auto-added -->
:::layer beginner
Up to JoyPro 2.6.1 the repair that runs when the command lists change (Refresh DB, an update with new lists) could
switch on aircraft in your relations that you never bound. This finds such a step in the profile's history and lists
the aircraft it switched on that are still on, per relation what goes and what stays. `--apply` takes them out again,
`--keep` keeps them and stops asking. The GUI asks right after a profile opens.

```powershell
engine --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" profile auto-added --text
engine --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" profile auto-added --apply --skip "3fa1c2d9e0b7" --text
```
:::

:::layer advanced
- Without `--apply` or `--keep` read-only.
- A step counts when it was made by a change (not an undo, a restore, an import, a take-over or a builder such as Auto
  Group), its version has another DB fingerprint than the version it was made on, and aircraft went from "not in the
  action" to "on" in existing actions of at least 3 relations. Relations the step's label names (your own edit) are
  left out; so is an aircraft an import brought in since. A step right after a `Before import from` checkpoint, or one
  that made 3 or more new relations or binds, counts as an import or builder too.
- `Default` and the other input layers of DCS are no aircraft: listed with `NonAircraft`, not in `Aircraft` and
  `RelationCount`. `--apply` takes them out like the rest. The GUI shows no notice when there is nothing else.
- `--apply` puts each aircraft back to "not in the action", except the keys given with `--skip`; `--keep` changes no
  relation. Both are one profile version (one undo step) and mark the steps as looked at, so they are not offered again.
:::

:::layer technical
- Output: JSON `Found`, `Aircraft`, `NonAircraft`, `RelationCount`, `Steps` (`Version`, `Utc`, `Local`, `Label`,
  `Aircraft`, `NonAircraft`, `Relations`), `Relations` (`Relation`, `Remove`, `Keeps`, `KeepsAircraft`, `NonAircraft`),
  `Entries` (`Key`, `Version`, `Relation`, `Game`, `Id`, `Plane`, `Title`, `NonAircraft`, `Skipped`), after
  `--apply`/`--keep` also `Applied`, `Kept`, `Removed`, `RemovedNonAircraft`, `KeptAircraft`, `Message`;
  `--text` for a table.
- History labels: `Took out aircraft JoyPro added: n aircraft in m relations`, `Kept the aircraft JoyPro added`.
- Exit code `0`. Related: [[cli:profile history]], [[cli:profile load]].
:::

<!-- entry: profile merge -->
:::layer beginner
Combines two or more profile files into one. Relations only one file has are added; where both have a relation of the
same name, the mode decides which one wins. The GUI's **Merge** (profile bar, **Options** panel) and **Merge Profile**
on the Relations and Binds pages run it with your profile as the left file; they ask first and keep a copy of your
profile.

```powershell
engine profile merge "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" "C:\Users\Pilot\Documents\JoyPro\Friend.pr0file" --mode left --save "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file"
```
:::

:::layer advanced
| `--mode` | When both files have a relation of the same name and it differs |
|---|---|
| `right` (default; also `r`, `overwrite`, `keep-right`) | the later file's relation and bind win |
| `left` (also `l`, `keep-left`) | the first file's relation stays |
| `ask` (also `a`, `prompt`) | the console asks per conflict; refused without an interactive console |

- Files are merged from left to right: the first file is the base, every further file is merged into it.
- Relations are matched by name (case does not matter); identical ones are skipped.
- Also merged, with the same rule: device aliases and pictures, device IDs, aircraft aliases, modifier renames, the DCS
  force feedback settings, the force feedback settings of the games, the layout export and the last selected DCS
  folder.
- `--save <file>` (also `--save-profile`) writes the result; `--overwrite-mode` is another name for `--mode`.
- With the global `--profile` the result is also auto-saved into that profile file. The source files are only read.
:::

:::layer technical
- Output: text lines `Merged profiles: <n>`, `Conflict mode: <mode>`, `Conflicts: <n>`, `Resolved Left: <n>`,
  `Resolved Right: <n>`, the counts, and `Saved merged profile: <path>`.
- Exit code `0`; `1` with fewer than two files (`profile merge requires at least two source files ...`), a missing file
  (`profile merge source does not exist: <path>`), an unknown mode (`... Use left|right|ask.`), both `--mode` and
  `--overwrite-mode`, or an unknown option.
- Related: [[cli:profile history]] (undo), [[cli:profile summary]].
:::

<!-- entry: profile new -->
:::layer beginner
Creates an empty profile file: no relations, binds or modifiers. The GUI's **New profile** runs it with a new file
(`My profile.pr0file`, `My profile 2.pr0file`, ...) in `Documents\JoyPro2\profiles`. Fill it afterwards with an
import, for example [[cli:dcs import]].

```powershell
engine profile new "C:\Users\Pilot\Documents\JoyPro\New Setup.pr0file"
```
:::

:::layer advanced
- `<path.pr0file>` is required. Missing folders are created.
- An existing file of that name is overwritten without asking. Its old content is not lost: it stays in the profile
  history of that path as a version ([[cli:profile history]], `restore` brings it back), unless the history is
  switched off.
- JoyPro's own settings (the meta settings) are kept; the new file becomes the last opened profile, so the next command
  without `--profile` opens it.
- `--profile` is not changed by this command (it is only read first).
:::

:::layer technical
- Output: `Created empty profile: <path>`; exit code `0`. Without a path: `profile new requires a target .pr0file path.`
  and exit code `1`.
- The file is written atomically (a temp file next to it, then swapped in).
- Related: [[cli:profile load]], [[cli:profile save]], [[cli:profile summary]].
:::

<!-- entry: profile history -->
:::layer beginner
Lists the versions of your profile, newest first. Every save of the profile is one version, and so is the state before
each import or export. The GUI's **History** in the top bar shows the same list.

```powershell
engine --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" profile history --text
```
:::

:::layer advanced
- `engine profile history` and `engine profile history list` are the same. `--profile` is required.
- Per version: number, local time, kind (`change`, `open`, `external`, `checkpoint`, `restore`, `undo`, `redo`), its
  name and what it changed in short, `[kept]` for a version the clean-up never removes and `[current]` for the version
  the profile is at.
- The list also tells whether undo and redo are possible and what they would take back or bring back.
- Read-only. The other actions: [[cli:profile history show]] (and `diff`), [[cli:profile history restore]] (and
  `undo`, `redo`), [[cli:profile history keep]] (and `checkpoint`), [[cli:profile history prune]] (and `usage`).
:::

:::layer technical
- Output: JSON (`Profile`, `Folder`, `Cursor`, `CanUndo`, `UndoLabel`, `CanRedo`, `RedoLabel`, `Bytes`, `Kept`,
  `Versions` with `Id`, `Utc`, `Local`, `Kind`, `Label`, `Summary`, `Keep`, `Size`, `Parent`, `From`, `Current`), or
  text with `--text` (`History of <file>: <n> version(s), <n> kept, <size>`, one line per version).
- Exit code `1` with `engine profile history list needs --profile <file>.` or `Unknown profile history list
  argument(s): ...`.
- The history lives in `%LOCALAPPDATA%\JoyPro2\ProfileHistory\<profile id>\` (`history.json` and one compressed copy
  per distinct content). `JOYPRO_PROFILE_HISTORY=off` switches the recording off. See [[doc:profile-history]].
:::

<!-- entry: profile history show -->
:::layer beginner
Shows what one version of the profile changed, or with `diff` the changes between two versions: relations created or
renamed, binds moved, modifiers, tuning and settings. The **History** panel's **Details** and **Compare** show the same.

```powershell
engine --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" profile history show 12 --text
```
:::

:::layer advanced
- `show <id>`: the changes of that version against the version it was made on.
- `diff <id> [<id>|current]`: the changes from the first version to the second; without the second, to the version the
  profile is at now.
- A version is its number (`12` or `#12`), `latest` (the newest) or `current` (where the profile is now). The numbers
  come from [[cli:profile history]].
- A version that was changed outside JoyPro (another program, an older JoyPro) has no details; the output says so.
- Read-only. `--profile` is required.
:::

:::layer technical
- Output: JSON (`From`, `To`, `Available`, `Summary`, `Changes`), or text with `--text`: for `show` the line
  `Version <id> | <time> | <kind>` (with `| kept`) and its description, for `diff` `Changes from version <a> to <b>:`,
  then one line per change, or `no change in relations, binds or settings`.
- Exit code `1` with the usage line (`engine profile history show <version id>`, `... diff <version id> [<version
  id>|current]`), `Version <id> is not in the history.` or `Unknown version: <id>`.
- Related: [[cli:profile history restore]]; [[doc:profile-history]].
:::

<!-- entry: profile history restore -->
:::layer beginner
Puts an older version of the profile back, or takes the last change back (`undo`) and brings it back again (`redo`). The
top bar's **Undo**, **Redo** and the **History** panel's **Restore** do the same.

```powershell
engine --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" profile history restore 12 --text
```
:::

:::layer advanced
- `restore <id>`: writes that version into the profile file. The restore is a version of its own, so `undo` returns to
  the state before it. It clears the redo list. A version is its number, `latest` or `current`.
- `undo`: writes back the version the last change was made on; the undone version can come back with `redo`.
- `redo`: writes back the newest undone version. Any new change clears what could be redone.
- Safety: before it writes, a profile file that was changed outside JoyPro is kept as a version of its own, so nothing
  is lost. A checkpoint before an export does not count as a change: `undo` after an export takes back your last edit,
  not the export.
- Only the profile file changes; the games keep their binds until the next export. `--profile` is required.
:::

:::layer technical
- Output: JSON (`Action`, `Changed`, `Message`, `Version`), or the message with `--text`: `Restored the version of
  <time>.`, `Undone: <description>.`, `Redone: <description>.`, `The profile is already at this version.`, `Nothing to
  undo.`, `Nothing to redo.` or `This profile has no history yet.` (exit code `0`, `Changed` false for the last four).
- Exit code `1` with `engine profile history restore <version id> (a version of 'engine profile history list').` when
  the id is missing or not in the history, or `... needs --profile <file>.`
- The engine does not save its loaded profile afterwards, so the restored file stays as written. Related:
  [[cli:profile history]], [[cli:support unpack]]; [[doc:profile-history]].
:::

<!-- entry: profile history keep -->
:::layer beginner
Marks a version of the profile as kept, so the automatic clean-up never removes it, or keeps the profile as it is now as
a named version (`checkpoint`). Use a checkpoint before a big change you may want to go back from.

```powershell
engine --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" profile history checkpoint --label "Before new throttle" --text
```
:::

:::layer advanced
- `keep <id> [on|off]`: `on` (or nothing) keeps the version; `off` (or `false`) lets the clean-up remove it again. Any
  other second word counts as on. A version is its number, `latest` or `current`.
- `checkpoint [--label <text>]`: the profile file as it is now becomes a version of kind `checkpoint` named by the
  label (default `Checkpoint`). A checkpoint does not move the undo position. When the history is switched off,
  nothing is recorded and the output says so.
- Only the history changes, never the profile. `--profile` is required.
:::

:::layer technical
- Output: JSON (`Action`, `Changed`, `Message`, `Version`), or the message with `--text`: `Version <id> is kept (never
  cleaned up).`, `Version <id> may be cleaned up again.`, `Kept as version <id>.` or `The profile history is switched
  off.`
- Exit code `1` with `engine profile history keep <version id> [on|off]`, `Version <id> is not in the history.`, `The
  profile file does not exist: <file>` (checkpoint) or `... needs --profile <file>.`
- Related: [[cli:profile history prune]], [[cli:profile history]]; [[doc:profile-history]].
:::

<!-- entry: profile history prune -->
:::layer beginner
Cleans up the profile history now, or shows how much room it takes (`usage`). JoyPro also cleans up by itself every
6 hours, so you rarely need it; the **History** panel's **Clean up** does the same for all profiles.

```powershell
engine --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" profile history prune --dry-run --text
```
:::

:::layer advanced
- `prune`: the history of `--profile`. It keeps every version of the last 24 hours, the newest of every hour up to 7
  days, of every day up to 60 days, of every week after that; then the oldest versions go until the history is under
  200 MB.
- `prune --all`: every profile's history (no `--profile` needed), then the oldest versions of all profiles go until all
  histories together are under 1 GB.
- Never removed: kept versions ([[cli:profile history keep]]), the newest version, the version the profile is at and
  what can still be redone. Copies no version uses any more are deleted.
- `--dry-run` lists what would go and removes nothing; with `--all` it counts the 1 GB step too.
- `usage [--all]`: versions, kept versions and size of this profile's history (or of all); read-only.
:::

:::layer technical
- Output: JSON (`Action`, `Changed`, `Message`, `Removed`, `BytesBefore`, `BytesAfter`, `VersionsLeft`; for `usage`
  `Profiles`, `Versions`, `Kept`, `Bytes`), or text with `--text`: `Removed <n> version(s): <size> -> <size>, <n>
  version(s) left.` (`Would remove` on a dry run) with one line per removed version; `Profile history: <n> version(s),
  <n> kept, <size> in <folder>`.
- Exit code `0`; `1` with `engine profile history prune needs --profile <file>.` when neither `--profile` nor `--all`
  is given.
- Folder: `%LOCALAPPDATA%\JoyPro2\ProfileHistory`. Related: [[cli:profile history]]; [[doc:profile-history]].
:::

<!-- entry: dcs defaults restore -->
:::layer beginner
Puts back the device default files an older JoyPro moved out of the DCS installation into
`Saved Games\DCS\BACKUP_JOYSTICK_DEFAULTS`. Use it once if an older JoyPro version moved them. The GUI's **Put back
files an older JoyPro moved** (Settings > DCS: default binds, "Folders and older JoyPro backups") runs it after asking.
Close DCS first.

```powershell
engine dcs defaults restore --path "C:\Users\Pilot\Saved Games\DCS" --install-path "D:\Games\DCS World"
```
:::

:::layer advanced
- Paths: the Saved Games folder (first positional or `--path`) and the DCS installation (second positional or
  `--install-path`); missing ones are detected (`Auto-detected DCS ... path: ...` on stderr).
- `--backup-path <folder>` reads another backup folder (default `<Saved Games>\BACKUP_JOYSTICK_DEFAULTS`).
- Every aircraft folder of the backup is copied back into `<install>\Mods\aircraft\<aircraft>`, with its subfolders;
  existing files are replaced. Each replaced file goes into a game backup set first (undo:
  [[cli:backups restore]]).
- Refused when DCS is not installed, and while `DCS` or `DCS_updater` runs. `--dry-run` counts the files and folders
  it would copy and changes nothing (also while DCS runs). Today's way to move these files is [[cli:dcs device-luas]].
:::

:::layer technical
- Output: JSON of the result (paths, aircraft scanned, folders touched, files restored, `DryRun`, warnings, errors), or
  text with `--text` (`Dry Run: true ...` on a dry run).
- Exit code `0`; `1` with `dcs defaults restore failed: ...` (no `Mods\aircraft` in the installation, no backup
  folder) or when a path is missing (`... requires a DCS Saved Games path. Try: engine games paths --game DCS --text`).
- Related: [[cli:dcs defaults cut]], [[cli:dcs strip-defaults]], [[cli:dcs device-luas]].
:::

<!-- entry: dcs defaults cut -->
:::layer beginner
The older way to move DCS's device-specific default files out of the DCS installation: into
`Saved Games\DCS\BACKUP_JOYSTICK_DEFAULTS`. The GUI no longer runs it; its switch **Remove DCS's device-specific
default files from the install** uses [[cli:dcs device-luas]], which keeps the files in JoyPro's own folder and moves
them again after a DCS update. Close DCS first.

```powershell
engine dcs defaults cut --path "C:\Users\Pilot\Saved Games\DCS" --install-path "D:\DCS World" --text
```
:::

:::layer advanced
- Paths: the Saved Games folder (first positional, `--path` or `--saved-games`) and the DCS installation (second
  positional or `--install-path`); missing ones are detected (`Auto-detected DCS ... path: ...` on stderr). The
  installation may also be given as its `Mods` or `Mods\aircraft` folder.
- `--backup-path <folder>` moves the files into another folder (default `<Saved Games>\BACKUP_JOYSTICK_DEFAULTS`).
- In every `Mods\aircraft\<aircraft>\Input\<input>\joystick` folder, `default.lua` is copied into the backup folder
  (it stays in DCS) and every other file of the folder is moved there, under `<aircraft>\Input\<input>\joystick`.
  A file of the same name in the backup folder is replaced.
- Safety: every moved file and every file it creates or replaces in the backup folder goes into a game backup set
  first (undo: [[cli:backups restore]], or [[cli:dcs defaults restore]] for the backup folder). `--dry-run` counts
  the files it would move and copy and changes nothing (also while DCS runs). Refused while `DCS` or `DCS_updater`
  runs, and when DCS is not installed or switched off in Settings > Games.
:::

:::layer technical
- Output: JSON of the result (`Action` `cut`, the paths, `AircraftRoot`, `AircraftScanned`, `JoystickFoldersScanned`,
  `DefaultFilesCopied`, `StickSpecificFilesMoved`, `DryRun`, `Warnings`, `Errors`), or text with `--text` ("DCS Stick
  Defaults", `Dry Run: true ...` on a dry run).
- Exit code `0`; `1` with `dcs defaults cut failed: ...` (for example `Couldn't find DCS install aircraft path under:
  <path>` or a file that could not be moved; the result is printed first), `DCS is running (...). Close DCS and its
  updater before dcs defaults cut.`, when a path is missing (`... requires a DCS Saved Games path. Try: engine games
  paths --game DCS --text`) or with `Unknown dcs defaults argument(s): ...`.
- Only `Mods\aircraft` of the installation is looked at, not `Saved Games\DCS\Mods`.
- Related: [[cli:dcs defaults restore]], [[cli:dcs device-luas]], [[cli:dcs strip-defaults]]; game page [[game:dcs]].
:::

<!-- entry: dcs strip-defaults -->
:::layer beginner
Switches DCS's own default joystick binds off inside the DCS installation, for every device, also the ones JoyPro
does not write. JoyPro's exports already remove the default binds of the devices they write, so this is an extra
option, off by default. The GUI's **Strip DCS default binds in the DCS install** (Settings > DCS: default binds)
runs it after asking; **Preview files**, **Apply now**, **Restore DCS files** and **Check again** use it too. Close
DCS first.

```powershell
engine dcs strip-defaults apply --dry-run --text
```
:::

:::layer advanced
| Action | What it does |
|---|---|
| `apply` (default) | patches every joystick input file of DCS that is not patched yet and remembers the switch |
| `restore` (or `--restore`) | puts every patched file back from its backup, the UI layer too, and switches it off |
| `status` (or `--status`, also before the action word) | counts the files: patched, not patched, device files in DCS and kept by JoyPro; changes nothing |
| `auto` | does again what is switched on (this option and [[cli:dcs device-luas]]); nothing to do when both are off |

- The files: each `*.lua` in a folder named `joystick` below `<DCS>\Mods`, `<DCS>\Config` and
  `Saved Games\DCS*\Mods` (not below a `keyboard` folder, not `*.diff.lua`) that has `keyCommands`, `axisCommands`
  or `external_profile`. `Saved Games\DCS\Config\Input`, where your own binds are, is never touched.
- Patching wraps the original file in a function and sets `combos = nil` on every entry of what it returns, so DCS
  loads the same commands without default binds. The first line is the marker `-- [[DCS_STRIP_JOYSTICK_DEFAULTS v1]]`
  and the original is kept as `<file>.nocombo.bak`: the same marker and backup name as the community strip script,
  so either one restores the other's files.
- After patching, DCS's own Lua (`bin\luae.exe`, or `--luae <file>`) loads every patched file next to its original;
  a file it cannot load is put back at once. `--no-load-check` skips this check (meant for tests).
- `--skip-ui-layer [true|false]` leaves the files of the UI layer (`UiLayer\joystick`: menus and VR controllers)
  unpatched; without it the last choice is used (GUI: **Keep the UI layer's default binds (menus, VR)**).
- `--remember false` patches or restores without changing the stored switch (default `true`).
- `--install-path <folder>` and `--saved-games <folder>` (also `--path`) pick the folders; without them the detected
  DCS installation and every `Saved Games\DCS*` folder are used.
- After a DCS update or repair the original files are back: with the switch on, JoyPro runs `auto` at its start and
  before every headless DCS list refresh ([[cli:dcs refresh clean]] and [[cli:dcs refresh id]]; the click flow with
  `--headless-lua false` does not).
- `--remove` is refused here (`dcs strip-defaults has no --remove: --restore ...`); it belongs to
  [[cli:dcs device-luas]].
- Safety: `apply` and `restore` are refused while `DCS` or `DCS_updater` runs; `--dry-run` lists the files and
  writes nothing (also while DCS runs). Patched files fail the integrity check of multiplayer servers that enforce it
  (pure scripts / IC); `restore` puts DCS's files back. A missing write access to the DCS folder stops the run.
:::

:::layer technical
- Output: JSON of the result (`Action`, `DryRun`, `InstallPath`, `SavedGamesPaths`, `DcsVersion`, `StripEnabled`,
  `SkipUiLayer`, `RemoveDeviceLuas`, `CleanProfilesStale`, `ProfileFiles`, `Patched`, `AlreadyPatched`, `NotPatched`,
  `Restored`, `LoadChecked`, `LoadCompared`, `DefaultBindsSwitchedOff`, the device file counts, `NeedsReapply`,
  `IntegrityNote`, `Files`, `Warnings`, `Errors`), or text with `--text` ("DCS default binds in the install").
- Exit code `0`; `1` with `dcs strip-defaults <action> failed: ...` (the result is printed first), for example `DCS
  World was not found. Set its folder in Settings > Games or pass --install-path.`, `DCS is running (...). Close DCS
  and its updater first.`, `DCS's Lua cannot load the patched <file> (...); the original was put back.` or `No backup
  <file>.nocombo.bak for the patched <file>: repair DCS (DCS_updater.exe repair) to get the original back.` Also
  `Unknown dcs strip-defaults action: <x> (use apply, restore, status or auto).` and `Unknown dcs strip-defaults
  argument(s): ...`. Without an action word the message names `apply`.
- The switches are kept in `%LOCALAPPDATA%\JoyPro2\DcsDefaults\state.json`.
- Related: [[cli:dcs device-luas]], [[cli:dcs verify-clean]], [[cli:dcs export]]; game page [[game:dcs]].
:::

<!-- entry: dcs device-luas -->
:::layer beginner
Moves DCS's default files for single devices ("Joystick - HOTAS Warthog.lua" and similar) out of the DCS installation
into JoyPro's backup folder, so DCS uses its general `default.lua` for every device; `restore` puts them back. Off by
default. The GUI's **Remove DCS's device-specific default files from the install** (Settings > DCS: default binds)
runs it after asking, then rebuilds the DCS list once; **Preview device files** runs `list`. Close DCS first.

```powershell
engine dcs device-luas list --text
```
:::

:::layer advanced
| Action | What it does |
|---|---|
| `list` (default; also `status` or `--status`) | the device files DCS has now; changes nothing |
| `remove` (or `--remove`) | moves them into JoyPro's backup folder and remembers the switch |
| `restore` (or `--restore`) | puts every kept file back and switches it off |

- The files: `<device type>.lua` (not `default.lua`) and template diffs `<device type>.diff.lua` in the `joystick`
  folders below `<DCS>\Mods`, `<DCS>\Config` and `Saved Games\DCS*\Mods`. A file another Lua file of the module's
  Input folder names (a shared part) stays, and so does the UI layer (the VR controllers' defaults).
- A file patched by [[cli:dcs strip-defaults]] takes its `.nocombo.bak` along, and gets it back on `restore`.
- After a DCS update brought files back, `remove` (or `auto` of [[cli:dcs strip-defaults]]) moves them again; the
  newer file replaces JoyPro's older copy. On `restore` a file DCS already brought back stays (warning "Kept the file
  DCS brought back").
- `--remember false` moves or restores without changing the stored switch (default `true`).
  `--install-path <folder>` and `--saved-games <folder>` (also `--path`) pick the folders.
- After `remove` or `restore` JoyPro's DCS clean profiles no longer match what DCS loads: rebuild them with
  [[cli:dcs refresh clean]] (the GUI does it); until then [[cli:dcs export]] warns.
- Safety: `remove` and `restore` are refused while `DCS` or `DCS_updater` runs; `--dry-run` lists the files and moves
  nothing. A missing write access to the DCS folder stops the run.
:::

:::layer technical
- Output: the same result as [[cli:dcs strip-defaults]] (`DeviceFilesInInstall`, `DeviceFilesMoved`,
  `DeviceFilesRestored`, `DeviceFilesBackedUp`, `CleanProfilesStale`, `Files`, `Warnings`, `Errors`), or text with
  `--text`.
- Exit code `0`; `1` with `dcs device-luas <action> failed: ...`, for example `JoyPro's copy of <file> is missing
  (...): repair DCS to get it back.`, or `Unknown dcs device-luas action: <x> (use list, remove or restore).`
- The files are kept in `%LOCALAPPDATA%\JoyPro2\DcsDefaults\DeviceLuas\<folder hash>\` with `manifest.json`
  (original path, SHA-256, time moved). `restore` also names a `BACKUP_JOYSTICK_DEFAULTS` folder an older JoyPro left
  ([[cli:dcs defaults restore]]).
- Related: [[cli:dcs strip-defaults]], [[cli:dcs defaults restore]], [[cli:dcs verify-clean]]; game page
  [[game:dcs]].
:::

<!-- entry: dcs verify-clean -->
:::layer beginner
Checks which of DCS's default binds are still active on your joysticks with the binds of a Saved Games folder, using
DCS's own input code without starting the game. After a clean export it should find none on the devices JoyPro wrote.
It only reads.

```powershell
engine dcs verify-clean "C:\Users\Pilot\Saved Games\DCS" --text
```
:::

:::layer advanced
- Path: the Saved Games folder (positional, `--path` or `--saved-games`) and `--install-path`; missing ones are
  detected.
- `--plane <name>` / `--planes <a|b>` checks only those aircraft (an unknown name fails: `Plane filter was provided,
  but no DCS headless profile candidates matched.`).
- `--device <name>` / `--devices <a|b>` checks only those devices: the DCS device name, its template name, or a part
  of the name. Lists take `|`, `,` or `;`.
- Checked are the joysticks connected now; `--include-disconnected true` also the devices that only have files, each
  through a fake stick of its type.
- `--max-listed <n>` limits the listed defaults (default 200); the counts are always complete.
- `--dump-active <file>` writes every bind DCS has active on the checked joysticks (not only defaults), one sorted line
  `profile|device|kind|command hash|key|modifiers|filter` each: run it before and after an export and compare.
- Safety: works on a temporary copy of `Config\Input` (`wizard.lua`, `disabled.lua`, each aircraft's `modifiers.lua`
  and `joystick\*.diff.lua`); the Saved Games folder is not changed. Not refused while DCS runs.
:::

:::layer technical
- Output: JSON (`SavedGamesPath`, `InstallPath`, `ProfilesChecked`, `ProfileNames`, `Devices`, `DefaultsLeft`,
  `DefaultsLeftOnJoyProDevices`, `Left` with profile, device, kind, command, key and whether JoyPro wrote the file,
  `ActiveDumpFile`, `ActiveBinds`, `Steps`, `Warnings`, `Errors`), or text with `--text` ("DCS default-bind check",
  the defaults left per device).
- "Defaults Left On JoyPro Devices" counts the defaults on devices whose file JoyPro wrote (a `.jp` next to the
  `.diff.lua`).
- Exit code `0`; `1` with `dcs verify-clean failed: ...`, for example `luae.exe was not found under the DCS install's
  bin folder.`, `The DCS default-bind check timed out.` (15 minutes), `DCS Saved Games path does not exist: <path>`,
  `Invalid --max-listed value: '<x>'.` or `Unknown dcs verify-clean argument(s): ...`. The dump file is written only
  without errors.
- Related: [[cli:dcs export]], [[cli:dcs strip-defaults]], [[cli:dcs device-luas]], [[cli:dcs refresh clean]]; game
  page [[game:dcs]].
:::

<!-- entry: dcs refresh clean -->
:::layer beginner
Builds JoyPro's DCS "clean profiles": the default binds DCS puts on every device type in every aircraft. The DCS export
removes exactly these from the devices it writes. Use it after a DCS update; it needs DCS installed. The GUI runs it in
headless mode after the first DCS command list was built, after **Apply now** / **Restore DCS files** when needed, and
with **Refresh Clean DB** (Settings > Command lists of DCS, Star Citizen and IL-2).

```powershell
engine dcs refresh clean --path "C:\Users\Pilot\Saved Games\DCS" --install-path "D:\Games\DCS World" --headless-lua true
```
:::

:::layer advanced
Two ways, chosen by `--headless-lua` (default `true`, in the engine and in the GUI):

- **Headless** (`--headless-lua true` or no `--headless-lua`): DCS is not started and no window opens. Several copies of DCS's own
  `bin\luae.exe` run in the background, each in a temporary Saved Games folder, load every input profile with a
  generic stick and one stand-in stick per device type, clear them and save; those files become the clean profiles.
  Your `Config\Input` is never changed. Before it, JoyPro applies the DCS install options you switched on. The run can
  be cancelled until the clean profiles are written.
- **Legacy click mode** (only with an explicit `--headless-lua false`): DCS starts in a window (or a running DCS is used) and JoyPro clicks
  through its menus: gear icon, Controls, Clear All, Check All, Yes. Before, `Config\Input` is copied to
  `Config\CLInput\Input` and removed, and `options.lua` is set to windowed and GUI scale 1.0; afterwards both come
  back, also after an error. Leave mouse and keyboard alone: a moved mouse or another active window stops the run.

| Option | Meaning |
|---|---|
| `--plane` / `--planes` | only these aircraft (headless only) |
| `--dry-run` | headless: lists the aircraft, writes nothing; legacy: plans the clicks without starting DCS |
| `--launch true\|false` | legacy: start DCS (default `true`); `false` needs a running DCS; in headless mode only an explicit `--launch` warns that it is ignored |
| `--keep-running` | legacy: leave DCS open afterwards (default `false`) |
| `--ocr true\|false` | legacy: find the menu entries by picture instead of `Clicks.xml` positions |
| `--settle-seconds <n>` | legacy: wait after the clicks (default `60`) |
| `--menu-wait-seconds`, `--ocr-menu-timeout-seconds` | legacy: waits for the main menu (defaults `45`, `600`) |
| `--default-stick`, `--clicks-xml`, `--clicks-dir`, `--dcs-exe`, `--gui-scale`, `--window-*`, `--preview-clicks`, `--preview-image` | legacy click details; ignored in headless mode (with a warning) |

- `--confirm` (default `true`) asks "Type 'yes'" in an interactive console; without one the run goes on with a warning.
- Refused when DCS is not installed.
:::

:::layer technical
- Output: JSON of the refresh (paths, steps, aircraft, clicks, warnings, errors), or text with `--text`; progress lines
  with `--progress`.
- Exit code `0`; `1` with `dcs refresh clean failed: ...`, a missing path (`dcs refresh clean requires a DCS Saved
  Games path.` / `... install path.`) or an invalid number; `3` when cancelled.
- Writes `CleanProfile\DCS` and `KeyboardCleanProfile\DCS` in the game-data folder (`%LOCALAPPDATA%\JoyPro2\GameData`).
  An aircraft whose Lua run keeps failing keeps its previous clean profiles (a warning names it).
- Related: [[cli:dcs refresh id]], [[cli:dcs verify-clean]], [[cli:dcs export]], [[cli:db check]].
:::

<!-- entry: dcs refresh id -->
:::layer beginner
Builds JoyPro's DCS command list: every aircraft's actions with their ids and names, from your DCS installation. Use it
after a DCS update or a new module; it needs DCS installed. **Update** on the DCS card of the Start page and **Build All
Missing Lists** run it headless; **Refresh ID DB** (Settings > Command lists of DCS, Star Citizen and IL-2) runs it with
the options set there.

```powershell
engine dcs refresh id --path "C:\Users\Pilot\Saved Games\DCS" --install-path "D:\Games\DCS World" --headless-lua true --plane F-16C_50
```
:::

:::layer advanced
- **Headless** (the default, `--headless-lua true`, also the GUI's way): DCS is not started. Several `luae.exe` runs of DCS's own Lua
  read the input profiles (and your `Config\Input`, read only) and write DCS's per-aircraft HTML lists into a temporary
  folder; JoyPro copies them into its command list. Cancelable until the files are copied.
- **Legacy click mode** (only with an explicit `--headless-lua false`): DCS starts in a window and JoyPro clicks gear
  icon, Controls, the aircraft list, then **Make HTML** for one aircraft after the other (arrow down to the next). As in
  [[cli:dcs refresh clean]], `options.lua` is set to windowed and GUI scale 1.0 for the run and restored, and a moved
  mouse or another active window stops it. `--modules-to-scan <n>` adds loop rounds on top of the aircraft found.
- `--plane` / `--planes`, `--dry-run`, `--launch`, `--keep-running`, `--ocr`, `--confirm` and the click details work as
  in [[cli:dcs refresh clean]].
- Refused when DCS is not installed.
:::

:::layer technical
- Output: JSON or `--text` as for [[cli:dcs refresh clean]]. Exit code `0`; `1` with `dcs refresh id failed: ...`;
  `3` when cancelled.
- Writes `DB\DCS\*.html` in the game-data folder. After a real run (not a dry run) the DCS build is recorded for
  [[cli:db check-updates]].
- Related: [[cli:db planes]], [[cli:db summary]], [[cli:dcs refresh preview-clicks]].
:::

<!-- entry: dcs refresh preview-clicks -->
:::layer beginner
Shows where the legacy click mode of the DCS refreshes would click, as a picture with the click points, without
clicking anything. Use it before a legacy refresh on an unusual screen. The GUI's **Preview Clicks** (Settings >
Command lists of DCS, Star Citizen and IL-2, legacy mode) runs it. It needs DCS installed.

```powershell
engine dcs refresh preview-clicks --ocr false --window-left 0 --window-top 0 --window-width 2560 --window-height 1440 --preview-image "C:\Users\Pilot\Pictures\dcs-clicks.png"
```
:::

:::layer advanced
- `--ocr false` (default): the click points come from `Clicks.xml` (`--clicks-xml`, default `<program folder>\Clicks`)
  in the DCS window (a running DCS, or `--window-left/-top/-width/-height`, all four together), scaled by the GUI scale
  of DCS's options or `--gui-scale`.
- `--ocr true`: looks for the menu pictures (`--clicks-dir`) on the screen and reports which were found.
- `--preview-image <file.png>`: where the picture goes (default `%TEMP%\joypro_dcs_click_preview_<date-time>.png`).
- Nothing in DCS or JoyPro's lists changes. `dcs refresh` without a verb is this command.
:::

:::layer technical
- Output: JSON (window, GUI scale, each click with its name and position, picture path, warnings), or text with
  `--text`.
- Exit code `0`; `1` with `dcs refresh preview-clicks failed: ...`, an incomplete window override (`Window override
  requires --window-left, --window-top, --window-width, and --window-height.`) or `--plane` / `--headless-lua` (only
  for id and clean).
- Related: [[cli:dcs refresh clean]], [[cli:dcs refresh id]].
:::

<!-- entry: dcs ffb list -->
:::layer beginner
Lists the DCS force feedback direction settings of your devices: swap axes, invert X, invert Y. The GUI's **Refresh
Devices** in Settings > DCS force feedback runs it to fill the table, and runs it again after every Apply or Clear.

```powershell
engine --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" dcs ffb list --connected --text
```
:::

:::layer advanced
- The list holds the devices of the profile, the connected devices and every device with a stored setting.
- `--connected` shows only connected devices, `--profile-devices` only the profile's (both together: no filter).
- `--device <name>` / `--devices <csv>` (or a name as the first argument) limit the list; a name that does not match
  exactly is resolved to the closest DCS device name (`--confirm false` takes it without asking; `--path` names the
  Saved Games folder used for that).
- Read-only. The settings live in the profile, so pass `--profile`.
:::

:::layer technical
- Output: JSON (`Devices` with `Device`, `HasSetting`, `InvertX`, `InvertY`, `SwapAxis`, `Connected`, `Profile`;
  `Warnings`), or text with `--text`.
- Exit code `0`; `1` on an unknown option.
- Related: [[cli:dcs ffb set]], [[cli:dcs ffb clear]], [[cli:ffb set]].
:::

<!-- entry: dcs ffb set -->
:::layer beginner
Stores swap axes, invert X and invert Y for one device in the profile. The next DCS export writes them into that
device's force feedback settings of every exported aircraft. The GUI's **Apply**, **Apply Selected** and **Set FFB**
in Settings > DCS force feedback run it.

```powershell
engine --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" dcs ffb set "VPforce Rhino FFB" --swap-axis true --invert-x false --invert-y false
```
:::

:::layer advanced
- Exactly one device (first argument, `--device` or `--devices`); its name is resolved like in [[cli:dcs ffb list]].
- At least one of `--invert-x`, `--invert-y`, `--swap-axis` (each `true|false`; a bare flag means `true`). Flags you
  leave out keep their stored value; a device without a setting gets one.
- Nothing in DCS changes now: [[cli:dcs export]] writes the values as the device's force feedback block of each
  exported aircraft, adjusted per aircraft where JoyPro knows a correction.
- With `--profile` the profile is saved afterwards (one undo step).
:::

:::layer technical
- Output: JSON (`Action`, `Devices`, `Updated`, the three values, `Warnings`), or text with `--text`.
- Exit code `0`; `1` with `dcs ffb set requires at least one setting: --invert-x, --invert-y, or --swap-axis.`,
  `dcs ffb set requires a device name.`, `dcs ffb set accepts exactly one device target.` or `No DCS device matched
  for dcs ffb set.`
- Related: [[cli:dcs ffb clear]], [[cli:dcs ffb list]].
:::

<!-- entry: dcs ffb clear -->
:::layer beginner
Removes the stored DCS force feedback direction settings of one or more devices, or of all with `--all`. The device
then gets DCS's default direction again at the next export. The GUI's **Clear**, **Clear Selected**, **Clear All** and
**Clear FFB** in Settings > DCS force feedback run it.

```powershell
engine --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" dcs ffb clear "VPforce Rhino FFB" --confirm false
```
:::

:::layer advanced
- Devices: first argument, `--device`, `--devices`; names are resolved like in [[cli:dcs ffb list]].
- `--all` without names removes every stored setting.
- Only the profile changes; with `--profile` it is saved afterwards (one undo step).
:::

:::layer technical
- Output: JSON (`Action`, `All`, `Devices`, `Removed`, `Warnings`), or text with `--text`.
- Exit code `0`; `1` with `dcs ffb clear requires a device name or --all.` or `No DCS FFB settings matched the
  requested device(s).`
- Related: [[cli:dcs ffb set]], [[cli:dcs export]].
:::

<!-- entry: il2 scan -->
:::layer beginner
Shows what [[cli:il2 import]] would bring from IL-2 Great Battles with the same options, without changing the profile
or the game. On the Start page **Look first** in the import wizard and **Scan** in the expert import run it.

```powershell
engine il2 scan "D:\Games\IL-2 Sturmovik Battle of Stalingrad" --text
```
:::

:::layer advanced
- The path is the IL-2 folder (positional or `--path`); without one the installation is detected (`Auto-detected IL2
  root path: ...` on stderr).
- `--device` / `--devices`, `--ignore-device` / `--ignore-devices`, `--import-inverted`, `--import-curvature`,
  `--import-deadzone` and `--confirm` work as in [[cli:il2 import]].
- Reads only. Refused when IL-2 is not installed.
:::

:::layer technical
- Output: JSON of the scan, or a text report with `--text`. Exit code `0`; `1` with `il2 scan failed: ...` (for
  example `Missing global.actions: <file>`) or without a path (`il2 scan requires an IL2 root path. Try: engine games
  paths --game IL2 --text`).
- Save options (`--save-profile`, `--save-last`) and `--mode` are refused here.
:::

<!-- entry: il2 import -->
:::layer beginner
Reads the binds you made in IL-2 Great Battles into your JoyPro profile, one relation per action and input. Nothing in
IL-2 changes. **Import now** in the Start page's import wizard and **Import** in the expert import run it.

```powershell
engine il2 import "D:\Games\IL-2 Sturmovik Battle of Stalingrad" --merge --import-inverted --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file"
```
:::

:::layer advanced
Reads `data\input` of the IL-2 folder: `global.actions` (required), `devices.txt`, `current.responses` (axis responses)
and JoyPro's own `jp.actions` (relation names and groups of an earlier export).

- `--merge` adds to the open profile (an action that fits an existing relation joins it); without it the import
  replaces the profile's relations and binds.
- New relations get the group `IL2` unless `jp.actions` names groups.
- `--device` / `--devices` limit the import to these devices; `--ignore-device` / `--ignore-devices` leave devices
  out (the `--vjoy-tunnel` device is always left out). A name that does not match exactly is resolved to the closest
  device; `--confirm false` takes it without asking.
- `--import-inverted`, `--import-curvature`, `--import-deadzone` bring that axis setting into the profile.
- Safety: reads only; refused when IL-2 is not installed; the profile is kept as the version "Before import from IL-2"
  first.
:::

:::layer technical
- Output: JSON of the import (relations and binds imported, merged into existing, skipped, warnings), or text with
  `--text`. Exit code `0`; `1` with `il2 import failed: ...`.
- `--save-profile <file>` saves the result to that file, `--save-last` to the last-profile file; with `--profile` the
  profile is saved after the import. A successful import marks IL-2 as imported for [[cli:profile game-state]].
- Related: [[cli:il2 scan]], [[cli:il2 export]], [[cli:db refresh-il2]], [[cli:sync pending]].
:::

<!-- entry: starcitizen scan -->
:::layer beginner
Shows what [[cli:starcitizen import]] would bring from Star Citizen with the same options, without changing the profile
or the game. On the Start page **Look first** in the import wizard and **Scan** in the expert import run it.

```powershell
engine starcitizen scan "D:\Games\StarCitizen\LIVE" --include-keyboard false --text
```
:::

:::layer advanced
- The path is a Star Citizen installation, branch or profile folder (positional or `--path`); without one the profile
  folder (else the installation) is detected. `--source <path>` names a folder or `actionmaps.xml` that is read first.
- `--device(s)`, `--ignore-device(s)`, `--include-keyboard` and `--confirm` work as in [[cli:starcitizen import]].
- Reads only. Without JoyPro's Star Citizen command list it is built from the installed game first. Refused when Star
  Citizen is not installed.
:::

:::layer technical
- Output: JSON of the scan, or a text report with `--text`. Exit code `0`; `1` with `starcitizen scan failed: ...`.
- Export options (`--mode`, `--keep-keyboard-defaults`, `--prefer-local-order`, `--nuke-untouched-devices`) and save
  options are refused here.
:::

<!-- entry: starcitizen import -->
:::layer beginner
Reads your Star Citizen ship binds from `actionmaps.xml` into your JoyPro profile. Nothing in the game changes. **Import
now** in the Start page's import wizard and **Import** in the expert import run it.

```powershell
engine starcitizen import "D:\Games\StarCitizen\LIVE" --merge --include-keyboard false --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file"
```
:::

:::layer advanced
Reads the profile folder's `actionmaps.xml` and JoyPro's `actionmaps.jp` (relation names of an earlier export).

- The game's default joystick binds that are active on js1 (no rebind of the action) come in as binds of the js1
  device, so the profile holds what the game does.
- Axis settings: each joystick axis rebind gets the values the game uses (its option group on its slot, the device
  options); different values become the relation's Star Citizen tuning.
- `--merge` adds to the open profile; without it the import replaces the profile's relations and binds.
- `--include-keyboard true|false` (default `false`) also reads keyboard binds.
- `--device(s)` / `--ignore-device(s)` limit the devices (the `--vjoy-tunnel` device is always left out); names are
  resolved to the closest device, `--confirm false` takes the suggestion without asking.
- `--source <path>` is read before the path.
- Safety: reads only; refused when Star Citizen is not installed; the profile is kept as the version "Before import
  from Star Citizen" first. Without a command list JoyPro builds it from the installed game first.
:::

:::layer technical
- Output: JSON of the import, or text with `--text` (paths, `actionmaps.xml`, metadata file, devices). Exit code `0`;
  `1` with `starcitizen import failed: ...`.
- `--save-profile <file>`, `--save-last`; with `--profile` the profile is saved after the import. A successful import
  marks Star Citizen as imported for [[cli:profile game-state]].
- Related: [[cli:starcitizen scan]], [[cli:starcitizen export]], [[cli:db refresh-starcitizen]].
:::

<!-- entry: starcitizen list-devices -->
:::layer beginner
Lists the devices Star Citizen's `actionmaps.xml` has joystick slots for, plus the keyboard and the gamepad. The GUI's
**Refresh Star Citizen** in the Start page's expert import runs it to fill the device choices.

```powershell
engine starcitizen list-devices "D:\Games\StarCitizen\LIVE" --text
```
:::

:::layer advanced
- Path, `--path` and `--source` as in [[cli:starcitizen scan]]; aliases `starcitizen devices` and `starcitizen list`.
- Each slot's product name is resolved to the device name JoyPro uses; `Keyboard` is always listed, the gamepad as its
  product or `Controller (Gamepad)`.
- Read-only; works also when Star Citizen is not detected as installed. Device filters are refused here.
:::

:::layer technical
- Output: JSON (`RootPath`, `SourcePath`, `Devices`, `Warnings`), or text with `--text`. Exit code `0`.
- Related: [[cli:devices list]], [[cli:starcitizen import]].
:::

<!-- entry: starcitizen labels -->
:::layer beginner
Reads Star Citizen's own names of its controls ("Pitch" for `v_pitch`) from the installed game, so JoyPro shows them
instead of a readable form of the id. [[cli:db refresh-starcitizen]] runs it after each refresh. Close the game first.

```powershell
engine starcitizen labels --text
```
:::

:::layer advanced
- It takes the text keys JoyPro's Star Citizen command list names (`AllBinds.xml`) and their English texts from
  `Data\Localization\english\global.ini` inside the game's `Data.p4k`. Only those keys are kept, in one line each,
  without the game's colour markup.
- `--path <install>`: the game folder (`Data.p4k` there, in `LIVE`, in `StarCitizen\LIVE` or in another subfolder);
  without it the detected installation is used.
- `--dry-run` reads everything and writes nothing.
- Safety: reads the game's files only, and only while Star Citizen is closed.
:::

:::layer technical
- Output: JSON (`Archive`, `File`, `Keys`, `Labels`, `Warnings`, `Error`), or with `--text` one line `Star Citizen
  labels: <n> of <m> text keys named (<archive>) -> <file>`.
- Exit code `0`; `1` with `JoyPro has no Star Citizen command list yet (engine db refresh-starcitizen).`, `Data.p4k of
  Star Citizen was not found ...`, `Star Citizen is running (...): close it first; JoyPro reads its files only while
  it is closed.`, `global.ini could not be read from ...`, or the usage line for another argument.
- Writes `%LOCALAPPDATA%\JoyPro2\GameData\DB\StarCitizen\labels.tsv` (`key<TAB>text`, with a header line that has the
  date); reading the archive uses `%LOCALAPPDATA%\JoyPro2\Cache\StarCitizen`.
- Related: [[cli:db refresh-starcitizen]], [[cli:starcitizen scan]], [[cli:starcitizen import]]; game page
  [[game:starcitizen]].
:::

<!-- entry: db summary -->
:::layer beginner
Checks JoyPro's DCS command list against its DCS clean profiles and prints the counts: aircraft lists, missing clean
profiles, name matches by alias. The GUI's **DB Summary** (Settings > Command lists (DB) and game paths) runs it.

```powershell
engine db summary --text
```
:::

:::layer advanced
- For every aircraft list `DB\DCS\<aircraft>.html` it looks for `CleanProfile\DCS\<aircraft>.cf`; a name that only
  matches after normalizing counts as an alias match. A few aircraft are not checked.
- `--keyboard` also checks `KeyboardCleanProfile\DCS` (off by default); `--no-keyboard` turns it off again.
- `--strict-names` is accepted (it changes the result of [[cli:db check]] only).
- A filter word (for example `F-16`) limits the check to aircraft whose name or files contain it.
- Read-only. The lists are read from the game-data folder, else the program folder ([[cli:db paths]]).
:::

:::layer technical
- Output: JSON of the whole check, or with `--text` the counts (`Modules`, `Missing Clean`, `Missing Keyboard Clean`,
  `Alias-Matched Clean`, ...) and errors.
- Errors in the result: no DCS command list yet (hint: [[cli:dcs refresh id]] `--headless-lua true`), no clean
  profiles yet (hint: [[cli:dcs refresh clean]] `--headless-lua true`). Exit code `0` anyway.
- Related: [[cli:db list]], [[cli:db check]].
:::

<!-- entry: db paths -->
:::layer beginner
Shows where JoyPro reads its lists from: the program folder for JoyPro's own data, and the game-data folder for
everything built from your games (command lists, aircraft lists, clean profiles), with the game version each list was
built from.

```powershell
engine db paths --text
```
:::

:::layer advanced
- No options. Read-only.
- The program folder's own data: `DB\Functions`, `DB\Devices`, `DB\Llm`, `DB\DevicePhotos`, `DB\DeviceLayouts`,
  `DB\DeviceDrawings`, `DB\Cockpits`, `DB\Detents` and `TOOLS`.
- Per game folder (`DB\<game>`, `CleanProfile\<game>`, `KeyboardCleanProfile\<game>`): whether it is read from the
  game-data folder or the program folder, and its recorded build (`version` or `steam-build` from
  `source-version.txt`).
- The game-data folder's copy wins whenever it exists. Refreshes write only there, so an update of JoyPro never
  replaces your lists.
:::

:::layer technical
- Output: JSON (`ProgramFolder`, `GameDataFolder`, `Folders` with `Folder`, `Source` (`game-data` or `program`),
  `Path`, `Build`), or text with `--text` (`DB Paths`, the two folders, one line per game folder). Exit code `0`; `1`
  with `Unknown db paths argument(s): ...`.
- Game-data folder: `%LOCALAPPDATA%\JoyPro2\GameData` (or `<JOYPRO_DATA_DIR>\GameData`).
- Related: [[cli:db check-updates]], [[cli:db summary]], [[cli:games installs]]; [[doc:installs]].
:::

<!-- entry: db list -->
:::layer beginner
Lists every DCS aircraft list with its clean profile state, one line per aircraft. The GUI's **DB List** (Settings >
Command lists (DB) and game paths) runs it with the **Plane Filter**.

```powershell
engine db list F-16 --missing --text
```
:::

:::layer advanced
- Text line per aircraft: `<name> | clean=ok|missing|skip|alias:<file> | keyboard_clean=... | db=<file>`;
  `keyboard_clean` is `n/a` without `--keyboard`.
- `--missing` shows only aircraft with something missing (text output only).
- `--keyboard`, `--no-keyboard`, `--strict-names` and the filter as in [[cli:db summary]]. Read-only.
:::

:::layer technical
- Output: JSON (the whole check, every row), or the list with `--text`. Exit code `0`.
- Related: [[cli:db summary]], [[cli:db check]], [[cli:dcs refresh clean]].
:::

<!-- entry: db check -->
:::layer beginner
The same check as [[cli:db summary]], but it fails when a DCS aircraft has no clean profile. Use it in scripts. The
GUI's **DB Check** (Settings > Command lists (DB) and game paths) runs it.

```powershell
engine db check --strict-names --text
```
:::

:::layer advanced
- Fails on errors of the check (no command list, no clean profiles) and on missing clean (and, with `--keyboard`,
  keyboard clean) profiles.
- `--strict-names` also fails when a clean profile was only found by alias.
- Filter, `--keyboard` and `--no-keyboard` as in [[cli:db summary]]. Read-only.
:::

:::layer technical
- Output: JSON, or with `--text` the summary plus the list of missing entries; then `db check ok` on success.
- Exit code `0`; `1` with `db check failed: <errors>`, `db check failed: missing clean profile files = <n>` or `db
  check failed: clean file name mismatches = <n> (alias-matched).`
- Related: [[cli:dcs refresh clean]], [[cli:dcs verify-clean]].
:::

<!-- entry: db games -->
:::layer beginner
Lists the games JoyPro has command lists for on this PC, with how many aircraft each has and where the list is. The
GUI's **DB Games** (Settings) and **List DB Games** (Start page) run it; the GUI also fills its game choices from it.

```powershell
engine db games --text
```
:::

:::layer advanced
- One row per game folder of `DB` (data folders such as `Functions` or `Devices` are left out); each folder is read
  from the game-data folder, else the program folder.
- A filter word keeps the games whose name or folder contains it.
- Read-only. The GUI leaves out games you hid.
:::

:::layer technical
- Output: JSON (`Games` with `Game`, `SourceFolder`, `DbPath`, `Planes`; `Errors`), or text lines
  `<game> | planes=<n> | folder=<folder> | db=<path>` with `--text`. Exit code `0`.
- Related: [[cli:db planes]], [[cli:db paths]], [[cli:db check-updates]].
:::

<!-- entry: db check-updates -->
:::layer beginner
Checks which installed games were updated since JoyPro built their command list, and which have no list yet. The Start
page shows the result as a notice with **Update** / **Keep DB** per game; **Check Again** and the notice's buttons run
it.

```powershell
engine db check-updates --text
```
:::

:::layer advanced
- Per game it compares the installed build (DCS version and module folders, Star Citizen's build, Steam build ids,
  ...) with `source-version.txt` in the game's command list folder, which every refresh writes.
- States: `up-to-date`, `game-updated` (with the changes and the refresh command), `no-catalog` (installed, no list
  yet), `baseline-recorded` (first check: the build is recorded), `no-baseline`, `not-installed`.
- `--accept <game>` (repeatable or comma-separated) marks that game's list as current (**Keep DB**); `--accept-all`
  marks all (**Keep All DBs, Hide Notice**).
- `--no-record` only reports: no build is recorded, not even a first one (then `no-baseline`).
- Writes only `source-version.txt` in the game-data folder; the command lists themselves do not change.
:::

:::layer technical
- Output: JSON (`Games` with `Game`, `DisplayName`, `Installed`, `InstalledVersion`, `DbVersion`, `State`, `Message`,
  `RefreshCommand`, `Details`; `Outdated`; `Warnings`), or text with `--text`. Exit code `0`.
- Related: [[cli:dcs refresh id]], [[cli:db refresh-il2]], [[cli:db refresh-starcitizen]], [[cli:hllv refresh-db]].
:::

<!-- entry: db planes -->
:::layer beginner
Lists the aircraft (or ships, vehicles) of each game's command list. The GUI's **DB Planes** (Settings) and **List DB
Planes** (Start page) run it, and the DCS import uses `db planes --game DCS` for its aircraft list when Saved Games
has none.

```powershell
engine db planes Hornet --game DCS --text
```
:::

:::layer advanced
- `--game <name>` keeps the games whose name or folder contains it (only valid with `db planes`).
- A filter word keeps the aircraft that contain it; games without a match are left out.
- Read-only.
:::

:::layer technical
- Output: JSON as [[cli:db games]] with the aircraft lists, or with `--text` `DB Planes: <n> (Games: <n>)` and one
  block per game. Exit code `0`; `1` when `--game` is used with another db action.
- Related: [[cli:db games]], [[cli:dcs import]].
:::

<!-- entry: db fingerprint -->
:::layer beginner
Prints one fingerprint of JoyPro's command lists of DCS, IL-2 Great Battles and Star Citizen. Two PCs (or two
versions) with the same fingerprint have the same lists. The GUI's **DB Fingerprint** (Settings) runs it.

```powershell
engine db fingerprint --text
```
:::

:::layer advanced
- SHA-256 over the files of those three lists (each read where JoyPro reads it, the game-data folder first), with
  paths relative to their folder, plus the version of the load correction's rules. These are exactly the inputs of
  the load correction.
- `source-version.txt` is not counted.
- File hashes are cached by size and time in `<data>\Cache\db-file-hashes.tsv`, so only new or changed files are read.
- Read-only.
:::

:::layer technical
- Output: JSON (`fingerprint`, `fileCount`, `totalBytes`, `includedRoots`, `warnings`), or with `--text`
  `DB Fingerprint:`, `Files:`, `Bytes:`, `Roots:` and `Warnings:`. Exit code `0`.
- Related: [[cli:db paths]], [[cli:profile load]].
:::

<!-- entry: db refresh-il2 -->
:::layer beginner
Adds the actions of IL-2 Great Battles' key mapping files that JoyPro's IL-2 command list lacks. Use it after an IL-2
update. **Update** on the IL-2 card of the Start page and **Refresh IL-2 DB** (Settings > Command lists of DCS, Star
Citizen and IL-2) run it.

```powershell
engine db refresh-il2 "D:\Games\IL-2 Sturmovik Battle of Stalingrad" --dry-run
```
:::

:::layer advanced
- Source: the IL-2 folder, its `data\input` folder or one `.actions` file (positional or `--path`); without one the
  installed game is used.
- It reads `global.actions` and the other `*.actions` (not JoyPro's `jp.actions`) and appends each missing action to
  `DB\IL2\IL2.actions` with the game's English description. Existing lines stay byte for byte, except a title the game
  left as an internal key, which gets JoyPro's own name (listed as renamed titles).
- `--dry-run` (bare, or `true|false`) reads and reports, writes nothing.
- `--db-path <folder>` writes the list into another folder instead of the game-data folder.
- Without a source path it is refused when IL-2 is not installed. `engine il2 refresh-db` is the same command.
:::

:::layer technical
- Output: JSON of the refresh (list file, actions added, renamed titles, warnings), or text with `--text`.
- Exit code `0`; `1` with `db refresh-il2 failed: <reason>`.
- After a real run into JoyPro's own list the IL-2 build is recorded for [[cli:db check-updates]].
- Related: [[cli:il2 import]], [[cli:db games]].
:::

<!-- entry: db refresh-starcitizen -->
:::layer beginner
Builds JoyPro's Star Citizen command list from the installed game: the ship actions, their option groups, the joystick
option tree and the game's default joystick binds. **Update** on the Star Citizen card of the Start page and **Refresh
Star Citizen DB** (Settings > Command lists of DCS, Star Citizen and IL-2) run it.

```powershell
engine db refresh-starcitizen --install-path "D:\Games\StarCitizen" --branch LIVE --dry-run true
```
:::

:::layer advanced
- Sources, best first: `--source <file or folder>` (for example an extracted `defaultProfile.xml`), the installation
  (`--install-path`, `--path` or the positional path; a positional file counts as the source), else every detected
  installation. `--include-user-profiles true|false` (default `true`) also takes the players' `actionmaps.xml` as
  candidates when no installation is named.
- `--branch <name>` (`LIVE`, `PTU`, ...) reads only that branch.
- `--dry-run true|false` (default `false`; a bare flag is `true`) reports without writing.
- `--db-path <folder>` writes elsewhere instead of `DB\StarCitizen` in the game-data folder.
- Refused when Star Citizen is not installed.
:::

:::layer technical
- Output: JSON of the refresh (source, branch, action count, files), or text with `--text`.
- Exit code `0`; `1` with `db refresh-starcitizen failed: <reason>`, for example `No Star Citizen action map source
  file found. Try --source <actionmaps.xml> or --install-path <StarCitizen root>.`
- Writes `AllBinds.xml` and `actionmaps.xml`. After a real run the build is recorded for [[cli:db check-updates]] and
  the English names of the controls are refreshed ([[cli:starcitizen labels]]; problems as `Note: ...` on stderr).
- Related: [[cli:starcitizen import]], [[cli:starcitizen export]].
:::

<!-- entry: games installs -->
:::layer beginner
Lists every game JoyPro supports: installed or not, where, how it was found, and where it keeps its controls. Import,
export, scan and refresh are refused for games that are not installed. The GUI builds the Settings > Games cards, the
Start page cards and the export tiles from it; **Detect again** (Settings) and **Look again** (Start page) run it.

```powershell
engine games installs --game DCS --text
```
:::

:::layer advanced
- `--game <id>` (or `--games`, several) limits the list.
- Per game: installed, installation folder, how it was found (Steam, a known place, the Windows apps list, your own
  folder), your config location, whether you hid it, its config paths, and how many actions JoyPro knows for it with
  the reason when there are none.
- Read-only. Your folders and hidden games come from `game-installs.json`, `game-configs.json` and
  `games-disabled.json` in `%LOCALAPPDATA%\JoyPro2` ([[doc:installs]]).
:::

:::layer technical
- Output: JSON (`Games`, `Warnings`), or text with `--text`. Exit code `0`; `1` on an unknown option.
- Related: [[cli:games install set]], [[cli:games install disable]], [[cli:games paths]].
:::

<!-- entry: games install disable -->
:::layer beginner
Marks games as ones you do not have: they count as not installed and the GUI hides them; `enable` shows them again.
Your binds for them stay in the profile. The hide button on a Start page card, **Hide** / **Show again** in Settings >
Games and the first-start setup run it.

```powershell
engine games install disable WarThunder X4
```
:::

:::layer advanced
- One or more game ids (separated by spaces or commas); `enable` takes the same list.
- A hidden game: import, export, scan and refresh are refused, it gets no export tile and the watcher does not watch
  it ([[cli:sync status]] shows `disabled`).
- Saved in `games-disabled.json` in `%LOCALAPPDATA%\JoyPro2`. No game file is touched.
:::

:::layer technical
- Output: JSON (`Games`: the changed entries, `Warnings`), or text with `--text`. Exit code `0`; `1` with
  `Unknown game: <name>. Known: ...` or without a game (the usage line).
- Related: [[cli:games installs]], [[cli:games install set]].
:::

<!-- entry: games install set -->
:::layer beginner
Tells JoyPro where a game is installed, for example a portable copy or a game on another drive that detection does not
find. The folder then counts as the game's installation. `clear` forgets it and detects the game again. Settings >
Games (**Choose folder...**, **Choose .exe...**, **Use detected**) and the first-start game setup run it.

```powershell
engine games install set DCS "D:\DCS World"
```
:::

:::layer advanced
- `set <game> <folder>`: the game is a game id or name (`DCS`, `il2`, `bms`, `Falcon BMS`, ...). The folder may also
  be the game's `.exe`, its launcher or a folder inside it: JoyPro takes the folder that holds the game's executable
  (also a `game` subfolder, or up to four levels up) and says so in a note.
- Detection never replaces your folder. If the executable is not in it, the game still counts as installed, with the
  note that the marker file is missing.
- `clear <game>` (or `unset`): forgets your folder.
- A game you hid ("I do not have it") stays hidden; show it with `engine games install enable <game>`
  ([[cli:games install disable]]).
- Changes only `game-installs.json` in `%LOCALAPPDATA%\JoyPro2`; no game file is touched.
:::

:::layer technical
- Output: the game's entry as in [[cli:games installs]] (JSON `Games`, `Warnings`; text with `--text`, notes as
  `Note: ...`). Exit code `0`.
- Exit code `1` with the usage line `engine games install set <game> <folder> | clear <game> | disable <game> [<game>
  ...] | enable <game> [<game> ...]`, `Unknown game: <x>. Known: ...` or `The folder does not exist: <folder>`.
- Related: [[cli:games config set]], [[cli:games installs]], [[cli:games paths]]; [[doc:installs]].
:::

<!-- entry: games config set -->
:::layer beginner
Tells JoyPro where a game keeps its controls, for example `Saved Games\DCS.openbeta` instead of `Saved Games\DCS`.
JoyPro then looks there first wherever it looks for the game's controls. `clear` goes back to what JoyPro finds. The
first-start game setup runs it.

```powershell
engine games config set DCS "C:\Users\Pilot\Saved Games\DCS.openbeta"
```
:::

:::layer advanced
- `set <game> <file or folder>`: the location must exist. It comes first wherever JoyPro looks for the game's
  controls; the found locations follow.
- Not for games that keep their controls inside the installation (IL-2 Great Battles, IL-2 Korea, IL-2 1946, Falcon
  BMS, X-Plane 11 and 12, VTOL VR): set their installation with [[cli:games install set]] instead.
- `clear <game>` (or `unset`): forgets your location.
- A location that no longer exists is ignored until it is there again.
- Changes only `game-configs.json` in `%LOCALAPPDATA%\JoyPro2`; no game file is touched.
:::

:::layer technical
- Output: the game's entry as in [[cli:games installs]] (JSON `Games`, `Warnings`; text with `--text`, the line
  `Config location (yours): <path>`). Exit code `0`.
- Exit code `1` with the usage line `engine games config set <game> <file or folder> | clear <game>`, `Unknown game:
  ...`, `The file or folder does not exist: <path>` or `<game> keeps its controls in its installation folder; set that
  instead: engine games install set <id> <folder>.`
- Related: [[cli:games paths]], [[cli:games installs]], [[cli:sync status]]; [[doc:installs]].
:::

<!-- entry: games paths -->
:::layer beginner
Shows where JoyPro finds each game's installation and config folders. Use it when an import or export says it has no
path. The GUI's **Game Paths** (Settings > Command lists (DB) and game paths) runs it.

```powershell
engine games paths --game DCS --text
```
:::

:::layer advanced
- `--game <name>` / `--games <csv>` limit the list.
- Per game: the preferred installation and config folder (the ones import and export use when no path is given) and
  every installation and config folder found.
- Read-only. `engine games` without an action is this command.
:::

:::layer technical
- Output: JSON of the catalog, or with `--text` per game `Preferred Install`, `Preferred Config`, `Install Paths`,
  `Config Paths` and the warnings. Exit code `0`.
- Related: [[cli:games installs]], [[cli:db paths]].
:::

<!-- entry: games integrations -->
:::layer beginner
Lists the other games JoyPro reads besides DCS, IL-2 Great Battles and Star Citizen, with their ids and aliases. The
GUI's **Load Games** in the Start page's expert import runs it, and the import wizard and the Export page use it.

```powershell
engine games integrations --text
```
:::

:::layer advanced
- No options. Read-only.
- Per game: id, name, aliases (the names the CLI accepts, for example `engine wt import`), its command list folder,
  whether it is experimental, notes, whether it has a command list refresh, named game profiles or an aircraft list,
  and the actions it offers (`paths`, `catalog`, `scan`, `import`, `export`, `refresh-db`).
:::

:::layer technical
- Output: JSON (`Integrations`), or with `--text` one line per game `<id> | <name> | aliases=... | experimental=... |
  refresh-db=...`. Exit code `0`.
- Related: [[cli:games installs]], [[cli:games support]].
:::

<!-- entry: games support -->
:::layer beginner
Prints how far JoyPro supports each game: the feature matrix taken from the code plus the verification notes. The
GUI's **Supported games** on the Start page writes it as an HTML page and opens it in your browser.

```powershell
engine games support --html --out "C:\Users\Pilot\Desktop\supported-games.html"
```
:::

:::layer advanced
- One format: `--markdown` (the page of docs/games/support.md), `--html` (one HTML file), `--json`; without one JSON,
  or text with `--text`.
- `--notes <file>` reads the verification notes from that JSON file instead of the copy inside engine.exe.
- A note may lower a cell, never claim more than the code does. Read-only.
:::

:::layer technical
- Exit code `0`; `1` when the notes miss a game or claim more than the code (each problem as `games support: ...` on
  stderr; the page is still written). `1` also for two formats at once (`Use one of --markdown, --html, --json.`) or
  `--notes` without a file.
- Related: [[cli:games integrations]].
:::

<!-- entry: games devices -->
:::layer beginner
Lists the connected game devices with their VID:PID, HID name and the axes, buttons and hats DirectInput reports. The
GUI's **Device Details** (Settings > Devices) runs it, and **Create Report** puts its list into the report's
diagnostics.

```powershell
engine games devices --text
```
:::

:::layer advanced
- No options. Read-only.
- Per device: the name JoyPro uses, its DirectInput index, VID:PID (`?` when unknown), the HID product string, the
  axes (as tokens such as `JOY_X`), button and hat counts, and the HID path; `capabilities unknown` when DirectInput
  did not report them.
:::

:::layer technical
- Output: JSON (`Devices`, `Warnings`), or text with `--text`. Exit code `0`; `1` on an extra argument.
- Related: [[cli:devices list]], [[cli:hud devices]].
:::

<!-- entry: sync status -->
:::layer beginner
Shows per game whether the game changed the config files (or registry values) JoyPro manages since JoyPro's last
export, import or accept. The watcher runs it after a game closed; the Start page and Settings > Games use it to show
what is watched ([[doc:sync-watcher]]).

```powershell
engine sync status --game DCS --text
```
:::

:::layer advanced
- `--game <id>` checks one game (DCS, IL2Game, StarCitizen or an id of [[cli:games integrations]]); `--path <config>`
  checks another config of it (needs `--game`).
- States: `unchanged`, `changed` (with the changed files and, for DCS, the aircraft), `untracked` (no export or import
  recorded yet: not watched), `missing` (no config found), `not-installed`, `disabled` (hidden game).
- A file with new content counts; a new write time with the same content does not. Configs JoyPro exported to elsewhere
  are listed too.
- Read-only. Also reports what the watcher needs: folders and file filters or the registry key, the game's processes
  and the settle time.
:::

:::layer technical
- Output: JSON (`DataFolder`, `Games` with `State`, `ConfigPath`, `Kind`, `Fingerprint`, `ChangedFiles`,
  `ChangedPlanes`, `Watch`, `ProcessNames`, `SettleSeconds`, ...), or text with `--text`. Records are in
  `%LOCALAPPDATA%\JoyPro2\Sync\<game>.json`.
- Exit code `0`; `1` with `Unknown game: ...`, `--path needs --game.` or an unknown option.
- Related: [[cli:sync diff]], [[cli:sync accept]], [[cli:sync pending]].
:::

<!-- entry: sync accept -->
:::layer beginner
Records the game's current config as known, without importing anything. The watcher then stops offering those changes,
and the next export writes your profile over them. The watcher dialog's **Ignore these** runs it; the GUI also runs it
by itself when a change touches nothing JoyPro manages.

```powershell
engine sync accept --game DCS
```
:::

:::layer advanced
- `--game <id>` is required; `--path <config>` names another config of the game.
- Stores a new fingerprint and baseline of the config in the sync record. The profile and the game do not change.
:::

:::layer technical
- Output: JSON (`Game`, `ConfigPath`, `State` `unchanged`, `Fingerprint`, `RecordedUtc`, `Baseline`, `Warnings`), or a
  text line with `--text`.
- Exit code `0`; `1` with `engine sync accept needs --game <id>.` or `No <game> config found: <path>`.
- Related: [[cli:sync status]], [[cli:sync apply]].
:::

<!-- entry: sync diff -->
:::layer beginner
Lists what the game changed in the binds and settings JoyPro manages, compared with your profile, with the relations
each change could go to. The watcher runs it to fill its dialog after a game closed.

```powershell
engine --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" sync diff --game DCS --text
```
:::

:::layer advanced
- `--game <id>` is required; `--path <config>` names another config. Pass `--profile`.
- Kinds: `added`, `removed`, `changed-tuning`, `changed-modifiers`, `changed-setting` (a game-wide setting the profile
  holds). Each change has an id, candidate relations and a suggested target (a relation, `new` or `ignore`).
- Only what JoyPro manages counts: devices the profile uses, actions JoyPro writes, values compared rounded as the game
  uses them, against the baseline of the last record.
- The config is read into a scratch copy; the loaded profile and the game do not change.
:::

:::layer technical
- Output: JSON (`Game`, `ConfigPath`, `Tracked`, `Baseline`, `Planes`, `ProfileBinds`, `FileBinds`, `Changes`,
  `Warnings`, `Errors`), or text with `--text`.
- Exit code `0`; `1` when the config cannot be read (`sync diff failed: ...` on stderr) or without `--game`.
- Change ids stay the same while the file and the profile do not change, so [[cli:sync apply]] can use them.
:::

<!-- entry: sync apply -->
:::layer beginner
Takes the game's changes into your profile, each into the relation you chose, and records the config as known. The
watcher dialog's **Take over** runs it with your choices.

```powershell
engine --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" sync apply --game DCS --choices "C:\Users\Pilot\Documents\choices.json"
```
:::

:::layer advanced
- `--choices <file.json>`: `{"Choices":[{"Id":"c1a2b3c4d5","Target":"Gear Toggle"},{"Id":"...","Target":"new",
  "Name":"My Relation"},{"Id":"...","Target":"ignore"}]}` (or the bare list). Ids come from [[cli:sync diff]].
- `Target`: a relation name, `new` (a new relation, `Name` optional) or `ignore`; for a `changed-setting` any target
  but `ignore` takes the game's value. Changes not listed count as ignored.
- Removals are applied first, so a moved bind is not undone by its old relation's removal.
- Afterwards the config counts as known: ignored changes are not offered again.
- With `--profile` the profile is saved afterwards (one undo step). The game does not change.
:::

:::layer technical
- Output: JSON (`Applied`, `Ignored`, `NewRelations`, `ChangedRelations`, `Fingerprint`, `Steps`, `Warnings`), or
  text with `--text`.
- Exit code `0`; `1` with `engine sync apply needs --choices <json file>.`, `Choices file not found: <path>`, a file
  that is no choices list, or `sync apply: the game's config could not be read: ...`. A change id that is no longer in
  the config is skipped with a warning.
- Related: [[cli:sync accept]], [[cli:sync pending]].
:::

<!-- entry: support bundle -->
:::layer beginner
Writes the support file: one zip with what is needed to find a problem. **Create Report** in Settings > Report a
problem runs it: first with `--list` to show what goes in, then to write the zip. Nothing is uploaded; you send the
zip yourself.

```powershell
engine --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" --out "C:\Users\Pilot\Desktop\JoyPro2-support.zip" support bundle --include-profile true
```
:::

:::layer advanced
- Always in it: `summary.txt` (versions, system, JoyPro's folders, per game the environment, the devices, every part
  with its size and what was trimmed), JoyPro's logs (at least the last 14 days, back to the open profile's oldest
  history version, at most 100 MB, the oldest trimmed first), crash reports and the last errors, JoyPro's settings and
  state files, the devices with the vJoy / HidHide state, and per game the watcher's record and JoyPro's backups.
- `--include-profile true|false` (default `true` when `--profile` is given): also the open profile, its history, the
  games' current bind files and JoyPro 1's profiles (`Documents\JoyPro`).
- `--days <n>`: logs of at least the last n days (default 14). `--history-cap-mb <n>`: most megabytes of history copies
  (default 50, newest first; `summary.txt` names the ones left out). `--game <id>`: only that game's part.
- `--list` only prints what the zip would hold; nothing is written.
- Every text is cleaned: the user folder becomes `%USERPROFILE%`, user and PC names, e-mail addresses and secrets are
  removed. The originals are only read.
:::

:::layer technical
- `--out <file.zip>` is the zip itself (required unless `--list`). The report of the parts goes to stdout as JSON
  (`Zip`, `ListOnly`, `Days`, `LogsFrom`, `IncludeProfile`, `Profile`, history counts, parts) or text with `--text`.
- Exit code `0`; `1` with the reason on stderr: `engine support bundle needs --out <zip> (or --list to see what it
  would hold).`, `--include-profile takes true or false, not ...`, `--history-cap-mb takes a number of megabytes, not
  ...` or an unknown option.
- Related: [[cli:games devices]], [[cli:backups list]].
:::

<!-- entry: support unpack -->
:::layer beginner
A helper for maintainers: unpacks a support file that someone sent, so its profile and profile history can be looked at
and restored in a separate folder. Nothing of the JoyPro on this PC is touched.

```powershell
engine support unpack "C:\Users\Pilot\Desktop\JoyPro2-support.zip" --to "C:\Users\Pilot\Desktop\case1" --text
```
:::

:::layer advanced
- `<file.zip>` (the support file of [[cli:support bundle]]) and `--to <folder>` are required.
- The whole zip goes to `<folder>\support`, the profile to `<folder>\profile`, its history to
  `<folder>\data\ProfileHistory\<id of that path>`, with the history pointed at the copy.
- Then, with the data folder set to the copy (`$env:JOYPRO_DATA_DIR = '<folder>\data'`), [[cli:profile history]],
  [[cli:profile history show]] and [[cli:profile history restore]] work on the copy; the text output prints these lines.
- Files of the same name in the target folder are overwritten. Entries that would land outside the folder stop the
  command.
:::

:::layer technical
- Output: JSON (`Support`, `Profile`, `DataFolder`, `HistoryFolder`, `HistoryFiles`), or text with `--text`
  (`Unpacked: ...`, `Profile: ...`, `History: ...`, the next commands; `This support file holds no profile.` when it
  has none). Exit code `0`.
- Exit code `1` on stderr: `Use: engine support unpack <support zip> --to <folder>.` or `Support file not found:
  <file>`.
- See [[doc:logging]] ("Restoring a version from a support file").
:::

<!-- entry: usage harvest -->
:::layer beginner
Reads what your games logged since the last time (logs, journals, logbooks, replays, Steam's playtime) into JoyPro's
usage store. JoyPro does this at start and after a game closed; **Read the games' logs now** in Settings > Usage
statistics runs it at once.

```powershell
engine usage harvest --game DCS --text
```
:::

:::layer advanced
- `--game <id>` (repeatable) reads only those games.
- `--full` reads every file again, also the known ones; a session read again replaces its record, never counts twice.
- `--dry-run` reads and reports, writes nothing.
- `--no-steam` leaves Steam's playtime out.
- Every game file is opened read-only with sharing, so a running game is not disturbed. Nothing leaves the PC
  ([[doc:usage-statistics]]).
:::

:::layer technical
- Output: JSON of the report (`FilesRead`, `SessionsNew`, per source what it gave, ...), or text with `--text`.
- Exit code `0`; `1` on an unknown option. The store is `%LOCALAPPDATA%\JoyPro2\Usage`.
- Related: [[cli:usage ranking]], [[cli:usage sources]], [[cli:usage stats]].
:::

<!-- entry: usage ranking -->
:::layer beginner
Lists your most used games and your most used aircraft, ships and vehicles, from the usage store. Settings > Usage
statistics shows it (top 15); Auto Assign, Auto Group, the Setup wizard and Lay out with AI use it as their style
reference.

```powershell
engine --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" usage ranking --top 10 --text
```
:::

:::layer advanced
- `--game <id>` keeps only that game; `--top <n>` the number of rows (default 25).
- Order of the units: pinned first, then units with logged time, then units the profile only binds (with `--profile`,
  by bind count), excluded ones last. Games: the higher of logged time and Steam's or the game's own totals.
- Pins and exclusions come from Settings (**Pin**, **Exclude**). Read-only.
:::

:::layer technical
- Output: JSON (`Folder`, `Games`, `Units`, `References` (the model per class), `Pinned`, `Excluded`), or text with
  `--text`. Exit code `0`; `1` on an unknown option.
- Related: [[cli:usage harvest]], [[cli:usage stats]].
:::

<!-- entry: usage sources -->
:::layer beginner
Lists where JoyPro reads how much you play each game: logs, logbooks, journals and Steam's playtime, whether each place
exists, and which logs the watcher follows live while a game runs. The GUI asks for it at start to follow those logs.

```powershell
engine usage sources --game DCS --text
```
:::

:::layer advanced
- `--game <id>` (repeatable) limits the list to those games; without it every game.
- Per source: the game (or "any game"), the kind, folder and file pattern, `(not there)` when the folder is missing,
  `[followed live]` for logs the watcher copies while the game runs, and a note.
- Read-only: it lists the places; [[cli:usage harvest]] reads them. Everything stays on this PC.
:::

:::layer technical
- Output: JSON (`Folder` (the usage store), `LiveFolder`, `Sources` with `Game`, `Kind`, `Title`, `Directory`,
  `Pattern`, `Recursive`, `LiveTail`, `Exists`, `Note`; `Tail` with the folder, filter and process names the watcher
  follows; `Steam` with Steam's config files), or text with `--text` (`Usage sources (store: <folder>)`, one line per
  source, `Steam playtime: <file>`).
- Exit code `0`; `1` with `Unknown usage sources argument(s): ...`.
- Related: [[cli:usage harvest]], [[cli:usage ranking]]; [[doc:usage-statistics]].
:::

<!-- entry: usage stats -->
:::layer beginner
Everything the Statistics page shows: hours per game and per aircraft, ship or vehicle, streaks, when you play, your
longest session, exports per game and, with a profile, your binds per device. `engine usage` without an action is the
same command.

```powershell
engine --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" usage stats --text
```
:::

:::layer advanced
- `--top <n>`: how many aircraft, ships and vehicles (default 50). Games are always all.
- Excluded games and units ([[cli:usage]]) are left out; units the profile only binds (no logged time) are not
  counted.
- With `--profile`: binds per device, how the profile grew (from its history; the counts are cached in the usage
  store's `Cache` folder) and the oldest bind whose aircraft or games have no recorded play.
- It reads the usage store that [[cli:usage harvest]] fills and changes neither the store's data nor the profile.
:::

:::layer technical
- Output: JSON (`Generated`, `HasData`, `Totals` (hours, sessions, games, units, first and last play, active days,
  streaks), `Games`, `Units`, `Counters`, `Weekdays`, `HoursOfDay`, `Months`, `LongestSession`, `Exports`, `Profile`,
  `Devices`, `OldestUnusedBind`, `Growth`, `Facts`, `Sources`), or text with `--text`.
- Exit code `0`; `1` with `Unknown usage stats argument(s): ...`.
- Store: `%LOCALAPPDATA%\JoyPro2\Usage`. Related: [[cli:usage ranking]], [[cli:usage sources]];
  [[doc:usage-statistics]].
:::

<!-- entry: usage -->
:::layer beginner
Your choices on the usage ranking: pin an aircraft, ship or vehicle, so it is the model for new aircraft of its class
(Auto Assign, Auto Group, the Setup wizard), or exclude one, so it ranks last and is left out of the statistics. The
**Pin** and **Exclude** buttons of Settings > Usage statistics run it.

```powershell
engine usage pin DCS F-16C_50 --text
```
:::

:::layer advanced
- `pin <game> [<unit>]`: pinned units rank first; pinning removes an exclusion.
- `unpin <game> [<unit>]`: removes the pin.
- `exclude <game> [<unit>]`: the unit ranks last and is not a model; excluding removes a pin.
- `include <game> [<unit>]`: removes the exclusion.
- Without `<unit>` the choice is about the whole game (a pinned game ranks first, an excluded one is left out of the
  statistics).
- The game is a game id or name (`DCS`, `il2`, ...) and must be a game JoyPro knows. The unit is the id the ranking
  shows (the part after `|` of `Id` in [[cli:usage ranking]], any case) and must be in the ranking (logged usage, or
  with `--profile` the profile's binds); a pin or exclusion already stored can always be taken back.
- Changes only `overrides.json` of the usage store (backed up once a day, [[cli:usage backups]]).
:::

:::layer technical
- Output: JSON (`Action`, `Id` (`<game>|<unit>`), `Pinned`, `Excluded`), or with `--text` `Pinned <id>`,
  `Unpinned <id>`, `Excluded <id>` or `Included <id>`. Exit code `0`.
- Exit code `1` with `usage <action> <game> [<unit>]` when the game is missing or there are more words, `Unknown game:
  <game> ...`, `No usage of '<unit>' in <game> is known ...`, or `Unknown usage action: ...`.
- Related: [[cli:usage ranking]], [[cli:usage stats]]; [[doc:usage-statistics]].
:::

<!-- entry: usage backups -->
:::layer beginner
Lists the daily backups of the usage store, or puts one back with `engine usage restore <day>`. Use it when the
statistics look wrong after a change.

```powershell
engine usage backups --text
```
:::

:::layer advanced
- `backups`: the days with a backup, newest first. A backup is made before the first change of a day (harvest, pin,
  exclude); the newest 7 days are kept.
- `restore <day>`: the day as listed (`yyyy-MM-dd`); anything else (a path, `..`) is refused. Before it writes, the current files go into today's backup when
  there is none yet, so a restore can be undone with today's backup. Files the backup does not have stay as they are.
- Only the usage store changes (sessions, totals, files read, exports, pins and exclusions); profiles and games do not.
:::

:::layer technical
- Output: JSON (`Folder`, `Days`; restore: `Restored`, `Sessions`), or text with `--text` (`Usage store backups (newest
  first):` and one day per line; `Restored the usage store of <day>.`). Exit code `0`.
- Exit code `1` with `usage restore <day> (engine usage backups lists them)` or `No usage backup of <day> (engine usage
  backups lists the days).`
- Backups: `%LOCALAPPDATA%\JoyPro2\Usage\Backups\<day>\`. Related: [[cli:usage harvest]], [[cli:usage]];
  [[doc:usage-statistics]].
:::

<!-- entry: telemetry games -->
:::layer beginner
Lists the games whose telemetry JoyPro can record (like a flight recorder for each round, mission or flight), what
JoyPro listens to for each one, whether a recording can become a Tacview file, and whether the game is recorded now.

```powershell
engine telemetry games --text
```
:::

:::layer advanced
- Ten games: DCS World, IL-2 Great Battles, IL-2 Series: Korea, War Thunder, X-Plane 12, X-Plane 11, Microsoft Flight
  Simulator 2024 and 2020, Falcon BMS and Elite Dangerous.
- Per game: the way JoyPro reads it (for example `UDP 41992 (JoyPro's DCS hook)`, `HTTP 127.0.0.1:8111 (the game's
  local web server)`, SimConnect through your own `SimConnect.dll`, BMS's shared memory, Elite's `Status.json` and
  journal), the game's processes, ACMI yes or no (IL-2 and Elite send no position, so no Tacview export), and what has
  to be set up once (DCS: JoyPro's hook; IL-2: JoyPro's address in `startup.cfg`, both on the Force Feedback page,
  [[cli:ffb telemetry setup]]).
- "Recorded" is true when recording is switched on and the game is not switched off for it
  ([[cli:telemetry config set]]).
- Read-only; no option except the global `--text` (and `--folder`, which changes nothing here).
:::

:::layer technical
- Output: JSON (`Enabled`, `Games` with `Id`, `Title`, `Transport`, `ProcessNames`, `Acmi`, `Setup`, `Recorded`), or
  with `--text` "Telemetry recording is on|off. Games with telemetry: <n>" and two lines per game (`[recorded]` behind
  the recorded ones).
- Exit code `0`; `1` with `Unknown telemetry games argument(s): ...`.
- Settings read from `%LOCALAPPDATA%\JoyPro2\Telemetry\telemetry-recording.json` (with `JOYPRO_DATA_DIR`:
  `<data>\Telemetry\`); a file that cannot be read means the defaults (recording off).
- Related: [[cli:telemetry config show]], [[cli:telemetry record]], [[cli:ffb telemetry status]]; reference
  [[doc:telemetry-recording]].
:::

<!-- entry: telemetry config show -->
:::layer beginner
Shows the telemetry recording settings: whether recording is on, where the sessions go, how much space they may take,
how long they are kept, the values per second and the per-game switches. It is what Settings > Telemetry recording
in the GUI edits.

```powershell
engine telemetry config show --text
```
:::

:::layer advanced
- The values, with their defaults: `enabled` false, `folder` "" (= `<data>\Telemetry\Recordings`), `maxTotalMb`
  4096 (0 = no size cap), `keepDays` 0 (= keep), `sampleHz` 20 (0 = every frame the game sends), `keepRaw` true,
  `sessionTimeoutSeconds` 60, and `games` with `enabled` per game (a game without an entry is recorded when the
  global switch is on).
- Values out of range are shown clamped (for example the session timeout to 5..3600 s), as the recorder uses them.
- Read-only. `engine telemetry config` without `show` or `set` shows too.
:::

:::layer technical
- Output: JSON (`ConfigPath`, `Folder` = the recordings folder in use, `Config`, `Warnings`), or with `--text` the
  settings as indented JSON.
- Exit code `0`; `1` with `Unknown telemetry config argument(s): ...`. A settings file that cannot be read gives the
  warning `The telemetry recording settings could not be read (...); the defaults apply.`
- File: `%LOCALAPPDATA%\JoyPro2\Telemetry\telemetry-recording.json` (`<data>` = `JOYPRO_DATA_DIR` when set).
- Related: [[cli:telemetry config set]], [[cli:telemetry games]]; reference [[doc:telemetry-recording]].
:::

<!-- entry: telemetry config set -->
:::layer beginner
Changes the telemetry recording settings, for example switches recording on. Recording is off until you do this (or
switch it on in Settings > Telemetry recording); while it is off nothing is recorded or polled.

```powershell
engine telemetry config set --param enabled=true --param folder="D:\Recordings" --param keep-days=30 --text
```
:::

:::layer advanced
- `--param key=value`, as often as needed (`_` in a key counts as `-`):

| Key | Value |
|---|---|
| `enabled` | `true` or `false`: the global switch |
| `folder` | the recordings folder, any drive; empty (`folder=`) = the default `<data>\Telemetry\Recordings` |
| `max-total-mb` | space for recordings in MB, 0 = no cap (at most 10000000) |
| `keep-days` | delete sessions older than this, 0 = keep (at most 100000) |
| `sample-hz` | frames per second written, 0 = every frame the game sends (at most 1000); events are always kept |
| `keep-raw` | `true` or `false`: also keep what the game sent unprocessed |
| `session-timeout` | seconds without telemetry that end a session (5..3600; a longer pause starts a new one) |

- `--game <game>` switches one game: then only `--param enabled=true|false` is allowed. The game is a recording game
  id or a short name (`il2`, `korea`, `wt`, `xp12`, `xp11`, `msfs`, `bms`, `elite`; see [[cli:telemetry games]]).
- Numbers are clamped to their range; the file is written once at the end, only when every parameter was valid.
- Safety: only the settings file changes; no recording is deleted here (the new space and age limits apply at the
  next session start, or now with `engine telemetry prune`, [[cli:telemetry recover]]). A running recorder of the GUI
  reads the file again when the GUI tells it to.
:::

:::layer technical
- Output: JSON (`ConfigPath`, `Changes`, `Warnings`, `Config`), or with `--text` "Telemetry recording settings:
  <file>" and one line per change.
- Exit code `0`; `1` (nothing written) with `telemetry config set needs --param key=value (...)`, `--param needs
  key=value: ...`, `<key> must be true or false.`, `<key> must be a number >= 0.`, `Unknown telemetry setting
  '<key>' (...)`, `With --game only enabled=true|false can be set.`, `JoyPro records no telemetry of '<game>'. See:
  engine telemetry games.` or `Unknown telemetry config argument(s): ...`.
- File: `%LOCALAPPDATA%\JoyPro2\Telemetry\telemetry-recording.json` (`<data>` = `JOYPRO_DATA_DIR` when set),
  written through a temporary file.
- Related: [[cli:telemetry config show]], [[cli:telemetry sessions]]; reference [[doc:telemetry-recording]].
:::

<!-- entry: telemetry sessions -->
:::layer beginner
Lists the recorded sessions, newest first: date, game, aircraft, duration and size. The number in front of each one
can be used as `<session>` in the other telemetry commands.

```powershell
engine telemetry sessions --game DCS --text
```
:::

:::layer advanced
- `--game <game>` shows the sessions of one game (a recording game id or short name, see [[cli:telemetry games]]).
- `--folder <folder>` lists another folder than the one of the settings (for example recordings copied from another
  PC).
- A session still being written is marked `[recording]`, one cut off by a crash `[unfinished]`
  ([[cli:telemetry recover]] finishes it).
- Read-only. `engine telemetry` alone, and `engine telemetry list`, do the same.
:::

:::layer technical
- Output: JSON (`Folder`, `TotalBytes`, `Sessions` with `Path`, `FileName`, `Game`, `GameTitle`, `Created`,
  `CreatedLocal`, `DurationSeconds`, `Duration`, `SizeBytes`, `Size`, `Aircraft`, `Maps`, `Frames`, `Events`,
  `RawPackets`, `Channels`, `Complete`, `Recording`, `EndReason`, `Acmi`; then `Problems`, `Warnings`), or with
  `--text` "Recorded sessions in <folder>: <n> (<size>)" and one numbered line per session.
- Exit code `0` (a file that cannot be read is listed as a problem); `1` with `Unknown telemetry sessions argument(s):
  ...`. A `--game` JoyPro does not record lists nothing.
- Files: `*.jptel` in the folder, named like `2026-10-18_14-03-22_DCS.jptel` (local start time and game).
- Related: [[cli:telemetry show]], [[cli:telemetry export]], [[cli:telemetry delete]]; reference
  [[doc:telemetry-recording]].
:::

<!-- entry: telemetry show -->
:::layer beginner
Shows one recorded session: game, aircraft, map, start, duration, size, what it holds (frames, events, raw data),
why it ended, whether a Tacview export is possible, and its channels by category.

```powershell
engine telemetry show latest --text
```
:::

:::layer advanced
- `<session>`: a path to a `.jptel` file, a file name in the recordings folder (with or without `.jptel`), `latest`,
  or a number of [[cli:telemetry sessions]] (1 = newest).
- `--folder <folder>` looks in another folder than the one of the settings.
- The whole file is read once to count the events by kind. Read-only.
:::

:::layer technical
- Output: JSON (`Session` as in the sessions list, `Header`, `Summary`, `Nav` = where the game's position and
  attitude are, `Channels` with `Key`, `Title`, `Unit`, `Category`, `Min`, `Max`, and `Events` = count per kind), or
  with `--text` a few lines ("ACMI export: yes" or "no (no position in this game's telemetry)", the channels grouped
  by category with their units, the event kinds by count).
- Exit code `0`; `1` with `Which session? A path, a file name, "latest" or a number from: engine telemetry sessions
  --text`, `No recorded session '<x>' in <folder>.` or `Unknown telemetry show argument(s): ...`.
- Related: [[cli:telemetry preview]], [[cli:telemetry export]]; reference [[doc:telemetry-recording]].
:::

<!-- entry: telemetry export -->
:::layer beginner
Turns a recorded session into a file for Tacview (ACMI 2.2) or into CSV tables for a spreadsheet. The GUI's **Export
for Tacview (ACMI)** and **Export CSV** in Settings > Telemetry recording do the same.

```powershell
engine telemetry export latest --format acmi-zip --to "C:\Users\Pilot\Documents\Tacview\last-flight.zip.acmi" --text
```
:::

:::layer advanced
- `<session>`: path, file name, `latest` or a number of [[cli:telemetry sessions]]; `--folder <folder>` looks in
  another folder.
- `--format` (default `acmi-zip`):
  - `acmi-zip`: a `.zip.acmi` (a zip with one `.txt.acmi`); `zip` and `zip.acmi` mean the same.
  - `acmi`: a plain `.txt.acmi`; `txt.acmi` and `text` mean the same.
  - `csv`: a folder with `<session>.csv` (time and every channel, one row per frame), `<session>-events.csv`,
    `<session>-channels.csv` (key, title, unit, category, range) and, unless `--no-per-channel`, one file per channel
    in `<session>-channels\` with time and value at every change.
- `--to <file or folder>`: where it goes (a file for ACMI, a folder for CSV). Default: `Exports\` beside the session,
  `<session>.zip.acmi`, `<session>.txt.acmi` or `<session>-csv\`. An existing file of that name is replaced.
- `--max-hz <n>`: at most this many frames a second (ACMI default 20; CSV default every frame, and it thins only the
  main table, not the per-channel files). A value that is not a number means the default.
- ACMI needs a position: sessions of IL-2 and Elite Dangerous (and any session without a frame with a position) can
  only go to CSV. War Thunder has no map coordinates, so it is placed on a flat world around 0/0.
- Safety: the session itself is never changed; the ACMI file is written to a temporary file first.
:::

:::layer technical
- Output: JSON (`Session`, `Format`, `Output`, `Files`, `Frames`, `Rows`, `Objects`, `Warnings`), or with `--text`
  "Exported <file> as <format>: <target> (<n> rows, <n> other objects)" and the warnings.
- Exit code `0`; `1` with `--format acmi|acmi-zip|csv`, `This recording has no position (<game> does not send one):
  ACMI needs one. Export CSV instead.`, `This recording has no frame with a position ...`, a session that cannot be
  found (as for [[cli:telemetry show]]) or `Unknown telemetry export argument(s): ...`.
- The global `--out` is the engine's output file as with every command; the export target is `--to`.
- Related: [[cli:telemetry show]], [[cli:telemetry preview]]; reference [[doc:telemetry-recording]].
:::

<!-- entry: telemetry preview -->
:::layer beginner
Reads a few channels of a recorded session reduced to a short series each, for a small timeline. Without a choice it
takes altitude, speed and G (or pitch, roll, throttle, angle of attack), as the timeline in Settings > Telemetry
recording does.

```powershell
engine telemetry preview latest --channel dcs.alt --channel dcs.ias --points 300 --text
```
:::

:::layer advanced
- `<session>`: path, file name, `latest` or a number of [[cli:telemetry sessions]]; `--folder <folder>` looks in
  another folder.
- `--channel <key>` (repeatable): the channels to read, by the keys [[cli:telemetry show]] lists. A key the session
  does not have is left out without a message. Without `--channel`: the first three channels with values among
  altitude, speed, G, pitch, roll, throttle and angle of attack, else the first channels of the session.
- `--points <n>` (default 200, 4..5000; not a number = 200): about this many points per channel. Each time bucket
  keeps its minimum and maximum, so peaks stay visible.
- Read-only.
:::

:::layer technical
- Output: JSON (`Session`, `Series` with `Key`, `Title`, `Unit`, `Min`, `Max`, `Times` in seconds from the session
  start, `Values`), or with `--text` one line per channel: "<key> (<title>, <unit>): <n> points, <min> .. <max>".
- Exit code `0`; `1` for a session that cannot be found (as for [[cli:telemetry show]]) or `Unknown telemetry preview
  argument(s): ...`.
- Related: [[cli:telemetry show]], [[cli:telemetry export]]; reference [[doc:telemetry-recording]].
:::

<!-- entry: telemetry delete -->
:::layer beginner
Deletes recorded sessions. The GUI's **Delete** in Settings > Telemetry recording does the same after asking; the
command does not ask.

```powershell
engine telemetry delete 3 4 --text
```
:::

:::layer advanced
- One or more `<session>` values: path, file name, `latest` or a number of [[cli:telemetry sessions]] (the numbers
  refer to the list before anything is deleted). `--folder <folder>` looks in another folder. `remove` is the same
  command.
- Every session is looked up first: when one cannot be found nothing is deleted.
- Safety: a session being recorded right now is never deleted (it is listed as not deleted). Deleted files are gone,
  not in the recycle bin.
:::

:::layer technical
- Output: JSON (`Deleted`, `Problems`), or with `--text` "Deleted <n> session(s)." with the files and a "not deleted:"
  line per problem (for example `<file> is being recorded right now.`).
- Exit code `0`; `1` when a session could not be deleted (the problems also go to the error output), without a
  session (`telemetry delete <session>... (a path, a file name, "latest" or a number)`), or when a session cannot be
  found (`No recorded session '<x>' in <folder>.`).
- Related: [[cli:telemetry sessions]], [[cli:telemetry recover]] (`prune` deletes by the retention settings).
:::

<!-- entry: telemetry recover -->
:::layer beginner
Finishes sessions that were cut off by a crash or a killed process, so they can be shown and exported again
(`engine telemetry recover`). `engine telemetry prune` applies the retention settings now: it deletes the sessions
that are too old or do not fit into the space for recordings.

```powershell
engine telemetry recover --text
```
:::

:::layer advanced
- `recover`: every `.jptel` in the folder without a summary that is not being written: the damaged tail is cut and
  the summary is written from the data. Complete sessions are left alone. The recorder does the same itself when it
  starts.
- `prune`: first the sessions older than `keep-days` (0 = no age limit), then the oldest until the folder is at most
  `max-total-mb` (0 = no size limit). The recorder does the same before and after every session.
- `--folder <folder>` works on another folder than the one of the settings (`prune` still uses the limits of the
  settings).
- Safety: the session being recorded is never touched by either. `prune` deletes files for good; set the limits with
  [[cli:telemetry config set]] and look at [[cli:telemetry sessions]] first.
:::

:::layer technical
- Output of `recover`: JSON (`Folder`, `Recovered`, `Problems`), or with `--text` "Finished <n> session(s) that were
  cut off." with the files and "problem:" lines.
- Output of `prune`: JSON (`Folder`, `Deleted`, `Problems`), or with `--text` "Deleted <n> session(s) (size cap
  <size or none>, keep days <n or no limit>)." with the files and "problem:" lines.
- Exit code `0`; `1` when a session could not be read, finished or deleted (the others are done; the problems are
  listed), or with `Unknown telemetry recover argument(s): ...` or `Unknown telemetry prune argument(s): ...`.
- Related: [[cli:telemetry delete]], [[cli:telemetry config show]]; reference [[doc:telemetry-recording]].
:::

<!-- entry: telemetry record -->
:::layer beginner
Records the telemetry of one game now, in this window, also when recording is switched off in the settings. Start the
game (or start this first and then the game); Ctrl+C ends the recording, and it also ends when the game
exits.

```powershell
engine telemetry record --game DCS --seconds 600 --text
```
:::

:::layer advanced
- `--game <game>` (required): a recording game id or short name ([[cli:telemetry games]]). DCS and IL-2 need their
  one-time setup first ([[cli:ffb telemetry setup]]).
- `--seconds <n>`: stops after that time; without it, it runs until Ctrl+C.
- `--folder <folder>`: writes the session there instead of the folder of the settings.
- `--sample-hz <n>`: frames per second written (0 = every frame the game sends), instead of the setting.
- `--no-raw`: does not keep what the game sent unprocessed.
- `--no-game-check`: listens without the game's process. Without it, it waits for the game ("waiting for DCS.exe /
  DCS_mt.exe") and stops when the game exits.
- It runs JoyPro's force feedback process (`JoyPro.Ffb.exe record --force`), which also does the GUI's automatic
  recordings. Only one process can listen on a game's telemetry port: do not run it while the GUI records or runs
  force feedback for the same game.
- Safety: only listens locally and writes the session file; nothing is sent anywhere and the game is not changed.
:::

:::layer technical
- Output: a status line "Recording <game> telemetry (Ctrl+C ends it): JoyPro.Ffb record ..." and then the recorder's
  state lines (waiting, listening, recording with the session file and its frames, and the reason it ended); the
  exit code is the recorder's.
- Exit code `1` without a known `--game` (the usage line lists the ids), with `Unknown telemetry record argument(s):
  ...`, or with `JoyPro.Ffb.exe not found (<path>); it reads the games' telemetry. Set JOYPRO_FFB_EXE to its path.`
  (it is looked for in `ffb\` beside the engine's folder, or at `JOYPRO_FFB_EXE`).
- The session goes to `<folder>\<date>_<time>_<game>.jptel`; afterwards [[cli:telemetry sessions]] lists it.
- Related: [[cli:telemetry show]], [[cli:telemetry export]], [[cli:ffb telemetry status]]; reference
  [[doc:telemetry-recording]].
:::

<!-- entry: vtolvr refresh-db -->
:::layer beginner
Builds JoyPro's VTOL VR command list from the installed BYOJoystick mod and your own `BYOJ_<seat>.xml` files. **Check
again** in the VTOL VR panel of the Start page runs it once the mod is installed, and **Update** on the VTOL VR card
runs it too.

```powershell
engine vtolvr refresh-db --dry-run
```
:::

:::layer advanced
- Reads the mod's `BYOJoystick.dll` read only (the DLL is not loaded or run): each seat's targets with category and
  input type. New targets come in; known ones keep their title and category and gain the seats they lack.
- Then the files `BYOJ_<seat>.xml` of the game's `SaveData` folder are read on top; files of unknown seats are skipped
  with a warning.
- Path (positional or `--path`): `BYOJoystick.dll` or its folder, the `SaveData` folder, a file in it or the VTOL VR
  folder.
- `--dry-run` writes nothing. The list is written only when it changed.
- Refused when VTOL VR is not installed.
:::

:::layer technical
- Output: JSON of the refresh (steps such as `Actions: <old> -> <new>`, files written, warnings, errors), or text with
  `--text`.
- Exit code `0`; `1` when nothing could be read, for example "Neither the BYOJoystick mod nor a VTOL VR SaveData folder
  was found. ..." (the list stays unchanged).
- After a real run the build is recorded for [[cli:db check-updates]].
- Related: [[cli:vtolvr mod]], [[cli:vtolvr import]], [[cli:vtolvr-native refresh-db]].
:::

<!-- entry: vtolvr mod -->
:::layer beginner
Shows whether the BYOJoystick mod for VTOL VR is on this PC (`status`), or prepares its install from the Steam Workshop
(`prepare`). In the VTOL VR panel of the Start page, **Install BYOJoystick** runs `prepare` after your tick and **Check
again** runs `status`. JoyPro ships no part of the mod; Steam downloads it.

```powershell
engine vtolvr mod status --text
```
:::

:::layer advanced
- `status` (default): Mod Loader installed, mod installed, its folder, `BYOJoystick.dll` with SHA-256, size and file
  version, and how many targets JoyPro reads from it.
- `prepare --accept-license`: the flag is the GUI's tick. It backs up your `SaveData\BYOJ_*.xml` files into the game
  backup store ([[cli:backups list]] `--game VTOLVR`) and returns the Steam addresses to open: the Mod Loader when it
  is missing, the Workshop item 3276352374 when the mod is missing. JoyPro writes no mod file.
- Not refused when VTOL VR is not detected.
:::

:::layer technical
- Output: JSON (`ModLoaderInstalled`, `ModInstalled`, `ModFolder`, `DllPath`, `DllSha256`, `DllSize`, `DllVersion`,
  `Targets`, the Workshop and source pages, `OpenUrls` and `BackedUp` after prepare), or text with `--text`.
- Exit code `0`; `1` for an unknown verb or `prepare` without `--accept-license` (the reason in the JSON `Errors`).
- Related: [[cli:vtolvr refresh-db]], [[cli:vtolvr export]].
:::

<!-- entry: hllv oodle -->
:::layer beginner
Reports how JoyPro can read Hell Let Loose: Vietnam's packed files: whether the game has its own Oodle library, or
which other installed game has one. Before a command list refresh of the game, the GUI runs it and asks you once
whether JoyPro may use the other game's library.

```powershell
engine hllv oodle --text
```
:::

:::layer advanced
- `--path <game folder>` (or a positional path) looks there instead of the detected installation.
- Reports the game's container file, the game's own library, and when it has none, another installed game's library
  with that game's name.
- With your yes the GUI passes that library to [[cli:hllv refresh-db]] as `--oodle-dll <path>`; it is used where it
  is, never copied. Without it JoyPro uses its own list of the gamepad keys. **Ask again** in Settings > Games forgets
  the answer.
- Read-only; not refused when the game is not detected.
:::

:::layer technical
- Output: JSON (`Container`, `OwnDll`, `ForeignDll`, `ForeignGame`, `Maker`, `Warnings`), or with `--text` the lines
  `Container:`, `Own library:`, `Another game's library:`. Exit code `0`.
- Related: [[cli:hllv refresh-db]].
:::

<!-- entry: overlay dcs-hook -->
:::layer beginner
Installs, removes or checks the DCS hook: a small script that tells JoyPro which aircraft you fly, for the HUD and
overlay. On the HUD page **Install DCS hook**, **Remove** and **Check** run it. Close DCS before installing or removing.

```powershell
engine overlay dcs-hook install --path "C:\Users\Pilot\Saved Games\DCS"
```
:::

:::layer advanced
- `status` (default): installed when `Scripts\JoyProOverlay.lua` exists and `Scripts\Export.lua` has JoyPro's line.
- `install`: writes `Scripts\JoyProOverlay.lua` and adds one line to `Export.lua` (creates it when missing). The line
  is wrapped in `pcall`, so a missing hook never breaks `Export.lua`; the hook keeps and calls the export functions of
  other scripts. In a mission DCS sends the aircraft over UDP to 127.0.0.1:1992 (again every 5 s) and `exit` at the end.
- `remove`: takes JoyPro's line out of `Export.lua` and deletes the hook file.
- `--path <Saved Games\DCS>`; without it the Saved Games folder is detected. `--dry-run` lists the changes only.
- Safety: install and remove are refused while DCS runs; both files go into a game backup set first (undo:
  [[cli:backups restore]]).
:::

:::layer technical
- Output: JSON (`Action`, `DryRun`, `ExportLua`, `HookFile`, `Installed`, `Changes`, `Warnings`, `Errors`), or text
  with `--text`.
- Exit code `0`; `1` with `No DCS Saved Games folder found; use --path <Saved Games\DCS>.`, `Close DCS first: it reads
  Export.lua when a mission starts.`, `DCS Saved Games folder not found: <path>` or an unknown verb.
- Related: [[cli:overlay map]], [[cli:hud devices]].
:::

<!-- entry: overlay map -->
:::layer beginner
Lists the bound buttons of your profile with their modifiers and the game's titles, for the overlay that shows what a
pressed button does. The HUD runs it when it starts, when the running game changes, when the DCS hook reports an
aircraft and when you pick an aircraft in its list.

```powershell
engine overlay map --game DCS --plane F-16C_50 --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" --text
```
:::

:::layer advanced
- Only button relations with a bind (device and button) are listed; axes are not.
- `--game <id>`: only relations with an action of that game (`DCS`, `IL2Game` or `il2`, `StarCitizen` or `sc`, or a
  game of [[cli:games integrations]]); an unknown game gives a warning. Without it every bound button is listed,
  without titles.
- `--plane <name>`: DCS only, the relations active for that aircraft, with the titles of its command list; it implies
  `--game DCS`.
- `--unit <DCS unit type>`: the unit type the DCS hook reports (`A-10C_2`), turned into the profile's aircraft name:
  the same name, else an aircraft of the same module (front seat first), else a name that starts alike. Used only
  without `--plane`.
- Titles come from DCS's command list of the aircraft, or the command list of a registry game; IL-2 and Star Citizen
  show the relation names only. A title equal to the relation name is left out.
- Reads the profile only; nothing is written.
:::

:::layer technical
- Output: JSON (`Game`, `Plane`, `DcsPlanes` = the profile's DCS aircraft, `ProcessNames` per game, which the HUD uses
  to see the running game, `Entries` with `Relation`, `Title`, `Device`, `Input`, `Modifiers` with name, device,
  input and switch, `Warnings`), or text with `--text` (`<device> <input> + <modifiers>: <relation> (<title>)`, then
  `Warning: ...` lines).
- Exit code `0`; `1` with `Unknown overlay map argument(s): ...` or `engine overlay map|dcs-hook` for another verb.
- Related: [[cli:overlay dcs-hook]], [[cli:hud devices]]; reference [[doc:hud]].
:::

<!-- entry: media -->
:::layer beginner
Lists the pictures JoyPro shows for games and DCS aircraft: a cover per game and each DCS module's icon and menu
picture. The GUI asks for it once per session in the background.

```powershell
engine media games --text
```
:::

:::layer advanced
- `games`: per game JoyPro knows, the first picture that works: the cover Steam keeps on this PC for a game of your
  library, else a cover downloaded once from the game's web source (Steam's store, or the publisher's site), else the
  icon of the game's own executable (Star Citizen, Falcon BMS, DCS). A game without any gets JoyPro's badge in the GUI.
- `aircraft`: per DCS aircraft, the icon and main menu picture its module ships (`Skins\1\icon.png`,
  `Skins\1\ME\BackGround-*.png`, found through the module's `entry.lua`); seat and game-mode variants use their base
  aircraft.
- `sources`: the web source of every game's cover (address, kind, page it was taken from); nothing is downloaded.
- Without an argument: games and aircraft.
- `--no-internet-pictures` neither downloads a cover nor uses one downloaded before (GUI: "Game covers from the
  internet" off in Settings > Privacy). A failed download is tried again a day later, not on every run.
:::

:::layer technical
- Output: JSON (`Games` with `GameId`, `Name`, `Cover`, `CoverSource` `steam-cache`/`download`/`exe-icon`,
  `CoverKind` `cover`/`logo`, `CoverUrl`, `SteamAppIds`; `Aircraft` with `Plane`, `Module`, `DisplayName`, `Icon`,
  `Background`; `Warnings`), or text with `--text` (one line per game and aircraft, then `Warning: ...` lines).
- Exit code `0`; `1` with the usage line `engine media [games|aircraft|sources] [--no-internet-pictures]`.
- Downloaded covers are kept in `%LOCALAPPDATA%\JoyPro2\Media\Covers\<GameId>.<ext>` (with a `.source` file, and a
  `.miss` file after a failed download); executable icons in `%LOCALAPPDATA%\JoyPro2\Media\<GameId>.png`.
- Related: [[cli:games installs]], [[cli:overlay map]]; privacy: [[doc:privacy]].
:::

<!-- entry: hud devices -->
:::layer beginner
Lists the connected devices with everything the HUD draws: the product picture address, axes with their role, buttons,
hats and the layout picture with the button positions. **Reload devices** on the HUD page and **Look again** in
Settings > Devices run it.

```powershell
engine hud devices --grip "VPforce Rhino FFB {B4280440-0000-0000-0000-504944564944}=vkb-gunfighter-mcg" --text
```
:::

:::layer advanced
- Per device: what DirectInput reports, the catalog model (picture address, kind, hand, the meaning of each axis and
  button) and the layout picture: the one the profile assigned to the device, else the best template JoyPro has.
- `--grip <device>=<catalog id>` (repeatable) names the grip on a base that takes several; such a base lists its grip
  choices. The GUI passes the grips set in the HUD designer or on the Layout page.
- `--no-device-photos` leaves the makers' photo addresses out (the GUI passes it when Settings > Privacy says
  **Never**).
- Read-only. The engine downloads no picture; the GUI does, per device, after your yes.
:::

:::layer technical
- Output: JSON of the devices, or text with `--text`. Exit code `0`; `1` with `--grip needs <device>=<catalog id>.`
  or an unknown option.
- Related: [[cli:games devices]], [[cli:devices list]], [[cli:overlay dcs-hook]].
:::
