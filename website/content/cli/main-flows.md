Hand-written texts of the engine commands of the four main flows (import, relations, export, check). Each block below
belongs to the command named in its marker; build.py puts it on the command's entry of the CLI reference.

<!-- entry: dcs export -->
:::layer beginner
Writes your profile's binds into DCS, so the game's controls menu shows them. Use it after you changed binds in JoyPro
and want to fly with them; the GUI's **Export now** on the DCS tile runs it. Close DCS first.

```powershell
engine dcs export --mode clean --nuke-untouched-devices true --planes "F-16C_50|FA-18C_hornet" --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" --text
```
:::

:::layer advanced
For each device and aircraft it computes the binds DCS has active (the device's defaults minus the file's removals,
plus its additions and changes), decides which shall be active by the mode, and writes only real differences from
DCS's defaults into `<aircraft>\joystick\<device>.diff.lua`, with JoyPro's `.jp` beside it and the aircraft's
`modifiers.lua`.

| `--mode` | `--nuke-untouched-devices` | In DCS, in the exported aircraft |
|---|---|---|
| `clean` | `true` (GUI default) | your devices hold exactly your binds; connected devices without binds are emptied (defaults too) |
| `clean` | `false` | your devices hold exactly your binds; other devices unchanged |
| `merge-overwrite` (engine default) | `true` / `false` | your binds win on their inputs, each bound action loses its other inputs; other binds stay; nuke as above |
| `merge` | `true` / `false` | your binds win on their inputs; the game's other binds and defaults stay (a moved action keeps its old input) |
| `add` | (not used) | nothing removed; a bind on an input DCS uses for another function is not written and reported (`SkippedTakenInputs`) |

- `--clean-all-aircraft true` (default `false`) also clears the written devices in every other installed aircraft (not
  UiLayer or Camera).
- Keyboard: never cleared or nuked. `--keep-keyboard-defaults false` writes the profile's keyboard binds and removes
  only the DCS default keys they take; `--clear-keyboard-defaults true` removes every DCS keyboard default of those
  aircraft.
- `--plane` / `--planes` limit the export to these aircraft (several names separated by `|`, `,` or `;`).
- Never touched: devices passed with `--ignore-device(s)` and the `--vjoy-tunnel` device are never cleared.
- Safety: refused while DCS runs; every file is backed up first (undo: [[cli:backups restore]]) and the profile is kept
  as the version "Before export to DCS".
:::

:::layer technical
- Output: JSON of the export result, or a text report with `--text` (written to `--out` when given); exit code `0`, `1`
  when the export reports an error (`dcs export failed: ...` on stderr) or DCS runs.
- Without a path it uses the detected Saved Games folder (`Auto-detected DCS Saved Games path: ...` on stderr); none
  found fails with a hint to [[cli:games paths]].
- Import options (`--device`, `--include-defaults`, `--confirm` ...) are refused here.
- After a successful run it records the export for [[cli:sync pending]] and stores the binds an "Only add" run could not
  write as export marks ([[cli:export-marks list]]); the profile is saved quietly when those marks changed.
- Related: [[cli:dcs import]], [[cli:dcs verify-clean]], [[cli:profile game-state]], [[cli:backups list]].
:::

<!-- entry: dcs import -->
:::layer beginner
Reads the binds you already have in DCS into your JoyPro profile, one relation per function and button. Use it once
at the start, or after you changed binds inside DCS; nothing in DCS changes. The GUI's **Import** on the Start page
runs it.

```powershell
engine dcs import "C:\Users\Pilot\Saved Games\DCS" --merge --include-defaults --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file"
```
:::

:::layer advanced
Reads `Saved Games\DCS\Config\Input` (or `--path`) into the profile: one relation per function and input, modifiers
from `modifiers.lua`, tuning from the files, names and groups from JoyPro's `.jp` files.

- `--merge` adds to the open profile instead of replacing it.
- `--include-defaults` brings every default bind DCS has active for an imported device (so a clean + nuke export
  afterwards leaves DCS as it was).
- `--plane(s)`, `--device(s)` limit what is read; `--ignore-device(s)` leaves devices out (the `--vjoy-tunnel` device
  is always left out). A device name that does not match exactly is resolved to the closest one; `--confirm false`
  takes the suggestion without asking.
- `--include-keyboard` (default `true`) reads the keyboard too; `--ignore-easy-planes true` skips the `_easy`
  variants.
- `--import-inverted`, `--import-slider`, `--import-curvature`, `--import-deadzone`, `--import-saturation-x`,
  `--import-saturation-y` bring that axis setting into the profile.
- `--only-connected true` skips devices that are not plugged in; `--merge-same-name` (default `true`) reads one
  device under several Windows IDs as one.
- Safety: reads only; the profile is kept as the version "Before import from DCS" first.
:::

:::layer technical
- Output: JSON of the import result, or a text report with `--text` (paths, matched aircraft and devices, files and
  entries parsed); exit code `0`, `1` on an error (`dcs import failed: ...`).
- `--save-profile <file>` saves the result to that profile file, `--save-last` to the last used one; with `--profile`
  the profile is saved after the import.
- Export options (`--mode`, `--nuke-untouched-devices` ...) are refused here. A successful import marks DCS as
  imported for [[cli:profile game-state]].
- Files read: `Config\Input\<aircraft>\joystick|keyboard\*.diff.lua`, the `.jp` files beside them, `modifiers.lua`.
- Related: [[cli:dcs scan]] (the same without changing the profile), [[cli:dcs export]], [[cli:sync pending]].
:::

<!-- entry: dcs scan -->
:::layer beginner
Shows what an import from DCS would bring, without changing your profile or DCS. Use it to look before you import,
for example to see which aircraft and devices JoyPro finds. The Start page's **Look first** / **Scan** runs it.

```powershell
engine dcs scan "C:\Users\Pilot\Saved Games\DCS" --only-connected true --text
```
:::

:::layer advanced
Takes the same filters and settings as [[cli:dcs import]] (`--plane(s)`, `--device(s)`, `--ignore-device(s)`,
`--include-defaults`, `--include-keyboard`, `--ignore-easy-planes`, the `--import-*` axis settings, `--only-connected`,
`--merge-same-name`, `--confirm`) and reports what they would match. Nothing is written: no profile change, no file in
DCS, no backup.
:::

:::layer technical
- Output: the same result as the import (JSON, or a text report with `--text`); exit code `0`, `1` on an error
  (`dcs scan failed: ...`).
- `--save-profile` and `--save-last` are import options and refused here, as are the export options (`--mode` ...).
- Related: [[cli:dcs import]], [[cli:games paths]].
:::

<!-- entry: il2 export -->
:::layer beginner
Writes your profile's binds into IL-2 Great Battles. Use it after you changed binds in JoyPro; close IL-2 first. The
Export page's **Export now** on the IL-2 tile runs it.

```powershell
engine il2 export "D:\Games\IL-2 Sturmovik Battle of Stalingrad" --mode clean --nuke-untouched-devices true --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" --text
```
:::

:::layer advanced
Writes the profile into IL-2's `data\input` files (`global.actions` keeps its order, new lines at the end; the axis
responses) and JoyPro's `jp.actions`.

- `--mode`: `add` never removes and skips a bind whose input the game uses for another action; `merge` also removes the
  game's bind on an input the profile gives another action; `merge-overwrite` also takes the profile's actions off
  their other inputs; `clean` releases the other binds of the written devices.
- `--nuke-untouched-devices true` also clears the connected joysticks the profile does not use.
- Never touched: mouse lines, lines JoyPro cannot read, devices that are not connected, devices passed with
  `--ignore-device(s)` and the `--vjoy-tunnel` device.
- Safety: refused while IL-2 runs; every file is backed up first (undo: [[cli:backups list|engine backups restore]]);
  the profile is kept as the version "Before export to IL-2".
:::

:::layer technical
- Output: JSON of the export result, or text with `--text`; exit code `0`, `1` on an error or while the game runs.
- `jp.actions` holds JoyPro's names, groups and tuning for a later [[cli:il2 import]].
- Related: [[cli:il2 import]], [[cli:il2 scan]], [[cli:db refresh-il2]], [[cli:export-marks list]].
:::

<!-- entry: starcitizen export -->
:::layer beginner
Writes your profile's ship binds into Star Citizen. Use it after you changed binds in JoyPro; close the game first. The
Export page's **Export now** on the Star Citizen tile runs it.

```powershell
engine starcitizen export "C:\Program Files\Roberts Space Industries\StarCitizen\LIVE" --mode clean --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" --text
```
:::

:::layer advanced
Writes the ship actions into `actionmaps.xml` of the branch (LIVE, PTU ...) with JoyPro's `actionmaps.jp`.

- `--mode`: `add` never changes a game bind and skips a bind whose input another ship action uses; `merge` and
  `merge-overwrite` put the profile's input into the action's slot and release the game's bind on an input the profile
  gives another ship action; `clean` releases the ship binds of the written devices.
- `--nuke-untouched-devices true` releases those of the connected devices the profile does not use.
- Clean and nuke release only cataloged ship actions, and switch off the game's own joystick defaults on js1 when js1
  is written (clean) or unused (nuke) and connected.
- Stale duplicate joystick slots of a connected device are removed and their binds moved to the slot it keeps.
- Never touched: on-foot, vehicle and menu binds stay byte for byte; devices passed with `--ignore-device(s)` and the
  `--vjoy-tunnel` device are never cleared.
- Safety: refused while Star Citizen runs; backed up first; the profile is kept as the version "Before export to Star
  Citizen".
:::

:::layer technical
- Output: JSON of the export result, or text with `--text`; exit code `0`, `1` on an error or while the game runs.
- `--path` / `--source` name the branch folder or the profile file; `--keep-keyboard-defaults`, `--prefer-local-order`
  and `--include-keyboard` take `true|false`.
- Related: [[cli:starcitizen import]], [[cli:starcitizen list-devices]], [[cli:db refresh-starcitizen]],
  [[cli:backups list]].
:::

<!-- entry: profile validate -->
:::layer beginner
Checks your profile for problems: one button that does two things, two actions of one aircraft in one relation,
unknown commands, modifier problems. It changes nothing. The Check's **Check now** runs it.

```powershell
engine profile validate --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" --text
```
:::

:::layer advanced
- Errors: one input in two relations with an action of the same aircraft on (one button does two things), two actions
  of one aircraft on in one relation, unknown commands.
- Warnings: modifier problems and other findings that do not count as errors.
- `--fail-on-warnings true` (default `false`) makes warnings fail the exit code too (for scripts).
- The GUI's Check builds its cards from this and the detailed reports of [[cli:relations conflicts]],
  [[cli:relations duplicates]] and [[cli:export-marks list]].
:::

:::layer technical
- Output: JSON with the errors and warnings, or text with `--text`.
- Exit code `1` with `profile validation failed: errors = <n>.` when there are errors, or with
  `profile validation failed: warnings = <n> (--fail-on-warnings true).`; else `0`.
- Read-only; another argument fails with `Unknown profile validate argument(s)`.
:::

<!-- entry: relations conflicts -->
:::layer beginner
Lists the Check's two errors in detail and fixes them: one button that does two things in the same aircraft, or one
relation with two actions of one aircraft. The Check's fix buttons run it.

```powershell
engine relations conflicts --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" --text
```
:::

:::layer advanced
- `InputConflicts`: one input, several relations, same aircraft. `ActionConflicts`: one relation, two actions of one
  aircraft.
- A fix names its conflicts with `--keys "k;k"` (the `Key` of each; for `--remove-actions` `game|id|aircraft`) and
  takes exactly one of:
  - `--keep <relation>`: the input stays only in that relation, the aircraft is switched off in the others;
  - `--keep-action <id>`: keeps one action of the relation;
  - `--off`: switches all of them off;
  - `--ignore` (formerly `--keep-both`): records it as intended (`KeptDuplicates`); not listed again until one more
    relation joins the input (one more action the relation), then it is listed with `ShownAgain` and `NowAlsoIn`;
  - `--remove-actions`: takes unknown actions out.
- `--include-kept` also lists the ignored ones (`Kept`), `--list-kept` only them; `--forget-kept [--keys]` lists them
  again (without keys every ignored conflict). A fix also forgets ignored conflicts that are gone.
- Each fix is one profile version (one undo step).
:::

:::layer technical
- Output: JSON (`InputConflicts`, `ActionConflicts`) or text with `--text`.
- Without a fix option it only reads. Errors: two fix options together, a fix without `--keys`, keys that are gone
  (`Those conflicts are not there any more`), a relation or action that is not part of them.
- Related: [[cli:profile validate]], [[cli:relations duplicates]], [[cli:relations merge-copies]].
:::

<!-- entry: relations search -->
:::layer beginner
Finds an action in every game's command list by words, the way you would say it ("gear", "speed brake", "fire
missile"). It changes nothing. The Relations page's search uses it.

```powershell
engine relations search "landing gear" --button --game DCS --plane "F-16C_50" --text
```
:::

:::layer advanced
Searches titles, the function catalog's functions, synonyms and game terms, with typo help (`DidYouMean`). Results are
grouped by game, each game's main control first, each row with how it was found (`exact`, `function`, `synonym`,
`described`, `words`) and, for DCS, the aircraft where the id has that meaning.

- `--game g1|g2`, `--plane p1|p2`, `--category <name>`, `--axis` or `--button` (not both) narrow the results.
- `--limit N` and `--per-game N` cap the rows.
- `--batch <file>` runs one search per line of the file (`#` lines skipped) and returns them together.
- `--profile` only marks which relations already use a row.
:::

:::layer technical
- Output: JSON grouped by game, or text with `--text`. Read-only.
- Related: [[cli:relations browse]], [[cli:relations add]], [[cli:relations suggest]].
:::

<!-- entry: relations add -->
:::layer beginner
Creates a new relation: one button or axis of yours with the actions it shall do in your games. The Relations page
uses it when you create a relation.

```powershell
engine relations add "Gear" --button --node "DCS:gear_up_down:F-16C_50|FA-18C_hornet" --group "Systems" --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file"
```
:::

:::layer advanced
- `--node Game:Id:Plane1|Plane2` per action (DCS: the aircraft; other games: the game id once); `--id` with `--game`
  and `--planes` is the same for one action.
- `--from <relation>` takes the actions of an existing relation.
- `--axis`, `--button` or `--type axis|button` sets the kind; without them it comes from `--from` or the actions.
  Axes and buttons in one relation are refused.
- `--group <g>` puts it into a group; `--device`, `--input` and `--modifier` bind it right away.
- One undo step.
:::

:::layer technical
- Output: the result as JSON, or text with `--text`; with `--profile` the profile is saved.
- Errors: no name, no source (`--node`, `--id` with `--game`, or `--from`), a malformed `--node`, an unknown option.
- Related: [[cli:relations search]], [[cli:relations show]], [[cli:relations suggest]], [[cli:binds set]].
:::

<!-- entry: relations suggest -->
:::layer beginner
Suggests what else belongs into a relation, without AI: the same function in your other games and aircraft. The
Relations page's suggestions use it.

```powershell
engine relations suggest "Gear" --min medium --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" --text
```
:::

:::layer advanced
Sources: the function catalog, your own style (habits from at least two of your own decisions), the same DCS id where
it means the same, and similar titles in any game. Scores: high from 85, medium from 55.

- `--min high|medium|low`, `--limit N`, `--game g1|g2` filter one relation's suggestions.
- `--all` suggests for every relation; `--all --missing` lists per relation the free actions of games and aircraft it
  lacks, `--per-relation N` caps each (default 25, `0` = all).
- To act on them: [[cli:relations suggest apply]] (`--add` adds, `--take` moves from another relation, `--never`
  dismisses for good, `--remove` restores a dismissed one).
:::

:::layer technical
- Output: JSON with each suggestion's score and reason, or text with `--text`. Read-only.
- Related: [[cli:relations suggest apply]], [[cli:relations search]].
:::

<!-- entry: export-marks list -->
:::layer beginner
Lists the binds an "Only add" export did not write because the game uses their button for another function. The Check
shows them as "Not written to the game".

```powershell
engine export-marks list --game DCS --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" --text
```
:::

:::layer advanced
Each mark names the bind, the game's function on that button (or that there was no free place) and the date. Marks
clear at the next export that writes the bind or finds the button free. `--game <id>` shows one game's marks.
`engine export-marks dismiss --game <id> --relation <name> [--aircraft <a>]` is the Check's "Keep as is": no card for
these marks any more.
:::

:::layer technical
- Output: JSON or text with `--text`; another argument fails with `Unknown export-marks argument(s)`.
- The marks are stored in the profile; an export replaces the marks of its game and aircraft and saves the profile
  quietly.
- Related: [[cli:profile validate]], [[cli:dcs export]].
:::

<!-- entry: export-marks dismiss -->
:::layer beginner
The Check's **Keep as is** for a bind that an "Only add" export did not write: the marks of that relation get no card
any more. The game keeps its own bind on that button, and JoyPro stops asking about it.

```powershell
engine export-marks dismiss --game DCS --relation "Gear" --aircraft F-16C_50 --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" --text
```
:::

:::layer advanced
- `--game <id>` and `--relation <name>` are required; `--aircraft <a>` dismisses only the marks of that aircraft,
  without it every mark of the relation in that game. Names are compared without case; the game is a game id
  (`DCS`, `IL2Game`, `StarCitizen`, `MSFS2024`, ...) or an alias (`il2`, `wt`, ...), see [[cli:export-marks list]].
- A dismissed mark stays in the list with `[kept as is]`. A later export that skips the same bind on the same button
  keeps it dismissed; when the bind is written or the conflict changes, the mark goes or comes back as a new one.
- Changes only the profile (saved with `--profile`, one undo step). Nothing in the game changes.
:::

:::layer technical
- Output: the marks after the change as for [[cli:export-marks list]] (JSON `Marks`, `Changed`, or text with
  `--text`, ending `Changed: <n>`). Exit code `0`, also when nothing matched: then the profile is not changed and the
  warning `No open mark of '<relation>' in <game> to dismiss; ...` says so (`Warnings` in the JSON, `Warning:` in the
  text).
- Exit code `1` with `export-marks dismiss needs --game <id> and --relation <name>.`, `Unknown game: ...` or `Unknown
  export-marks argument(s): ...`.
- Related: [[cli:export-marks list]], [[cli:profile validate]], [[cli:dcs export]].
:::

<!-- entry: profile game-state -->
:::layer beginner
Tells whether your profile has imported from or exported to a game, and whether the game has binds of its own. The GUI
uses it to ask "Import first?" before the first export into a game that has its own binds.

```powershell
engine profile game-state --game DCS --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" --text
```
:::

:::layer advanced
`--game <id>` is required; `--path <config>` checks another config folder than the detected one. Read-only.
:::

:::layer technical
- Output: JSON or text with `--text`; without `--game`, or with another argument, it fails with the usage line.
- Related: [[cli:dcs import]], [[cli:sync pending]], [[cli:games installs]].
:::

<!-- entry: backups list -->
:::layer beginner
Lists the backups JoyPro made of a game's control files before each export, shows one, or puts one back. The Export
page's **Undo last export** and **Game config backups** use it.

```powershell
engine backups list --game DCS --text
engine backups restore latest --game DCS --dry-run --text
```
:::

:::layer advanced
- `list [--game <id>]`: the backup sets.
- `show <id>`: the files of one set and their original paths.
- `restore <id|latest> [--game <id>] [--dry-run]`: puts the files back and removes files the export created;
  `--dry-run` shows what would happen and writes nothing.
- Safety: a restore backs up the current files first (so it can be undone too) and is refused while the game runs or
  when the game is not installed. The newest 30 sets per game are kept.
:::

:::layer technical
- Sets live in `%LOCALAPPDATA%\JoyPro2\GameBackups\<date-time>_<game>\` with `manifest.json` (the original paths).
- Output: JSON or text with `--text`. Exit code `1` when the id is missing (`needs a backup id (or latest)`), unknown
  (`No backup '<id>' ... See engine backups list.`) or the restore reports errors.
- See [[doc:backups]].
:::

<!-- entry: sync pending -->
:::layer beginner
Shows what you changed in JoyPro since the last export of a game that the game does not have yet. The GUI asks before
an import that would bring the old binds back.

```powershell
engine sync pending --game DCS --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" --text
```
:::

:::layer advanced
`--game <id>` is required (DCS, IL2Game, StarCitizen or a game of [[cli:games integrations]]); `--path <config>`
checks another config folder and needs `--game`. Read-only.
:::

:::layer technical
- Output: JSON or text with `--text`. Exit code `1` for an unknown game or without `--game`.
- Related: [[cli:sync status]], [[cli:sync diff]], [[cli:profile game-state]], [[doc:sync-watcher]].
:::
