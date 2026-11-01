Hand-written texts of the import and export commands of every game the engine reaches through its game registry (all
games except DCS, IL-2 Great Battles and Star Citizen). Each block below belongs to the command named in its marker.

<!-- entry: msfs2024 import -->
:::layer beginner
Reads the controls you set up in Microsoft Flight Simulator 2024 (your own profiles of the Steam and the Microsoft
Store version) into your JoyPro profile, one relation per function and input. Nothing in the sim changes. Use it once
at the start, or after you changed binds in the sim. The GUI runs it with **Import** on the MSFS 2024 card of the Start
page (**Look first** runs [[cli:msfs2024 scan]] instead).

```powershell
engine msfs2024 import --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" --text
```
:::

:::layer advanced
It reads the profiles the sim uses per device and category (Airplanes and General): Steam
`userdata\<id>\2537590\remote\inputprofile_<n>`, the Microsoft Store save under
`%LOCALAPPDATA%\Packages\Microsoft.Limitless_8wekyb3d8bbwe\SystemAppData\wgs\`, and JoyPro's preset package only for
devices without a profile of their own. Flight and General binds come in, with the axis values of the Sensitivity page.

| Option | What it does |
|---|---|
| (none) | adds to the open profile (merge); `--merge` is accepted and means the same |
| `--replace` | starts over: every relation of the profile is replaced by what the sim has (GUI: **Start over from this game**) |
| `--include-disconnected` | also the profiles of devices that are not connected (the sim keeps every device it ever saw) |
| `--include-sim-defaults` | also the default presets the sim ships for devices whose use the sim's records do not show |
| `--limit-to-profile-aircraft` | a bind the sim has only in the profiles of some aircraft is limited to those aircraft at once |
| `--game-profile <name>` | takes that profile when a device has several (else the one the sim's records name, else the newest) |

- Without `--limit-to-profile-aircraft` such binds come in for all aircraft and are listed under "Only In Some
  Aircraft" with the command that limits each one. A relation you had limited, or one JoyPro exported, keeps its
  aircraft.
- Keyboard binds are not read in MSFS, so `--include-keyboard` changes nothing here.
- Names, groups, modifiers and tuning of binds JoyPro exported come back from its `.joypro-meta.json`.
- Safety: reads only, and it is not refused while the sim runs. With `--profile` the profile is kept as the version
  "Before import from Microsoft Flight Simulator 2024" first.
- `--ignore-device <name>` / `--ignore-devices <a;b>` leave the binds of those devices out (a name without
  `{GUID}` means every device of that name); a step line says how many binds were left out.
:::

:::layer technical
- Output: JSON of the import result (`Bindings`, `RelationsImported`, `NodesAddedToExisting`, `OnlyInAircraft`,
  `Steps`, `Warnings`, `Errors`), or a text report with `--text`; exit code `0`, `1` when the result has errors (for
  example `No Microsoft Flight Simulator 2024 config file found. Pass the path explicitly.`).
- Without a path it reads what the sim uses (the locations [[cli:msfs2024 paths]] lists). A first import on a fresh
  install builds the command list from the sim first (a step line says so).
- Refused: `--aircraft` (export only), unknown options (`Unknown MSFS2024 import argument(s): ...`), and the import
  when the game is not installed or switched off in Settings > Games.
- With `--profile` the profile is saved afterwards. The imported inputs are recorded as JoyPro's (in
  `%LOCALAPPDATA%\JoyPro2\Ownership\MSFS`), and a sim JoyPro never exported to is watched from now on
  ([[cli:sync status]]).
- Related: [[cli:msfs2024 scan]], [[cli:msfs2024 export]], [[cli:relations aircraft set]], [[cli:sync pending]];
  game page [[game:msfs2024]].
:::

<!-- entry: msfs2024 export -->
:::layer beginner
Writes your binds into Microsoft Flight Simulator 2024: into the profile the sim uses for each device and category
(Steam or Microsoft Store), into JoyPro's preset package for aircraft the sim has not set up yet, and into import files
where a profile cannot be written. Close the sim first. The GUI's **Export now** on the MSFS 2024 tile runs it.

```powershell
engine msfs2024 export --mode clean --nuke-untouched-devices --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" --text
```
:::

:::layer advanced
The modes work per profile of yours and only in the managed contexts.

| `--mode` | In your MSFS 2024 profiles |
|---|---|
| `add` | a bind goes in only where no other action of that profile uses the input combination; nothing is removed |
| `merge` | the other action on your combination loses it; an action keeps its old combination |
| `merge-overwrite` (engine default) | as `merge`, and every action you bind loses its other combinations, also in the profiles of other connected devices |
| `clean` (GUI default) | the profiles of the connected devices JoyPro writes keep only your binds |

- `--nuke-untouched-devices` never clears one of your own MSFS 2024 profiles. With `add` it is not used (warning).
- `--user-profiles false` leaves your own profiles alone (GUI tick **write into my MSFS profiles**, on by default).
  `--import-files` writes an import file for every device and category, not only where a profile could not be written.
- `--aircraft <key>` also writes the relations limited to that aircraft. Without it, a relation limited to aircraft
  goes only into profiles whose aircraft are all ticked, never into the preset package or the import files.
- `--game-profile <name>` picks one profile when a device has several.
- Never touched: menu, world map and developer contexts, keyboard, mouse and gamepad profiles, `profile_00`, the
  presets Asobo ships, other containers of the Store save, devices that are not connected, `--ignore-device(s)` and
  the `--vjoy-tunnel` device. Keyboard binds are not written, `--include-keyboard` changes nothing.
- Safety: refused while `FlightSimulator2024` runs (the Store version's launch helper counts too); the Steam file or
  the whole Store save folder is backed up first (undo: [[cli:backups list]]); the profile is kept as the version
  "Before export to Microsoft Flight Simulator 2024". `--dry-run` writes nothing and is allowed while the sim runs.
:::

:::layer technical
- Output: JSON of the export result (`BindingsWritten`, `BindingsSkipped`, `FilesWritten`, `ProfileUpdates` per
  device and category with status `updated`, `unchanged`, `dry-run` or `import-file`, `SkippedTakenInputs`), or a text
  report with `--text` (section "Profiles"); exit code `1` on an error.
- Errors: `Microsoft Flight Simulator 2024 is running (...). Close the game first; it overwrites its config on exit.`,
  `Invalid export mode '<x>'. Use clean, add, merge or merge-overwrite.`, and a refusal when the path is the Store save
  folder.
- Without a path it uses the first location of [[cli:msfs2024 paths]] (JoyPro's package in `Community2024`). Import
  files go to `Documents\JoyPro2\MSFS 2024 import files` (step line "MSFS 2024 import files written ...").
- A bind whose action is not in the command list is skipped with a warning. A profile without MSFS 2024 binds writes
  nothing ("Nothing was written.", exit `0`).
- After a real run: JoyPro's `.joypro-meta.json` for the next import, the watcher's fingerprint and the export marks
  ([[cli:export-marks list]]); the profile is saved only when those marks changed.
- Related: [[cli:msfs2024 import]], [[cli:msfs2024 refresh-db]], [[cli:sync pending]]; game page [[game:msfs2024]].
:::

<!-- entry: msfs2024 paths -->
:::layer beginner
Lists where JoyPro finds the input profiles of MSFS 2024 on this PC. The first one is what [[cli:msfs2024 scan]],
[[cli:msfs2024 import]] and [[cli:msfs2024 export]] use when you give no path. **Detect** in the **Other Games** panel
(Start page, **Expert import**) runs it and puts the first one into **File / Folder**.

```powershell
engine msfs2024 paths --text
```
:::

:::layer advanced
- First JoyPro's own package `<Community>\joypro-input-profiles` (the `Community` folder the sim's `UserCfg.opt`
  names); an export writes there.
- Then every Steam folder `userdata\<id>\2537590\remote` that holds input profiles, and the Microsoft Store save
  `%LOCALAPPDATA%\Packages\Microsoft.Limitless_8wekyb3d8bbwe\SystemAppData\wgs` when it exists.
- A config location you set yourself ([[cli:games config set]], Settings > Games) comes first.
- No options. Reads only; it also works while the game runs or when it is not installed.
:::

:::layer technical
- Output: JSON (`Game`, `DisplayName`, `ConfigPaths`, `Warnings`), or text with `--text` ("Microsoft Flight Simulator
  2024 Config Paths", "Paths: <n>" and one line per location). Exit code `0`, also when nothing was found (empty
  list).
- `engine msfs2024` without an action runs `paths`.
- The entries are folders, not single files: each holds one profile per device and category.
- Related: [[cli:msfs2024 scan]], [[cli:games paths]], [[cli:games install set]]; game page [[game:msfs2024]].
:::

<!-- entry: msfs2024 catalog -->
:::layer beginner
Lists the MSFS 2024 actions JoyPro manages (its command list): id, axis or button, category and title. An export
writes only these actions, and the relation editor and the search list them.

```powershell
engine msfs2024 catalog --text
```
:::

:::layer advanced
- The list is `%LOCALAPPDATA%\JoyPro2\GameData\DB\MSFS2024\actions.tsv` (when that folder does not exist, the
  `DB\MSFS2024` folder next to the engine).
- It is built from the sim's presets and your profiles by [[cli:msfs2024 refresh-db]], or by the first scan, import or
  export.
- `catalog` only shows the list; it never builds it. Titles that look like ids are shown readable.
- No options. Reads only; works without the game installed.
:::

:::layer technical
- Output: JSON (`Game`, `Source`, `Actions` with `Id`, `Title`, `IsAxis`, `Category`, `Warnings`), or text with
  `--text` ("Microsoft Flight Simulator 2024 Action Catalog", "Source:", "Actions: <n>", then `<id> | axis|button |
  <category> | <title>` per action).
- Without a list: the warning `Microsoft Flight Simulator 2024 action catalog not found: <path>` and an empty list,
  exit code `0`.
- Add-on actions that belong to one aircraft are limited to it in the file; `catalog` does not show that column.
- Related: [[cli:msfs2024 refresh-db]], [[cli:msfs2024 export]], [[cli:db games]]; game page [[game:msfs2024]].
:::

<!-- entry: msfs2024 scan -->
:::layer beginner
Shows the joystick binds MSFS 2024 has now, without changing your profile or the game. Use it to look before an
import. **Look first** on the game's card of the Start page and **Scan** in the **Other Games** panel run it.

```powershell
engine msfs2024 scan --text
```
:::

:::layer advanced
- It reads what the sim uses: your Steam and Microsoft Store profiles and JoyPro's preset package, the devices that
  are connected, with the axis values of the Sensitivity page.
- Path (positional or `--path`): a profile file or a folder of profiles.
- `--ignore-device <name>` / `--ignore-devices <a;b>` (separated by `|` or `;`, `--ignore-devices` also by commas) leave the binds of those devices out
  (a name without `{GUID}` means every device of that name); a step line names them and counts the binds.
- Keyboard binds are not read in this game.
- `--game-profile <name>` reads that profile when a device has several; `--include-disconnected` also reads devices
  that are not connected; `--include-sim-defaults` also the sim's default presets whose use its records do not show.
- A path inside JoyPro's package means the package. `--limit-to-profile-aircraft` (import only) and `--aircraft`
  (export only) are refused.
- Safety: reads only and is not refused while the game runs. It is refused when the game is not installed (set its
  folder: [[cli:games install set]]) or switched off in Settings > Games.
- A fresh install has no command list yet: the first scan builds it from the installed game and saves it (a step line
  says so).
:::

:::layer technical
- Output: JSON (`ConfigPath`, `BindingsRead`, `Bindings` with `ActionId`, `Device`, `Input`, `IsAxis`, `Reformers`,
  `Inverted`, then `Steps`, `Warnings`, `Errors`), or text with `--text` ("Microsoft Flight Simulator 2024 Scan",
  "Config:", "Bindings Read:" and the list).
- Exit code `0`; `1` when the result has errors, for example `No Microsoft Flight Simulator 2024 config file found.
  Pass the path explicitly.`
- Unknown options fail with `Unknown MSFS2024 scan argument(s): ...`.
- Binds of the vJoy device JoyPro forwards into (`--vjoy-tunnel`) are left out.
- Related: [[cli:msfs2024 import]], [[cli:msfs2024 paths]], [[cli:msfs2024 catalog]]; game page [[game:msfs2024]].
:::

<!-- entry: msfs2024 refresh-db -->
:::layer beginner
Builds JoyPro's MSFS 2024 command list from the sim's own input presets and your profiles, or adds the actions that
are new. Run it after a game update, or when JoyPro says the list is missing. **Refresh DB** in the **Other Games**
panel (its **DB Source** box is the path) and the Start page's notice "Command lists to build or update" run it.

```powershell
engine msfs2024 refresh-db --dry-run --text
```
:::

:::layer advanced
- Without a path it reads the sim's input presets (Steam `<install>\Packages\fs-input-profiles`, Microsoft Store
  `<drive>\XboxGames\Microsoft Flight Simulator 2024\Content\Packages`), your own profiles (Steam and Microsoft
  Store) and JoyPro's own package: every action of the managed contexts (flight and general; menus, world map and
  developer tools stay out), each with its context.
- Also: the flight events the sim's `en-US.locPak` names but no preset binds, the input presets of add-on packages in
  Community and Marketplace (their new actions are limited to that aircraft; packed packages are skipped), and
  contexts corrected to the ones your profiles list.
- New actions and raw event names get the name the sim's Controls menu shows; other known titles stay.
- The list of installed aircraft (`aircraft.tsv`, for `--aircraft` and the GUI's **Export Aircraft** box) is updated
  in the same run.
- Path: a profile file or a folder of exported profiles instead of the sim.
- `--dry-run` reads everything and writes nothing (step "Dry run: nothing was written."). Without it the list is
  written only when it changed.
- Safety: only JoyPro's list changes, never a game file; it is refused when the game is not installed or switched off
  in Settings > Games. The GUI can cancel it while the game's files are read; nothing is written then.
:::

:::layer technical
- Output: JSON of the refresh (`DryRun`, `FilesWritten`, `Steps` with `Actions: <old> -> <new>`, `Warnings`,
  `Errors`), or text with `--text`.
- Exit code `0`; `1` when nothing could be read: `No Microsoft Flight Simulator 2024 input presets found: neither the
  sim's own (...) nor your own profiles (...). Set the install folder or pass --path with exported profiles.`; the
  list stays unchanged.
- Writes `%LOCALAPPDATA%\JoyPro2\GameData\DB\MSFS2024\actions.tsv` (with a header line that has the date), and
  `aircraft.tsv` next to it. After a real run the game's build is recorded for [[cli:db check-updates]].
- Related: [[cli:msfs2024 catalog]], [[cli:msfs2024 scan]], [[cli:db check-updates]]; game page [[game:msfs2024]].
:::

<!-- entry: msfs2020 import -->
:::layer beginner
Reads the device profiles of Microsoft Flight Simulator 2020 (Steam or Microsoft Store) into your JoyPro profile, one
relation per function and input. Nothing in the sim changes. The GUI runs it with **Import** on the MSFS 2020 card of
the Start page.

```powershell
engine msfs2020 import --include-disconnected --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" --text
```
:::

:::layer advanced
It reads one profile per device: Steam `userdata\<id>\1250410\remote\inputprofile_<n>`, or the Microsoft Store save
under `%LOCALAPPDATA%\Packages\Microsoft.FlightSimulator_8wekyb3d8bbwe\SystemAppData\wgs\`, with the axis values of
the device's Sensitivity page.

| Option | What it does |
|---|---|
| (none) | adds to the open profile (merge); `--merge` means the same |
| `--replace` | starts over: every relation of the profile is replaced by what the sim has |
| `--include-disconnected` | also the profiles of devices that are not connected |
| `--include-sim-defaults` | also shipped presets whose use the sim's records do not show |
| `--game-profile <name>` | takes that profile when a device has several |

- `--limit-to-profile-aircraft` is an MSFS 2024 option: MSFS 2020 keeps one profile per device for every aircraft, so
  binds come in for all aircraft. Given here it does not fail; a warning says that it applies to MSFS 2024 only.
- Keyboard binds are not read in MSFS; `--include-keyboard` changes nothing.
- Safety: reads only, not refused while the sim runs; with `--profile` the profile is kept as the version "Before
  import from Microsoft Flight Simulator 2020" first.
- `--ignore-device <name>` / `--ignore-devices <a;b>` leave the binds of those devices out (a name without
  `{GUID}` means every device of that name); a step line says how many binds were left out.
:::

:::layer technical
- Output: JSON of the import result or a text report with `--text`; exit code `1` on an error (`No Microsoft Flight
  Simulator 2020 config file found. Pass the path explicitly.`).
- Without a path it reads the locations of [[cli:msfs2020 paths]]; a fresh install builds the command list first.
- `--aircraft` is refused here (export only); unknown options fail with `Unknown MSFS2020 import argument(s): ...`.
- With `--profile` the profile is saved afterwards. The imported inputs are recorded as JoyPro's, and a sim JoyPro
  never exported to is watched from now on.
- Related: [[cli:msfs2020 scan]], [[cli:msfs2020 export]], [[cli:sync pending]]; game page [[game:msfs2020]].
:::

<!-- entry: msfs2020 export -->
:::layer beginner
Writes your binds into the Steam version's device profiles of Microsoft Flight Simulator 2020 (the `inputprofile_*`
files the sim already has). Close the sim first. The GUI's **Export now** on the MSFS 2020 tile runs it.

```powershell
engine msfs2020 export --mode clean --nuke-untouched-devices --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file"
```
:::

:::layer advanced
| `--mode` | In the device profiles |
|---|---|
| `add` | a bind is written only where no other action has that input combination; nothing is removed |
| `merge` | the other action on your combination loses it; an action keeps its old combination |
| `merge-overwrite` (engine default) | as `merge`, and every action you bind loses its other combinations in the profiles of connected devices |
| `clean` (GUI default) | the profiles of the connected devices JoyPro binds keep only your binds |

- `--nuke-untouched-devices`: the profiles of connected devices without a bind in your profile are cleared (not with
  `add`).
- `--aircraft <key>` also writes the relations limited to that aircraft (for all aircraft: MSFS 2020 has one profile
  per device); without it those relations are skipped with a warning. `--game-profile <name>` picks one profile.
- Only existing `inputprofile_*` files are edited; a device needs a profile made in the sim. The Microsoft Store
  profiles are read only: the Store save folder as the path is refused.
- `--user-profiles` and `--import-files` are MSFS 2024 options: given here they change nothing, and a warning says
  that they apply to MSFS 2024 only.
- Never touched: menu, world map and developer contexts, keyboard, mouse and gamepad profiles, `profile_00`, devices
  that are not connected, `--ignore-device(s)` and the `--vjoy-tunnel` device. Keyboard binds are not written.
- Safety: refused while `FlightSimulator` runs; backed up first (undo: [[cli:backups list]]); the profile is kept as
  "Before export to Microsoft Flight Simulator 2020". `--dry-run` writes nothing and is allowed while the sim runs.
:::

:::layer technical
- Output: JSON of the export result or a text report with `--text`; exit code `1` on an error (the sim runs, an
  invalid `--mode`, no config file found).
- Without a path it uses the first location of [[cli:msfs2020 paths]].
- A bind whose action is not in the command list is skipped with a warning; a profile without MSFS 2020 binds writes
  nothing (exit `0`).
- After a real run: JoyPro's `.joypro-meta.json`, the watcher's fingerprint and the export marks
  ([[cli:export-marks list]]).
- Related: [[cli:msfs2020 import]], [[cli:msfs2020 refresh-db]]; game page [[game:msfs2020]].
:::

<!-- entry: msfs2020 paths -->
:::layer beginner
Lists where JoyPro finds the input profiles of MSFS 2020 on this PC. The first one is what [[cli:msfs2020 scan]],
[[cli:msfs2020 import]] and [[cli:msfs2020 export]] use when you give no path. **Detect** in the **Other Games** panel
(Start page, **Expert import**) runs it and puts the first one into **File / Folder**.

```powershell
engine msfs2020 paths --text
```
:::

:::layer advanced
- Every Steam folder `userdata\<id>\1250410\remote` that holds input profiles, and the Microsoft Store save
  `%LOCALAPPDATA%\Packages\Microsoft.FlightSimulator_8wekyb3d8bbwe\SystemAppData\wgs` when it exists.
- A config location you set yourself ([[cli:games config set]], Settings > Games) comes first.
- No options. Reads only; it also works while the game runs or when it is not installed.
:::

:::layer technical
- Output: JSON (`Game`, `DisplayName`, `ConfigPaths`, `Warnings`), or text with `--text` ("Microsoft Flight Simulator
  2020 Config Paths", "Paths: <n>" and one line per location). Exit code `0`, also when nothing was found (empty
  list).
- `engine msfs2020` without an action runs `paths`.
- The entries are folders, not single files: each holds one profile per device and category.
- Related: [[cli:msfs2020 scan]], [[cli:games paths]], [[cli:games install set]]; game page [[game:msfs2020]].
:::

<!-- entry: msfs2020 catalog -->
:::layer beginner
Lists the MSFS 2020 actions JoyPro manages (its command list): id, axis or button, category and title. An export
writes only these actions, and the relation editor and the search list them.

```powershell
engine msfs2020 catalog --text
```
:::

:::layer advanced
- The list is `%LOCALAPPDATA%\JoyPro2\GameData\DB\MSFS2020\actions.tsv` (when that folder does not exist, the
  `DB\MSFS2020` folder next to the engine).
- It is built from the sim's presets and your profiles by [[cli:msfs2020 refresh-db]], or by the first scan, import or
  export.
- `catalog` only shows the list; it never builds it. Titles that look like ids are shown readable.
- No options. Reads only; works without the game installed.
:::

:::layer technical
- Output: JSON (`Game`, `Source`, `Actions` with `Id`, `Title`, `IsAxis`, `Category`, `Warnings`), or text with
  `--text` ("Microsoft Flight Simulator 2020 Action Catalog", "Source:", "Actions: <n>", then `<id> | axis|button |
  <category> | <title>` per action).
- Without a list: the warning `Microsoft Flight Simulator 2020 action catalog not found: <path>` and an empty list,
  exit code `0`.
- Add-on actions that belong to one aircraft are limited to it in the file; `catalog` does not show that column.
- Related: [[cli:msfs2020 refresh-db]], [[cli:msfs2020 export]], [[cli:db games]]; game page [[game:msfs2020]].
:::

<!-- entry: msfs2020 scan -->
:::layer beginner
Shows the joystick binds MSFS 2020 has now, without changing your profile or the game. Use it to look before an
import. **Look first** on the game's card of the Start page and **Scan** in the **Other Games** panel run it.

```powershell
engine msfs2020 scan --text
```
:::

:::layer advanced
- It reads what the sim uses: your Steam and Microsoft Store profiles, the devices that are connected, with the axis
  values of the Sensitivity page.
- Path (positional or `--path`): a profile file or a folder of profiles.
- `--ignore-device <name>` / `--ignore-devices <a;b>` (separated by `|` or `;`, `--ignore-devices` also by commas) leave the binds of those devices out
  (a name without `{GUID}` means every device of that name); a step line names them and counts the binds.
- Keyboard binds are not read in this game.
- `--game-profile <name>` reads that profile when a device has several; `--include-disconnected` also reads devices
  that are not connected; `--include-sim-defaults` also the sim's default presets whose use its records do not show.
- `--user-profiles` and `--import-files` are MSFS 2024 options: accepted here, a warning says they were ignored.
  `--limit-to-profile-aircraft` (import only) and `--aircraft` (export only) are refused.
- Safety: reads only and is not refused while the game runs. It is refused when the game is not installed (set its
  folder: [[cli:games install set]]) or switched off in Settings > Games.
- A fresh install has no command list yet: the first scan builds it from the installed game and saves it (a step line
  says so).
:::

:::layer technical
- Output: JSON (`ConfigPath`, `BindingsRead`, `Bindings` with `ActionId`, `Device`, `Input`, `IsAxis`, `Reformers`,
  `Inverted`, then `Steps`, `Warnings`, `Errors`), or text with `--text` ("Microsoft Flight Simulator 2020 Scan",
  "Config:", "Bindings Read:" and the list).
- Exit code `0`; `1` when the result has errors, for example `No Microsoft Flight Simulator 2020 config file found.
  Pass the path explicitly.`
- Unknown options fail with `Unknown MSFS2020 scan argument(s): ...`.
- Binds of the vJoy device JoyPro forwards into (`--vjoy-tunnel`) are left out.
- Related: [[cli:msfs2020 import]], [[cli:msfs2020 paths]], [[cli:msfs2020 catalog]]; game page [[game:msfs2020]].
:::

<!-- entry: msfs2020 refresh-db -->
:::layer beginner
Builds JoyPro's MSFS 2020 command list from the sim's own input presets and your profiles, or adds the actions that
are new. Run it after a game update, or when JoyPro says the list is missing. **Refresh DB** in the **Other Games**
panel (its **DB Source** box is the path) and the Start page's notice "Command lists to build or update" run it.

```powershell
engine msfs2020 refresh-db --dry-run --text
```
:::

:::layer advanced
- Without a path it reads the sim's input presets (Steam `<install>\Packages\fs-input-profiles` or `<install>\Input`,
  Microsoft Store `<drive>\XboxGames\Microsoft Flight Simulator\Content\Packages`), your own profiles (Steam and
  Microsoft Store): every action of the managed contexts (flight and general; menus, world map and developer tools
  stay out), each with its context.
- Also: the flight events the sim's `en-US.locPak` names but no preset binds, the input presets of add-on packages in
  Community and Marketplace (their new actions are limited to that aircraft; packed packages are skipped), and
  contexts corrected to the ones your profiles list.
- New actions and raw event names get the name the sim's Controls menu shows; other known titles stay.
- The list of installed aircraft (`aircraft.tsv`, for `--aircraft` and the GUI's **Export Aircraft** box) is updated
  in the same run.
- Path: a profile file or a folder of exported profiles instead of the sim.
- `--dry-run` reads everything and writes nothing (step "Dry run: nothing was written."). Without it the list is
  written only when it changed.
- Safety: only JoyPro's list changes, never a game file; it is refused when the game is not installed or switched off
  in Settings > Games. The GUI can cancel it while the game's files are read; nothing is written then.
:::

:::layer technical
- Output: JSON of the refresh (`DryRun`, `FilesWritten`, `Steps` with `Actions: <old> -> <new>`, `Warnings`,
  `Errors`), or text with `--text`.
- Exit code `0`; `1` when nothing could be read: `No Microsoft Flight Simulator 2020 input presets found: neither the
  sim's own (...) nor your own profiles (...). Set the install folder or pass --path with exported profiles.`; the
  list stays unchanged.
- Writes `%LOCALAPPDATA%\JoyPro2\GameData\DB\MSFS2020\actions.tsv` (with a header line that has the date), and
  `aircraft.tsv` next to it. After a real run the game's build is recorded for [[cli:db check-updates]].
- Related: [[cli:msfs2020 catalog]], [[cli:msfs2020 scan]], [[cli:db check-updates]]; game page [[game:msfs2020]].
:::

<!-- entry: xplane12 import -->
:::layer beginner
Reads the joystick assignments of X-Plane 12 into your JoyPro profile: axis uses, button commands, reverse flags and
response curves, one relation per function and input. Nothing in X-Plane changes. The GUI runs it with **Import** on
the X-Plane 12 card of the Start page.

```powershell
engine xplane12 import --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" --text
```
:::

:::layer advanced
It reads `<install>\Output\preferences\X-Plane Joystick Settings.prf`, or a saved control profile
`...\control profiles\<Name> - Joysticks.prf` given as the path. X-Plane 12 must have been started once with your
devices connected, or the file does not exist yet.

- Default: adds to the open profile (`--merge` means the same). `--replace` starts over: every relation of the
  profile is replaced by what X-Plane 12 has.
- The file holds no keyboard binds (`X-Plane Keys.prf` is not read), so `--include-keyboard` changes nothing.
- Curves come in as the relation's X-Plane 12 tuning; names, groups and tuning of binds JoyPro exported come back from
  `<file>.joypro-meta.json`.
- Safety: reads only, not refused while X-Plane runs; with `--profile` the profile is kept as the version "Before
  import from X-Plane 12" first.
- `--ignore-device <name>` / `--ignore-devices <a;b>` leave the binds of those devices out (a name without
  `{GUID}` means every device of that name); a step line says how many binds were left out.
:::

:::layer technical
- Output: JSON of the import result or a text report with `--text`; exit code `1` on an error (`No X-Plane 12 config
  file found. Pass the path explicitly.`).
- Without a path it reads the first file of [[cli:xplane12 paths]]; a fresh install builds the command list from the
  installed `Commands.txt` first.
- Unknown options fail with `Unknown XPlane12 import argument(s): ...`; MSFS options (`--game-profile`,
  `--include-disconnected` ...) are refused.
- With `--profile` the profile is saved afterwards. The imported inputs are recorded as JoyPro's in
  `<file>.joypro.json` (so `merge-overwrite` can later replace them), and a game JoyPro never exported to is watched
  from now on.
- Related: [[cli:xplane12 scan]], [[cli:xplane12 export]], [[cli:xplane11 import]]; game page [[game:xplane]].
:::

<!-- entry: xplane12 export -->
:::layer beginner
Writes your binds into X-Plane 12's joystick preferences, so **Settings > Joystick** shows them. Close X-Plane first.
The GUI's **Export now** on the X-Plane 12 tile runs it.

```powershell
engine xplane12 export --mode clean --nuke-untouched-devices --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" --text
```
:::

:::layer advanced
It edits `<install>\Output\preferences\X-Plane Joystick Settings.prf` (or the control profile you pass) line by line:
axis uses, button commands, reverse flags and response curves.

| `--mode` | In X-Plane 12 |
|---|---|
| `add` | free inputs only; a bind on an input with another use or command is not written and is listed |
| `merge` | replaces the other use or command on your input, keeps everything else; an axis use moves |
| `merge-overwrite` (engine default) | also releases other axes and buttons of connected devices that hold a use or command you bind |
| `clean` (GUI default) | also releases every other flight axis and command on the devices you write |

- `--nuke-untouched-devices`: connected devices you do not write lose their flight uses and commands (not with
  `add`). X-Plane is a flight-only game, so this covers every flight use of those devices.
- Curves: in `add` and `merge` a curve made in X-Plane stays when JoyPro's tuning is linear (warning);
  `merge-overwrite` and `clean` write JoyPro's curve. Game settings you stored for X-Plane 12 change only lines the
  file already has.
- Never touched: devices that are not connected, `--ignore-device(s)`, the `--vjoy-tunnel` device, view, camera, VR
  and custom-command axes, plugin and aircraft commands, calibration lines, `X-Plane Keys.prf`. Keyboard binds and
  modifiers are not written.
- Safety: refused while `X-Plane` runs (both versions use that name); backed up first (`.jpbak` and the backup store,
  undo: [[cli:backups list]]); the profile is kept as "Before export to X-Plane 12". `--dry-run` writes nothing and is
  allowed while X-Plane runs.
:::

:::layer technical
- Output: JSON of the export result (`BindingsWritten`, `BindingsSkipped`, `FilesWritten`, `SkippedTakenInputs`) or
  a text report with `--text`; exit code `1` on an error (`X-Plane 12 is running (X-Plane). Close the game first; it
  overwrites its config on exit.`, an invalid `--mode`, no file found).
- Without a path it writes the first file of [[cli:xplane12 paths]].
- A bind whose command is not in the command list is skipped with a warning; without X-Plane 12 binds nothing is
  written (exit `0`).
- After a real run: `<file>.joypro.json` and `<file>.joypro-meta.json` beside the file, the watcher's fingerprint and
  the export marks ([[cli:export-marks list]]).
- Related: [[cli:xplane12 import]], [[cli:binds tuning set]], [[cli:ffb set]]; game page [[game:xplane]].
:::

<!-- entry: xplane12 paths -->
:::layer beginner
Lists where JoyPro finds the joystick settings of X-Plane 12 on this PC. The first one is what [[cli:xplane12 scan]],
[[cli:xplane12 import]] and [[cli:xplane12 export]] use when you give no path. **Detect** in the **Other Games** panel
(Start page, **Expert import**) runs it and puts the first one into **File / Folder**.

```powershell
engine xplane12 paths --text
```
:::

:::layer advanced
- Per installation: `<install>\Output\preferences\X-Plane Joystick Settings.prf`, then the saved control profiles
  `...\control profiles\<Name> - Joysticks.prf` (newest first).
- The installations come from `%LOCALAPPDATA%\x-plane_install_12.txt` and Steam (app 2014780); a folder set with
  [[cli:games install set]] is used instead.
- A config location you set yourself ([[cli:games config set]], Settings > Games) comes first.
- No options. Reads only; it also works while the game runs or when it is not installed.
:::

:::layer technical
- Output: JSON (`Game`, `DisplayName`, `ConfigPaths`, `Warnings`), or text with `--text` ("X-Plane 12 Config Paths",
  "Paths: <n>" and one line per location). Exit code `0`, also when nothing was found (empty list).
- `engine xplane12` without an action runs `paths`.
- Related: [[cli:xplane12 scan]], [[cli:games paths]], [[cli:games install set]]; game page [[game:xplane]].
:::

<!-- entry: xplane12 catalog -->
:::layer beginner
Lists the X-Plane 12 actions JoyPro manages (its command list): id, axis or button, category and title. An export
writes only these actions, and the relation editor and the search list them.

```powershell
engine xplane12 catalog --text
```
:::

:::layer advanced
- The list is `%LOCALAPPDATA%\JoyPro2\GameData\DB\XPlane12\actions.tsv` (when that folder does not exist, the
  `DB\XPlane12` folder next to the engine).
- It is built from the installed `Commands.txt` by [[cli:xplane12 refresh-db]], or by the first scan, import or
  export.
- `catalog` only shows the list; it never builds it. Titles that look like ids are shown readable.
- No options. Reads only; works without the game installed.
:::

:::layer technical
- Output: JSON (`Game`, `Source`, `Actions` with `Id`, `Title`, `IsAxis`, `Category`, `Warnings`), or text with
  `--text` ("X-Plane 12 Action Catalog", "Source:", "Actions: <n>", then `<id> | axis|button | <category> | <title>`
  per action).
- Without a list: the warning `X-Plane 12 action catalog not found: <path>` and an empty list, exit code `0`.
- Related: [[cli:xplane12 refresh-db]], [[cli:xplane12 export]], [[cli:db games]]; game page [[game:xplane]].
:::

<!-- entry: xplane12 scan -->
:::layer beginner
Shows the joystick binds X-Plane 12 has now, without changing your profile or the game. Use it to look before an
import. **Look first** on the game's card of the Start page and **Scan** in the **Other Games** panel run it.

```powershell
engine xplane12 scan --text
```
:::

:::layer advanced
- It reads the axis uses, button commands, reverse flags and response curves of the joystick settings file. X-Plane 12
  writes the file once it was started with your devices connected.
- Path (positional or `--path`): another `.prf` file, for example a saved control profile.
- `--ignore-device <name>` / `--ignore-devices <a;b>` (separated by `|` or `;`, `--ignore-devices` also by commas) leave the binds of those devices out
  (a name without `{GUID}` means every device of that name); a step line names them and counts the binds.
- Keyboard binds are not read in this game.
- Safety: reads only and is not refused while the game runs. It is refused when the game is not installed (set its
  folder: [[cli:games install set]]) or switched off in Settings > Games.
- A fresh install has no command list yet: the first scan builds it from the installed game and saves it (a step line
  says so).
:::

:::layer technical
- Output: JSON (`ConfigPath`, `BindingsRead`, `Bindings` with `ActionId`, `Device`, `Input`, `IsAxis`, `Reformers`,
  `Inverted`, then `Steps`, `Warnings`, `Errors`), or text with `--text` ("X-Plane 12 Scan", "Config:", "Bindings
  Read:" and the list).
- Exit code `0`; `1` when the result has errors, for example `No X-Plane 12 config file found. Pass the path
  explicitly.`
- Unknown options fail with `Unknown XPlane12 scan argument(s): ...`; MSFS options (`--game-profile`,
  `--include-disconnected`, `--include-sim-defaults`) are refused.
- Binds of the vJoy device JoyPro forwards into (`--vjoy-tunnel`) are left out.
- Related: [[cli:xplane12 import]], [[cli:xplane12 paths]], [[cli:xplane12 catalog]]; game page [[game:xplane]].
:::

<!-- entry: xplane12 refresh-db -->
:::layer beginner
Builds JoyPro's X-Plane 12 command list from the installed `Commands.txt`, or adds the actions that are new. Run it
after a game update, or when JoyPro says the list is missing. **Refresh DB** in the **Other Games** panel (its **DB
Source** box is the path) and the Start page's notice "Command lists to build or update" run it.

```powershell
engine xplane12 refresh-db --dry-run --text
```
:::

:::layer advanced
- It reads `<install>\Resources\plugins\Commands.txt` (path: the X-Plane folder, a folder that holds `Commands.txt`,
  or the file).
- Kept: every `sim/...` command of the flight groups (flight controls, engines, autopilot, fuel, electrical, lights,
  systems, radios, instruments, GPS, FMS, magnetos, starters, ice ...) with the file's description as title and its
  group as category, plus JoyPro's own axis list (category Axis).
- The list is rebuilt from the file; a command it no longer has leaves the list. X-Plane 11 has its own list
  ([[cli:xplane11 refresh-db]]).
- `--dry-run` reads everything and writes nothing (step "Dry run: nothing was written."). Without it the list is
  written only when it changed.
- Safety: only JoyPro's list changes, never a game file; it is refused when the game is not installed or switched off
  in Settings > Games. The GUI can cancel it while the game's files are read; nothing is written then.
:::

:::layer technical
- Output: JSON of the refresh (`DryRun`, `FilesWritten`, `Steps` with `Actions: <old> -> <new>`, `Warnings`,
  `Errors`), or text with `--text`.
- Exit code `0`; `1` when nothing could be read: `Commands.txt was not found. Pass --path with the X-Plane folder or
  <X-Plane>\Resources\plugins\Commands.txt.`; the list stays unchanged.
- Writes `%LOCALAPPDATA%\JoyPro2\GameData\DB\XPlane12\actions.tsv` (with a header line that has the date). After a
  real run the game's build is recorded for [[cli:db check-updates]].
- Related: [[cli:xplane12 catalog]], [[cli:xplane12 scan]], [[cli:db check-updates]]; game page [[game:xplane]].
:::

<!-- entry: xplane11 import -->
:::layer beginner
Reads the joystick assignments of X-Plane 11 into your JoyPro profile: axis uses, button commands, reverse flags and
response curves. Nothing in X-Plane changes. The GUI runs it with **Import** on the X-Plane 11 card of the Start page.

```powershell
engine xplane11 import "D:\Games\X-Plane 11\Output\preferences\X-Plane Joystick Settings.prf" --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file"
```
:::

:::layer advanced
It reads X-Plane 11's `<install>\Output\preferences\X-Plane Joystick Settings.prf`, or a control profile
`<Name> - Joysticks.prf` given as the path. The version must have been started once with your devices connected.

- Default: adds to the open profile (`--merge` means the same); `--replace` replaces every relation of the profile by
  what X-Plane 11 has.
- No keyboard binds in this file; `--include-keyboard` changes nothing.
- Curves come in as the relation's X-Plane 11 tuning; JoyPro's own data comes back from `<file>.joypro-meta.json`.
- Safety: reads only, not refused while X-Plane runs; the profile is kept as "Before import from X-Plane 11" first.
- `--ignore-device <name>` / `--ignore-devices <a;b>` leave the binds of those devices out (a name without
  `{GUID}` means every device of that name); a step line says how many binds were left out.
:::

:::layer technical
- Output: JSON of the import result or a text report with `--text`; exit code `1` on an error (`No X-Plane 11 config
  file found. Pass the path explicitly.`).
- Without a path it reads the first file of [[cli:xplane11 paths]]; a fresh install builds the command list first.
- With `--profile` the profile is saved afterwards. The imported inputs are recorded as JoyPro's in
  `<file>.joypro.json` (so `merge-overwrite` can later replace them), and a game JoyPro never exported to is watched
  from now on.
- Related: [[cli:xplane11 scan]], [[cli:xplane11 export]], [[cli:xplane12 import]]; game page [[game:xplane]].
:::

<!-- entry: xplane11 export -->
:::layer beginner
Writes your binds into X-Plane 11's joystick preferences, so **Settings > Joystick** shows them. Close X-Plane first.
The GUI's **Export now** on the X-Plane 11 tile runs it.

```powershell
engine xplane11 export --mode merge --dry-run --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" --text
```
:::

:::layer advanced
It edits X-Plane 11's `X-Plane Joystick Settings.prf` (or the control profile you pass) line by line.

| `--mode` | In X-Plane 11 |
|---|---|
| `add` | free inputs only; a bind on an input with another use or command is listed, not written |
| `merge` | replaces the other use or command on your input, keeps everything else; an axis use moves |
| `merge-overwrite` (engine default) | also releases other axes and buttons of connected devices that hold a use or command you bind |
| `clean` (GUI default) | also releases every other flight axis and command on the devices you write |

- `--nuke-untouched-devices`: connected devices you do not write lose their flight uses and commands (not with
  `add`).
- Curves: kept in `add` and `merge` when JoyPro's tuning is linear; written by `merge-overwrite` and `clean`. X-Plane
  11's game settings (control response, stability augmentation, flap detents) change only existing lines.
- Never touched: devices that are not connected, `--ignore-device(s)`, the `--vjoy-tunnel` device, view, camera and
  custom-command axes, plugin and aircraft commands, calibration lines, the keyboard file.
- Safety: refused while `X-Plane` runs (X-Plane 12 too, same process name); backed up first (undo:
  [[cli:backups list]]); the profile is kept as "Before export to X-Plane 11". `--dry-run` writes nothing.
:::

:::layer technical
- Output: JSON of the export result or a text report with `--text`; exit code `1` on an error (X-Plane runs, an
  invalid `--mode`, no file found).
- Without a path it writes the first file of [[cli:xplane11 paths]].
- After a real run: `<file>.joypro.json` and `<file>.joypro-meta.json`, the watcher's fingerprint, the export marks.
- Related: [[cli:xplane11 import]], [[cli:xplane12 export]]; game page [[game:xplane]].
:::

<!-- entry: xplane11 paths -->
:::layer beginner
Lists where JoyPro finds the joystick settings of X-Plane 11 on this PC. The first one is what [[cli:xplane11 scan]],
[[cli:xplane11 import]] and [[cli:xplane11 export]] use when you give no path. **Detect** in the **Other Games** panel
(Start page, **Expert import**) runs it and puts the first one into **File / Folder**.

```powershell
engine xplane11 paths --text
```
:::

:::layer advanced
- Per installation: `<install>\Output\preferences\X-Plane Joystick Settings.prf`, then the saved control profiles
  `...\control profiles\<Name> - Joysticks.prf` (newest first).
- The installations come from `%LOCALAPPDATA%\x-plane_install_11.txt` and Steam (app 269950); a folder set with
  [[cli:games install set]] is used instead.
- A config location you set yourself ([[cli:games config set]], Settings > Games) comes first.
- No options. Reads only; it also works while the game runs or when it is not installed.
:::

:::layer technical
- Output: JSON (`Game`, `DisplayName`, `ConfigPaths`, `Warnings`), or text with `--text` ("X-Plane 11 Config Paths",
  "Paths: <n>" and one line per location). Exit code `0`, also when nothing was found (empty list).
- `engine xplane11` without an action runs `paths`.
- Related: [[cli:xplane11 scan]], [[cli:games paths]], [[cli:games install set]]; game page [[game:xplane]].
:::

<!-- entry: xplane11 catalog -->
:::layer beginner
Lists the X-Plane 11 actions JoyPro manages (its command list): id, axis or button, category and title. An export
writes only these actions, and the relation editor and the search list them.

```powershell
engine xplane11 catalog --text
```
:::

:::layer advanced
- The list is `%LOCALAPPDATA%\JoyPro2\GameData\DB\XPlane11\actions.tsv` (when that folder does not exist, the
  `DB\XPlane11` folder next to the engine).
- It is built from the installed `Commands.txt` by [[cli:xplane11 refresh-db]], or by the first scan, import or
  export.
- `catalog` only shows the list; it never builds it. Titles that look like ids are shown readable.
- No options. Reads only; works without the game installed.
:::

:::layer technical
- Output: JSON (`Game`, `Source`, `Actions` with `Id`, `Title`, `IsAxis`, `Category`, `Warnings`), or text with
  `--text` ("X-Plane 11 Action Catalog", "Source:", "Actions: <n>", then `<id> | axis|button | <category> | <title>`
  per action).
- Without a list: the warning `X-Plane 11 action catalog not found: <path>` and an empty list, exit code `0`.
- Related: [[cli:xplane11 refresh-db]], [[cli:xplane11 export]], [[cli:db games]]; game page [[game:xplane]].
:::

<!-- entry: xplane11 scan -->
:::layer beginner
Shows the joystick binds X-Plane 11 has now, without changing your profile or the game. Use it to look before an
import. **Look first** on the game's card of the Start page and **Scan** in the **Other Games** panel run it.

```powershell
engine xplane11 scan --text
```
:::

:::layer advanced
- It reads the axis uses, button commands, reverse flags and response curves of the joystick settings file. X-Plane 11
  writes the file once it was started with your devices connected.
- Path (positional or `--path`): another `.prf` file, for example a saved control profile.
- `--ignore-device <name>` / `--ignore-devices <a;b>` (separated by `|` or `;`, `--ignore-devices` also by commas) leave the binds of those devices out
  (a name without `{GUID}` means every device of that name); a step line names them and counts the binds.
- Keyboard binds are not read in this game.
- Safety: reads only and is not refused while the game runs. It is refused when the game is not installed (set its
  folder: [[cli:games install set]]) or switched off in Settings > Games.
- A fresh install has no command list yet: the first scan builds it from the installed game and saves it (a step line
  says so).
:::

:::layer technical
- Output: JSON (`ConfigPath`, `BindingsRead`, `Bindings` with `ActionId`, `Device`, `Input`, `IsAxis`, `Reformers`,
  `Inverted`, then `Steps`, `Warnings`, `Errors`), or text with `--text` ("X-Plane 11 Scan", "Config:", "Bindings
  Read:" and the list).
- Exit code `0`; `1` when the result has errors, for example `No X-Plane 11 config file found. Pass the path
  explicitly.`
- Unknown options fail with `Unknown XPlane11 scan argument(s): ...`; MSFS options (`--game-profile`,
  `--include-disconnected`, `--include-sim-defaults`) are refused.
- Binds of the vJoy device JoyPro forwards into (`--vjoy-tunnel`) are left out.
- Related: [[cli:xplane11 import]], [[cli:xplane11 paths]], [[cli:xplane11 catalog]]; game page [[game:xplane]].
:::

<!-- entry: xplane11 refresh-db -->
:::layer beginner
Builds JoyPro's X-Plane 11 command list from the installed `Commands.txt`, or adds the actions that are new. Run it
after a game update, or when JoyPro says the list is missing. **Refresh DB** in the **Other Games** panel (its **DB
Source** box is the path) and the Start page's notice "Command lists to build or update" run it.

```powershell
engine xplane11 refresh-db --dry-run --text
```
:::

:::layer advanced
- It reads `<install>\Resources\plugins\Commands.txt` (path: the X-Plane folder, a folder that holds `Commands.txt`,
  or the file).
- Kept: every `sim/...` command of the flight groups (flight controls, engines, autopilot, fuel, electrical, lights,
  systems, radios, instruments, GPS, FMS, magnetos, starters, ice ...) with the file's description as title and its
  group as category, plus JoyPro's own axis list (category Axis).
- The list is rebuilt from the file; a command it no longer has leaves the list. X-Plane 12 has its own list
  ([[cli:xplane12 refresh-db]]).
- `--dry-run` reads everything and writes nothing (step "Dry run: nothing was written."). Without it the list is
  written only when it changed.
- Safety: only JoyPro's list changes, never a game file; it is refused when the game is not installed or switched off
  in Settings > Games. The GUI can cancel it while the game's files are read; nothing is written then.
:::

:::layer technical
- Output: JSON of the refresh (`DryRun`, `FilesWritten`, `Steps` with `Actions: <old> -> <new>`, `Warnings`,
  `Errors`), or text with `--text`.
- Exit code `0`; `1` when nothing could be read: `Commands.txt was not found. Pass --path with the X-Plane folder or
  <X-Plane>\Resources\plugins\Commands.txt.`; the list stays unchanged.
- Writes `%LOCALAPPDATA%\JoyPro2\GameData\DB\XPlane11\actions.tsv` (with a header line that has the date). After a
  real run the game's build is recorded for [[cli:db check-updates]].
- Related: [[cli:xplane11 catalog]], [[cli:xplane11 scan]], [[cli:db check-updates]]; game page [[game:xplane]].
:::

<!-- entry: fsx import -->
:::layer beginner
Reads the active controls file of Microsoft Flight Simulator X (boxed) into your JoyPro profile: axes, buttons and
hats of your devices with each axis's sensitivity and null zone. Nothing in the sim changes. The GUI runs it with
**Import** on the FSX card of the Start page.

```powershell
engine fsx import --include-keyboard --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" --text
```
:::

:::layer advanced
It reads `%APPDATA%\Microsoft\FSX\Controls\Standard.XML`, or the file `fsx.cfg` names under `[CONTROLS]
Controls_Current`. Commands are the sim's event names (`GEAR_TOGGLE`, `AXIS_AILERONS_SET` ...).

- Default: adds to the open profile (`--merge` means the same); `--replace` replaces every relation of the profile by
  what FSX has.
- `--include-keyboard` also reads the keyboard map (keys with Ctrl, Shift and Alt as modifiers).
- A device is found by the GUID in its map name, else by its name when exactly one connected device has it (warning).
- Safety: reads only, not refused while the sim runs; the profile is kept as "Before import from Microsoft Flight
  Simulator X" first.
- `--ignore-device <name>` / `--ignore-devices <a;b>` leave the binds of those devices out (a name without
  `{GUID}` means every device of that name); a step line says how many binds were left out.
:::

:::layer technical
- Output: JSON of the import result or a text report with `--text` (keyboard binds left out are counted in a step:
  "add --include-keyboard to include them"); exit code `1` on an error (`No Microsoft Flight Simulator X config file
  found. Pass the path explicitly.`).
- Without a path it reads the first file of [[cli:fsx paths]] (the active file); a fresh install builds the command
  list first.
- With `--profile` the profile is saved afterwards. The imported inputs are recorded as JoyPro's in
  `<file>.joypro.json` (so `merge-overwrite` can later replace them), and a game JoyPro never exported to is watched
  from now on.
- Related: [[cli:fsx scan]], [[cli:fsx export]], [[cli:fsxse import]]; game page [[game:fsx-p3d]].
:::

<!-- entry: fsx export -->
:::layer beginner
Writes your binds into the active controls file of Microsoft Flight Simulator X (boxed). Close the sim first. The GUI's
**Export now** on the FSX tile runs it.

```powershell
engine fsx export --mode clean --nuke-untouched-devices --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file"
```
:::

:::layer advanced
It edits the device maps of `%APPDATA%\Microsoft\FSX\Controls\Standard.XML` (or the active file) block by block;
comments and formatting stay.

| `--mode` | In the controls file |
|---|---|
| `add` | never removes or replaces a block; a bind on an input that has another event is listed, not written |
| `merge` | replaces the event on your input and keeps everything else |
| `merge-overwrite` (engine default) | also removes the other inputs of the events you bind (connected devices) |
| `clean` (GUI default) | the main maps of the devices you write keep only your binds |

- `--nuke-untouched-devices`: the main maps of the other connected devices are emptied, the maps stay (not with
  `add`).
- `--include-keyboard` also writes the keyboard map; only then can `merge-overwrite` take keys off an event.
- Axes get sensitivity and null zone (0 to 127); a hat is written as a whole. A device the file does not have gets a
  new map. Joystick buttons cannot carry modifiers.
- Never touched: maps of devices that are not connected, `--ignore-device(s)`, the `--vjoy-tunnel` device, the slew
  maps, `Mouse Yoke`, `Mouse Look`, key chords of two plain keys.
- Safety: refused while `fsx` runs; backed up first (undo: [[cli:backups list]]); the profile is kept as "Before
  export to Microsoft Flight Simulator X". `--dry-run` writes nothing.
:::

:::layer technical
- Output: JSON of the export result or a text report with `--text`; exit code `1` on an error (the sim runs, an
  invalid `--mode`, no file found).
- Without a path it writes the first file of [[cli:fsx paths]] (the active one).
- After a real run: `<file>.joypro.json` and `<file>.joypro-meta.json`, the watcher's fingerprint, the export marks
  ([[cli:export-marks list]]).
- Related: [[cli:fsx import]], [[cli:fsx refresh-db]]; game page [[game:fsx-p3d]].
:::

<!-- entry: fsx paths -->
:::layer beginner
Shows where JoyPro finds the control files of Microsoft Flight Simulator X (boxed) on this PC, in
`%APPDATA%\Microsoft\FSX\Controls\`, the active one first. **Detect** in the import wizard's **Expert import** section
runs it and takes the first.

```powershell
engine fsx paths --text
```
:::

:::layer advanced
- No options. Listed: your own config location of [[cli:games config set]] first, then the active file (the name
  `fsx.cfg` gives under `[CONTROLS] Controls_Current`, else `Standard.xml`), then every other `.xml` file of the folder.
- Read-only; also works when the sim is not installed or switched off.
:::

:::layer technical
- Output: JSON (`Game`, `DisplayName`, `ConfigPaths`, `Warnings`), or with `--text` `Microsoft Flight Simulator X
  Config Paths`, `Paths: <n>` and the files. Exit code `0`; an unknown option fails with `Unknown FSX paths
  argument(s): ...`.
- Related: [[cli:fsx scan]], [[cli:fsxse paths]], [[cli:games paths]]; game page [[game:fsx-p3d]].
:::

<!-- entry: fsx catalog -->
:::layer beginner
Lists the Microsoft Flight Simulator X events JoyPro knows: the sim's event names (`GEAR_TOGGLE`, `AXIS_AILERONS_SET`
...) with axis or button and category.

```powershell
engine fsx catalog --text
```
:::

:::layer advanced
- No options; read-only, also when the sim is not installed.
- The list is JoyPro's own FSX/Prepar3D event list plus the events found in the sim's control files
  ([[cli:fsx refresh-db]]). A fresh install has no list yet; the first scan, import, export or refresh builds it. Until
  then the command lists no action and warns `Microsoft Flight Simulator X action catalog not found: <path>`.
:::

:::layer technical
- Output: JSON (`Game`, `Source`, `Actions` with `Id`, `Title`, `IsAxis`, `Category`, `Warnings`), or with `--text`
  one line per action `<id> | axis|button | <category> | <title>`. Exit code `0`.
- File: `%LOCALAPPDATA%\JoyPro2\GameData\DB\FSX\actions.tsv`.
- Related: [[cli:fsx refresh-db]], [[cli:p3d4 catalog]]; game page [[game:fsx-p3d]].
:::

<!-- entry: fsx scan -->
:::layer beginner
Shows the binds of the active FSX control file, axes, buttons, hats and keyboard keys, without changing your profile
or the sim. **Look first** in the import wizard runs it.

```powershell
engine fsx scan --text
```
:::

:::layer advanced
- Reads the device maps (`<device name>{<GUID>}`) and the keyboard map of the file [[cli:fsx paths]] lists first, or
  of the file given (positional or `--path`).
- A device is found by the GUID in its map name, else by its name when exactly one connected device has it. A hat is
  shown on its up direction (the sim assigns the whole hat).
- Keys with Ctrl, Shift and Alt are listed without `--include-keyboard` (the import takes them only with it).
  Sensitivity and null zone are read but not listed. An event that is not in the list yet gets a warning naming
  [[cli:fsx refresh-db]].
- `--ignore-device <name>` / `--ignore-devices <a;b>` leave the binds of those devices out.
- Safety: reads only, also while the sim runs; a fresh install first builds and saves the command list. Refused when
  the sim is not installed or switched off in Settings > Games.
:::

:::layer technical
- Output: JSON (`Game`, `Action` `scan`, `ConfigPath`, `BindingsRead`, `Bindings`, `Steps`, `Warnings`, `Errors`), or
  a text report with `--text`.
- Exit code `0`; `1` on an error (`No Microsoft Flight Simulator X config file found. Pass the path explicitly.`).
  Unknown options fail with `Unknown FSX scan argument(s): ...`.
- Related: [[cli:fsx import]], [[cli:fsx catalog]]; game page [[game:fsx-p3d]].
:::

<!-- entry: fsx refresh-db -->
:::layer beginner
Adds new events to JoyPro's FSX command list: JoyPro's own FSX/Prepar3D event list, then every event used in the sim's
control files. **Update** on the FSX card of the Start page runs it.

```powershell
engine fsx refresh-db --dry-run --text
```
:::

:::layer advanced
- Rows the list already has stay; nothing is removed. Read without a path: the files of [[cli:fsx paths]], the sim's
  `Standard.xml` in its installation folder and `Controls\Standard.xmlx`.
- A path (positional or `--path`): a folder reads every `*.xml*` file in it, a file only that file.
- Only events of device maps and the keyboard map count. `--dry-run` writes nothing; otherwise the list is written only
  when it changed.
- Refused when the sim is not installed or switched off in Settings > Games; the sim may run.
:::

:::layer technical
- Output: JSON of the refresh (`DryRun`, `FilesWritten`, `Steps` such as `JoyPro's own FSX/Prepar3D event list: <n>
  new event(s).`, `Read <file>: <n> new event(s).` and `Actions: <old> -> <new>`, `Warnings`), or a text report with
  `--text`. A file that cannot be read gives a warning.
- Exit code `0`; `1` only for a refusal or an unknown option.
- Writes `%LOCALAPPDATA%\JoyPro2\GameData\DB\FSX\actions.tsv`.
- Related: [[cli:fsx catalog]], [[cli:fsxse refresh-db]]; game page [[game:fsx-p3d]].
:::

<!-- entry: fsxse import -->
:::layer beginner
Reads the active controls file of Microsoft Flight Simulator X: Steam Edition into your JoyPro profile: axes, buttons
and hats with each axis's sensitivity and null zone. Nothing in the sim changes. The GUI runs it with **Import** on the
FSX: Steam Edition card.

```powershell
engine fsxse import --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" --text
```
:::

:::layer advanced
It reads the controls file in `%APPDATA%\Microsoft\FSX-SE\Controls\` when boxed FSX is installed too, else in
`%APPDATA%\Microsoft\FSX\Controls\`.

- Default: adds to the open profile; `--replace` replaces every relation of the profile by what the sim has.
- `--include-keyboard` also reads the keyboard map.
- A device is found by the GUID in its map name, else by its name when exactly one connected device has it.
- Safety: reads only, not refused while the sim runs; the profile is kept as "Before import from Microsoft Flight
  Simulator X: Steam Edition" first.
- `--ignore-device <name>` / `--ignore-devices <a;b>` leave the binds of those devices out (a name without
  `{GUID}` means every device of that name); a step line says how many binds were left out.
:::

:::layer technical
- Output: JSON of the import result or a text report with `--text`; exit code `1` on an error (no config file found).
- Without a path it reads the first file of [[cli:fsxse paths]]; a fresh install builds the command list first.
- With `--profile` the profile is saved afterwards. The imported inputs are recorded as JoyPro's in
  `<file>.joypro.json` (so `merge-overwrite` can later replace them), and a game JoyPro never exported to is watched
  from now on.
- Related: [[cli:fsxse scan]], [[cli:fsxse export]], [[cli:fsx import]]; game page [[game:fsx-p3d]].
:::

<!-- entry: fsxse export -->
:::layer beginner
Writes your binds into the active controls file of Microsoft Flight Simulator X: Steam Edition. Close the sim first.
The GUI's **Export now** on the FSX: Steam Edition tile runs it.

```powershell
engine fsxse export --mode merge-overwrite --include-keyboard --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file"
```
:::

:::layer advanced
It edits the device maps of the Steam Edition's controls file (`%APPDATA%\Microsoft\FSX-SE\Controls\` next to boxed
FSX, else `%APPDATA%\Microsoft\FSX\Controls\`).

| `--mode` | In the controls file |
|---|---|
| `add` | never removes or replaces a block; a bind on a taken input is listed, not written |
| `merge` | replaces the event on your input, keeps everything else |
| `merge-overwrite` (engine default) | also removes the other inputs of the events you bind |
| `clean` (GUI default) | the main maps of the devices you write keep only your binds |

- `--nuke-untouched-devices` empties the main maps of the other connected devices (not with `add`);
  `--include-keyboard` also writes the keyboard map.
- Never touched: devices that are not connected, `--ignore-device(s)`, the `--vjoy-tunnel` device, slew and mouse
  maps.
- Safety: refused while `fsx` runs; backed up first (undo: [[cli:backups list]]); the profile is kept as "Before
  export to Microsoft Flight Simulator X: Steam Edition". `--dry-run` writes nothing.
:::

:::layer technical
- Output: JSON of the export result or a text report with `--text`; exit code `1` on an error.
- Without a path it writes the first file of [[cli:fsxse paths]]. The Steam Edition's file name is inferred, not yet
  checked on a real install.
- After a real run: `<file>.joypro.json` and `<file>.joypro-meta.json`, the watcher's fingerprint, the export marks.
- Related: [[cli:fsxse import]], [[cli:fsx export]]; game page [[game:fsx-p3d]].
:::

<!-- entry: fsxse paths -->
:::layer beginner
Shows where JoyPro finds the control files of Microsoft Flight Simulator X: Steam Edition on this PC, the active one
first. **Detect** in the import wizard's **Expert import** section runs it.

```powershell
engine fsxse paths --text
```
:::

:::layer advanced
- No options. Listed: your own config location of [[cli:games config set]] first, then the files of
  `%APPDATA%\Microsoft\FSX-SE\Controls\`; when that folder has none, those of `%APPDATA%\Microsoft\FSX\Controls\` (the
  Steam Edition installed alone uses that folder).
- The active file is the one `fsx_se.cfg` (else `fsx.cfg`) names under `[CONTROLS] Controls_Current`, else
  `Standard.xml`; every other `.xml` file follows.
- Read-only; also works when the sim is not installed or switched off.
:::

:::layer technical
- Output: JSON (`Game`, `DisplayName`, `ConfigPaths`, `Warnings`), or with `--text` `Microsoft Flight Simulator X:
  Steam Edition Config Paths`, `Paths: <n>` and the files. Exit code `0`; an unknown option fails with `Unknown FSXSE
  paths argument(s): ...`.
- Related: [[cli:fsxse scan]], [[cli:fsx paths]]; game page [[game:fsx-p3d]].
:::

<!-- entry: fsxse catalog -->
:::layer beginner
Lists the events of Microsoft Flight Simulator X: Steam Edition JoyPro knows: the sim's event names with axis or button
and category.

```powershell
engine fsxse catalog --text
```
:::

:::layer advanced
- No options; read-only, also when the sim is not installed.
- JoyPro's own FSX/Prepar3D event list plus the events found in the sim's control files, in a list of its own. A fresh
  install has no list yet; the first scan, import, export or [[cli:fsxse refresh-db]] builds it (until then: a warning
  and no action).
:::

:::layer technical
- Output: JSON (`Game`, `Source`, `Actions` with `Id`, `Title`, `IsAxis`, `Category`, `Warnings`), or with `--text`
  one line per action `<id> | axis|button | <category> | <title>`. Exit code `0`.
- File: `%LOCALAPPDATA%\JoyPro2\GameData\DB\FSXSE\actions.tsv`.
- Related: [[cli:fsxse refresh-db]], [[cli:fsx catalog]]; game page [[game:fsx-p3d]].
:::

<!-- entry: fsxse scan -->
:::layer beginner
Shows the binds of the active control file of FSX: Steam Edition, axes, buttons, hats and keyboard keys, without
changing your profile or the sim. **Look first** in the import wizard runs it.

```powershell
engine fsxse scan --text
```
:::

:::layer advanced
- Reads the device maps and the keyboard map of the file [[cli:fsxse paths]] lists first, or of the file given
  (positional or `--path`); devices are matched by the GUID in the map name, else by a unique name.
- Keys are listed without `--include-keyboard`; sensitivity and null zone are read but not listed; an event that is
  not in the list yet gets a warning naming [[cli:fsxse refresh-db]].
- `--ignore-device <name>` / `--ignore-devices <a;b>` leave the binds of those devices out.
- Safety: reads only, also while the sim runs; a fresh install first builds and saves the command list. Refused when
  the sim is not installed or switched off in Settings > Games.
:::

:::layer technical
- Output: JSON (`Game`, `Action` `scan`, `ConfigPath`, `BindingsRead`, `Bindings`, `Steps`, `Warnings`, `Errors`), or
  a text report with `--text`.
- Exit code `0`; `1` on an error (no config file found). Unknown options fail with `Unknown FSXSE scan argument(s):
  ...`.
- Related: [[cli:fsxse import]], [[cli:fsx scan]]; game page [[game:fsx-p3d]].
:::

<!-- entry: fsxse refresh-db -->
:::layer beginner
Adds new events to JoyPro's command list of FSX: Steam Edition: JoyPro's own event list, then every event used in the
sim's control files. **Update** on the FSX: Steam Edition card of the Start page runs it.

```powershell
engine fsxse refresh-db --text
```
:::

:::layer advanced
- Rows the list already has stay; nothing is removed. Read without a path: the files of [[cli:fsxse paths]], the
  sim's `Standard.xml` in its installation folder and `Controls\Standard.xmlx`. A path: a folder reads every `*.xml*`
  file in it, a file only that file.
- `--dry-run` writes nothing; otherwise the list is written only when it changed.
- Refused when the sim is not installed or switched off in Settings > Games; the sim may run.
:::

:::layer technical
- Output: JSON of the refresh (`DryRun`, `FilesWritten`, `Steps` with the new events per file and `Actions: <old> ->
  <new>`, `Warnings`), or a text report with `--text`.
- Exit code `0`; `1` only for a refusal or an unknown option.
- Writes `%LOCALAPPDATA%\JoyPro2\GameData\DB\FSXSE\actions.tsv` and records the sim's Steam build for
  [[cli:db check-updates]].
- Related: [[cli:fsxse catalog]], [[cli:fsx refresh-db]]; game page [[game:fsx-p3d]].
:::

<!-- entry: p3d4 import -->
:::layer beginner
Reads the active controls file of Prepar3D v4 into your JoyPro profile: axes, buttons and hats with each axis's
sensitivity and null zone. Nothing in the sim changes. The GUI runs it with **Import** on the Prepar3D v4 card.

```powershell
engine p3d4 import --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" --text
```
:::

:::layer advanced
It reads `%APPDATA%\Lockheed Martin\Prepar3D v4\Controls\Standard.xml`, or the file `Prepar3D.cfg` names under
`[CONTROLS]`.

- Default: adds to the open profile; `--replace` replaces every relation of the profile by what Prepar3D v4 has.
- `--include-keyboard` also reads the keyboard map.
- A device is found by the GUID in its map name, else by its name when exactly one connected device has it.
- Safety: reads only, not refused while the sim runs; the profile is kept as "Before import from Prepar3D v4" first.
- `--ignore-device <name>` / `--ignore-devices <a;b>` leave the binds of those devices out (a name without
  `{GUID}` means every device of that name); a step line says how many binds were left out.
:::

:::layer technical
- Output: JSON of the import result or a text report with `--text`; exit code `1` on an error (no config file found).
- Without a path it reads the first file of [[cli:p3d4 paths]]; a fresh install builds the command list first (it
  also reads the sim's `Standard.xmlx` presets).
- With `--profile` the profile is saved afterwards. The imported inputs are recorded as JoyPro's in
  `<file>.joypro.json` (so `merge-overwrite` can later replace them), and a game JoyPro never exported to is watched
  from now on.
- Related: [[cli:p3d4 scan]], [[cli:p3d4 export]]; game page [[game:fsx-p3d]].
:::

<!-- entry: p3d4 export -->
:::layer beginner
Writes your binds into the active controls file of Prepar3D v4. Close the sim first. The GUI's **Export now** on the
Prepar3D v4 tile runs it.

```powershell
engine p3d4 export --mode clean --nuke-untouched-devices --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file"
```
:::

:::layer advanced
It edits the device maps of `%APPDATA%\Lockheed Martin\Prepar3D v4\Controls\Standard.xml` (or the active file).

| `--mode` | In the controls file |
|---|---|
| `add` | never removes or replaces a block; a bind on a taken input is listed, not written |
| `merge` | replaces the event on your input, keeps everything else |
| `merge-overwrite` (engine default) | also removes the other inputs of the events you bind |
| `clean` (GUI default) | the main maps of the devices you write keep only your binds |

- `--nuke-untouched-devices` empties the main maps of the other connected devices (not with `add`);
  `--include-keyboard` also writes the keyboard map.
- Never touched: devices that are not connected, `--ignore-device(s)`, the `--vjoy-tunnel` device, slew and mouse
  maps, Prepar3D's per-SimObject maps, `Standard.xmlx`.
- Safety: refused while `Prepar3D` runs; backed up first (undo: [[cli:backups list]]); the profile is kept as "Before
  export to Prepar3D v4". `--dry-run` writes nothing.
:::

:::layer technical
- Output: JSON of the export result or a text report with `--text`; exit code `1` on an error (the sim runs, an
  invalid `--mode`, no file found).
- Without a path it writes the first file of [[cli:p3d4 paths]].
- After a real run: `<file>.joypro.json` and `<file>.joypro-meta.json`, the watcher's fingerprint, the export marks.
- Related: [[cli:p3d4 import]], [[cli:p3d5 export]]; game page [[game:fsx-p3d]].
:::

<!-- entry: p3d4 paths -->
:::layer beginner
Shows where JoyPro finds the control files of Prepar3D v4 on this PC, in
`%APPDATA%\Lockheed Martin\Prepar3D v4\Controls\`, the active one first. **Detect** in the import wizard's **Expert
import** section runs it.

```powershell
engine p3d4 paths --text
```
:::

:::layer advanced
- No options. Listed: your own config location of [[cli:games config set]] first, then the active file (the name
  `Prepar3D.cfg` gives under `[CONTROLS] Controls_Current`, else `Standard.xml`), then every other `.xml` file of the
  folder.
- Read-only; also works when the sim is not installed or switched off.
:::

:::layer technical
- Output: JSON (`Game`, `DisplayName`, `ConfigPaths`, `Warnings`), or with `--text` `Prepar3D v4 Config Paths`,
  `Paths: <n>` and the files. Exit code `0`; an unknown option fails with `Unknown P3D4 paths argument(s): ...`.
- Related: [[cli:p3d4 scan]], [[cli:p3d5 paths]], [[cli:games paths]]; game page [[game:fsx-p3d]].
:::

<!-- entry: p3d4 catalog -->
:::layer beginner
Lists the Prepar3D v4 events JoyPro knows: the sim's event names with axis or button and category.

```powershell
engine p3d4 catalog --text
```
:::

:::layer advanced
- No options; read-only, also when the sim is not installed.
- JoyPro's own FSX/Prepar3D event list plus the events found in the sim's control files and presets, in a list of its
  own. A fresh install has no list yet; the first scan, import, export or [[cli:p3d4 refresh-db]] builds it (until
  then: a warning and no action).
:::

:::layer technical
- Output: JSON (`Game`, `Source`, `Actions` with `Id`, `Title`, `IsAxis`, `Category`, `Warnings`), or with `--text`
  one line per action `<id> | axis|button | <category> | <title>`. Exit code `0`.
- File: `%LOCALAPPDATA%\JoyPro2\GameData\DB\P3D4\actions.tsv`.
- Related: [[cli:p3d4 refresh-db]], [[cli:fsx catalog]]; game page [[game:fsx-p3d]].
:::

<!-- entry: p3d4 scan -->
:::layer beginner
Shows the binds of the active Prepar3D v4 control file, axes, buttons, hats and keyboard keys, without changing your
profile or the sim. **Look first** in the import wizard runs it.

```powershell
engine p3d4 scan --text
```
:::

:::layer advanced
- Reads the device maps and the keyboard map of the file [[cli:p3d4 paths]] lists first, or of the file given
  (positional or `--path`); devices are matched by the GUID in the map name, else by a unique name.
- Keys are listed without `--include-keyboard`; sensitivity and null zone are read but not listed; an event that is
  not in the list yet gets a warning naming [[cli:p3d4 refresh-db]].
- `--ignore-device <name>` / `--ignore-devices <a;b>` leave the binds of those devices out.
- Safety: reads only, also while the sim runs; a fresh install first builds and saves the command list. Refused when
  the sim is not installed or switched off in Settings > Games.
:::

:::layer technical
- Output: JSON (`Game`, `Action` `scan`, `ConfigPath`, `BindingsRead`, `Bindings`, `Steps`, `Warnings`, `Errors`), or
  a text report with `--text`.
- Exit code `0`; `1` on an error (`No Prepar3D v4 config file found. Pass the path explicitly.`). Unknown options fail
  with `Unknown P3D4 scan argument(s): ...`.
- Related: [[cli:p3d4 import]], [[cli:p3d4 catalog]]; game page [[game:fsx-p3d]].
:::

<!-- entry: p3d4 refresh-db -->
:::layer beginner
Adds new events to JoyPro's Prepar3D v4 command list: JoyPro's own event list, then every event used in the sim's
control files and presets. **Update** on the Prepar3D v4 card of the Start page runs it.

```powershell
engine p3d4 refresh-db --text
```
:::

:::layer advanced
- Rows the list already has stay; nothing is removed. Read without a path: the files of [[cli:p3d4 paths]],
  `Standard.xml` in the installation folder and the preset `Controls\Standard.xmlx`. A path: a folder reads every
  `*.xml*` file in it, a file only that file.
- `--dry-run` writes nothing; otherwise the list is written only when it changed.
- Refused when the sim is not installed or switched off in Settings > Games; the sim may run.
:::

:::layer technical
- Output: JSON of the refresh (`DryRun`, `FilesWritten`, `Steps` with the new events per file and `Actions: <old> ->
  <new>`, `Warnings`), or a text report with `--text`.
- Exit code `0`; `1` only for a refusal or an unknown option.
- Writes `%LOCALAPPDATA%\JoyPro2\GameData\DB\P3D4\actions.tsv`.
- Related: [[cli:p3d4 catalog]], [[cli:p3d5 refresh-db]]; game page [[game:fsx-p3d]].
:::

<!-- entry: p3d5 import -->
:::layer beginner
Reads the active controls file of Prepar3D v5 into your JoyPro profile: axes, buttons and hats with each axis's
sensitivity and null zone. Nothing in the sim changes. The GUI runs it with **Import** on the Prepar3D v5 card.

```powershell
engine p3d5 import --include-keyboard --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file"
```
:::

:::layer advanced
It reads `%APPDATA%\Lockheed Martin\Prepar3D v5\Controls\Standard.xml`, or the file `Prepar3D.cfg` names under
`[CONTROLS]`.

- Default: adds to the open profile; `--replace` replaces every relation of the profile by what Prepar3D v5 has.
- `--include-keyboard` also reads the keyboard map.
- Safety: reads only, not refused while the sim runs; the profile is kept as "Before import from Prepar3D v5" first.
- `--ignore-device <name>` / `--ignore-devices <a;b>` leave the binds of those devices out (a name without
  `{GUID}` means every device of that name); a step line says how many binds were left out.
:::

:::layer technical
- Output: JSON of the import result or a text report with `--text`; exit code `1` on an error (no config file found).
- Without a path it reads the first file of [[cli:p3d5 paths]]; a fresh install builds the command list first.
- With `--profile` the profile is saved afterwards. The imported inputs are recorded as JoyPro's in
  `<file>.joypro.json` (so `merge-overwrite` can later replace them), and a game JoyPro never exported to is watched
  from now on.
- Related: [[cli:p3d5 scan]], [[cli:p3d5 export]]; game page [[game:fsx-p3d]].
:::

<!-- entry: p3d5 export -->
:::layer beginner
Writes your binds into the active controls file of Prepar3D v5. Close the sim first. The GUI's **Export now** on the
Prepar3D v5 tile runs it.

```powershell
engine p3d5 export --mode clean --nuke-untouched-devices --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file"
```
:::

:::layer advanced
It edits the device maps of `%APPDATA%\Lockheed Martin\Prepar3D v5\Controls\Standard.xml` (or the active file).

| `--mode` | In the controls file |
|---|---|
| `add` | never removes or replaces a block; a bind on a taken input is listed, not written |
| `merge` | replaces the event on your input, keeps everything else |
| `merge-overwrite` (engine default) | also removes the other inputs of the events you bind |
| `clean` (GUI default) | the main maps of the devices you write keep only your binds |

- `--nuke-untouched-devices` empties the main maps of the other connected devices (not with `add`);
  `--include-keyboard` also writes the keyboard map.
- Never touched: devices that are not connected, `--ignore-device(s)`, the `--vjoy-tunnel` device, slew and mouse
  maps, per-SimObject maps.
- Safety: refused while `Prepar3D` runs; backed up first (undo: [[cli:backups list]]); the profile is kept as "Before
  export to Prepar3D v5". `--dry-run` writes nothing.
:::

:::layer technical
- Output: JSON of the export result or a text report with `--text`; exit code `1` on an error.
- Without a path it writes the first file of [[cli:p3d5 paths]].
- After a real run: `<file>.joypro.json` and `<file>.joypro-meta.json`, the watcher's fingerprint, the export marks.
- Related: [[cli:p3d5 import]], [[cli:p3d6 export]]; game page [[game:fsx-p3d]].
:::

<!-- entry: p3d5 paths -->
:::layer beginner
Shows where JoyPro finds the control files of Prepar3D v5 on this PC, in
`%APPDATA%\Lockheed Martin\Prepar3D v5\Controls\`, the active one first. **Detect** in the import wizard's **Expert
import** section runs it.

```powershell
engine p3d5 paths --text
```
:::

:::layer advanced
- No options. Listed: your own config location of [[cli:games config set]] first, then the active file (named in
  `Prepar3D.cfg` under `[CONTROLS] Controls_Current`, else `Standard.xml`), then every other `.xml` file of the folder.
- Read-only; also works when the sim is not installed or switched off.
:::

:::layer technical
- Output: JSON (`Game`, `DisplayName`, `ConfigPaths`, `Warnings`), or with `--text` `Prepar3D v5 Config Paths`,
  `Paths: <n>` and the files. Exit code `0`; an unknown option fails with `Unknown P3D5 paths argument(s): ...`.
- Related: [[cli:p3d5 scan]], [[cli:p3d4 paths]], [[cli:p3d6 paths]]; game page [[game:fsx-p3d]].
:::

<!-- entry: p3d5 catalog -->
:::layer beginner
Lists the Prepar3D v5 events JoyPro knows: the sim's event names with axis or button and category.

```powershell
engine p3d5 catalog --text
```
:::

:::layer advanced
- No options; read-only, also when the sim is not installed.
- JoyPro's own FSX/Prepar3D event list plus the events found in the sim's files, in a list of its own. A fresh install
  has no list yet; the first scan, import, export or [[cli:p3d5 refresh-db]] builds it (until then: a warning and no
  action).
:::

:::layer technical
- Output: JSON (`Game`, `Source`, `Actions` with `Id`, `Title`, `IsAxis`, `Category`, `Warnings`), or with `--text`
  one line per action `<id> | axis|button | <category> | <title>`. Exit code `0`.
- File: `%LOCALAPPDATA%\JoyPro2\GameData\DB\P3D5\actions.tsv`.
- Related: [[cli:p3d5 refresh-db]]; game page [[game:fsx-p3d]].
:::

<!-- entry: p3d5 scan -->
:::layer beginner
Shows the binds of the active Prepar3D v5 control file, axes, buttons, hats and keyboard keys, without changing your
profile or the sim. **Look first** in the import wizard runs it.

```powershell
engine p3d5 scan --text
```
:::

:::layer advanced
- Reads the device maps and the keyboard map of the file [[cli:p3d5 paths]] lists first, or of the file given
  (positional or `--path`); devices are matched by the GUID in the map name, else by a unique name.
- Keys are listed without `--include-keyboard`; sensitivity and null zone are read but not listed; an event that is
  not in the list yet gets a warning naming [[cli:p3d5 refresh-db]].
- `--ignore-device <name>` / `--ignore-devices <a;b>` leave the binds of those devices out.
- Safety: reads only, also while the sim runs; a fresh install first builds and saves the command list. Refused when
  the sim is not installed or switched off in Settings > Games.
:::

:::layer technical
- Output: JSON (`Game`, `Action` `scan`, `ConfigPath`, `BindingsRead`, `Bindings`, `Steps`, `Warnings`, `Errors`), or
  a text report with `--text`.
- Exit code `0`; `1` on an error (`No Prepar3D v5 config file found. Pass the path explicitly.`). Unknown options fail
  with `Unknown P3D5 scan argument(s): ...`.
- Related: [[cli:p3d5 import]], [[cli:p3d5 catalog]]; game page [[game:fsx-p3d]].
:::

<!-- entry: p3d5 refresh-db -->
:::layer beginner
Adds new events to JoyPro's Prepar3D v5 command list: JoyPro's own event list, then every event used in the sim's
control files and presets. **Update** on the Prepar3D v5 card of the Start page runs it.

```powershell
engine p3d5 refresh-db --text
```
:::

:::layer advanced
- Rows the list already has stay; nothing is removed. Read without a path: the files of [[cli:p3d5 paths]],
  `Standard.xml` in the installation folder and the preset `Controls\Standard.xmlx`. A path: a folder reads every
  `*.xml*` file in it, a file only that file.
- `--dry-run` writes nothing; otherwise the list is written only when it changed.
- Refused when the sim is not installed or switched off in Settings > Games; the sim may run.
:::

:::layer technical
- Output: JSON of the refresh (`DryRun`, `FilesWritten`, `Steps` with the new events per file and `Actions: <old> ->
  <new>`, `Warnings`), or a text report with `--text`.
- Exit code `0`; `1` only for a refusal or an unknown option.
- Writes `%LOCALAPPDATA%\JoyPro2\GameData\DB\P3D5\actions.tsv`.
- Related: [[cli:p3d5 catalog]], [[cli:p3d6 refresh-db]]; game page [[game:fsx-p3d]].
:::

<!-- entry: p3d6 import -->
:::layer beginner
Reads the active controls file of Prepar3D v6 into your JoyPro profile: axes, buttons and hats with each axis's
sensitivity and null zone. Nothing in the sim changes. The GUI runs it with **Import** on the Prepar3D v6 card.

```powershell
engine p3d6 import --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" --text
```
:::

:::layer advanced
It reads `%APPDATA%\Lockheed Martin\Prepar3D v6\Controls\Standard.xml`, or the file `Prepar3D.cfg` names under
`[CONTROLS]`.

- Default: adds to the open profile; `--replace` replaces every relation of the profile by what Prepar3D v6 has.
- `--include-keyboard` also reads the keyboard map.
- Safety: reads only, not refused while the sim runs; the profile is kept as "Before import from Prepar3D v6" first.
- `--ignore-device <name>` / `--ignore-devices <a;b>` leave the binds of those devices out (a name without
  `{GUID}` means every device of that name); a step line says how many binds were left out.
:::

:::layer technical
- Output: JSON of the import result or a text report with `--text`; exit code `1` on an error (no config file found).
- Without a path it reads the first file of [[cli:p3d6 paths]]; a fresh install builds the command list first.
- With `--profile` the profile is saved afterwards. The imported inputs are recorded as JoyPro's in
  `<file>.joypro.json` (so `merge-overwrite` can later replace them), and a game JoyPro never exported to is watched
  from now on.
- Related: [[cli:p3d6 scan]], [[cli:p3d6 export]]; game page [[game:fsx-p3d]].
:::

<!-- entry: p3d6 export -->
:::layer beginner
Writes your binds into the active controls file of Prepar3D v6. Close the sim first. The GUI's **Export now** on the
Prepar3D v6 tile runs it.

```powershell
engine p3d6 export --mode clean --nuke-untouched-devices --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file"
```
:::

:::layer advanced
It edits the device maps of `%APPDATA%\Lockheed Martin\Prepar3D v6\Controls\Standard.xml` (or the active file).

| `--mode` | In the controls file |
|---|---|
| `add` | never removes or replaces a block; a bind on a taken input is listed, not written |
| `merge` | replaces the event on your input, keeps everything else |
| `merge-overwrite` (engine default) | also removes the other inputs of the events you bind |
| `clean` (GUI default) | the main maps of the devices you write keep only your binds |

- `--nuke-untouched-devices` empties the main maps of the other connected devices (not with `add`);
  `--include-keyboard` also writes the keyboard map.
- Never touched: devices that are not connected, `--ignore-device(s)`, the `--vjoy-tunnel` device, slew and mouse
  maps, per-SimObject maps.
- Safety: refused while `Prepar3D` runs; backed up first (undo: [[cli:backups list]]); the profile is kept as "Before
  export to Prepar3D v6". `--dry-run` writes nothing.
:::

:::layer technical
- Output: JSON of the export result or a text report with `--text`; exit code `1` on an error.
- Without a path it writes the first file of [[cli:p3d6 paths]].
- After a real run: `<file>.joypro.json` and `<file>.joypro-meta.json`, the watcher's fingerprint, the export marks.
- Related: [[cli:p3d6 import]], [[cli:p3d4 export]]; game page [[game:fsx-p3d]].
:::

<!-- entry: p3d6 paths -->
:::layer beginner
Shows where JoyPro finds the control files of Prepar3D v6 on this PC, in
`%APPDATA%\Lockheed Martin\Prepar3D v6\Controls\`, the active one first. **Detect** in the import wizard's **Expert
import** section runs it.

```powershell
engine p3d6 paths --text
```
:::

:::layer advanced
- No options. Listed: your own config location of [[cli:games config set]] first, then the active file (named in
  `Prepar3D.cfg` under `[CONTROLS] Controls_Current`, else `Standard.xml`), then every other `.xml` file of the folder.
- Read-only; also works when the sim is not installed or switched off.
:::

:::layer technical
- Output: JSON (`Game`, `DisplayName`, `ConfigPaths`, `Warnings`), or with `--text` `Prepar3D v6 Config Paths`,
  `Paths: <n>` and the files. Exit code `0`; an unknown option fails with `Unknown P3D6 paths argument(s): ...`.
- Related: [[cli:p3d6 scan]], [[cli:p3d5 paths]]; game page [[game:fsx-p3d]].
:::

<!-- entry: p3d6 catalog -->
:::layer beginner
Lists the Prepar3D v6 events JoyPro knows: the sim's event names with axis or button and category.

```powershell
engine p3d6 catalog --text
```
:::

:::layer advanced
- No options; read-only, also when the sim is not installed.
- JoyPro's own FSX/Prepar3D event list plus the events found in the sim's files, in a list of its own. A fresh install
  has no list yet; the first scan, import, export or [[cli:p3d6 refresh-db]] builds it (until then: a warning and no
  action).
:::

:::layer technical
- Output: JSON (`Game`, `Source`, `Actions` with `Id`, `Title`, `IsAxis`, `Category`, `Warnings`), or with `--text`
  one line per action `<id> | axis|button | <category> | <title>`. Exit code `0`.
- File: `%LOCALAPPDATA%\JoyPro2\GameData\DB\P3D6\actions.tsv`.
- Related: [[cli:p3d6 refresh-db]]; game page [[game:fsx-p3d]].
:::

<!-- entry: p3d6 scan -->
:::layer beginner
Shows the binds of the active Prepar3D v6 control file, axes, buttons, hats and keyboard keys, without changing your
profile or the sim. **Look first** in the import wizard runs it.

```powershell
engine p3d6 scan --text
```
:::

:::layer advanced
- Reads the device maps and the keyboard map of the file [[cli:p3d6 paths]] lists first, or of the file given
  (positional or `--path`); devices are matched by the GUID in the map name, else by a unique name.
- Keys are listed without `--include-keyboard`; sensitivity and null zone are read but not listed; an event that is
  not in the list yet gets a warning naming [[cli:p3d6 refresh-db]].
- `--ignore-device <name>` / `--ignore-devices <a;b>` leave the binds of those devices out.
- Safety: reads only, also while the sim runs; a fresh install first builds and saves the command list. Refused when
  the sim is not installed or switched off in Settings > Games.
:::

:::layer technical
- Output: JSON (`Game`, `Action` `scan`, `ConfigPath`, `BindingsRead`, `Bindings`, `Steps`, `Warnings`, `Errors`), or
  a text report with `--text`.
- Exit code `0`; `1` on an error (`No Prepar3D v6 config file found. Pass the path explicitly.`). Unknown options fail
  with `Unknown P3D6 scan argument(s): ...`.
- Related: [[cli:p3d6 import]], [[cli:p3d6 catalog]]; game page [[game:fsx-p3d]].
:::

<!-- entry: p3d6 refresh-db -->
:::layer beginner
Adds new events to JoyPro's Prepar3D v6 command list: JoyPro's own event list, then every event used in the sim's
control files and presets. **Update** on the Prepar3D v6 card of the Start page runs it.

```powershell
engine p3d6 refresh-db --text
```
:::

:::layer advanced
- Rows the list already has stay; nothing is removed. Read without a path: the files of [[cli:p3d6 paths]],
  `Standard.xml` in the installation folder and the preset `Controls\Standard.xmlx`. A path: a folder reads every
  `*.xml*` file in it, a file only that file.
- `--dry-run` writes nothing; otherwise the list is written only when it changed.
- Refused when the sim is not installed or switched off in Settings > Games; the sim may run.
:::

:::layer technical
- Output: JSON of the refresh (`DryRun`, `FilesWritten`, `Steps` with the new events per file and `Actions: <old> ->
  <new>`, `Warnings`), or a text report with `--text`.
- Exit code `0`; `1` only for a refusal or an unknown option.
- Writes `%LOCALAPPDATA%\JoyPro2\GameData\DB\P3D6\actions.tsv`.
- Related: [[cli:p3d6 catalog]], [[cli:p3d5 refresh-db]]; game page [[game:fsx-p3d]].
:::

<!-- entry: bms import -->
:::layer beginner
Reads your Falcon BMS controls from the launcher's device files into your JoyPro profile: buttons, hats and axes, with
the shift layer as a modifier and each axis's invert, dead zone and saturation step. Nothing in BMS changes. The GUI
runs it with **Import** on the Falcon BMS card of the Start page.

```powershell
engine bms import --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" --text
```
:::

:::layer advanced
It reads the folder `<BMS>\User\Config`: one `Setup.v100.<device> {<GUID>}.xml` per device (press slots, shifted
slots as the modifier `BMS Shift`, hats, axes, throttle detents).

- Default: adds to the open profile (`--merge` means the same); `--replace` replaces every relation of the profile by
  what BMS has.
- Game settings: the values JoyPro manages in `Falcon BMS User.cfg`, `Feedback.ini` and the pilot file's Enable Force
  Feedback byte become the profile's BMS settings (step line "Game settings imported: ...").
- Keyboard keys stay in BMS's key file and are not read; `--include-keyboard` changes nothing. Release slots are never
  imported.
- Safety: reads only, not refused while BMS runs; the profile is kept as "Before import from Falcon BMS" first.
- `--ignore-device <name>` / `--ignore-devices <a;b>` leave the binds of those devices out (a name without
  `{GUID}` means every device of that name); a step line says how many binds were left out.
:::

:::layer technical
- Output: JSON of the import result or a text report with `--text`; exit code `1` on an error (`No Falcon BMS config
  file found. Pass the path explicitly.`).
- Without a path it reads the first folder of [[cli:bms paths]] (BMS is found through its registry entry or a
  `Falcon BMS*` folder); a fresh install builds the command list from `BMS - Full.key` first.
- With `--profile` the profile is saved afterwards. The imported inputs are recorded as JoyPro's in
  `JoyPro.joypro.json`, and a game JoyPro never exported to is watched from now on.
- Related: [[cli:bms scan]], [[cli:bms export]], [[cli:ffb set]]; game page [[game:falcon-bms]].
:::

<!-- entry: bms export -->
:::layer beginner
Writes your binds into the Falcon BMS launcher's device files. On your next **Launch** the launcher builds the key file
and the axis setup from them. Close BMS and its launcher first. The GUI's **Export now** on the Falcon BMS tile runs it.

```powershell
engine bms export --mode clean --nuke-untouched-devices --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" --text
```
:::

:::layer advanced
It writes `<BMS>\User\Config\Setup.v100.<device> {<GUID>}.xml` per device, and the settings you stored into
`Falcon BMS User.cfg` (above the launcher's marker line), `Feedback.ini` and the pilot file `<callsign>.pop`.

| `--mode` | In BMS |
|---|---|
| `add` | free slots only; a bind on a slot with another callback is listed; a function BMS has on another axis stays there (warning) |
| `merge` | replaces the callback on your slot and keeps everything else; an axis function moves to your axis |
| `merge-overwrite` (engine default) | also releases the other press slots, hats and axes of connected devices that hold an action you bind |
| `clean` (GUI default) | releases everything else of the connected devices you write |

- `--nuke-untouched-devices`: connected devices without binds in the export are cleared (not with `add`). BMS is a
  flight-only game, so this covers every slot of those devices.
- A connected device without a device file gets one (the launcher's stock file of that model, else an empty one).
  Binds with the shift button's modifier go into the shifted slot; other modifiers are skipped with a warning.
- A profile without BMS binds still writes your stored settings and throttle detents.
- Never touched: device files of devices that are not connected, `--ignore-device(s)`, the `--vjoy-tunnel` device,
  keyboard keys and the key file, the files the launcher generates (`axismapping.dat`, `joystick.cal`,
  `DeviceSorting.txt`), the launcher's block in User.cfg. Keyboard binds are not written.
- Safety: refused while `Falcon BMS` or `FalconBMS_Alternative_Launcher` runs; backed up first (`.jpbak` and the
  backup store, undo: [[cli:backups list]]); the read-only attribute is restored; the profile is kept as "Before export
  to Falcon BMS". `--dry-run` writes nothing.
:::

:::layer technical
- Output: JSON of the export result (`BindingsWritten`, `BindingsSkipped`, `FilesWritten`, `SkippedTakenInputs`) or a
  text report with `--text`; exit code `1` on an error (`Falcon BMS is running (...). Close the game first; it
  overwrites its config on exit.`, an invalid `--mode`, no folder found).
- Without a path it writes the first folder of [[cli:bms paths]]. Only changed device files are written.
- A bind whose callback is not in the command list is skipped with a warning.
- After a real run: `JoyPro.joypro.json` and JoyPro's `.joypro-meta.json`, the watcher's fingerprint, the export marks
  ([[cli:export-marks list]]).
- Related: [[cli:bms import]], [[cli:ffb set]] (settings, detents), [[cli:binds tuning preview]]; game page
  [[game:falcon-bms]].
:::

<!-- entry: bms paths -->
:::layer beginner
Lists where JoyPro finds the config folder of Falcon BMS on this PC. The first one is what [[cli:bms scan]],
[[cli:bms import]] and [[cli:bms export]] use when you give no path. **Detect** in the **Other Games** panel (Start
page, **Expert import**) runs it and puts the first one into **File / Folder**.

```powershell
engine bms paths --text
```
:::

:::layer advanced
- `<BMS>\User\Config` of every installation found: the registry entries under `HKLM\SOFTWARE\Benchmark Sims`
  (`baseDir`) and `Falcon BMS*` folders at the root of the fixed drives, sorted by folder name (highest first). A
  folder set with [[cli:games install set]] is used instead.
- A config location you set yourself ([[cli:games config set]], Settings > Games) comes first.
- No options. Reads only; it also works while the game runs or when it is not installed.
:::

:::layer technical
- Output: JSON (`Game`, `DisplayName`, `ConfigPaths`, `Warnings`), or text with `--text` ("Falcon BMS Config Paths",
  "Paths: <n>" and one line per location). Exit code `0`, also when nothing was found (empty list).
- `engine bms` without an action runs `paths`.
- The entries are folders: BMS keeps one `Setup.v100.<device> {<GUID>}.xml` per device there.
- Related: [[cli:bms scan]], [[cli:games paths]], [[cli:games install set]]; game page [[game:falcon-bms]].
:::

<!-- entry: bms catalog -->
:::layer beginner
Lists the Falcon BMS actions JoyPro manages (its command list): id, axis or button, category and title. An export
writes only these actions, and the relation editor and the search list them.

```powershell
engine bms catalog --text
```
:::

:::layer advanced
- The list is `%LOCALAPPDATA%\JoyPro2\GameData\DB\FalconBMS\actions.tsv` (when that folder does not exist, the
  `DB\FalconBMS` folder next to the engine).
- It is built from `BMS - Full.key` by [[cli:bms refresh-db]], or by the first scan, import or export.
- `catalog` only shows the list; it never builds it. Titles that look like ids are shown readable.
- No options. Reads only; works without the game installed.
:::

:::layer technical
- Output: JSON (`Game`, `Source`, `Actions` with `Id`, `Title`, `IsAxis`, `Category`, `Warnings`), or text with
  `--text` ("Falcon BMS Action Catalog", "Source:", "Actions: <n>", then `<id> | axis|button | <category> | <title>`
  per action).
- Without a list: the warning `Falcon BMS action catalog not found: <path>` and an empty list, exit code `0`.
- Related: [[cli:bms refresh-db]], [[cli:bms export]], [[cli:db games]]; game page [[game:falcon-bms]].
:::

<!-- entry: bms scan -->
:::layer beginner
Shows the joystick binds Falcon BMS has now, without changing your profile or the game. Use it to look before an
import. **Look first** on the game's card of the Start page and **Scan** in the **Other Games** panel run it.

```powershell
engine bms scan --text
```
:::

:::layer advanced
- It reads one `Setup.v100.<device> {<GUID>}.xml` per device: press slots, shifted slots as the modifier `BMS Shift`,
  hats, axes and throttle detents.
- Path (positional or `--path`): another `User\Config` folder.
- `--ignore-device <name>` / `--ignore-devices <a;b>` (separated by `|` or `;`, `--ignore-devices` also by commas) leave the binds of those devices out
  (a name without `{GUID}` means every device of that name); a step line names them and counts the binds.
- Keyboard binds are not read in this game.
- Safety: reads only and is not refused while the game runs. It is refused when the game is not installed (set its
  folder: [[cli:games install set]]) or switched off in Settings > Games.
- A fresh install has no command list yet: the first scan builds it from the installed game and saves it (a step line
  says so).
:::

:::layer technical
- Output: JSON (`ConfigPath`, `BindingsRead`, `Bindings` with `ActionId`, `Device`, `Input`, `IsAxis`, `Reformers`,
  `Inverted`, then `Steps`, `Warnings`, `Errors`), or text with `--text` ("Falcon BMS Scan", "Config:", "Bindings
  Read:" and the list).
- Exit code `0`; `1` when the result has errors, for example `No Falcon BMS config file found. Pass the path
  explicitly.`
- Unknown options fail with `Unknown FalconBMS scan argument(s): ...`; MSFS options (`--game-profile`,
  `--include-disconnected`, `--include-sim-defaults`) are refused.
- Binds of the vJoy device JoyPro forwards into (`--vjoy-tunnel`) are left out.
- Related: [[cli:bms import]], [[cli:bms paths]], [[cli:bms catalog]]; game page [[game:falcon-bms]].
:::

<!-- entry: bms refresh-db -->
:::layer beginner
Builds JoyPro's Falcon BMS command list from the installed `BMS - Full.key`, or adds the actions that are new. Run it
after a game update, or when JoyPro says the list is missing. **Refresh DB** in the **Other Games** panel (its **DB
Source** box is the path) and the Start page's notice "Command lists to build or update" run it.

```powershell
engine bms refresh-db --dry-run --text
```
:::

:::layer advanced
- It reads `<BMS>\User\Config\BMS - Full.key` (path: that folder or the key file): the visible callbacks of the
  cockpit and flight sections (2.xx to 5.xx, 6.01 to 6.05 and 6.09) with the description as title and the section as
  category. UI, views, radio menus and development sections stay out.
- JoyPro's list of the launcher's axis functions (`axis/...`, category Axes) is added.
- The list is rebuilt from the key file; a callback it no longer has leaves the list.
- `--dry-run` reads everything and writes nothing (step "Dry run: nothing was written."). Without it the list is
  written only when it changed.
- Safety: only JoyPro's list changes, never a game file; it is refused when the game is not installed or switched off
  in Settings > Games. The GUI can cancel it while the game's files are read; nothing is written then.
:::

:::layer technical
- Output: JSON of the refresh (`DryRun`, `FilesWritten`, `Steps` with `Actions: <old> -> <new>`, `Warnings`,
  `Errors`), or text with `--text`.
- Exit code `0`; `1` when nothing could be read: `'BMS - Full.key' was not found. Pass the BMS User\Config folder or
  the key file.`; the list stays unchanged.
- Writes `%LOCALAPPDATA%\JoyPro2\GameData\DB\FalconBMS\actions.tsv` (with a header line that has the date). After a
  real run the game's build is recorded for [[cli:db check-updates]].
- Related: [[cli:bms catalog]], [[cli:bms scan]], [[cli:db check-updates]]; game page [[game:falcon-bms]].
:::

<!-- entry: il2korea import -->
:::layer beginner
Reads the key mapping of IL-2 Series: Korea into your JoyPro profile: every action on a stick, throttle, pedals or
panel, with modifiers, the invert of each axis and the values of the curve editor. Nothing in the game changes. The GUI
runs it with **Import** on the IL-2 Series: Korea card of the Start page.

```powershell
engine il2korea import --include-keyboard --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" --text
```
:::

:::layer advanced
It reads `<install>\data\input\general.actions` with `known.devices.json` (device numbers), `general.pitch` and
`shared.responses` (curves). `<install>` is `steamapps\common\IL2Series`, or the `game` folder below the standalone
launcher. The game writes `general.actions` when you press **ACCEPT** in Key mapping once.

- Default: adds to the open profile (`--merge` means the same); `--replace` replaces every relation of the profile by
  what Korea has.
- `--include-keyboard` also reads the keyboard keys of the actions.
- Curves and Relative Control come in as the relation's Korea tuning; JoyPro's names, groups, modifiers and tuning come
  back from `general.actions.joypro-meta.json`.
- Safety: reads only, not refused while the game runs; the profile is kept as "Before import from IL-2 Series: Korea"
  first.
- `--ignore-device <name>` / `--ignore-devices <a;b>` leave the binds of those devices out (a name without
  `{GUID}` means every device of that name); a step line says how many binds were left out.
:::

:::layer technical
- Output: JSON of the import result or a text report with `--text`; exit code `1` on an error (`No IL-2 Series: Korea
  config file found. Pass the path explicitly.`).
- The path may be the install folder, the `data\input` folder or the file itself. Without a path it reads the first
  location of [[cli:il2korea paths]]; a fresh install builds the command list first.
- With `--profile` the profile is saved afterwards. The imported inputs are recorded as JoyPro's in
  `<file>.joypro.json`, and a game JoyPro never exported to is watched from now on.
- Related: [[cli:il2korea scan]], [[cli:il2korea export]], [[cli:il2 import]]; game page [[game:il2-korea]].
:::

<!-- entry: il2korea export -->
:::layer beginner
Writes your binds into the key mapping of IL-2 Series: Korea and gives each device you write a device number, as the
game's own dialog does. Close the game first. The GUI's **Export now** on the IL-2 Series: Korea tile runs it.

```powershell
engine il2korea export --mode clean --nuke-untouched-devices --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" --text
```
:::

:::layer advanced
It writes `data\input\general.actions`, `known.devices.json`, the curves in `general.pitch` and `shared.responses`,
and the input settings you stored into `data\NSData\UserData\UserSettings.json`.

| `--mode` | In the game |
|---|---|
| `add` | nothing is removed; a bind whose input the game uses for another action is listed, not written |
| `merge` | your binds go in; the game's other inputs stay, except a game bind on an input you give another action |
| `merge-overwrite` (engine default) | as `merge`, and every action you bind loses its other joystick inputs (keyboard only with `--include-keyboard`) |
| `clean` (GUI default) | every joystick input of the devices you write is released, then your binds go in |

- `--nuke-untouched-devices`: connected devices without a bind in your profile lose their joystick inputs (not with
  `add`).
- `--include-keyboard` also writes your keyboard binds; without it keyboard keys never change.
- A device JoyPro writes keeps its number; a connected device without one gets the lowest free number from 1. A device
  without a number that is not connected is skipped.
- Never touched: keyboard keys, mouse inputs, key pairs on axes, devices that are not connected, lines the import
  cannot read, `--ignore-device(s)`, the `--vjoy-tunnel` device, `current.map`.
- Safety: refused while `IL2Series` runs; backed up first (undo: [[cli:backups list]]); the profile is kept as "Before
  export to IL-2 Series: Korea". `--dry-run` writes nothing. A folder Windows does not let JoyPro write is reported
  instead of writing half the files.
:::

:::layer technical
- Output: JSON of the export result or a text report with `--text`; exit code `1` on an error (`IL-2 Series: Korea is
  running (IL2Series). Close the game first; it overwrites its config on exit.`, an invalid `--mode`, no file found).
- Without a path it writes the first location of [[cli:il2korea paths]].
- After a real run: `<file>.joypro.json`, `general.actions.joypro-meta.json`, the watcher's fingerprint, the export
  marks ([[cli:export-marks list]]).
- Related: [[cli:il2korea import]], [[cli:binds tuning set]], [[cli:ffb set]]; game page [[game:il2-korea]].
:::

<!-- entry: il2korea paths -->
:::layer beginner
Lists where JoyPro finds the key mapping of IL-2 Korea on this PC. The first one is what [[cli:il2korea scan]],
[[cli:il2korea import]] and [[cli:il2korea export]] use when you give no path. **Detect** in the **Other Games** panel
(Start page, **Expert import**) runs it and puts the first one into **File / Folder**.

```powershell
engine il2korea paths --text
```
:::

:::layer advanced
- `<install>\data\input\general.actions` first, then the other `*.actions` files of that folder (newest first).
  `<install>` is the installation JoyPro found (`data\input` or `game\data\input` below it).
- A warning says when the folder or the file is missing: start the game, open Settings > Key mapping and press ACCEPT
  once.
- A config location you set yourself ([[cli:games config set]], Settings > Games) comes first.
- No options. Reads only; it also works while the game runs or when it is not installed.
:::

:::layer technical
- Output: JSON (`Game`, `DisplayName`, `ConfigPaths`, `Warnings`), or text with `--text` ("IL-2 Series: Korea Config
  Paths", "Paths: <n>" and one line per location). Exit code `0`, also when nothing was found (empty list).
- `engine il2korea` without an action runs `paths`.
- Related: [[cli:il2korea scan]], [[cli:games paths]], [[cli:games install set]]; game page [[game:il2-korea]].
:::

<!-- entry: il2korea catalog -->
:::layer beginner
Lists the IL-2 Korea actions JoyPro manages (its command list): id, axis or button, category and title. An export
writes only these actions, and the relation editor and the search list them.

```powershell
engine il2korea catalog --text
```
:::

:::layer advanced
- The list is `%LOCALAPPDATA%\JoyPro2\GameData\DB\IL2Korea\actions.tsv` (when that folder does not exist, the
  `DB\IL2Korea` folder next to the engine).
- It is built from your `general.actions` by [[cli:il2korea refresh-db]], or by the first scan, import or export.
- `catalog` only shows the list; it never builds it. Titles that look like ids are shown readable.
- No options. Reads only; works without the game installed.
:::

:::layer technical
- Output: JSON (`Game`, `Source`, `Actions` with `Id`, `Title`, `IsAxis`, `Category`, `Warnings`), or text with
  `--text` ("IL-2 Series: Korea Action Catalog", "Source:", "Actions: <n>", then `<id> | axis|button | <category> |
  <title>` per action).
- Without a list: the warning `IL-2 Series: Korea action catalog not found: <path>` and an empty list, exit code `0`.
- Related: [[cli:il2korea refresh-db]], [[cli:il2korea export]], [[cli:db games]]; game page [[game:il2-korea]].
:::

<!-- entry: il2korea scan -->
:::layer beginner
Shows the joystick binds IL-2 Korea has now, without changing your profile or the game. Use it to look before an
import. **Look first** on the game's card of the Start page and **Scan** in the **Other Games** panel run it.

```powershell
engine il2korea scan --text
```
:::

:::layer advanced
- It reads `general.actions` with `known.devices.json` (device numbers), `general.pitch` and `shared.responses`
  (curves).
- Path (positional or `--path`): the install folder, the `data\input` folder, the game's exe or an `.actions` file.
- `--ignore-device <name>` / `--ignore-devices <a;b>` (separated by `|` or `;`, `--ignore-devices` also by commas) leave the binds of those devices out
  (a name without `{GUID}` means every device of that name); a step line names them and counts the binds.
- Keyboard binds JoyPro reads in this game are listed too; [[cli:il2korea import]] takes them only with
  `--include-keyboard`.
- Safety: reads only and is not refused while the game runs. It is refused when the game is not installed (set its
  folder: [[cli:games install set]]) or switched off in Settings > Games.
- A fresh install has no command list yet: the first scan builds it from the installed game and saves it (a step line
  says so).
:::

:::layer technical
- Output: JSON (`ConfigPath`, `BindingsRead`, `Bindings` with `ActionId`, `Device`, `Input`, `IsAxis`, `Reformers`,
  `Inverted`, then `Steps`, `Warnings`, `Errors`), or text with `--text` ("IL-2 Series: Korea Scan", "Config:",
  "Bindings Read:" and the list).
- Exit code `0`; `1` when the result has errors, for example `No IL-2 Series: Korea config file found. Pass the path
  explicitly.`
- Unknown options fail with `Unknown IL2Korea scan argument(s): ...`; MSFS options (`--game-profile`,
  `--include-disconnected`, `--include-sim-defaults`) are refused.
- Binds of the vJoy device JoyPro forwards into (`--vjoy-tunnel`) are left out.
- Related: [[cli:il2korea import]], [[cli:il2korea paths]], [[cli:il2korea catalog]]; game page [[game:il2-korea]].
:::

<!-- entry: il2korea refresh-db -->
:::layer beginner
Builds JoyPro's IL-2 Korea command list from your key mapping file, or adds the actions that are new. Run it after a
game update, or when JoyPro says the list is missing. **Refresh DB** in the **Other Games** panel (its **DB Source**
box is the path) and the Start page's notice "Command lists to build or update" run it.

```powershell
engine il2korea refresh-db --dry-run --text
```
:::

:::layer advanced
- It reads every action id of `general.actions` (or of the install folder, `data\input` folder or file given as the
  path); an action whose inputs are axes is an axis.
- Titles: JoyPro's own action texts, else the known title, else IL-2 Great Battles' description of the same id (from
  JoyPro's IL-2 list or the installed Great Battles' `.actions` files), else one made from the id.
- Known actions stay in the list.
- `--dry-run` reads everything and writes nothing (step "Dry run: nothing was written."). Without it the list is
  written only when it changed.
- Safety: only JoyPro's list changes, never a game file; it is refused when the game is not installed or switched off
  in Settings > Games. The GUI can cancel it while the game's files are read; nothing is written then.
:::

:::layer technical
- Output: JSON of the refresh (`DryRun`, `FilesWritten`, `Steps` with `Actions: <old> -> <new>`, `Warnings`,
  `Errors`), or text with `--text`.
- Exit code `0`; `1` when nothing could be read: the read error of the file (for example when the game has not written
  `general.actions` yet); the list stays unchanged.
- Writes `%LOCALAPPDATA%\JoyPro2\GameData\DB\IL2Korea\actions.tsv` (with a header line that has the date). After a
  real run the game's build is recorded for [[cli:db check-updates]].
- Related: [[cli:il2korea catalog]], [[cli:il2korea scan]], [[cli:db check-updates]]; game page [[game:il2-korea]].
:::

<!-- entry: il21946 import -->
:::layer beginner
Reads the current pilot's binds of IL-2 Sturmovik: 1946 into your JoyPro profile, with the dead band and curve of each
axis. Nothing in the game changes. The GUI runs it with **Import** on the IL-2 Sturmovik: 1946 card of the Start page.

```powershell
engine il21946 import --include-keyboard --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" --text
```
:::

:::layer advanced
It reads the pilot's `<install>\Users\<pilot>\settings.ini` (the current pilot first, as `Users\all.ini` names it)
and the axis response lines of `<install>\conf.ini`. `<install>` is `steamapps\common\IL 2 Sturmovik 1946` for Steam.

- Default: adds to the open profile (`--merge` means the same); `--replace` replaces every relation of the profile by
  what the pilot file has.
- `--include-keyboard` also reads the keys (one modifier each).
- Devices are stored by Windows' order (`JoystickDevice0` to `3`): JoyPro maps each to the connected device at that
  position.
- For another pilot, pass that pilot's `settings.ini` as the path.
- Safety: reads only, not refused while the game runs; the profile is kept as "Before import from IL-2 Sturmovik:
  1946" first.
- `--ignore-device <name>` / `--ignore-devices <a;b>` leave the binds of those devices out (a name without
  `{GUID}` means every device of that name); a step line says how many binds were left out.
:::

:::layer technical
- Output: JSON of the import result or a text report with `--text`; exit code `1` on an error (`No IL-2 Sturmovik:
  1946 config file found. Pass the path explicitly.`).
- Without a path it reads the first file of [[cli:il21946 paths]] (the current pilot); a fresh install builds the
  command list first.
- With `--profile` the profile is saved afterwards. The imported inputs are recorded as JoyPro's in
  `settings.ini.joypro.json`, and a game JoyPro never exported to is watched from now on.
- Related: [[cli:il21946 scan]], [[cli:il21946 export]]; game page [[game:il2-1946]].
:::

<!-- entry: il21946 export -->
:::layer beginner
Writes your binds into the pilot's `settings.ini` of IL-2 Sturmovik: 1946 and the axis lines of `conf.ini`, so the
game's Controls page shows them. Close the game first. The GUI's **Export now** on the IL-2 Sturmovik: 1946 tile runs
it.

```powershell
engine il21946 export --mode clean --nuke-untouched-devices --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file"
```
:::

:::layer advanced
It edits the lines of `<install>\Users\<pilot>\settings.ini` in place and the `[rts_joystick]` sections of
`<install>\conf.ini`.

| `--mode` | In the game |
|---|---|
| `add` | nothing is removed; a bind on an input the game uses for another command is listed, not written |
| `merge` | your binds go in; a game bind on an input you give another command is removed, the rest stays |
| `merge-overwrite` (engine default) | as `merge`, and every command you bind loses its other inputs (keyboard only with `--include-keyboard`) |
| `clean` (GUI default) | the devices you write lose all their other lines in the managed sections |

- `--nuke-untouched-devices`: the other connected devices lose their lines (not with `add`).
- `--include-keyboard` also writes keys; joystick binds with modifiers are skipped (warning), because the game ignores
  a key held while a joystick button is pressed.
- The game reads the first four game controllers: a device at position five or later is refused. Axis response:
  dead band, ten curve points, filter and the one-sided flag; in `merge` a curve set in the game stays when JoyPro's is
  the plain default.
- Never touched: lines of devices that are not connected, lines the import cannot read, mouse lines, keyboard lines
  without `--include-keyboard`, other pilots' files, every other line of `conf.ini`.
- Safety: refused while `il2fb` runs; both files backed up first (undo: [[cli:backups list]]); the profile is kept as
  "Before export to IL-2 Sturmovik: 1946". `--dry-run` writes nothing.
:::

:::layer technical
- Output: JSON of the export result or a text report with `--text`; exit code `1` on an error (the game runs, an
  invalid `--mode`, no file found).
- Without a path it writes the current pilot's file of [[cli:il21946 paths]]; pass another `settings.ini` for another
  pilot.
- After a real run: `settings.ini.joypro.json`, JoyPro's `.joypro-meta.json`, the watcher's fingerprint, the export
  marks ([[cli:export-marks list]]).
- Related: [[cli:il21946 import]], [[cli:binds tuning set]]; game page [[game:il2-1946]].
:::

<!-- entry: il21946 paths -->
:::layer beginner
Shows where JoyPro finds IL-2 Sturmovik: 1946's binds on this PC: the pilots' `settings.ini` files in the game's
`Users` folder, the current pilot first. **Detect** in the import wizard's **Expert import** section runs it.

```powershell
engine il21946 paths --text
```
:::

:::layer advanced
- No options. Listed: your own config location first, then `<install>\Users\<pilot>\settings.ini` of the pilot
  `Users\all.ini` names as current, then the other pilots' files in name order.
- The files are found through the installation: when the game is not detected the list is empty; set its folder with
  [[cli:games install set]].
- Read-only.
:::

:::layer technical
- Output: JSON (`Game`, `DisplayName`, `ConfigPaths`, `Warnings`), or with `--text` `IL-2 Sturmovik: 1946 Config
  Paths`, `Paths: <n>` and the files. Exit code `0`; an unknown option fails with `Unknown IL21946 paths argument(s):
  ...`.
- Related: [[cli:il21946 scan]], [[cli:games installs]]; game page [[game:il2-1946]].
:::

<!-- entry: il21946 catalog -->
:::layer beginner
Lists the IL-2 Sturmovik: 1946 commands JoyPro knows: the game's command names per section (`move.aileron`, ...),
with axis or button and category.

```powershell
engine il21946 catalog --text
```
:::

:::layer advanced
- No options; read-only, also when the game is not installed.
- Categories follow the game's sections: Axes, Aircraft, Gunner, Misc, Views, Snap views, Pan views, Head movement,
  Orders and Time. Only the `move` section holds axes.
- A fresh install has no list yet; the first scan, import, export or [[cli:il21946 refresh-db]] builds it. Until then
  the command lists no action and warns `IL-2 Sturmovik: 1946 action catalog not found: <path>`.
:::

:::layer technical
- Output: JSON (`Game`, `Source`, `Actions` with `Id`, `Title`, `IsAxis`, `Category`, `Warnings`), or with `--text`
  one line per action `<id> | axis|button | <category> | <title>`; an id is `<section>.<command>`. Exit code `0`.
- File: `%LOCALAPPDATA%\JoyPro2\GameData\DB\IL21946\actions.tsv`.
- Related: [[cli:il21946 refresh-db]], [[cli:il2cod catalog]]; game page [[game:il2-1946]].
:::

<!-- entry: il21946 scan -->
:::layer beginner
Shows the current pilot's binds of IL-2 Sturmovik: 1946, joystick and keyboard, without changing your profile or the
game. **Look first** in the import wizard runs it.

```powershell
engine il21946 scan --text
```
:::

:::layer advanced
- Reads the `[HotKey <section>]` lines of the file [[cli:il21946 paths]] lists first (the current pilot), or of the
  `settings.ini` given (positional or `--path`), for another pilot.
- Devices are stored by Windows' order (`JoystickDevice0` to `3`): each is shown on the connected device at that
  position. Axes come with invert; keys with their modifier are listed without `--include-keyboard` (the import takes
  them only with it). The axis response of `conf.ini` is read but not listed.
- A command that is not in the list yet gets a warning that names [[cli:il21946 refresh-db]].
- `--ignore-device <name>` / `--ignore-devices <a;b>` leave the binds of those devices out.
- Safety: reads only, also while the game runs; a fresh install first builds and saves the command list. Refused when
  the game is not installed or switched off in Settings > Games.
:::

:::layer technical
- Output: JSON (`Game`, `Action` `scan`, `ConfigPath`, `BindingsRead`, `Bindings` with `Reformers` for modifiers,
  `Steps`, `Warnings`, `Errors`), or a text report with `--text`.
- Exit code `0`; `1` on an error (`No IL-2 Sturmovik: 1946 config file found. Pass the path explicitly.`). Unknown
  options fail with `Unknown IL21946 scan argument(s): ...`.
- Related: [[cli:il21946 import]], [[cli:il21946 catalog]]; game page [[game:il2-1946]].
:::

<!-- entry: il21946 refresh-db -->
:::layer beginner
Adds new IL-2 Sturmovik: 1946 commands to JoyPro's command list: JoyPro's own list of the game's commands, then every
command it finds in the game's and your pilots' files. **Update** on the IL-2 Sturmovik: 1946 card of the Start page
runs it.

```powershell
engine il21946 refresh-db --dry-run --text
```
:::

:::layer advanced
- Rows the list already has stay; nothing is removed. Read without a path: the pilots' `settings.ini` files of
  [[cli:il21946 paths]], `<install>\Users\default.ini` and `<install>\conf.ini`.
- Path (positional or `--path`): a `settings.ini` (that file plus its installation's `default.ini` and `conf.ini`) or
  the installation folder (those two plus every pilot's `settings.ini`).
- Command names with blanks are skipped. `--dry-run` writes nothing; otherwise the list is written only when it
  changed.
- Refused when the game is not installed or switched off in Settings > Games; the game may run.
:::

:::layer technical
- Output: JSON of the refresh (`DryRun`, `FilesWritten`, `Steps` such as `JoyPro's own IL-2 Sturmovik: 1946 command
  list: <n> new command(s).`, `Read <file>: <n> new command(s).` and `Actions: <old> -> <new>`, `Warnings`), or a text
  report with `--text`. A file that cannot be read gives a warning.
- Exit code `0`; `1` only for a refusal or an unknown option.
- Writes `%LOCALAPPDATA%\JoyPro2\GameData\DB\IL21946\actions.tsv` and records the game's Steam build for
  [[cli:db check-updates]].
- Related: [[cli:il21946 catalog]]; game page [[game:il2-1946]].
:::

<!-- entry: il2codblitz import -->
:::layer beginner
Reads the binds of IL-2 Sturmovik: Cliffs of Dover Blitz from `confUser.ini` into your JoyPro profile, with chords as
modifiers and the axis settings of each command. Nothing in the game changes. The GUI runs it with **Import** on the
Cliffs of Dover Blitz card of the Start page.

```powershell
engine il2codblitz import --include-keyboard --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" --text
```
:::

:::layer advanced
It reads `%USERPROFILE%\Documents\1C SoftClub\il-2 sturmovik cliffs of dover\confUser.ini` (the `[HotKey ...]`
sections and `[Config pilotMove]`). Blitz creates the folder on its first start.

- Default: adds to the open profile (`--merge` means the same); `--replace` replaces every relation of the profile by
  what the file has.
- `--include-keyboard` also reads the keys. Inputs held together with the firing one become JoyPro modifiers (keys or
  joystick buttons).
- Blitz names a device `<product name>-<id>`, the id taken from its USB path: JoyPro matches by name and id, else by
  name when only one connected device has it.
- Safety: reads only, not refused while the game runs; the profile is kept as "Before import from IL-2 Sturmovik:
  Cliffs of Dover Blitz" first.
- `--ignore-device <name>` / `--ignore-devices <a;b>` leave the binds of those devices out (a name without
  `{GUID}` means every device of that name); a step line says how many binds were left out.
:::

:::layer technical
- Output: JSON of the import result or a text report with `--text`; exit code `1` on an error (`No IL-2 Sturmovik:
  Cliffs of Dover Blitz config file found. Pass the path explicitly.`).
- Without a path it reads the first file of [[cli:il2codblitz paths]]; a fresh install builds the command list first.
- With `--profile` the profile is saved afterwards. The imported inputs are recorded as JoyPro's in
  `confUser.ini.joypro.json`, and a game JoyPro never exported to is watched from now on.
- Related: [[cli:il2codblitz scan]], [[cli:il2codblitz export]], [[cli:il2cod import]], [[cli:games devices]] (the
  device ids); game page [[game:il2-cliffs-of-dover]].
:::

<!-- entry: il2codblitz export -->
:::layer beginner
Writes your binds into the flight sections of `confUser.ini` of IL-2 Sturmovik: Cliffs of Dover Blitz. Close the game
first. The GUI's **Export now** on the Cliffs of Dover Blitz tile runs it.

```powershell
engine il2codblitz export --mode clean --nuke-untouched-devices --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file"
```
:::

:::layer advanced
It edits `Documents\1C SoftClub\il-2 sturmovik cliffs of dover\confUser.ini` with Blitz's device names. The original
game writes the same file with other names ([[cli:il2cod export]]).

| `--mode` | In the game |
|---|---|
| `add` | nothing is removed; a bind on an input the game uses for another command is listed, not written |
| `merge` | your binds go in; a game bind on an input you give another command is removed, the rest stays |
| `merge-overwrite` (engine default) | as `merge`, and every command you bind loses its other inputs (keyboard only with `--include-keyboard`) |
| `clean` (GUI default) | the devices you write keep only your binds in the managed sections |

- `--nuke-untouched-devices`: the other connected devices lose their lines in the managed sections (not with `add`).
- `--include-keyboard` also writes keys. An old line of a device whose name changed with the USB port is renamed
  (warning).
- Axis settings per command: sensitivity, centre and end dead zone for aileron, elevator and rudder, low and high dead
  zone for the others; in `merge` values set in the game stay when JoyPro's are all zero.
- Never touched: `[HotKey vehicle]` (ground vehicles), mouse lines, keyboard lines without `--include-keyboard`,
  devices that are not connected, lines without a device, unknown sections, `--ignore-device(s)`, the `--vjoy-tunnel`
  device; `conf.ini`, `user.ini` and "Save As" profiles.
- Safety: refused while Blitz's `Launcher64` runs; backed up first (undo: [[cli:backups list]]); the profile is kept
  as "Before export to IL-2 Sturmovik: Cliffs of Dover Blitz". `--dry-run` writes nothing.
:::

:::layer technical
- Output: JSON of the export result or a text report with `--text`; exit code `1` on an error (the game runs, an
  invalid `--mode`, no file found).
- Without a path it writes the first file of [[cli:il2codblitz paths]]. Up to 16 devices.
- After a real run: `confUser.ini.joypro.json`, JoyPro's `.joypro-meta.json`, the watcher's fingerprint, the export
  marks ([[cli:export-marks list]]).
- Related: [[cli:il2codblitz import]], [[cli:il2cod export]]; game page [[game:il2-cliffs-of-dover]].
:::

<!-- entry: il2codblitz paths -->
:::layer beginner
Shows where JoyPro finds the binds of IL-2 Sturmovik: Cliffs of Dover Blitz on this PC:
`%USERPROFILE%\Documents\1C SoftClub\il-2 sturmovik cliffs of dover\confUser.ini`; Blitz creates the folder on its first
start. **Detect** in the import wizard's **Expert import** section runs it.

```powershell
engine il2codblitz paths --text
```
:::

:::layer advanced
- No options. Listed: your own config location of [[cli:games config set]] first, then `confUser.ini` when it exists.
  Blitz and the original game share this file.
- Read-only; also works when the game is not installed or switched off.
:::

:::layer technical
- Output: JSON (`Game`, `DisplayName`, `ConfigPaths`, `Warnings`), or with `--text` `IL-2 Sturmovik: Cliffs of Dover
  Blitz Config Paths`, `Paths: <n>` and the file. Exit code `0`; an unknown option fails with `Unknown IL2CoDBlitz
  paths argument(s): ...`.
- Related: [[cli:il2codblitz scan]], [[cli:il2cod paths]]; game page [[game:il2-cliffs-of-dover]].
:::

<!-- entry: il2codblitz catalog -->
:::layer beginner
Lists the Cliffs of Dover Blitz commands JoyPro knows: the game's command names per section (`pilotMove.aileron`,
...), with axis or button and category.

```powershell
engine il2codblitz catalog --text
```
:::

:::layer advanced
- No options; read-only, also when the game is not installed.
- Categories follow the game's sections: Axes, Aircraft, Common, Gunner, Views, Time, Orders, Game and Vehicle (the
  ground vehicles). Only `pilotMove` holds axes.
- A fresh install has no list yet; the first scan, import, export or [[cli:il2codblitz refresh-db]] builds it. Until
  then the command lists no action and warns `IL-2 Sturmovik: Cliffs of Dover Blitz action catalog not found:
  <path>`.
:::

:::layer technical
- Output: JSON (`Game`, `Source`, `Actions` with `Id`, `Title`, `IsAxis`, `Category`, `Warnings`), or with `--text`
  one line per action `<id> | axis|button | <category> | <title>`; an id is `<section>.<command>`. Exit code `0`.
- File: `%LOCALAPPDATA%\JoyPro2\GameData\DB\IL2CoDBlitz\actions.tsv`.
- Related: [[cli:il2codblitz refresh-db]], [[cli:il2cod catalog]]; game page [[game:il2-cliffs-of-dover]].
:::

<!-- entry: il2codblitz scan -->
:::layer beginner
Shows the binds of Cliffs of Dover Blitz in `confUser.ini`, joystick and keyboard, with chords as modifiers, without
changing your profile or the game. **Look first** in the import wizard runs it.

```powershell
engine il2codblitz scan --text
```
:::

:::layer advanced
- Reads the `[HotKey <section>]` lines of the file [[cli:il2codblitz paths]] lists, or of the file given (positional
  or `--path`). Inputs held together with the firing one are shown as modifiers.
- Blitz names a device `<product name>-<id>`: JoyPro matches name and id, else the name when only one connected device
  has it. A device it cannot match gives one warning.
- Keys are listed without `--include-keyboard` (the import takes them only with it). The axis settings of
  `[Config pilotMove]` are read but not listed. A command that is not in the list yet gets a warning naming
  [[cli:il2codblitz refresh-db]].
- `--ignore-device <name>` / `--ignore-devices <a;b>` leave the binds of those devices out.
- Safety: reads only, also while the game runs; a fresh install first builds and saves the command list. Refused when
  the game is not installed or switched off in Settings > Games.
:::

:::layer technical
- Output: JSON (`Game`, `Action` `scan`, `ConfigPath`, `BindingsRead`, `Bindings` with `Reformers`, `Steps`,
  `Warnings`, `Errors`), or a text report with `--text`.
- Exit code `0`; `1` on an error (`No IL-2 Sturmovik: Cliffs of Dover Blitz config file found. Pass the path
  explicitly.`). Unknown options fail with `Unknown IL2CoDBlitz scan argument(s): ...`.
- Related: [[cli:il2codblitz import]], [[cli:games devices]]; game page [[game:il2-cliffs-of-dover]].
:::

<!-- entry: il2codblitz refresh-db -->
:::layer beginner
Adds new Cliffs of Dover Blitz commands to JoyPro's command list: JoyPro's own list of the game's commands, then every
command found in `confUser.ini` and the game's default binds. **Update** on the Cliffs of Dover Blitz card of the Start
page runs it.

```powershell
engine il2codblitz refresh-db --text
```
:::

:::layer advanced
- Rows the list already has stay; nothing is removed. Read without a path: the file of [[cli:il2codblitz paths]] and
  `confUser.ini` in the installation folder (the defaults a never-started install has).
- A path (positional or `--path`) reads only that file, or `confUser.ini` in that folder.
- Command names with blanks are skipped. `--dry-run` writes nothing; otherwise the list is written only when it
  changed.
- Refused when the game is not installed or switched off in Settings > Games; the game may run.
:::

:::layer technical
- Output: JSON of the refresh (`DryRun`, `FilesWritten`, `Steps` with `JoyPro's own ... command list: <n> new
  command(s).`, `Read <file>: <n> new command(s).` and `Actions: <old> -> <new>`, `Warnings`), or a text report with
  `--text`.
- Exit code `0`; `1` only for a refusal or an unknown option.
- Writes `%LOCALAPPDATA%\JoyPro2\GameData\DB\IL2CoDBlitz\actions.tsv` and records the game's Steam build for
  [[cli:db check-updates]].
- Related: [[cli:il2codblitz catalog]], [[cli:il2cod refresh-db]]; game page [[game:il2-cliffs-of-dover]].
:::

<!-- entry: il2cod import -->
:::layer beginner
Reads the binds of the original IL-2 Sturmovik: Cliffs of Dover from `confUser.ini` into your JoyPro profile, with
chords as modifiers and the axis settings of each command. Nothing in the game changes. The GUI runs it with
**Import** on the Cliffs of Dover card of the Start page.

```powershell
engine il2cod import --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" --text
```
:::

:::layer advanced
It reads `%USERPROFILE%\Documents\1C SoftClub\il-2 sturmovik cliffs of dover\confUser.ini`; the pre-Blitz mod
folder `...\il-2 sturmovik cliffs of dover - MOD\confUser.ini` is listed too.

- Default: adds to the open profile (`--merge` means the same); `--replace` replaces every relation of the profile by
  what the file has.
- `--include-keyboard` also reads the keys; chords become JoyPro modifiers.
- The original names devices `Joystick`, `Joystick1`, `Joystick2` ... in DirectInput order (up to 8): JoyPro maps each
  name to the connected device at that position.
- Safety: reads only, not refused while the game runs; the profile is kept as "Before import from IL-2 Sturmovik:
  Cliffs of Dover" first.
- `--ignore-device <name>` / `--ignore-devices <a;b>` leave the binds of those devices out (a name without
  `{GUID}` means every device of that name); a step line says how many binds were left out.
:::

:::layer technical
- Output: JSON of the import result or a text report with `--text`; exit code `1` on an error (no config file found).
- Without a path it reads the first file of [[cli:il2cod paths]]; a fresh install builds the command list first.
- With `--profile` the profile is saved afterwards. The imported inputs are recorded as JoyPro's in
  `confUser.ini.joypro.json`, and a game JoyPro never exported to is watched from now on.
- Related: [[cli:il2cod scan]], [[cli:il2cod export]], [[cli:il2codblitz import]]; game page
  [[game:il2-cliffs-of-dover]].
:::

<!-- entry: il2cod export -->
:::layer beginner
Writes your binds into the flight sections of `confUser.ini` of the original IL-2 Sturmovik: Cliffs of Dover. Close
the game first. The GUI's **Export now** on the Cliffs of Dover tile runs it.

```powershell
engine il2cod export --mode merge --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" --text
```
:::

:::layer advanced
It edits `Documents\1C SoftClub\il-2 sturmovik cliffs of dover\confUser.ini` with the original's device names
(`Joystick`, `Joystick1` ...); Blitz uses the same file with its own names ([[cli:il2codblitz export]]).

| `--mode` | In the game |
|---|---|
| `add` | nothing is removed; a bind on a taken input is listed, not written |
| `merge` | your binds go in; a game bind on an input you give another command is removed, the rest stays |
| `merge-overwrite` (engine default) | as `merge`, and every command you bind loses its other inputs (keyboard only with `--include-keyboard`) |
| `clean` (GUI default) | the devices you write keep only your binds in the managed sections |

- `--nuke-untouched-devices`: the other connected devices lose their lines in the managed sections (not with `add`).
- The original reads the first 8 game controllers; a device further down is refused.
- Never touched: `[HotKey vehicle]`, mouse lines, keyboard lines without `--include-keyboard`, devices that are not
  connected, unknown sections, `--ignore-device(s)`, the `--vjoy-tunnel` device.
- Safety: refused while the game's own `Launcher` runs (another program with that name does not count); backed up
  first (undo: [[cli:backups list]]); the profile is kept as "Before export to IL-2 Sturmovik: Cliffs of Dover".
  `--dry-run` writes nothing.
:::

:::layer technical
- Output: JSON of the export result or a text report with `--text`; exit code `1` on an error (the game runs, an
  invalid `--mode`, no file found).
- Without a path it writes the first file of [[cli:il2cod paths]].
- After a real run: `confUser.ini.joypro.json`, JoyPro's `.joypro-meta.json`, the watcher's fingerprint, the export
  marks.
- Related: [[cli:il2cod import]], [[cli:il2codblitz export]]; game page [[game:il2-cliffs-of-dover]].
:::

<!-- entry: il2cod paths -->
:::layer beginner
Shows where JoyPro finds the binds of the original IL-2 Sturmovik: Cliffs of Dover on this PC:
`%USERPROFILE%\Documents\1C SoftClub\il-2 sturmovik cliffs of dover\confUser.ini`. **Detect** in the import wizard's
**Expert import** section runs it.

```powershell
engine il2cod paths --text
```
:::

:::layer advanced
- No options. Listed: your own config location of [[cli:games config set]] first, then `confUser.ini` and the one of
  the pre-Blitz mod folder `...\il-2 sturmovik cliffs of dover - MOD\confUser.ini`, each when it exists.
- Read-only; also works when the game is not installed or switched off.
:::

:::layer technical
- Output: JSON (`Game`, `DisplayName`, `ConfigPaths`, `Warnings`), or with `--text` `IL-2 Sturmovik: Cliffs of Dover
  Config Paths`, `Paths: <n>` and the files. Exit code `0`; an unknown option fails with `Unknown IL2CoD paths
  argument(s): ...`.
- Related: [[cli:il2cod scan]], [[cli:il2codblitz paths]]; game page [[game:il2-cliffs-of-dover]].
:::

<!-- entry: il2cod catalog -->
:::layer beginner
Lists the Cliffs of Dover commands JoyPro knows: the game's command names per section (`pilotMove.aileron`, ...), with
axis or button and category.

```powershell
engine il2cod catalog --text
```
:::

:::layer advanced
- No options; read-only, also when the game is not installed.
- Same sections and categories as Blitz (Axes, Aircraft, Common, Gunner, Views, Time, Orders, Game, Vehicle), in a
  list of its own.
- A fresh install has no list yet; the first scan, import, export or [[cli:il2cod refresh-db]] builds it. Until then
  the command lists no action and warns `IL-2 Sturmovik: Cliffs of Dover action catalog not found: <path>`.
:::

:::layer technical
- Output: JSON (`Game`, `Source`, `Actions` with `Id`, `Title`, `IsAxis`, `Category`, `Warnings`), or with `--text`
  one line per action `<id> | axis|button | <category> | <title>`. Exit code `0`.
- File: `%LOCALAPPDATA%\JoyPro2\GameData\DB\IL2CoD\actions.tsv`.
- Related: [[cli:il2cod refresh-db]], [[cli:il2codblitz catalog]]; game page [[game:il2-cliffs-of-dover]].
:::

<!-- entry: il2cod scan -->
:::layer beginner
Shows the binds of the original Cliffs of Dover in `confUser.ini`, joystick and keyboard, with chords as modifiers,
without changing your profile or the game. **Look first** in the import wizard runs it.

```powershell
engine il2cod scan --text
```
:::

:::layer advanced
- Reads the `[HotKey <section>]` lines of the first file [[cli:il2cod paths]] lists, or of the file given (positional
  or `--path`).
- The original names devices `Joystick`, `Joystick1`, `Joystick2` ... in DirectInput order: each name is shown on the
  connected device at that position.
- Keys are listed without `--include-keyboard`; axis settings are read but not listed; a command that is not in the
  list yet gets a warning naming [[cli:il2cod refresh-db]].
- `--ignore-device <name>` / `--ignore-devices <a;b>` leave the binds of those devices out.
- Safety: reads only, also while the game runs; a fresh install first builds and saves the command list. Refused when
  the game is not installed or switched off in Settings > Games.
:::

:::layer technical
- Output: JSON (`Game`, `Action` `scan`, `ConfigPath`, `BindingsRead`, `Bindings`, `Steps`, `Warnings`, `Errors`), or
  a text report with `--text`.
- Exit code `0`; `1` on an error (no config file found). Unknown options fail with `Unknown IL2CoD scan argument(s):
  ...`.
- Related: [[cli:il2cod import]], [[cli:il2codblitz scan]]; game page [[game:il2-cliffs-of-dover]].
:::

<!-- entry: il2cod refresh-db -->
:::layer beginner
Adds new Cliffs of Dover commands to JoyPro's command list: JoyPro's own list of the game's commands, then every
command found in your `confUser.ini` and the game's default binds. **Update** on the Cliffs of Dover card of the Start
page runs it.

```powershell
engine il2cod refresh-db --text
```
:::

:::layer advanced
- Rows the list already has stay; nothing is removed. Read without a path: the files of [[cli:il2cod paths]] (the mod
  folder's too) and `confUser.ini` in the installation folder.
- A path (positional or `--path`) reads only that file, or `confUser.ini` in that folder.
- Command names with blanks are skipped. `--dry-run` writes nothing; otherwise the list is written only when it
  changed.
- Refused when the game is not installed or switched off in Settings > Games; the game may run.
:::

:::layer technical
- Output: JSON of the refresh (`DryRun`, `FilesWritten`, `Steps` with the new commands per file and `Actions: <old> ->
  <new>`, `Warnings`), or a text report with `--text`.
- Exit code `0`; `1` only for a refusal or an unknown option.
- Writes `%LOCALAPPDATA%\JoyPro2\GameData\DB\IL2CoD\actions.tsv` and records the game's Steam build for
  [[cli:db check-updates]].
- Related: [[cli:il2cod catalog]], [[cli:il2codblitz refresh-db]]; game page [[game:il2-cliffs-of-dover]].
:::

<!-- entry: elite import -->
:::layer beginner
Reads the ship flight binds of your Elite Dangerous preset into your JoyPro profile, with invert, dead zone and the
throttle range. Nothing in the game changes. The GUI runs it with **Import** on the Elite Dangerous card of the Start
page.

```powershell
engine elite import --include-keyboard --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" --text
```
:::

:::layer advanced
It reads the Ship preset's `<preset>.4.<minor>.binds` in
`%LOCALAPPDATA%\Frontier Developments\Elite Dangerous\Options\Bindings\` (`StartPreset.4.start` says which preset each
category uses).

- Default: adds to the open profile (`--merge` means the same); `--replace` replaces every relation of the profile by
  what the preset has.
- `--include-keyboard` also reads the keyboard keys of the flight actions. Up to three modifiers per place (keys or
  buttons of any device) become JoyPro modifiers.
- The throttle's **Throttle Axis Range** comes back as the relation's Slider (forward only) or no Slider (full range).
  Invert keeps DCS's direction: for pitch, throttle and the vertical thrusters the file's Inverted is turned over.
- A bind that uses half an axis as a button or modifier is skipped with a warning (the export leaves it in the game).
- Safety: reads only, not refused while the game runs; the profile is kept as "Before import from Elite Dangerous
  (experimental)" first.
- `--ignore-device <name>` / `--ignore-devices <a;b>` leave the binds of those devices out (a name without
  `{GUID}` means every device of that name); a step line says how many binds were left out.
:::

:::layer technical
- Output: JSON of the import result or a text report with `--text`; exit code `1` on an error (`No Elite Dangerous
  (experimental) config file found. Pass the path explicitly.`).
- Without a path it reads the file [[cli:elite paths]] lists first; a fresh install builds the command list from the
  game's presets first.
- With `--profile` the profile is saved afterwards; a game JoyPro never exported to is watched from now on.
- Related: [[cli:elite scan]], [[cli:elite export]], [[cli:starcitizen import]]; game page [[game:elite-dangerous]].
:::

<!-- entry: elite export -->
:::layer beginner
Writes your ship flight binds into your Elite Dangerous Ship preset (`Custom.4.x.binds`) in place; everything else in
the file stays. Close the game first, and save your controls once in the game so the preset exists. The GUI's **Export
now** on the Elite Dangerous tile runs it.

```powershell
engine elite export --mode clean --nuke-untouched-devices --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" --text
```
:::

:::layer advanced
Elite keeps two places (Primary, Secondary) per action. Every mode works on ship flight actions and on the places of
connected joysticks only.

| `--mode` | In the preset |
|---|---|
| `add` | a bind goes only into a free place; a bind whose input another flight action uses gets a mark "Not written to the game" |
| `merge` | the place of another flight action on your input is released; your bind takes a free place first |
| `merge-overwrite` (engine default) | as `merge`, and every action you bind loses its other places on your joysticks (keyboard only with `--include-keyboard`) |
| `clean` (GUI default) | on the devices you write, every flight place not in your profile is emptied (`{NoDevice}`) |

- `--nuke-untouched-devices`: the same for the flight places of connected joysticks the export does not use (not with
  `add`).
- A place holding a keyboard key, a mouse or pad input or a device JoyPro does not write is never overwritten; with no
  place left the bind is listed under "Binds Not Written, No Free Place In The Game" (every mode).
- `--include-keyboard` writes your flight keys; the keyboard is never cleared. Galaxy map actions are written only
  when General and Ship use the same file. Game settings you stored (yaw into roll, head look ...) change only keys
  the file has; a profile without Elite binds writes only those.
- Never touched: SRV, on-foot, camera, UI and mouse actions, half-axis binds, `<Hold>` and `<ToggleOn>`,
  `StartPreset.4.start`, the install folder, devices that are not connected, `--ignore-device(s)`, the `--vjoy-tunnel`
  device. Two identical sticks are both skipped.
- Safety: refused while `EliteDangerous64` runs; backed up first (`.jpbak` and the backup store, undo:
  [[cli:backups list]]); the profile is kept as "Before export to Elite Dangerous (experimental)". `--dry-run` writes
  nothing.
:::

:::layer technical
- Output: JSON of the export result (`SkippedTakenInputs` with the reason per bind) or a text report with `--text`;
  exit code `1` on an error (`Elite Dangerous (experimental) is running (...). Close the game first; it overwrites its
  config on exit.`, an invalid `--mode`, no preset found).
- Without a path it writes the preset [[cli:elite paths]] lists first. Line endings, tabs and unknown elements stay
  byte for byte.
- After a real run: `<preset>.binds.joypro-meta.json`, the watcher's fingerprint, the export marks
  ([[cli:export-marks list]]).
- The help note says "only ship flight actions": clean and nuke release only the ship flight actions of the command
  list on the written and unused devices.
- Related: [[cli:elite import]], [[cli:ffb set]], [[cli:export-marks dismiss]]; game page [[game:elite-dangerous]].
:::

<!-- entry: elite paths -->
:::layer beginner
Lists where JoyPro finds the Ship controls preset of Elite Dangerous on this PC. The first one is what
[[cli:elite scan]], [[cli:elite import]] and [[cli:elite export]] use when you give no path. **Detect** in the **Other
Games** panel (Start page, **Expert import**) runs it and puts the first one into **File / Folder**.

```powershell
engine elite paths --text
```
:::

:::layer advanced
- The Ship preset `<preset>.4.<minor>.binds` (the highest minor version) in `%LOCALAPPDATA%\Frontier
  Developments\Elite Dangerous\Options\Bindings\`; `StartPreset.4.start` names the preset (else `Custom`).
- A stock preset has no file: nothing is listed and a warning says to change one control in Elite (Options > Controls,
  Ship) and save, so the game creates `Custom.4.x.binds`.
- A config location you set yourself ([[cli:games config set]], Settings > Games) comes first.
- No options. Reads only; it also works while the game runs or when it is not installed.
:::

:::layer technical
- Output: JSON (`Game`, `DisplayName`, `ConfigPaths`, `Warnings`), or text with `--text` ("Elite Dangerous
  (experimental) Config Paths", "Paths: <n>" and one line per location). Exit code `0`, also when nothing was found
  (empty list).
- `engine elite` without an action runs `paths`.
- Related: [[cli:elite scan]], [[cli:games paths]], [[cli:games install set]]; game page [[game:elite-dangerous]].
:::

<!-- entry: elite catalog -->
:::layer beginner
Lists the Elite Dangerous actions JoyPro manages (its command list): id, axis or button, category and title. An export
writes only these actions, and the relation editor and the search list them.

```powershell
engine elite catalog --text
```
:::

:::layer advanced
- The list is `%LOCALAPPDATA%\JoyPro2\GameData\DB\EliteDangerous\actions.tsv` (when that folder does not exist, the
  `DB\EliteDangerous` folder next to the engine).
- It is built from the game's `.binds` files by [[cli:elite refresh-db]], or by the first scan, import or export.
- `catalog` only shows the list; it never builds it. Titles that look like ids are shown readable.
- No options. Reads only; works without the game installed.
:::

:::layer technical
- Output: JSON (`Game`, `Source`, `Actions` with `Id`, `Title`, `IsAxis`, `Category`, `Warnings`), or text with
  `--text` ("Elite Dangerous (experimental) Action Catalog", "Source:", "Actions: <n>", then `<id> | axis|button |
  <category> | <title>` per action).
- Without a list: the warning `Elite Dangerous (experimental) action catalog not found: <path>` and an empty list,
  exit code `0`.
- Related: [[cli:elite refresh-db]], [[cli:elite export]], [[cli:db games]]; game page [[game:elite-dangerous]].
:::

<!-- entry: elite scan -->
:::layer beginner
Shows the joystick binds Elite Dangerous has now, without changing your profile or the game. Use it to look before an
import. **Look first** on the game's card of the Start page and **Scan** in the **Other Games** panel run it.

```powershell
engine elite scan --text
```
:::

:::layer advanced
- It reads the preset's ship flight binds with up to three modifiers per place and the throttle's axis range.
- Path (positional or `--path`): another `.binds` file.
- `--ignore-device <name>` / `--ignore-devices <a;b>` (separated by `|` or `;`, `--ignore-devices` also by commas) leave the binds of those devices out
  (a name without `{GUID}` means every device of that name); a step line names them and counts the binds.
- Keyboard binds JoyPro reads in this game are listed too; [[cli:elite import]] takes them only with
  `--include-keyboard`.
- Safety: reads only and is not refused while the game runs. It is refused when the game is not installed (set its
  folder: [[cli:games install set]]) or switched off in Settings > Games.
- A fresh install has no command list yet: the first scan builds it from the installed game and saves it (a step line
  says so).
:::

:::layer technical
- Output: JSON (`ConfigPath`, `BindingsRead`, `Bindings` with `ActionId`, `Device`, `Input`, `IsAxis`, `Reformers`,
  `Inverted`, then `Steps`, `Warnings`, `Errors`), or text with `--text` ("Elite Dangerous (experimental) Scan",
  "Config:", "Bindings Read:" and the list).
- Exit code `0`; `1` when the result has errors, for example `No Elite Dangerous (experimental) config file found.
  Pass the path explicitly.`
- Unknown options fail with `Unknown EliteDangerous scan argument(s): ...`; MSFS options (`--game-profile`,
  `--include-disconnected`, `--include-sim-defaults`) are refused.
- Binds of the vJoy device JoyPro forwards into (`--vjoy-tunnel`) are left out.
- Related: [[cli:elite import]], [[cli:elite paths]], [[cli:elite catalog]]; game page [[game:elite-dangerous]].
:::

<!-- entry: elite refresh-db -->
:::layer beginner
Builds JoyPro's Elite Dangerous command list from the game's control presets, or adds the actions that are new. Run it
after a game update, or when JoyPro says the list is missing. **Refresh DB** in the **Other Games** panel (its **DB
Source** box is the path) and the Start page's notice "Command lists to build or update" run it.

```powershell
engine elite refresh-db --dry-run --text
```
:::

:::layer advanced
- Without a path it reads every `.binds` file of your `Options\Bindings` folder and of the game folder
  (`ControlSchemes`, `Products\*\ControlSchemes`). Path: one `.binds` file or a folder.
- Kept: the ship flight elements (an element with `<Binding>` is an axis, with `<Primary>`/`<Secondary>` a button);
  SRV, on-foot, camera and UI elements are counted and left out.
- Known actions keep their entry.
- `--dry-run` reads everything and writes nothing (step "Dry run: nothing was written."). Without it the list is
  written only when it changed.
- Safety: only JoyPro's list changes, never a game file; it is refused when the game is not installed or switched off
  in Settings > Games. The GUI can cancel it while the game's files are read; nothing is written then.
:::

:::layer technical
- Output: JSON of the refresh (`DryRun`, `FilesWritten`, `Steps` with `Actions: <old> -> <new>`, `Warnings`,
  `Errors`), or text with `--text`.
- Exit code `0`; `1` when nothing could be read: `No Elite Dangerous .binds files were found. Pass the game folder,
  its ControlSchemes folder or a .binds file (engine elite refresh-db <path>), or set the game folder (engine games
  install set EliteDangerous <folder>).`; the list stays unchanged.
- Writes `%LOCALAPPDATA%\JoyPro2\GameData\DB\EliteDangerous\actions.tsv` (with a header line that has the date). After
  a real run the game's build is recorded for [[cli:db check-updates]].
- Related: [[cli:elite catalog]], [[cli:elite scan]], [[cli:db check-updates]]; game page [[game:elite-dangerous]].
:::

<!-- entry: x4 import -->
:::layer beginner
Reads the space flight binds of X4: Foundations into your JoyPro profile, with the game's inverts and throttle mode.
Nothing in the game changes. The GUI runs it with **Import** on the X4 card of the Start page.

```powershell
engine x4 import --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" --text
```
:::

:::layer advanced
It reads `%USERPROFILE%\Documents\Egosoft\X4\<id>\inputmap.xml` (the active binds and `<config>` params) and the
device slots of `config.xml` in the same folder. `<id>` is the Steam account's folder.

- Default: adds to the open profile (`--merge` means the same); `--replace` replaces every relation of the profile by
  what X4 has.
- `--include-keyboard` also reads the flight controls' keyboard lines.
- Devices are matched to X4's slots through `config.xml` (DirectInput instance GUID).
- Safety: reads only, not refused while the game runs; the profile is kept as "Before import from X4: Foundations
  (experimental)" first.
- `--ignore-device <name>` / `--ignore-devices <a;b>` leave the binds of those devices out (a name without
  `{GUID}` means every device of that name); a step line says how many binds were left out.
:::

:::layer technical
- Output: JSON of the import result or a text report with `--text`; exit code `1` on an error (`No X4: Foundations
  (experimental) config file found. Pass the path explicitly.`).
- Without a path it reads the file [[cli:x4 paths]] lists first; a fresh install builds the command list from the
  game's files first.
- With `--profile` the profile is saved afterwards; a game JoyPro never exported to is watched from now on.
- Related: [[cli:x4 scan]], [[cli:x4 export]]; game page [[game:x4]].
:::

<!-- entry: x4 export -->
:::layer beginner
Writes your space flight binds into X4's `inputmap.xml` in place, on the slots of your devices; menu, first-person,
keyboard and mouse binds stay. Close X4 first, and pin every device to a slot once in the game. The GUI's **Export
now** on the X4 tile runs it.

```powershell
engine x4 export --mode clean --nuke-untouched-devices --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" --text
```
:::

:::layer advanced
A conflict is another flight control on the same slot and input; the map and detail-monitor twins of a control count as
one control.

| `--mode` | In `inputmap.xml` |
|---|---|
| `add` | nothing is removed; a bind whose input another flight control has gets a mark "Not written to the game" |
| `merge` | your lines go in beside the control's other inputs; only conflicts are removed |
| `merge-overwrite` (engine default) | as `merge`, and a control you bind loses its other lines on pinned slots of connected devices |
| `clean` (GUI default) | the flight binds of the written slots that are not in your profile are removed, also the stock gamepad flight binds on slot 1 |

- `--nuke-untouched-devices`: the same for slots `config.xml` pins to connected devices the export does not use; a slot
  without a pinned device is left alone (not with `add`).
- `--include-keyboard` writes flight keys (never keys X4's menu refuses) and lets `merge-overwrite` remove a control's
  keyboard lines.
- Inverts are one value per control for every device and are set only when every axis on that control agrees. The
  throttle relation's Slider sets the bidirectional throttle. Relation dead zones are not written; settings you stored
  for X4 (dead zone, adaptive steering, rumble, auto-roll) are, also by a profile without X4 binds.
- Never touched: menu, map, first-person, options and accessibility controls, keyboard and mouse lines without the
  option, slots of devices not connected or not pinned, gamepads, `--ignore-device(s)`, the `--vjoy-tunnel` device,
  `inputmap_1.xml` to `inputmap_5.xml` and your saves.
- Safety: refused while `X4` runs; both files backed up first (undo: [[cli:backups list]]); the profile is kept as
  "Before export to X4: Foundations (experimental)". `--dry-run` writes nothing.
:::

:::layer technical
- Output: JSON of the export result or a text report with `--text`; exit code `1` on an error (X4 runs, an invalid
  `--mode`, no file found). A device without a pinned slot is skipped ("assign the device to a slot in X4 once").
- Without a path it writes the file [[cli:x4 paths]] lists first. Untouched lines stay byte for byte.
- After a real run: `inputmap.xml.joypro-meta.json`, the watcher's fingerprint, the export marks
  ([[cli:export-marks list]]).
- The help note says "only ship flight actions": clean and nuke release only the space flight controls of the command
  list.
- Related: [[cli:x4 import]], [[cli:ffb set]]; game page [[game:x4]].
:::

<!-- entry: x4 paths -->
:::layer beginner
Lists where JoyPro finds the input map of X4: Foundations on this PC. The first one is what [[cli:x4 scan]],
[[cli:x4 import]] and [[cli:x4 export]] use when you give no path. **Detect** in the **Other Games** panel (Start
page, **Expert import**) runs it and puts the first one into **File / Folder**.

```powershell
engine x4 paths --text
```
:::

:::layer advanced
- `%USERPROFILE%\Documents\Egosoft\X4\<id>\inputmap.xml` of every account folder (and
  `Documents\Egosoft\X4\inputmap.xml`), newest first.
- A config location you set yourself ([[cli:games config set]], Settings > Games) comes first.
- No options. Reads only; it also works while the game runs or when it is not installed.
:::

:::layer technical
- Output: JSON (`Game`, `DisplayName`, `ConfigPaths`, `Warnings`), or text with `--text` ("X4: Foundations
  (experimental) Config Paths", "Paths: <n>" and one line per location). Exit code `0`, also when nothing was found
  (empty list).
- `engine x4` without an action runs `paths`.
- Related: [[cli:x4 scan]], [[cli:games paths]], [[cli:games install set]]; game page [[game:x4]].
:::

<!-- entry: x4 catalog -->
:::layer beginner
Lists the X4: Foundations actions JoyPro manages (its command list): id, axis or button, category and title. An export
writes only these actions, and the relation editor and the search list them.

```powershell
engine x4 catalog --text
```
:::

:::layer advanced
- The list is `%LOCALAPPDATA%\JoyPro2\GameData\DB\X4\actions.tsv` (when that folder does not exist, the `DB\X4` folder
  next to the engine).
- It is built from the game's Controls menu by [[cli:x4 refresh-db]], or by the first scan, import or export.
- `catalog` only shows the list; it never builds it. Titles that look like ids are shown readable.
- No options. Reads only; works without the game installed.
:::

:::layer technical
- Output: JSON (`Game`, `Source`, `Actions` with `Id`, `Title`, `IsAxis`, `Category`, `Warnings`), or text with
  `--text` ("X4: Foundations (experimental) Action Catalog", "Source:", "Actions: <n>", then `<id> | axis|button |
  <category> | <title>` per action).
- Without a list: the warning `X4: Foundations (experimental) action catalog not found: <path>` and an empty list,
  exit code `0`.
- Related: [[cli:x4 refresh-db]], [[cli:x4 export]], [[cli:db games]]; game page [[game:x4]].
:::

<!-- entry: x4 scan -->
:::layer beginner
Shows the joystick binds X4: Foundations has now, without changing your profile or the game. Use it to look before an
import. **Look first** on the game's card of the Start page and **Scan** in the **Other Games** panel run it.

```powershell
engine x4 scan --text
```
:::

:::layer advanced
- It reads the active binds and `<config>` params of `inputmap.xml`, and the device slots of `config.xml` in the same
  folder (DirectInput instance GUID).
- Path (positional or `--path`): another `inputmap.xml`.
- `--ignore-device <name>` / `--ignore-devices <a;b>` (separated by `|` or `;`, `--ignore-devices` also by commas) leave the binds of those devices out
  (a name without `{GUID}` means every device of that name); a step line names them and counts the binds.
- Keyboard binds JoyPro reads in this game are listed too; [[cli:x4 import]] takes them only with
  `--include-keyboard`.
- Safety: reads only and is not refused while the game runs. It is refused when the game is not installed (set its
  folder: [[cli:games install set]]) or switched off in Settings > Games.
- A fresh install has no command list yet: the first scan builds it from the installed game and saves it (a step line
  says so).
:::

:::layer technical
- Output: JSON (`ConfigPath`, `BindingsRead`, `Bindings` with `ActionId`, `Device`, `Input`, `IsAxis`, `Reformers`,
  `Inverted`, then `Steps`, `Warnings`, `Errors`), or text with `--text` ("X4: Foundations (experimental) Scan",
  "Config:", "Bindings Read:" and the list).
- Exit code `0`; `1` when the result has errors, for example `No X4: Foundations (experimental) config file found.
  Pass the path explicitly.`
- Unknown options fail with `Unknown X4 scan argument(s): ...`; MSFS options (`--game-profile`,
  `--include-disconnected`, `--include-sim-defaults`) are refused.
- Binds of the vJoy device JoyPro forwards into (`--vjoy-tunnel`) are left out.
- Related: [[cli:x4 import]], [[cli:x4 paths]], [[cli:x4 catalog]]; game page [[game:x4]].
:::

<!-- entry: x4 refresh-db -->
:::layer beginner
Builds JoyPro's X4: Foundations command list from the installed game's Controls menu, or adds the actions that are
new. Run it after a game update, or when JoyPro says the list is missing. **Refresh DB** in the **Other Games** panel
(its **DB Source** box is the path) and the Start page's notice "Command lists to build or update" run it.

```powershell
engine x4 refresh-db --dry-run --text
```
:::

:::layer advanced
- Without a path it reads the installed game (`01.cat` ... and `X4.exe`): the menu script
  `ui/addons/ego_gameoptions/gameoptions.lua` and the English texts `t/0001-l044.xml`. Path: the X4 folder, or a
  folder with those two files (a file means its folder).
- New space-flight controls of the menu come in; the names of numeric control ids come from `X4.exe` (else JoyPro's
  `order.tsv`). New ids and menu functions are added to `order.tsv` and `functions.tsv` next to the list.
- A known action that is no longer a space-flight control of the menu stays, with a warning.
- `--dry-run` reads everything and writes nothing (step "Dry run: nothing was written."). Without it the list is
  written only when it changed.
- Safety: only JoyPro's list changes, never a game file; it is refused when the game is not installed or switched off
  in Settings > Games. The GUI can cancel it while the game's files are read; nothing is written then.
:::

:::layer technical
- Output: JSON of the refresh (`DryRun`, `FilesWritten`, `Steps` with `Actions: <old> -> <new>`, `Warnings`,
  `Errors`), or text with `--text`.
- Exit code `0`; `1` when nothing could be read: `X4: Foundations was not found. Pass --path with the X4 folder
  (01.cat ... and X4.exe) or a folder with ui/addons/ego_gameoptions/gameoptions.lua and t/0001-l044.xml.`; the list
  stays unchanged.
- Writes `%LOCALAPPDATA%\JoyPro2\GameData\DB\X4\actions.tsv` (with a header line that has the date), and adds the new
  lines to `order.tsv` and `functions.tsv` next to it. After a real run the game's build is recorded for
  [[cli:db check-updates]].
- Related: [[cli:x4 catalog]], [[cli:x4 scan]], [[cli:db check-updates]]; game page [[game:x4]].
:::

<!-- entry: warthunder import -->
:::layer beginner
Reads the aircraft and helicopter controls of War Thunder into your JoyPro profile, with every value of the game's Axis
setup dialog and the game-wide control options. Nothing in the game changes. The GUI runs it with **Import** on the
War Thunder card of the Start page.

```powershell
engine warthunder import --include-keyboard --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" --text
```
:::

:::layer advanced
It reads `%USERPROFILE%\Documents\My Games\WarThunder\Saves\<uid>\production\machine.blk` (`<uid>` is your account;
`Saves\last` before a login). An exported controls preset can be given as the path too.

- Default: adds to the open profile (`--merge` means the same); `--replace` replaces every relation of the profile by
  what the file has.
- `--include-keyboard` also reads the flight keys. The last button of a shortcut is the input, the others in the block
  become modifiers.
- Game options (sensitivities, inverts, WEP, autotrim, force feedback) become the profile's War Thunder settings (step
  line "Game settings imported: ..."; from `machine.blk` only).
- Devices are matched through the game's `deviceMapping{}` (VID:PID), which is only read.
- Safety: reads only, not refused while the game runs; the profile is kept as "Before import from War Thunder" first.
- `--ignore-device <name>` / `--ignore-devices <a;b>` leave the binds of those devices out (a name without
  `{GUID}` means every device of that name); a step line says how many binds were left out.
:::

:::layer technical
- Output: JSON of the import result or a text report with `--text`; exit code `1` on an error (`No War Thunder config
  file found. Pass the path explicitly.`).
- Without a path it reads the file [[cli:warthunder paths]] lists first; a fresh install builds the command list from
  the game's archives first ([[cli:warthunder refresh-db]]).
- With `--profile` the profile is saved afterwards; a game JoyPro never exported to is watched from now on.
- Related: [[cli:warthunder scan]], [[cli:warthunder export]], [[cli:ffb set]]; game page [[game:war-thunder]].
:::

<!-- entry: warthunder export -->
:::layer beginner
Writes your aircraft and helicopter binds and axis values into War Thunder's `machine.blk` in place. Tanks, ships,
infantry and the menus are never touched. Close the game first. The GUI's **Export now** on the War Thunder tile runs
it.

```powershell
engine warthunder export --mode clean --nuke-untouched-devices --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" --text
```
:::

:::layer advanced
Only cataloged aircraft and helicopter controls count; a conflict is another of those on the same input.

| `--mode` | In `machine.blk` |
|---|---|
| `add` | a shortcut is written only when no other flight control uses that input; no block is removed, no bound axis replaced |
| `merge` | your shortcuts go in beside the control's others; only conflicts are removed (an axis of another control on yours gets `axisId -1`) |
| `merge-overwrite` (engine default) | as `merge`, and a control you bind loses its other shortcuts on connected devices the game lists (keyboard only with `--include-keyboard`) |
| `clean` (GUI default) | as `merge-overwrite`, and the other flight shortcuts and axes of the written devices are released |

- `--nuke-untouched-devices`: the same for connected devices without binds in your profile (not with `add`).
- Axis values in the game's units (dead zone, nonlinearity, multiplier, correction, invert, relative control); a
  relation without War Thunder values gets its JoyPro curve fitted. The game options you stored are written, also by a
  profile without War Thunder binds.
- The file's `version:i=N` line is kept, so the new content is uploaded at login.
- Never touched: tank, ship, submarine, infantry and menu controls, `deviceMapping{}`, `config{}`, other settings
  lines, mouse binds, devices that are not connected or not listed by the game, `--ignore-device(s)`, the
  `--vjoy-tunnel` device, the keyboard without `--include-keyboard`.
- Safety: refused while `aces` runs; backed up first (`.jpbak` and the backup store, undo: [[cli:backups list]]; a
  restore raises the file's version); the profile is kept as "Before export to War Thunder". `--dry-run` writes
  nothing.
:::

:::layer technical
- Output: JSON of the export result or a text report with `--text`; exit code `1` on an error (`War Thunder is running
  (aces). Close the game first; it overwrites its config on exit.`, an invalid `--mode`, no file found).
- Without a path it writes the file [[cli:warthunder paths]] lists first. Hat diagonals, mouse buttons and axis
  modifiers are not written.
- After a real run: `machine.blk.joypro-meta.json`, the watcher's fingerprint, the export marks
  ([[cli:export-marks list]]).
- Related: [[cli:warthunder import]], [[cli:ffb set]], [[cli:warthunder refresh-db]]; game page [[game:war-thunder]].
:::

<!-- entry: warthunder paths -->
:::layer beginner
Lists where JoyPro finds the controls file of War Thunder on this PC. The first one is what [[cli:warthunder scan]],
[[cli:warthunder import]] and [[cli:warthunder export]] use when you give no path. **Detect** in the **Other Games**
panel (Start page, **Expert import**) runs it and puts the first one into **File / Folder**.

```powershell
engine warthunder paths --text
```
:::

:::layer advanced
- Every `%USERPROFILE%\Documents\My Games\WarThunder\Saves\<folder>\production\machine.blk`: the account
  `Saves\lastlogin.blk` names first, then the newest (`Saves\last` is the copy from before a login).
- A config location you set yourself ([[cli:games config set]], Settings > Games) comes first.
- No options. Reads only; it also works while the game runs or when it is not installed.
:::

:::layer technical
- Output: JSON (`Game`, `DisplayName`, `ConfigPaths`, `Warnings`), or text with `--text` ("War Thunder Config Paths",
  "Paths: <n>" and one line per location). Exit code `0`, also when nothing was found (empty list).
- `engine warthunder` without an action runs `paths`.
- Related: [[cli:warthunder scan]], [[cli:games paths]], [[cli:games install set]]; game page [[game:war-thunder]].
:::

<!-- entry: warthunder catalog -->
:::layer beginner
Lists the War Thunder actions JoyPro manages (its command list): id, axis or button, category and title. An export
writes only these actions, and the relation editor and the search list them.

```powershell
engine warthunder catalog --text
```
:::

:::layer advanced
- The list is `%LOCALAPPDATA%\JoyPro2\GameData\DB\WarThunder\actions.tsv` (when that folder does not exist, the
  `DB\WarThunder` folder next to the engine).
- It is built from the game's archives by [[cli:warthunder refresh-db]], or by the first scan, import or export.
- `catalog` only shows the list; it never builds it. Titles that look like ids are shown readable.
- No options. Reads only; works without the game installed.
:::

:::layer technical
- Output: JSON (`Game`, `Source`, `Actions` with `Id`, `Title`, `IsAxis`, `Category`, `Warnings`), or text with
  `--text` ("War Thunder Action Catalog", "Source:", "Actions: <n>", then `<id> | axis|button | <category> |
  <title>` per action).
- Without a list: the warning `War Thunder action catalog not found: <path>` and an empty list, exit code `0`.
- Related: [[cli:warthunder refresh-db]], [[cli:warthunder export]], [[cli:db games]]; game page [[game:war-thunder]].
:::

<!-- entry: warthunder scan -->
:::layer beginner
Shows the joystick binds War Thunder has now, without changing your profile or the game. Use it to look before an
import. **Look first** on the game's card of the Start page and **Scan** in the **Other Games** panel run it.

```powershell
engine warthunder scan --text
```
:::

:::layer advanced
- It reads the aircraft and helicopter controls of `machine.blk`: the last button of a shortcut is the input, the
  others in the block are modifiers; devices are matched through the game's `deviceMapping{}` (VID:PID).
- Path (positional or `--path`): another `machine.blk` or an exported controls preset.
- `--ignore-device <name>` / `--ignore-devices <a;b>` (separated by `|` or `;`, `--ignore-devices` also by commas) leave the binds of those devices out
  (a name without `{GUID}` means every device of that name); a step line names them and counts the binds.
- Keyboard binds JoyPro reads in this game are listed too; [[cli:warthunder import]] takes them only with
  `--include-keyboard`.
- `--no-game-archives` also applies when the scan builds the first list (see [[cli:warthunder refresh-db]]).
- Safety: reads only and is not refused while the game runs. It is refused when the game is not installed (set its
  folder: [[cli:games install set]]) or switched off in Settings > Games.
- A fresh install has no command list yet: the first scan builds it from the installed game and saves it (a step line
  says so).
:::

:::layer technical
- Output: JSON (`ConfigPath`, `BindingsRead`, `Bindings` with `ActionId`, `Device`, `Input`, `IsAxis`, `Reformers`,
  `Inverted`, then `Steps`, `Warnings`, `Errors`), or text with `--text` ("War Thunder Scan", "Config:", "Bindings
  Read:" and the list).
- Exit code `0`; `1` when the result has errors, for example `No War Thunder config file found. Pass the path
  explicitly.`
- Unknown options fail with `Unknown WarThunder scan argument(s): ...`; MSFS options (`--game-profile`,
  `--include-disconnected`, `--include-sim-defaults`) are refused.
- Binds of the vJoy device JoyPro forwards into (`--vjoy-tunnel`) are left out.
- Related: [[cli:warthunder import]], [[cli:warthunder paths]], [[cli:warthunder catalog]]; game page
  [[game:war-thunder]].
:::

<!-- entry: warthunder refresh-db -->
:::layer beginner
Builds JoyPro's War Thunder command list from the installed game's files, or adds the actions that are new. Run it
after a game update, or when JoyPro says the list is missing. **Refresh DB** in the **Other Games** panel (its **DB
Source** box is the path) and the Start page's notice "Command lists to build or update" run it.

```powershell
engine warthunder refresh-db --dry-run --text
```
:::

:::layer advanced
- Without a path (or with the game folder) it reads the two control scripts `shortcutsgroupaircraft.nut` and
  `shortcutsgrouphelicopter.nut` from `gui.vromfs.bin` and the titles of `controls.csv` from `lang.vromfs.bin`, in a
  temporary folder that is deleted afterwards.
- Path: the game folder, or a folder that holds the two scripts (also with the older `sl_` prefix) and optionally
  `controls.csv`.
- The list is rebuilt from the scripts: every axis and shortcut (not the `USEROPT_` options), category Aircraft or
  Helicopter; titles from `controls.csv`, else the known title, else the id. An action the scripts no longer have
  leaves the list.
- `--dry-run` reads everything and writes nothing (step "Dry run: nothing was written."). Without it the list is
  written only when it changed.
- `--no-game-archives` (or `--read-game-archives false`; GUI: Settings > Games, "War Thunder: read the full command
  list from the game's archives") leaves the game's archives alone: the list is the current one plus JoyPro's own
  War Thunder function list (a step line says so). A folder with the scripts given as the path is read either way.
- Safety: only JoyPro's list changes, never a game file; it is refused when the game is not installed or switched off
  in Settings > Games. The GUI can cancel it while the game's files are read; nothing is written then.
:::

:::layer technical
- Output: JSON of the refresh (`DryRun`, `FilesWritten`, `Steps` with `Actions: <old> -> <new>`, `Warnings`,
  `Errors`), or text with `--text`.
- Exit code `0`; `1` when nothing could be read: `War Thunder was not found. Pass --path with the game folder
  (gui.vromfs.bin) or a folder that holds the control scripts ...`, `<script> was not found or not readable under
  <path>.` or `Could not read War Thunder's archives in <folder>: ...`; the list stays unchanged.
- Writes `%LOCALAPPDATA%\JoyPro2\GameData\DB\WarThunder\actions.tsv` (with a header line that has the date). After a
  real run the game's build is recorded for [[cli:db check-updates]].
- Related: [[cli:warthunder catalog]], [[cli:warthunder scan]], [[cli:db check-updates]]; game page
  [[game:war-thunder]].
:::

<!-- entry: arma3 import -->
:::layer beginner
Reads the aircraft and helicopter binds of your Arma 3 profile into your JoyPro profile, with the sensitivity, dead
zone and steepness of each axis. Nothing in the game changes. The GUI runs it with **Import** on the Arma 3 card of the
Start page.

```powershell
engine arma3 import --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" --text
```
:::

:::layer advanced
It reads `%USERPROFILE%\Documents\Arma 3\<name>.Arma3Profile` (or another profile in `Documents\Arma 3 - Other
Profiles\<name>\` given as the path): the `key<Action>[]` lines of the cataloged flight actions and the device arrays
of `class JoysticksList`.

- Default: adds to the open profile (`--merge` means the same); `--replace` replaces every relation of the profile by
  what the profile file has.
- Keyboard binds are not read in Arma 3: `--include-keyboard` changes nothing.
- The response values per device axis come in as the relation's Arma 3 tuning; JoyPro's own data comes back from
  `<profile>.joypro-meta.json`.
- A binary (rapified) profile is not read.
- Safety: reads only, not refused while the game runs; the profile is kept as "Before import from Arma 3" first.
- `--ignore-device <name>` / `--ignore-devices <a;b>` leave the binds of those devices out (a name without
  `{GUID}` means every device of that name); a step line says how many binds were left out.
:::

:::layer technical
- Output: JSON of the import result or a text report with `--text`; exit code `1` on an error (`No Arma 3 config file
  found. Pass the path explicitly.`).
- Without a path it reads the profile [[cli:arma3 paths]] lists first; a fresh install builds the command list from
  the game's config first.
- With `--profile` the profile is saved afterwards; a game JoyPro never exported to is watched from now on.
- Related: [[cli:arma3 scan]], [[cli:arma3 export]], [[cli:arma2oa import]]; game page [[game:arma3]].
:::

<!-- entry: arma3 export -->
:::layer beginner
Writes your aircraft and helicopter binds into your Arma 3 profile; only the key lines of the flight actions change, your
keyboard and mouse binds stay. Close Arma 3 first, and start it once with every device connected. The GUI's **Export
now** on the Arma 3 tile runs it.

```powershell
engine arma3 export --mode clean --nuke-untouched-devices --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" --text
```
:::

:::layer advanced
Only the lines of cataloged aircraft and helicopter actions change; helicopters and planes are different vehicles.

| `--mode` | In the profile |
|---|---|
| `add` | nothing is removed; a bind whose code another action of the same vehicle has gets a mark "Not written to the game" |
| `merge` | your codes go in; a code of another action of the same vehicle that you put elsewhere is dropped |
| `merge-overwrite` (engine default) | as `merge`, and an action you bind loses its other joystick codes of connected joysticks |
| `clean` (GUI default) | the flight lines also lose every other code of the written devices |

- `--nuke-untouched-devices`: the same for the codes of other connected joysticks (not with `add`). Arma 3 is not a
  flight-only game, so infantry, vehicle and view lines are never touched by either.
- A device the game's list does not have is skipped ("start the game once with it connected"); a disabled device that
  gets binds is switched on (the export says so). Invert is written as the opposite half of the axis; the analog
  thrust and speedbrake get both halves.
- Response values are written only where you set them for Arma 3 or they came from the import. Game settings you
  stored are written, also by a profile without Arma 3 binds (then no key line changes).
- Never touched: keyboard, mouse, TrackIR and Xbox pad codes, joysticks that are not connected, `--ignore-device(s)`,
  the `--vjoy-tunnel` device, new device list entries, `.vars.Arma3Profile`. Keyboard binds and modifiers are not
  written.
- Safety: refused while `arma3_x64`, `arma3` or `arma3battleye` runs; backed up first (`.jpbak` and the backup store,
  undo: [[cli:backups list]]); the profile is kept as "Before export to Arma 3". `--dry-run` writes nothing.
:::

:::layer technical
- Output: JSON of the export result or a text report with `--text`; exit code `1` on an error (`Arma 3 is running
  (...). Close the game first; it overwrites its config on exit.`, an invalid `--mode`, no profile found, a binary
  profile).
- Without a path it writes the profile [[cli:arma3 paths]] lists first.
- After a real run: `<profile>.joypro-meta.json`, the watcher's fingerprint, the export marks
  ([[cli:export-marks list]]).
- Related: [[cli:arma3 import]], [[cli:ffb set]] (device on/off, game settings); game page [[game:arma3]].
:::

<!-- entry: arma3 paths -->
:::layer beginner
Lists where JoyPro finds the profiles of Arma 3 on this PC. The first one is what [[cli:arma3 scan]],
[[cli:arma3 import]] and [[cli:arma3 export]] use when you give no path. **Detect** in the **Other Games** panel
(Start page, **Expert import**) runs it and puts the first one into **File / Folder**.

```powershell
engine arma3 paths --text
```
:::

:::layer advanced
- Every `*.Arma3Profile` in `%USERPROFILE%\Documents\Arma 3\` and below `Documents\Arma 3 - Other Profiles\`, newest
  first; the game's `.vars` and `.3den` files are skipped.
- A config location you set yourself ([[cli:games config set]], Settings > Games) comes first.
- No options. Reads only; it also works while the game runs or when it is not installed.
:::

:::layer technical
- Output: JSON (`Game`, `DisplayName`, `ConfigPaths`, `Warnings`), or text with `--text` ("Arma 3 Config Paths",
  "Paths: <n>" and one line per location). Exit code `0`, also when nothing was found (empty list).
- `engine arma3` without an action runs `paths`.
- Related: [[cli:arma3 scan]], [[cli:games paths]], [[cli:games install set]]; game page [[game:arma3]].
:::

<!-- entry: arma3 catalog -->
:::layer beginner
Lists the Arma 3 actions JoyPro manages (its command list): id, axis or button, category and title. An export writes
only these actions, and the relation editor and the search list them.

```powershell
engine arma3 catalog --text
```
:::

:::layer advanced
- The list is `%LOCALAPPDATA%\JoyPro2\GameData\DB\Arma3\actions.tsv` (when that folder does not exist, the `DB\Arma3`
  folder next to the engine).
- It is built from the game's config and your profile by [[cli:arma3 refresh-db]], or by the first scan, import or
  export.
- `catalog` only shows the list; it never builds it. Titles that look like ids are shown readable.
- No options. Reads only; works without the game installed.
:::

:::layer technical
- Output: JSON (`Game`, `Source`, `Actions` with `Id`, `Title`, `IsAxis`, `Category`, `Warnings`), or text with
  `--text` ("Arma 3 Action Catalog", "Source:", "Actions: <n>", then `<id> | axis|button | <category> | <title>` per
  action).
- Without a list: the warning `Arma 3 action catalog not found: <path>` and an empty list, exit code `0`.
- Related: [[cli:arma3 refresh-db]], [[cli:arma3 export]], [[cli:db games]]; game page [[game:arma3]].
:::

<!-- entry: arma3 scan -->
:::layer beginner
Shows the joystick binds Arma 3 has now, without changing your profile or the game. Use it to look before an import.
**Look first** on the game's card of the Start page and **Scan** in the **Other Games** panel run it.

```powershell
engine arma3 scan --text
```
:::

:::layer advanced
- It reads the `key<Action>[]` lines of the cataloged flight actions and the device arrays of `class JoysticksList`,
  with the sensitivity, dead zone and steepness of each axis. A binary (rapified) profile is not read.
- Path (positional or `--path`): another profile file.
- `--ignore-device <name>` / `--ignore-devices <a;b>` (separated by `|` or `;`, `--ignore-devices` also by commas) leave the binds of those devices out
  (a name without `{GUID}` means every device of that name); a step line names them and counts the binds.
- Keyboard binds are not read in this game.
- Safety: reads only and is not refused while the game runs. It is refused when the game is not installed (set its
  folder: [[cli:games install set]]) or switched off in Settings > Games.
- A fresh install has no command list yet: the first scan builds it from the installed game and saves it (a step line
  says so).
:::

:::layer technical
- Output: JSON (`ConfigPath`, `BindingsRead`, `Bindings` with `ActionId`, `Device`, `Input`, `IsAxis`, `Reformers`,
  `Inverted`, then `Steps`, `Warnings`, `Errors`), or text with `--text` ("Arma 3 Scan", "Config:", "Bindings Read:"
  and the list).
- Exit code `0`; `1` when the result has errors, for example `No Arma 3 config file found. Pass the path explicitly.`
- Unknown options fail with `Unknown Arma3 scan argument(s): ...`; MSFS options (`--game-profile`,
  `--include-disconnected`, `--include-sim-defaults`) are refused.
- Binds of the vJoy device JoyPro forwards into (`--vjoy-tunnel`) are left out.
- Related: [[cli:arma3 import]], [[cli:arma3 paths]], [[cli:arma3 catalog]]; game page [[game:arma3]].
:::

<!-- entry: arma3 refresh-db -->
:::layer beginner
Builds JoyPro's Arma 3 command list from the installed game's config and your profile, or adds the actions that are
new. Run it after a game update, or when JoyPro says the list is missing. **Refresh DB** in the **Other Games** panel
(its **DB Source** box is the path) and the Start page's notice "Command lists to build or update" run it.

```powershell
engine arma3 refresh-db --dry-run --text
```
:::

:::layer advanced
- Without a path it reads the game folder's `Dta\bin.pbo` (the action list of
  `CfgDefaultKeysPresets/Default/Mappings`) with the names of `Dta\languagecore_f.pbo`, and the first profile of
  [[cli:arma3 paths]].
- Path: a folder is read as the game folder, a file as a profile (its key lines only).
- Kept: the flight actions (names starting with Heli, Air, Plane, vtol, Flaps, LandGear, AutoHover), the other actions
  JoyPro's function list uses, Eject, EngineToggle, LaunchCM and NextCM. JoyPro's axis relations (`Axis.*`) come
  from JoyPro itself. Actions found only in the profile get the category "Flight (from profile)".
- Known actions keep their entry.
- `--dry-run` reads everything and writes nothing (step "Dry run: nothing was written."). Without it the list is
  written only when it changed.
- Safety: only JoyPro's list changes, never a game file; it is refused when the game is not installed or switched off
  in Settings > Games. The GUI can cancel it while the game's files are read; nothing is written then.
:::

:::layer technical
- Output: JSON of the refresh (`DryRun`, `FilesWritten`, `Steps` with `Actions: <old> -> <new>`, `Warnings`,
  `Errors`), or text with `--text`.
- Exit code `0`; `1` when nothing could be read: `Arma 3: neither the game's config nor a profile was found. Set the
  game folder (Settings > Games) or pass --path.`; the list stays unchanged.
- Writes `%LOCALAPPDATA%\JoyPro2\GameData\DB\Arma3\actions.tsv` (with a header line that has the date). After a real
  run the game's build is recorded for [[cli:db check-updates]].
- Related: [[cli:arma3 catalog]], [[cli:arma3 scan]], [[cli:db check-updates]]; game page [[game:arma3]].
:::

<!-- entry: arma2oa import -->
:::layer beginner
Reads the aircraft and helicopter binds of your Arma 2: Operation Arrowhead profile into your JoyPro profile, with the
sensitivity slider of each axis. Nothing in the game changes. The GUI runs it with **Import** on the Operation
Arrowhead card of the Start page.

```powershell
engine arma2oa import --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" --text
```
:::

:::layer advanced
It reads `%USERPROFILE%\Documents\ArmA 2\<name>.ArmA2OAProfile` (or one in `Documents\ArmA 2 Other Profiles\<name>\`):
Operation Arrowhead, Combined Operations and the DayZ mod use this file.

- Default: adds to the open profile (`--merge` means the same); `--replace` replaces every relation of the profile by
  what the file has.
- Keyboard binds are not read; `--include-keyboard` changes nothing. Arma 2 knows no joystick buttons with modifiers.
- Both halves of an axis come back as one axis relation; stored full-axis codes are read too.
- Safety: reads only, not refused while the game runs; the profile is kept as "Before import from Arma 2: Operation
  Arrowhead" first.
- `--ignore-device <name>` / `--ignore-devices <a;b>` leave the binds of those devices out (a name without
  `{GUID}` means every device of that name); a step line says how many binds were left out.
:::

:::layer technical
- Output: JSON of the import result or a text report with `--text`; exit code `1` on an error (`No Arma 2: Operation
  Arrowhead config file found. Pass the path explicitly.`).
- Without a path it reads the profile [[cli:arma2oa paths]] lists first; a fresh install builds the command list from
  `Expansion\dta\bin.pbo` first.
- With `--profile` the profile is saved afterwards; a game JoyPro never exported to is watched from now on.
- Related: [[cli:arma2oa scan]], [[cli:arma2oa export]], [[cli:arma2 import]]; game page [[game:arma2]].
:::

<!-- entry: arma2oa export -->
:::layer beginner
Writes your aircraft and helicopter binds into your Arma 2: Operation Arrowhead profile; only the key lines of the
flight actions change. Close the game first, and start it once with every device connected. The GUI's **Export now**
on the Operation Arrowhead tile runs it.

```powershell
engine arma2oa export --mode clean --nuke-untouched-devices --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file"
```
:::

:::layer advanced
Only the lines of cataloged aircraft and helicopter actions change; a missing cataloged key line is added after the last
key line.

| `--mode` | In the profile |
|---|---|
| `add` | nothing is removed; a bind whose code another action of the same vehicle has gets a mark "Not written to the game" |
| `merge` | your codes go in; a code of another action of the same vehicle that you put elsewhere is dropped |
| `merge-overwrite` (engine default) | as `merge`, and an action you bind loses its other joystick codes of connected joysticks |
| `clean` (GUI default) | the flight lines also lose every other code of the written devices, the game's automatic defaults included |

- `--nuke-untouched-devices`: the same for other connected joysticks (not with `add`).
- Axes are written as halves; the throttle ("Thrust (Analogue)") and the airbrake get both halves of the lever.
  Sensitivity sliders are written only where you set them for Operation Arrowhead (or `family:arma2`) or they came from
  the import. 32 buttons, 8 axes and the first hat per device.
- Never touched: infantry, vehicle and menu lines, keyboard, mouse, TrackIR and pad codes, joysticks that are not
  connected, `--ignore-device(s)`, the `--vjoy-tunnel` device, new device entries, `ArmA2OA.cfg`.
- Safety: refused while `arma2oa` or `arma2oa_be` runs; backed up first (undo: [[cli:backups list]]); the profile is
  kept as "Before export to Arma 2: Operation Arrowhead". `--dry-run` writes nothing.
:::

:::layer technical
- Output: JSON of the export result or a text report with `--text`; exit code `1` on an error (the game runs, an
  invalid `--mode`, no profile found, a binary profile).
- Without a path it writes the profile [[cli:arma2oa paths]] lists first.
- After a real run: `<profile>.joypro-meta.json`, the watcher's fingerprint, the export marks
  ([[cli:export-marks list]]).
- Related: [[cli:arma2oa import]], [[cli:arma2 export]], [[cli:ffb set]]; game page [[game:arma2]].
:::

<!-- entry: arma2oa paths -->
:::layer beginner
Lists where JoyPro finds the profiles of Arma 2: Operation Arrowhead on this PC. The first one is what
[[cli:arma2oa scan]], [[cli:arma2oa import]] and [[cli:arma2oa export]] use when you give no path. **Detect** in the
**Other Games** panel (Start page, **Expert import**) runs it and puts the first one into **File / Folder**.

```powershell
engine arma2oa paths --text
```
:::

:::layer advanced
- Every `*.ArmA2OAProfile` in `%USERPROFILE%\Documents\ArmA 2\` and below `Documents\ArmA 2 Other Profiles\`, newest
  first; `.vars` and `.3den` files are skipped. Combined Operations and the DayZ mod use the same profiles.
- A config location you set yourself ([[cli:games config set]], Settings > Games) comes first.
- No options. Reads only; it also works while the game runs or when it is not installed.
:::

:::layer technical
- Output: JSON (`Game`, `DisplayName`, `ConfigPaths`, `Warnings`), or text with `--text` ("Arma 2: Operation Arrowhead
  Config Paths", "Paths: <n>" and one line per location). Exit code `0`, also when nothing was found (empty list).
- `engine arma2oa` without an action runs `paths`.
- Related: [[cli:arma2oa scan]], [[cli:games paths]], [[cli:games install set]]; game page [[game:arma2]].
:::

<!-- entry: arma2oa catalog -->
:::layer beginner
Lists the Arma 2: Operation Arrowhead actions JoyPro manages (its command list): id, axis or button, category and
title. An export writes only these actions, and the relation editor and the search list them.

```powershell
engine arma2oa catalog --text
```
:::

:::layer advanced
- The list is `%LOCALAPPDATA%\JoyPro2\GameData\DB\Arma2OA\actions.tsv` (when that folder does not exist, the
  `DB\Arma2OA` folder next to the engine).
- It is built from the game's config and your profile by [[cli:arma2oa refresh-db]], or by the first scan, import or
  export.
- `catalog` only shows the list; it never builds it. Titles that look like ids are shown readable.
- No options. Reads only; works without the game installed.
:::

:::layer technical
- Output: JSON (`Game`, `Source`, `Actions` with `Id`, `Title`, `IsAxis`, `Category`, `Warnings`), or text with
  `--text` ("Arma 2: Operation Arrowhead Action Catalog", "Source:", "Actions: <n>", then `<id> | axis|button |
  <category> | <title>` per action).
- Without a list: the warning `Arma 2: Operation Arrowhead action catalog not found: <path>` and an empty list, exit
  code `0`.
- Related: [[cli:arma2oa refresh-db]], [[cli:arma2oa export]], [[cli:db games]]; game page [[game:arma2]].
:::

<!-- entry: arma2oa scan -->
:::layer beginner
Shows the joystick binds Arma 2: Operation Arrowhead has now, without changing your profile or the game. Use it to
look before an import. **Look first** on the game's card of the Start page and **Scan** in the **Other Games** panel
run it.

```powershell
engine arma2oa scan --text
```
:::

:::layer advanced
- It reads the key lines of the cataloged actions and the devices of `class JoysticksList` (32 buttons per device; a
  device in another mode than Custom counts as off), with each axis's sensitivity slider.
- Path (positional or `--path`): another profile file.
- `--ignore-device <name>` / `--ignore-devices <a;b>` (separated by `|` or `;`, `--ignore-devices` also by commas) leave the binds of those devices out
  (a name without `{GUID}` means every device of that name); a step line names them and counts the binds.
- Keyboard binds are not read in this game.
- Safety: reads only and is not refused while the game runs. It is refused when the game is not installed (set its
  folder: [[cli:games install set]]) or switched off in Settings > Games.
- A fresh install has no command list yet: the first scan builds it from the installed game and saves it (a step line
  says so).
:::

:::layer technical
- Output: JSON (`ConfigPath`, `BindingsRead`, `Bindings` with `ActionId`, `Device`, `Input`, `IsAxis`, `Reformers`,
  `Inverted`, then `Steps`, `Warnings`, `Errors`), or text with `--text` ("Arma 2: Operation Arrowhead Scan",
  "Config:", "Bindings Read:" and the list).
- Exit code `0`; `1` when the result has errors, for example `No Arma 2: Operation Arrowhead config file found. Pass
  the path explicitly.`
- Unknown options fail with `Unknown Arma2OA scan argument(s): ...`; MSFS options (`--game-profile`,
  `--include-disconnected`, `--include-sim-defaults`) are refused.
- Binds of the vJoy device JoyPro forwards into (`--vjoy-tunnel`) are left out.
- Related: [[cli:arma2oa import]], [[cli:arma2oa paths]], [[cli:arma2oa catalog]]; game page [[game:arma2]].
:::

<!-- entry: arma2oa refresh-db -->
:::layer beginner
Builds JoyPro's Arma 2: Operation Arrowhead command list from the installed game's config and your profile, or adds
the actions that are new. Run it after a game update, or when JoyPro says the list is missing. **Refresh DB** in the
**Other Games** panel (its **DB Source** box is the path) and the Start page's notice "Command lists to build or
update" run it.

```powershell
engine arma2oa refresh-db --dry-run --text
```
:::

:::layer advanced
- Without a path it reads `Expansion\dta\bin.pbo` of the game folder (the action list of `CfgDefaultKeysMapping`) with
  the names of `Expansion\dta\languagecore.pbo`, and the first profile of [[cli:arma2oa paths]].
- Path: a folder is read as the game folder, a file as a profile.
- Kept: the flight actions (Heli, Flaps, LandGear, AutoHover, LaunchCM, NextCM), the actions JoyPro's function list
  uses, and the weapon, view and push-to-talk actions a HOTAS often carries (Fire, SwitchWeapon, Eject, GetOut, the
  look and zoom actions ...). JoyPro's axis relations (`Axis.*`) come from JoyPro itself.
- Known actions keep their entry.
- `--dry-run` reads everything and writes nothing (step "Dry run: nothing was written."). Without it the list is
  written only when it changed.
- Safety: only JoyPro's list changes, never a game file; it is refused when the game is not installed or switched off
  in Settings > Games. The GUI can cancel it while the game's files are read; nothing is written then.
:::

:::layer technical
- Output: JSON of the refresh (`DryRun`, `FilesWritten`, `Steps` with `Actions: <old> -> <new>`, `Warnings`,
  `Errors`), or text with `--text`.
- Exit code `0`; `1` when nothing could be read: `Arma 2: Operation Arrowhead: neither the game's config nor a profile
  was found. Set the game folder (Settings > Games) or pass --path.`; the list stays unchanged.
- Writes `%LOCALAPPDATA%\JoyPro2\GameData\DB\Arma2OA\actions.tsv` (with a header line that has the date). After a real
  run the game's build is recorded for [[cli:db check-updates]].
- Related: [[cli:arma2oa catalog]], [[cli:arma2oa scan]], [[cli:db check-updates]]; game page [[game:arma2]].
:::

<!-- entry: arma2 import -->
:::layer beginner
Reads the aircraft and helicopter binds of your Arma 2 profile (the standalone game, `arma2.exe`) into your JoyPro
profile, with the sensitivity slider of each axis. Nothing in the game changes. The GUI runs it with **Import** on the
Arma 2 card of the Start page.

```powershell
engine arma2 import --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" --text
```
:::

:::layer advanced
It reads `%USERPROFILE%\Documents\ArmA 2\<name>.ArmA2Profile` (or one in `Documents\ArmA 2 Other Profiles\<name>\`).
Operation Arrowhead is the separate game `arma2oa`.

- Default: adds to the open profile (`--merge` means the same); `--replace` replaces every relation of the profile by
  what the file has.
- Keyboard binds are not read; `--include-keyboard` changes nothing.
- Safety: reads only, not refused while the game runs; the profile is kept as "Before import from Arma 2" first.
- `--ignore-device <name>` / `--ignore-devices <a;b>` leave the binds of those devices out (a name without
  `{GUID}` means every device of that name); a step line says how many binds were left out.
:::

:::layer technical
- Output: JSON of the import result or a text report with `--text`; exit code `1` on an error (`No Arma 2 config file
  found. Pass the path explicitly.`).
- Without a path it reads the profile [[cli:arma2 paths]] lists first; a fresh install builds the command list from
  `Dta\bin.pbo` first.
- With `--profile` the profile is saved afterwards; a game JoyPro never exported to is watched from now on.
- Related: [[cli:arma2 scan]], [[cli:arma2 export]], [[cli:arma2oa import]]; game page [[game:arma2]].
:::

<!-- entry: arma2 export -->
:::layer beginner
Writes your aircraft and helicopter binds into your Arma 2 profile; only the key lines of the flight actions change.
Close the game first, and start it once with every device connected. The GUI's **Export now** on the Arma 2 tile runs
it.

```powershell
engine arma2 export --mode clean --nuke-untouched-devices --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file"
```
:::

:::layer advanced
| `--mode` | In the profile |
|---|---|
| `add` | nothing is removed; a bind whose code another action of the same vehicle has gets a mark "Not written to the game" |
| `merge` | your codes go in; a code of another action of the same vehicle that you put elsewhere is dropped |
| `merge-overwrite` (engine default) | as `merge`, and an action you bind loses its other joystick codes of connected joysticks |
| `clean` (GUI default) | the flight lines also lose every other code of the written devices, the game's automatic defaults included |

- `--nuke-untouched-devices`: the same for other connected joysticks (not with `add`).
- Arma 2 alone cannot join both halves of a lever: the throttle and airbrake get one half. 32 buttons, 8 axes and the
  first hat per device.
- Never touched: infantry, vehicle and menu lines, keyboard, mouse, TrackIR and pad codes, joysticks that are not
  connected, `--ignore-device(s)`, the `--vjoy-tunnel` device, new device entries, `ArmA2.cfg`.
- Safety: refused while `arma2` runs; backed up first (undo: [[cli:backups list]]); the profile is kept as "Before
  export to Arma 2". `--dry-run` writes nothing.
:::

:::layer technical
- Output: JSON of the export result or a text report with `--text`; exit code `1` on an error (the game runs, an
  invalid `--mode`, no profile found).
- Without a path it writes the profile [[cli:arma2 paths]] lists first.
- After a real run: `<profile>.joypro-meta.json`, the watcher's fingerprint, the export marks.
- Related: [[cli:arma2 import]], [[cli:arma2oa export]]; game page [[game:arma2]].
:::

<!-- entry: arma2 paths -->
:::layer beginner
Lists where JoyPro finds the profiles of Arma 2 on this PC. The first one is what [[cli:arma2 scan]],
[[cli:arma2 import]] and [[cli:arma2 export]] use when you give no path. **Detect** in the **Other Games** panel
(Start page, **Expert import**) runs it and puts the first one into **File / Folder**.

```powershell
engine arma2 paths --text
```
:::

:::layer advanced
- Every `*.ArmA2Profile` in `%USERPROFILE%\Documents\ArmA 2\` and below `Documents\ArmA 2 Other Profiles\`, newest
  first; `.vars` and `.3den` files are skipped. Operation Arrowhead's profiles are the other game
  [[cli:arma2oa paths]].
- A config location you set yourself ([[cli:games config set]], Settings > Games) comes first.
- No options. Reads only; it also works while the game runs or when it is not installed.
:::

:::layer technical
- Output: JSON (`Game`, `DisplayName`, `ConfigPaths`, `Warnings`), or text with `--text` ("Arma 2 Config Paths",
  "Paths: <n>" and one line per location). Exit code `0`, also when nothing was found (empty list).
- `engine arma2` without an action runs `paths`.
- Related: [[cli:arma2 scan]], [[cli:games paths]], [[cli:games install set]]; game page [[game:arma2]].
:::

<!-- entry: arma2 catalog -->
:::layer beginner
Lists the Arma 2 actions JoyPro manages (its command list): id, axis or button, category and title. An export writes
only these actions, and the relation editor and the search list them.

```powershell
engine arma2 catalog --text
```
:::

:::layer advanced
- The list is `%LOCALAPPDATA%\JoyPro2\GameData\DB\Arma2\actions.tsv` (when that folder does not exist, the `DB\Arma2`
  folder next to the engine).
- It is built from the game's config and your profile by [[cli:arma2 refresh-db]], or by the first scan, import or
  export.
- `catalog` only shows the list; it never builds it. Titles that look like ids are shown readable.
- No options. Reads only; works without the game installed.
:::

:::layer technical
- Output: JSON (`Game`, `Source`, `Actions` with `Id`, `Title`, `IsAxis`, `Category`, `Warnings`), or text with
  `--text` ("Arma 2 Action Catalog", "Source:", "Actions: <n>", then `<id> | axis|button | <category> | <title>` per
  action).
- Without a list: the warning `Arma 2 action catalog not found: <path>` and an empty list, exit code `0`.
- Related: [[cli:arma2 refresh-db]], [[cli:arma2 export]], [[cli:db games]]; game page [[game:arma2]].
:::

<!-- entry: arma2 scan -->
:::layer beginner
Shows the joystick binds Arma 2 has now, without changing your profile or the game. Use it to look before an import.
**Look first** on the game's card of the Start page and **Scan** in the **Other Games** panel run it.

```powershell
engine arma2 scan --text
```
:::

:::layer advanced
- It reads the key lines of the cataloged actions and the devices of `class JoysticksList` (32 buttons per device; a
  device in another mode than Custom counts as off), with each axis's sensitivity slider.
- Path (positional or `--path`): another profile file.
- `--ignore-device <name>` / `--ignore-devices <a;b>` (separated by `|` or `;`, `--ignore-devices` also by commas) leave the binds of those devices out
  (a name without `{GUID}` means every device of that name); a step line names them and counts the binds.
- Keyboard binds are not read in this game.
- Safety: reads only and is not refused while the game runs. It is refused when the game is not installed (set its
  folder: [[cli:games install set]]) or switched off in Settings > Games.
- A fresh install has no command list yet: the first scan builds it from the installed game and saves it (a step line
  says so).
:::

:::layer technical
- Output: JSON (`ConfigPath`, `BindingsRead`, `Bindings` with `ActionId`, `Device`, `Input`, `IsAxis`, `Reformers`,
  `Inverted`, then `Steps`, `Warnings`, `Errors`), or text with `--text` ("Arma 2 Scan", "Config:", "Bindings Read:"
  and the list).
- Exit code `0`; `1` when the result has errors, for example `No Arma 2 config file found. Pass the path explicitly.`
- Unknown options fail with `Unknown Arma2 scan argument(s): ...`; MSFS options (`--game-profile`,
  `--include-disconnected`, `--include-sim-defaults`) are refused.
- Binds of the vJoy device JoyPro forwards into (`--vjoy-tunnel`) are left out.
- Related: [[cli:arma2 import]], [[cli:arma2 paths]], [[cli:arma2 catalog]]; game page [[game:arma2]].
:::

<!-- entry: arma2 refresh-db -->
:::layer beginner
Builds JoyPro's Arma 2 command list from the installed game's config and your profile, or adds the actions that are
new. Run it after a game update, or when JoyPro says the list is missing. **Refresh DB** in the **Other Games** panel
(its **DB Source** box is the path) and the Start page's notice "Command lists to build or update" run it.

```powershell
engine arma2 refresh-db --dry-run --text
```
:::

:::layer advanced
- Without a path it reads `Dta\bin.pbo` of the game folder (the action list of `CfgDefaultKeysMapping`) with the names
  of `Dta\languagecore.pbo`, and the first profile of [[cli:arma2 paths]].
- Path: a folder is read as the game folder, a file as a profile.
- Kept: the flight actions (Heli, Flaps, LandGear, AutoHover, LaunchCM, NextCM), the actions JoyPro's function list
  uses, and the weapon, view and push-to-talk actions a HOTAS often carries. JoyPro's axis relations (`Axis.*`, with
  the split lever `Axis.ThrottleBrake`) come from JoyPro itself.
- Known actions keep their entry.
- `--dry-run` reads everything and writes nothing (step "Dry run: nothing was written."). Without it the list is
  written only when it changed.
- Safety: only JoyPro's list changes, never a game file; it is refused when the game is not installed or switched off
  in Settings > Games. The GUI can cancel it while the game's files are read; nothing is written then.
:::

:::layer technical
- Output: JSON of the refresh (`DryRun`, `FilesWritten`, `Steps` with `Actions: <old> -> <new>`, `Warnings`,
  `Errors`), or text with `--text`.
- Exit code `0`; `1` when nothing could be read: `Arma 2: neither the game's config nor a profile was found. Set the
  game folder (Settings > Games) or pass --path.`; the list stays unchanged.
- Writes `%LOCALAPPDATA%\JoyPro2\GameData\DB\Arma2\actions.tsv` (with a header line that has the date). After a real
  run the game's build is recorded for [[cli:db check-updates]].
- Related: [[cli:arma2 catalog]], [[cli:arma2 scan]], [[cli:db check-updates]]; game page [[game:arma2]].
:::

<!-- entry: reforger import -->
:::layer beginner
Reads the helicopter binds of JoyPro's joystick preset for Arma Reforger into your JoyPro profile, with multiplier and
invert. Nothing in the game changes. The GUI runs it with **Import** on the Arma Reforger card of the Start page.

```powershell
engine reforger import --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" --text
```
:::

:::layer advanced
It reads `%USERPROFILE%\Documents\My Games\ArmaReforger\profile\.save\settings\customInputConfigs\JoyPro.conf` and maps
the game's `joystick0` to `joystick3` back to your devices through the device order rules of the game's engine
settings.

- Default: adds to the open profile (`--merge` means the same); `--replace` replaces every relation of the profile by
  what the preset has.
- Keyboard binds and modifiers are not read; `--include-keyboard` changes nothing.
- Safety: reads only, not refused while the game runs; the profile is kept as "Before import from Arma Reforger"
  first.
- `--ignore-device <name>` / `--ignore-devices <a;b>` leave the binds of those devices out (a name without
  `{GUID}` means every device of that name); a step line says how many binds were left out.
:::

:::layer technical
- Output: JSON of the import result or a text report with `--text`; exit code `1` on an error (`No Arma Reforger
  config file found. Pass the path explicitly.`).
- Without a path it reads the file [[cli:reforger paths]] lists first; a fresh install builds the command list from
  the game's archives first.
- With `--profile` the profile is saved afterwards; a game JoyPro never exported to is watched from now on.
- Related: [[cli:reforger scan]], [[cli:reforger export]]; game page [[game:arma-reforger]].
:::

<!-- entry: reforger export -->
:::layer beginner
Writes JoyPro's joystick preset `JoyPro.conf` for Arma Reforger with your helicopter flight binds, and sorts your
devices into the game's four joystick slots. Close the game first; then pick **JoyPro** once under Settings >
Controllers > Joystick preset and restart the game. The GUI's **Export now** on the Arma Reforger tile runs it.

```powershell
engine reforger export --mode clean --nuke-untouched-devices --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" --text
```
:::

:::layer advanced
Only the flight `Action` blocks of the preset change; a block left without sources is removed.

| `--mode` | In `JoyPro.conf` |
|---|---|
| `add` | every source stays; a bind whose joystick input another flight action uses gets a mark "Not written to the game" |
| `merge` | your sources go in; another flight action's source on the same input is removed |
| `merge-overwrite` (engine default) | as `merge`, and an action you bind loses its other joystick sources of connected devices |
| `clean` (GUI default) | the flight sources of the devices you write are replaced by yours |

- `--nuke-untouched-devices`: the same for connected devices without binds in your profile (not with `add`).
- Device order: JoyPro edits the `JoystickOrderRule` entries of `ReforgerEngineSettings.conf` (slots 0 to 3, a slot you
  chose with `joystickSlot` first) in every settings folder it finds; without a saved settings file it warns. More than
  four devices get a warning.
- Per bind a multiplier and invert; no dead zones or curves. Game settings you stored (HOTAS collective mode, force
  feedback intensity) go into `ReforgerGameSettings.conf`, also from a profile without Reforger binds.
- Never touched: `InputUserSettings.conf`, your other presets, infantry, vehicle and menu actions, keyboard, mouse and
  pad sources, joysticks that are not connected, `--ignore-device(s)`, the `--vjoy-tunnel` device, other lines of the
  settings files.
- Safety: refused while `ArmaReforgerSteam`, `ArmaReforger_BE` or `ArmaReforger` runs; backed up first (undo:
  [[cli:backups list]]); the profile is kept as "Before export to Arma Reforger". `--dry-run` writes nothing.
:::

:::layer technical
- Output: JSON of the export result or a text report with `--text`; exit code `1` on an error (the game runs, an
  invalid `--mode`, no file found).
- Without a path it writes the file [[cli:reforger paths]] lists first.
- After a real run: `JoyPro.conf.joypro-meta.json`, the watcher's fingerprint, the export marks
  ([[cli:export-marks list]]).
- Related: [[cli:reforger import]], [[cli:ffb set]] (`joystickSlot`, game settings); game page
  [[game:arma-reforger]].
:::

<!-- entry: reforger paths -->
:::layer beginner
Lists where JoyPro finds the joystick preset of Arma Reforger on this PC. The first one is what [[cli:reforger scan]],
[[cli:reforger import]] and [[cli:reforger export]] use when you give no path. **Detect** in the **Other Games** panel
(Start page, **Expert import**) runs it and puts the first one into **File / Folder**.

```powershell
engine reforger paths --text
```
:::

:::layer advanced
- `%USERPROFILE%\Documents\My Games\ArmaReforger\profile\.save\settings\customInputConfigs\JoyPro.conf`, listed as
  soon as the game's `settings` folder exists. The file itself is JoyPro's preset: the first export writes it.
- A config location you set yourself ([[cli:games config set]], Settings > Games) comes first.
- No options. Reads only; it also works while the game runs or when it is not installed.
:::

:::layer technical
- Output: JSON (`Game`, `DisplayName`, `ConfigPaths`, `Warnings`), or text with `--text` ("Arma Reforger Config
  Paths", "Paths: <n>" and one line per location). Exit code `0`, also when nothing was found (empty list).
- `engine reforger` without an action runs `paths`.
- Before the first export the listed file does not exist yet; a scan or import then reports `Reforger input config not
  found: <path>`.
- Related: [[cli:reforger scan]], [[cli:games paths]], [[cli:games install set]]; game page [[game:arma-reforger]].
:::

<!-- entry: reforger catalog -->
:::layer beginner
Lists the Arma Reforger actions JoyPro manages (its command list): id, axis or button, category and title. An export
writes only these actions, and the relation editor and the search list them.

```powershell
engine reforger catalog --text
```
:::

:::layer advanced
- The list is `%LOCALAPPDATA%\JoyPro2\GameData\DB\ArmaReforger\actions.tsv` (when that folder does not exist, the
  `DB\ArmaReforger` folder next to the engine).
- It is built from the game's archives by [[cli:reforger refresh-db]], or by the first scan, import or export.
- `catalog` only shows the list; it never builds it. Titles that look like ids are shown readable.
- No options. Reads only; works without the game installed.
:::

:::layer technical
- Output: JSON (`Game`, `Source`, `Actions` with `Id`, `Title`, `IsAxis`, `Category`, `Warnings`), or text with
  `--text` ("Arma Reforger Action Catalog", "Source:", "Actions: <n>", then `<id> | axis|button | <category> |
  <title>` per action).
- Without a list: the warning `Arma Reforger action catalog not found: <path>` and an empty list, exit code `0`.
- Related: [[cli:reforger refresh-db]], [[cli:reforger export]], [[cli:db games]]; game page [[game:arma-reforger]].
:::

<!-- entry: reforger scan -->
:::layer beginner
Shows the joystick binds Arma Reforger has now, without changing your profile or the game. Use it to look before an
import. **Look first** on the game's card of the Start page and **Scan** in the **Other Games** panel run it.

```powershell
engine reforger scan --text
```
:::

:::layer advanced
- It reads the helicopter flight `Action` blocks of `JoyPro.conf` with multiplier and invert, and maps the game's
  `joystick0` to `joystick3` back to your devices through the device order of the game's engine settings.
- Path (positional or `--path`): another preset file.
- `--ignore-device <name>` / `--ignore-devices <a;b>` (separated by `|` or `;`, `--ignore-devices` also by commas) leave the binds of those devices out
  (a name without `{GUID}` means every device of that name); a step line names them and counts the binds.
- Keyboard binds are not read in this game.
- Safety: reads only and is not refused while the game runs. It is refused when the game is not installed (set its
  folder: [[cli:games install set]]) or switched off in Settings > Games.
- A fresh install has no command list yet: the first scan builds it from the installed game and saves it (a step line
  says so).
:::

:::layer technical
- Output: JSON (`ConfigPath`, `BindingsRead`, `Bindings` with `ActionId`, `Device`, `Input`, `IsAxis`, `Reformers`,
  `Inverted`, then `Steps`, `Warnings`, `Errors`), or text with `--text` ("Arma Reforger Scan", "Config:", "Bindings
  Read:" and the list).
- Exit code `0`; `1` when the result has errors, for example `No Arma Reforger config file found. Pass the path
  explicitly.`, or `Reforger input config not found: <path>` before the first export.
- Unknown options fail with `Unknown ArmaReforger scan argument(s): ...`; MSFS options (`--game-profile`,
  `--include-disconnected`, `--include-sim-defaults`) are refused.
- Binds of the vJoy device JoyPro forwards into (`--vjoy-tunnel`) are left out.
- Related: [[cli:reforger import]], [[cli:reforger paths]], [[cli:reforger catalog]]; game page
  [[game:arma-reforger]].
:::

<!-- entry: reforger refresh-db -->
:::layer beginner
Builds JoyPro's Arma Reforger command list from the installed game's archives, or adds the actions that are new. Run
it after a game update, or when JoyPro says the list is missing. **Refresh DB** in the **Other Games** panel (its **DB
Source** box is the path) and the Start page's notice "Command lists to build or update" run it.

```powershell
engine reforger refresh-db --dry-run --text
```
:::

:::layer advanced
- Without a path it reads the game folder's `.pak` archives under `addons`: `Configs/System/chimeraInputCommon.conf`
  (the flight contexts of its action manager), the key binding menu and `Language/localization.en_us.conf` for the
  titles.
- Path: the game folder, one `.pak` file, a folder with the extracted files, or `chimeraInputCommon.conf` itself.
- Then your own input configs (the `*.conf` presets of `customInputConfigs` and `InputUserSettings.conf`) are read for
  flight actions mods add.
- Known actions keep their entry.
- `--dry-run` reads everything and writes nothing (step "Dry run: nothing was written."). Without it the list is
  written only when it changed.
- Safety: only JoyPro's list changes, never a game file; it is refused when the game is not installed or switched off
  in Settings > Games. The GUI can cancel it while the game's files are read; nothing is written then.
:::

:::layer technical
- Output: JSON of the refresh (`DryRun`, `FilesWritten`, `Steps` with `Actions: <old> -> <new>`, `Warnings`,
  `Errors`), or text with `--text`.
- Exit code `0`; `1` when nothing could be read: `Arma Reforger was not found. Set its folder (engine games install
  set reforger <folder>) or pass the game folder, a .pak file or the extracted
  Configs\System\chimeraInputCommon.conf.`; the list stays unchanged.
- Writes `%LOCALAPPDATA%\JoyPro2\GameData\DB\ArmaReforger\actions.tsv` (with a header line that has the date). After a
  real run the game's build is recorded for [[cli:db check-updates]].
- Related: [[cli:reforger catalog]], [[cli:reforger scan]], [[cli:db check-updates]]; game page
  [[game:arma-reforger]].
:::

<!-- entry: vtolvr import -->
:::layer beginner
Reads the BYOJoystick mod's binds of VTOL VR into your JoyPro profile: every target per vehicle seat, from your sticks,
throttles and (on request) the keyboard. Nothing in the game changes. The GUI runs it with **Import** on the VTOL VR
card of the Start page.

```powershell
engine vtolvr import --include-keyboard --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" --text
```
:::

:::layer advanced
It reads the seat files `<install>\SaveData\BYOJ_<seat>.xml` (`AV42C`, `FA26B`, `F45A`, `AH94Front`, `AH94Rear`,
`T55Front`, `T55Rear`, `EF24GFront`, `EF24GRear`). The command list exists only with the mod installed
([[cli:vtolvr mod]]).

- Default: adds to the open profile (`--merge` means the same); `--replace` replaces every relation of the profile by
  what the seat files have.
- `--include-keyboard` also reads the keyboard binds (with their one modifier key).
- Devices are matched by the DirectInput instance GUID; invert and the mod's Deadzone number come in per bind.
- Safety: reads only, not refused while the game runs; the profile is kept as "Before import from VTOL VR (BYOJoystick
  mod)" first.
- `--ignore-device <name>` / `--ignore-devices <a;b>` leave the binds of those devices out (a name without
  `{GUID}` means every device of that name); a step line says how many binds were left out.
:::

:::layer technical
- Output: JSON of the import result or a text report with `--text`; exit code `1` on an error (`No VTOL VR (BYOJoystick
  mod) config file found. Pass the path explicitly.`).
- Without a path it reads the location [[cli:vtolvr paths]] lists first.
- With `--profile` the profile is saved afterwards. The imported inputs are recorded as JoyPro's, and a game JoyPro
  never exported to is watched from now on.
- Related: [[cli:vtolvr scan]], [[cli:vtolvr export]], [[cli:vtolvr-native import]]; game page [[game:vtol-vr]].
:::

<!-- entry: vtolvr export -->
:::layer beginner
Writes your binds into the BYOJoystick mod's seat files of VTOL VR; a relation goes into every seat whose vehicle has
that target. Close VTOL VR first. The GUI's **Export now** on the VTOL VR tile runs it.

```powershell
engine vtolvr export --mode clean --nuke-untouched-devices --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" --text
```
:::

:::layer advanced
It writes `<install>\SaveData\BYOJ_<seat>.xml`, one file per vehicle seat, atomically (a temporary file, then
replace).

| `--mode` | Per seat file |
|---|---|
| `add` | a bind on an input another target uses (with the same modifier state) is listed, not written |
| `merge` | the other target on your input loses it; a moved target keeps its old input |
| `merge-overwrite` (engine default) | as `merge`, and your targets leave their other inputs on connected devices (keys only with `--include-keyboard`) |
| `clean` (GUI default) | the devices JoyPro writes keep only your binds |

- `--nuke-untouched-devices`: connected devices without binds are cleared (not with `add`). VTOL VR counts as a
  flight-only game.
- `--include-keyboard` writes your keyboard binds; without it the keyboard stays.
- Per bind: invert and the mod's one Deadzone number. Hat diagonals, a button on an axis target and an axis on a button
  target are skipped with a warning.
- Never touched: devices that are not connected, `--ignore-device(s)`, the `--vjoy-tunnel` device, inputs JoyPro cannot
  read, the seat's modifier button unless your profile brings its own, `gameSettings.cfg`.
- Safety: refused while `VTOLVR` runs; backed up first (undo: [[cli:backups list]]); the profile is kept as "Before
  export to VTOL VR (BYOJoystick mod)". `--dry-run` writes nothing.
:::

:::layer technical
- Output: JSON of the export result or a text report with `--text`; exit code `1` on an error (`VTOL VR (BYOJoystick
  mod) is running (VTOLVR). Close the game first; it overwrites its config on exit.`, an invalid `--mode`, no location
  found).
- Without a path it writes the location [[cli:vtolvr paths]] lists first.
- After a real run: JoyPro's `.joypro-meta.json`, the watcher's fingerprint, the export marks
  ([[cli:export-marks list]]).
- Related: [[cli:vtolvr import]], [[cli:vtolvr mod]], [[cli:vtolvr-native export]]; game page [[game:vtol-vr]].
:::

<!-- entry: vtolvr paths -->
:::layer beginner
Lists where JoyPro finds the seat files folder of VTOL VR (BYOJoystick mod) on this PC. The first one is what
[[cli:vtolvr scan]], [[cli:vtolvr import]] and [[cli:vtolvr export]] use when you give no path. **Detect** in the
**Other Games** panel (Start page, **Expert import**) runs it and puts the first one into **File / Folder**.

```powershell
engine vtolvr paths --text
```
:::

:::layer advanced
- `<Steam library>\steamapps\common\VTOL VR\SaveData` of every Steam library that has VTOL VR. With a folder set by
  [[cli:games install set]] only that folder's `SaveData`.
- A config location you set yourself ([[cli:games config set]], Settings > Games) comes first.
- No options. Reads only; it also works while the game runs or when it is not installed.
:::

:::layer technical
- Output: JSON (`Game`, `DisplayName`, `ConfigPaths`, `Warnings`), or text with `--text` ("VTOL VR (BYOJoystick mod)
  Config Paths", "Paths: <n>" and one line per location). Exit code `0`, also when nothing was found (empty list).
- `engine vtolvr` without an action runs `paths`.
- The entry is a folder: the mod keeps one `BYOJ_<seat>.xml` per vehicle seat there.
- Related: [[cli:vtolvr scan]], [[cli:games paths]], [[cli:games install set]]; game page [[game:vtol-vr]].
:::

<!-- entry: vtolvr catalog -->
:::layer beginner
Lists the VTOL VR (BYOJoystick mod) actions JoyPro manages (its command list): id, axis or button, category and title.
An export writes only these actions, and the relation editor and the search list them.

```powershell
engine vtolvr catalog --text
```
:::

:::layer advanced
- The list is `%LOCALAPPDATA%\JoyPro2\GameData\DB\VTOLVR\actions.tsv` (when that folder does not exist, the
  `DB\VTOLVR` folder next to the engine).
- It is built from the installed BYOJoystick mod and your seat files by [[cli:vtolvr refresh-db]] ([[cli:vtolvr mod]]
  installs the mod).
- `catalog` only shows the list; it never builds it. Titles that look like ids are shown readable.
- No options. Reads only; works without the game installed.
:::

:::layer technical
- Output: JSON (`Game`, `Source`, `Actions` with `Id`, `Title`, `IsAxis`, `Category`, `Warnings`), or text with
  `--text` ("VTOL VR (BYOJoystick mod) Action Catalog", "Source:", "Actions: <n>", then `<id> | axis|button |
  <category> | <title>` per action).
- Without a list: the warning `VTOL VR (BYOJoystick mod) action catalog not found: <path>` and an empty list, exit
  code `0`.
- Related: [[cli:vtolvr refresh-db]], [[cli:vtolvr export]], [[cli:db games]]; game page [[game:vtol-vr]].
:::

<!-- entry: vtolvr scan -->
:::layer beginner
Shows the joystick binds VTOL VR (BYOJoystick mod) has now, without changing your profile or the game. Use it to look
before an import. **Look first** on the game's card of the Start page and **Scan** in the **Other Games** panel run
it.

```powershell
engine vtolvr scan --text
```
:::

:::layer advanced
- It reads the seat files `BYOJ_<seat>.xml` (`AV42C`, `FA26B`, `F45A`, `AH94Front`, `AH94Rear`, `T55Front`, `T55Rear`,
  `EF24GFront`, `EF24GRear`); devices are matched by the DirectInput instance GUID, invert and the mod's Deadzone
  come per bind.
- Path (positional or `--path`): another `SaveData` folder.
- `--ignore-device <name>` / `--ignore-devices <a;b>` (separated by `|` or `;`, `--ignore-devices` also by commas) leave the binds of those devices out
  (a name without `{GUID}` means every device of that name); a step line names them and counts the binds.
- Keyboard binds JoyPro reads in this game are listed too; [[cli:vtolvr import]] takes them only with
  `--include-keyboard`.
- Safety: reads only and is not refused while the game runs. It is refused when the game is not installed (set its
  folder: [[cli:games install set]]) or switched off in Settings > Games.
- A fresh install has no command list yet: the first scan builds it from the installed mod and your seat files;
  without the mod a warning names [[cli:vtolvr refresh-db]] and the binds cannot be matched.
:::

:::layer technical
- Output: JSON (`ConfigPath`, `BindingsRead`, `Bindings` with `ActionId`, `Device`, `Input`, `IsAxis`, `Reformers`,
  `Inverted`, then `Steps`, `Warnings`, `Errors`), or text with `--text` ("VTOL VR (BYOJoystick mod) Scan",
  "Config:", "Bindings Read:" and the list).
- Exit code `0`; `1` when the result has errors, for example `No VTOL VR (BYOJoystick mod) config file found. Pass the
  path explicitly.`
- Unknown options fail with `Unknown VTOLVR scan argument(s): ...`; MSFS options (`--game-profile`,
  `--include-disconnected`, `--include-sim-defaults`) are refused.
- Binds of the vJoy device JoyPro forwards into (`--vjoy-tunnel`) are left out.
- Related: [[cli:vtolvr import]], [[cli:vtolvr paths]], [[cli:vtolvr catalog]]; game page [[game:vtol-vr]].
:::

<!-- entry: vtolvr-native import -->
:::layer beginner
Reads VTOL VR's own binds into your JoyPro profile: **Rudder**, **Wheel Brake L** and **Wheel Brake R** on your pedals,
the only actions the game itself binds to a joystick. Nothing in the game changes. The GUI runs it with **Import** on
the card of VTOL VR's native part.

```powershell
engine vtolvr-native import --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" --text
```
:::

:::layer advanced
It reads the Rewired joystick maps in `%APPDATA%\Boundless Dynamics, LLC\VTOLVR\SaveData\inputbindings.cfg`.

- Default: adds to the open profile (`--merge` means the same); `--replace` replaces every relation of the profile by
  what the file has.
- No keyboard or modifier binds in this part; `--include-keyboard` changes nothing.
- Invert and, for the toe brakes, the axis range come in.
- Safety: reads only, not refused while the game runs; the profile is kept as "Before import from VTOL VR (native
  rudder and brakes)" first.
- `--ignore-device <name>` / `--ignore-devices <a;b>` leave the binds of those devices out (a name without
  `{GUID}` means every device of that name); a step line says how many binds were left out.
:::

:::layer technical
- Output: JSON of the import result or a text report with `--text`; exit code `1` on an error (`No VTOL VR (native
  rudder and brakes) config file found. Pass the path explicitly.`).
- Without a path it reads the file [[cli:vtolvr-native paths]] lists first; a fresh install builds the command list
  from the game's `level0` first.
- With `--profile` the profile is saved afterwards; a game JoyPro never exported to is watched from now on.
- Related: [[cli:vtolvr-native scan]], [[cli:vtolvr-native export]], [[cli:vtolvr import]]; game page
  [[game:vtol-vr]].
:::

<!-- entry: vtolvr-native export -->
:::layer beginner
Writes your rudder and toe brake binds into VTOL VR's own `inputbindings.cfg`, and the control settings you stored into
`gameSettings.cfg`. Close VTOL VR first. The GUI's **Export now** on the native VTOL VR tile runs it.

```powershell
engine vtolvr-native export --mode clean --nuke-untouched-devices --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file"
```
:::

:::layer advanced
It writes the joystick maps of `%APPDATA%\Boundless Dynamics, LLC\VTOLVR\SaveData\inputbindings.cfg` atomically
(temporary file, then replace).

| `--mode` | In `inputbindings.cfg` |
|---|---|
| `add` | free axes only; a taken axis is listed, not written |
| `merge` | the action on your axis is replaced; everything else stays |
| `merge-overwrite` (engine default) | as `merge`, and your actions leave their other axes on connected devices |
| `clean` (GUI default) | the other actions of the written devices are cleared |

- `--nuke-untouched-devices`: connected devices without binds are cleared (not with `add`).
- Only devices the game has listed are written. Invert, and the axis range (Full or Positive) of the toe brakes; no
  dead zones or curves.
- Control settings you stored (Hardware Control Rudder, VR dead zones, haptics) go into `gameSettings.cfg`, also from a
  profile without native binds; JoyPro never creates that file.
- Never touched: button, keyboard and mouse maps, input behaviours, calibration maps, controller assignments, devices
  that are not connected, `--ignore-device(s)`, the `--vjoy-tunnel` device, the legacy `<install>\SaveData\inputbindings.cfg`.
- Safety: refused while `VTOLVR` runs; backed up first (undo: [[cli:backups list]]); the profile is kept as "Before
  export to VTOL VR (native rudder and brakes)". `--dry-run` writes nothing.
:::

:::layer technical
- Output: JSON of the export result or a text report with `--text`; exit code `1` on an error (the game runs, an
  invalid `--mode`, no file found).
- Without a path it writes the file [[cli:vtolvr-native paths]] lists first.
- After a real run: JoyPro's `.joypro-meta.json`, the watcher's fingerprint, the export marks.
- Related: [[cli:vtolvr-native import]], [[cli:vtolvr export]], [[cli:ffb set]] (`--game VTOLVR`); game page
  [[game:vtol-vr]].
:::

<!-- entry: vtolvr-native paths -->
:::layer beginner
Lists where JoyPro finds the input bindings file of VTOL VR (native rudder and brakes) on this PC. The first one is
what [[cli:vtolvr-native scan]], [[cli:vtolvr-native import]] and [[cli:vtolvr-native export]] use when you give no
path. **Detect** in the **Other Games** panel (Start page, **Expert import**) runs it and puts the first one into
**File / Folder**.

```powershell
engine vtolvr-native paths --text
```
:::

:::layer advanced
- `%APPDATA%\Boundless Dynamics, LLC\VTOLVR\SaveData\inputbindings.cfg`, listed when the file exists.
- A config location you set yourself ([[cli:games config set]], Settings > Games) comes first.
- No options. Reads only; it also works while the game runs or when it is not installed.
:::

:::layer technical
- Output: JSON (`Game`, `DisplayName`, `ConfigPaths`, `Warnings`), or text with `--text` ("VTOL VR (native rudder and
  brakes) Config Paths", "Paths: <n>" and one line per location). Exit code `0`, also when nothing was found (empty
  list).
- `engine vtolvr-native` without an action runs `paths`.
- Related: [[cli:vtolvr-native scan]], [[cli:games paths]], [[cli:games install set]]; game page [[game:vtol-vr]].
:::

<!-- entry: vtolvr-native catalog -->
:::layer beginner
Lists the VTOL VR (native rudder and brakes) actions JoyPro manages (its command list): id, axis or button, category
and title. An export writes only these actions, and the relation editor and the search list them.

```powershell
engine vtolvr-native catalog --text
```
:::

:::layer advanced
- The list is `%LOCALAPPDATA%\JoyPro2\GameData\DB\VTOLVRNative\actions.tsv` (when that folder does not exist, the
  `DB\VTOLVRNative` folder next to the engine).
- It is built from the game's `level0` by [[cli:vtolvr-native refresh-db]], or by the first scan, import or export.
- `catalog` only shows the list; it never builds it. Titles that look like ids are shown readable.
- No options. Reads only; works without the game installed.
:::

:::layer technical
- Output: JSON (`Game`, `Source`, `Actions` with `Id`, `Title`, `IsAxis`, `Category`, `Warnings`), or text with
  `--text` ("VTOL VR (native rudder and brakes) Action Catalog", "Source:", "Actions: <n>", then `<id> | axis|button
  | <category> | <title>` per action).
- Without a list: the warning `VTOL VR (native rudder and brakes) action catalog not found: <path>` and an empty list,
  exit code `0`.
- Related: [[cli:vtolvr-native refresh-db]], [[cli:vtolvr-native export]], [[cli:db games]]; game page
  [[game:vtol-vr]].
:::

<!-- entry: vtolvr-native scan -->
:::layer beginner
Shows the joystick binds VTOL VR (native rudder and brakes) has now, without changing your profile or the game. Use it
to look before an import. **Look first** on the game's card of the Start page and **Scan** in the **Other Games**
panel run it.

```powershell
engine vtolvr-native scan --text
```
:::

:::layer advanced
- It reads the Rewired joystick maps of `inputbindings.cfg` (rudder, toe brakes and the other actions the game binds
  itself), with invert and, for the toe brakes, the axis range.
- Path (positional or `--path`): another `inputbindings.cfg`.
- `--ignore-device <name>` / `--ignore-devices <a;b>` (separated by `|` or `;`, `--ignore-devices` also by commas) leave the binds of those devices out
  (a name without `{GUID}` means every device of that name); a step line names them and counts the binds.
- Keyboard binds are not read in this game.
- Safety: reads only and is not refused while the game runs. It is refused when the game is not installed (set its
  folder: [[cli:games install set]]) or switched off in Settings > Games.
- A fresh install has no command list yet: the first scan builds it from the installed game and saves it (a step line
  says so).
:::

:::layer technical
- Output: JSON (`ConfigPath`, `BindingsRead`, `Bindings` with `ActionId`, `Device`, `Input`, `IsAxis`, `Reformers`,
  `Inverted`, then `Steps`, `Warnings`, `Errors`), or text with `--text` ("VTOL VR (native rudder and brakes) Scan",
  "Config:", "Bindings Read:" and the list).
- Exit code `0`; `1` when the result has errors, for example `No VTOL VR (native rudder and brakes) config file found.
  Pass the path explicitly.`
- Unknown options fail with `Unknown VTOLVRNative scan argument(s): ...`; MSFS options (`--game-profile`,
  `--include-disconnected`, `--include-sim-defaults`) are refused.
- Binds of the vJoy device JoyPro forwards into (`--vjoy-tunnel`) are left out.
- Related: [[cli:vtolvr-native import]], [[cli:vtolvr-native paths]], [[cli:vtolvr-native catalog]]; game page
  [[game:vtol-vr]].
:::

<!-- entry: vtolvr-native refresh-db -->
:::layer beginner
Builds JoyPro's VTOL VR (native rudder and brakes) command list from the installed game's Rewired action list, or adds
the actions that are new. Run it after a game update, or when JoyPro says the list is missing. **Refresh DB** in the
**Other Games** panel (its **DB Source** box is the path) and the Start page's notice "Command lists to build or
update" run it.

```powershell
engine vtolvr-native refresh-db --dry-run --text
```
:::

:::layer advanced
- It reads `VTOLVR_Data\level0` of the installation (path: the game folder, its `VTOLVR_Data` folder or the file):
  every user-assignable Rewired action that is not in the list comes in, with the game's descriptive name, axis or
  button and its Rewired id.
- A known action the game no longer has stays, with a warning.
- `--dry-run` reads everything and writes nothing (step "Dry run: nothing was written."). Without it the list is
  written only when it changed.
- Safety: only JoyPro's list changes, never a game file; it is refused when the game is not installed or switched off
  in Settings > Games. The GUI can cancel it while the game's files are read; nothing is written then.
:::

:::layer technical
- Output: JSON of the refresh (`DryRun`, `FilesWritten`, `Steps` with `Actions: <old> -> <new>`, `Warnings`,
  `Errors`), or text with `--text`.
- Exit code `0`; `1` when nothing could be read: `VTOL VR's VTOLVR_Data\level0 was not found. Pass --path with the
  game folder, its VTOLVR_Data folder or the file.` or the read error of the file; the list stays unchanged.
- Writes `%LOCALAPPDATA%\JoyPro2\GameData\DB\VTOLVRNative\actions.tsv` (with a header line that has the date). After a
  real run the game's build is recorded for [[cli:db check-updates]].
- Related: [[cli:vtolvr-native catalog]], [[cli:vtolvr-native scan]], [[cli:db check-updates]]; game page
  [[game:vtol-vr]].
:::

<!-- entry: nuclearoption import -->
:::layer beginner
Reads the joystick maps of your connected devices from Nuclear Option into your JoyPro profile: axes with their invert,
buttons, and buttons on an axis. Nothing in the game changes. Start the game once before, so its settings exist. The
GUI runs it with **Import** on the Nuclear Option card of the Start page.

```powershell
engine nuclearoption import --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" --text
```
:::

:::layer advanced
It reads the game's settings in the registry, `HKCU\Software\Shockfront\NuclearOption`: one `ControllerMap` value per
device for the player `Player0` (Rewired XML).

- Default: adds to the open profile (`--merge` means the same); `--replace` replaces every relation of the profile by
  what the game has.
- Keyboard binds and modifiers are not read; `--include-keyboard` changes nothing. Hats are not read yet.
- Two or more devices of one model cannot be told apart and are left out (warning). A device without a saved map uses
  the game's default map, which JoyPro cannot read.
- Safety: reads only, not refused while the game runs; the profile is kept as "Before import from Nuclear Option"
  first.
- `--ignore-device <name>` / `--ignore-devices <a;b>` leave the binds of those devices out (a name without
  `{GUID}` means every device of that name); a step line says how many binds were left out.
:::

:::layer technical
- Output: JSON of the import result or a text report with `--text`; exit code `1` on an error (`No Nuclear Option config
  file found. Pass the path explicitly.`).
- The path may be the registry key itself. Without one it reads the key [[cli:nuclearoption paths]] lists; a fresh
  install builds the command list from `NuclearOption_Data\resources.assets` first.
- JoyPro's names, groups and tuning come back from
  `%LOCALAPPDATA%\JoyPro2\GameMetadata\HKCU_Software_Shockfront_NuclearOption.joypro-meta.json`.
- Related: [[cli:nuclearoption scan]], [[cli:nuclearoption export]]; game page [[game:nuclear-option]].
:::

<!-- entry: nuclearoption export -->
:::layer beginner
Writes your Flight Controls binds into the joystick map of each device in Nuclear Option's settings; a device without a
saved map gets a new one. Close the game first. The GUI's **Export now** on the Nuclear Option tile runs it.

```powershell
engine nuclearoption export --mode clean --nuke-untouched-devices --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" --text
```
:::

:::layer advanced
It edits Player0's `ControllerMap` values under `HKCU\Software\Shockfront\NuclearOption`, only the element maps it
changes; a new map gets its list of known action ids, so the game adds no default binds to it.

| `--mode` | In the maps of your devices |
|---|---|
| `add` | nothing of the game changes; a bind whose button or axis drives another action (or the other pole of an axis) is listed |
| `merge` | a Flight Controls action on an input you give another action loses it; the action keeps its other inputs |
| `merge-overwrite` (engine default) | as `merge`, and every action (or pole) you bind leaves its other inputs on every connected device JoyPro can read |
| `clean` (GUI default) | every other Flight Controls action of the written devices is released |

- `--nuke-untouched-devices`: the Flight Controls actions of the other saved maps of connected devices are released;
  devices without a saved map keep the game's default map (not with `add`).
- No curves, keyboard binds, modifiers or hats are written; sliders are written with a warning.
- Never touched: actions outside Flight Controls (camera, gameplay, chat, debug, mission editor), keyboard and mouse
  maps, calibration maps, every game setting (`ThrottleUseNegative` too), devices that are not connected, two devices
  of one model, `--ignore-device(s)`, the `--vjoy-tunnel` device.
- Safety: refused while `NuclearOption` runs; Player0's controller-map values go into the backup store first (undo
  writes them back and removes values JoyPro added: [[cli:backups list]]); the profile is kept as "Before export to
  Nuclear Option". `--dry-run` writes nothing.
:::

:::layer technical
- Output: JSON of the export result or a text report with `--text`; exit code `1` on an error (`Nuclear Option is
  running (NuclearOption). Close the game first; it overwrites its config on exit.`, an invalid `--mode`, no key found).
- Without a path it writes the key [[cli:nuclearoption paths]] lists. A throttle axis bind with the game's negative
  throttle region off gets a warning.
- After a real run: the `.joypro-meta.json` in `%LOCALAPPDATA%\JoyPro2\GameMetadata`, the watcher's fingerprint, the
  export marks ([[cli:export-marks list]]).
- The help note says "only aircraft/helicopter actions"; here that is the Flight Controls category.
- Related: [[cli:nuclearoption import]], [[cli:tinycombatarena export]]; game page [[game:nuclear-option]].
:::

<!-- entry: nuclearoption paths -->
:::layer beginner
Shows where JoyPro finds Nuclear Option's binds on this PC: the game's registry key
`HKCU\Software\Shockfront\NuclearOption`, which exists once you started the game. Scan, import and export use it when
you give no path; **Detect** next to **File / Folder** in the import wizard's **Expert import** section runs it.

```powershell
engine nuclearoption paths --text
```
:::

:::layer advanced
- No options. Listed: your own config location of [[cli:games config set]] first, then the registry key when it
  exists. An empty list means the game has not saved any settings yet: start it once.
- Read-only; also works when the game is not installed or switched off in Settings > Games.
:::

:::layer technical
- Output: JSON (`Game`, `DisplayName`, `ConfigPaths`, `Warnings`), or with `--text` the lines `Nuclear Option Config
  Paths`, `Paths: <n>` and one line per location. Exit code `0`, also when nothing is found; an unknown option fails
  with `Unknown NuclearOption paths argument(s): ...`.
- Related: [[cli:nuclearoption scan]], [[cli:games paths]]; game page [[game:nuclear-option]].
:::

<!-- entry: nuclearoption catalog -->
:::layer beginner
Lists the Nuclear Option actions JoyPro knows, with axis or button and category: the actions the relation editor
offers for the game and the ones import and export match.

```powershell
engine nuclearoption catalog --text
```
:::

:::layer advanced
- No options; read-only, also when the game is not installed.
- The list comes from the game's own action list ([[cli:nuclearoption refresh-db]]); every category is in it (Flight
  Controls, Camera Controls, Gameplay, Chat ...), although export only releases Flight Controls actions. Each axis
  action also has the two button rows `<action>.Positive` and `<action>.Negative`.
- A fresh install has no list yet; the first scan, import, export or refresh builds it. Until then the command lists no
  action and warns `Nuclear Option action catalog not found: <path>`.
:::

:::layer technical
- Output: JSON (`Game`, `Source`, `Actions` with `Id`, `Title`, `IsAxis`, `Category`, `Warnings`), or with `--text`
  one line per action: `<id> | axis|button | <category> | <title>`. Exit code `0`.
- File: `%LOCALAPPDATA%\JoyPro2\GameData\DB\NuclearOption\actions.tsv` (fifth column: the game's numeric action id,
  which the binds in the registry use).
- Related: [[cli:nuclearoption refresh-db]], [[cli:relations search]]; game page [[game:nuclear-option]].
:::

<!-- entry: nuclearoption scan -->
:::layer beginner
Shows the joystick binds Nuclear Option has for your connected devices, without changing your profile or the game.
Use it to look before an import; **Look first** in the import wizard runs it.

```powershell
engine nuclearoption scan --text
```
:::

:::layer advanced
- Reads what [[cli:nuclearoption import]] reads: Player0's `ControllerMap` values under
  `HKCU\Software\Shockfront\NuclearOption`. A path (positional or `--path`) may name that key; without one the key
  of [[cli:nuclearoption paths]] is used.
- `--ignore-device <name>` / `--ignore-devices <a;b>` leave the binds of those devices out (a name without `{GUID}`
  means every device of that name); a step line counts them.
- Not read, with a warning each: hats, half-axis binds, maps of two devices of one model, devices that are not
  connected. No keyboard binds.
- Safety: reads the game only, also while it runs. On a fresh install it first builds JoyPro's command list from the
  game and saves it (the only file a scan writes). Refused when the game is not installed or switched off in
  Settings > Games.
:::

:::layer technical
- Output: JSON (`Game`, `Action` `scan`, `ConfigPath`, `BindingsRead`, `Bindings` with `ActionId`, `Device`, `Input`,
  `IsAxis`, `Reformers`, `Inverted`, then `Steps`, `Warnings`, `Errors`), or with `--text` a report with one line
  `<action> <- <device> <input>` per bind.
- Exit code `0`; `1` on an error: `No Nuclear Option config file found. Pass the path explicitly.`, `Nuclear Option has
  no settings yet (HKCU\Software\Shockfront\NuclearOption is missing): start the game once.`, or `The Nuclear Option
  catalog has no Rewired action ids. Run 'engine nuclearoption refresh-db' ...`. Unknown options fail with `Unknown
  NuclearOption scan argument(s): ...`.
- Related: [[cli:nuclearoption import]], [[cli:nuclearoption catalog]]; game page [[game:nuclear-option]].
:::

<!-- entry: nuclearoption refresh-db -->
:::layer beginner
Builds JoyPro's Nuclear Option command list from the installed game: every action of the game's input list with name,
axis or button and category. Run it after a game update; **Update** on the Nuclear Option card of the Start page runs
it.

```powershell
engine nuclearoption refresh-db --dry-run --text
```
:::

:::layer advanced
- Reads the game's action list from `NuclearOption_Data\resources.assets` of the detected installation. A path
  (positional or `--path`) may be the game folder, its data folder or the `.assets` file; **Refresh DB** in the import
  wizard's **Expert import** section passes its **DB Source** box this way.
- The list is rebuilt from the game: new actions come in, and an action the game no longer has drops out with a
  warning (`'<id>' is no longer in the game's action list.`).
- `--dry-run` reads and reports, writes nothing. Otherwise the list is written only when it changed.
- Refused when Nuclear Option is not installed or switched off in Settings > Games; the game may run.
:::

:::layer technical
- Output: JSON of the refresh (`Action` `refresh-db`, `DryRun`, `FilesWritten`, `Steps` such as `Read <n> Rewired
  action(s) from <file>.` and `Actions: <old> -> <new>`, `Warnings`, `Errors`), or a text report with `--text`.
- Exit code `0`; `1` when nothing could be read (`Nuclear Option's Rewired InputManager was not found. Pass --path with
  the game folder, its data folder or the .assets file.`); the list then stays as it was.
- Writes `%LOCALAPPDATA%\JoyPro2\GameData\DB\NuclearOption\actions.tsv` and records the game's Steam build in
  `source-version.txt` next to it for [[cli:db check-updates]].
- Related: [[cli:nuclearoption catalog]], [[cli:tinycombatarena refresh-db]]; game page [[game:nuclear-option]].
:::

<!-- entry: tinycombatarena import -->
:::layer beginner
Reads the joystick maps of your connected devices from Tiny Combat Arena into your JoyPro profile, from every tab of
the game's Control Mapper: axes with their invert, buttons, hat directions and buttons on an axis. Nothing in the game
changes. The GUI runs it with **Import** on the Tiny Combat Arena card of the Start page.

```powershell
engine tinycombatarena import --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" --text
```
:::

:::layer advanced
It reads the game's settings in the registry, `HKCU\Software\TinyCombat\Arena`: one `ControllerMap` value per device
and tab (Flight, Weapons, Camera, View Select, Map UI, System) for the player `Player`.

- Default: adds to the open profile (`--merge` means the same); `--replace` replaces every relation of the profile by
  what the game has.
- Keyboard binds and modifiers are not read; `--include-keyboard` changes nothing. The game's curves in `Controls.cfg`
  are not read.
- Two or more devices of one model cannot be told apart and are left out (warning).
- Safety: reads only, not refused while the game runs; the profile is kept as "Before import from Tiny Combat Arena"
  first.
- `--ignore-device <name>` / `--ignore-devices <a;b>` leave the binds of those devices out (a name without
  `{GUID}` means every device of that name); a step line says how many binds were left out.
:::

:::layer technical
- Output: JSON of the import result or a text report with `--text`; exit code `1` on an error (`No Tiny Combat Arena
  config file found. Pass the path explicitly.`).
- The path may be the registry key itself; without one it reads the key [[cli:tinycombatarena paths]] lists. A fresh
  install builds the command list from `Arena_Data\sharedassets*.assets` first.
- With `--profile` the profile is saved afterwards; a game JoyPro never exported to is watched from now on.
- Related: [[cli:tinycombatarena scan]], [[cli:tinycombatarena export]], [[cli:nuclearoption import]]; game page
  [[game:tiny-combat-arena]].
:::

<!-- entry: tinycombatarena export -->
:::layer beginner
Writes your binds into Tiny Combat Arena's joystick maps, each into the tab of its action (pitch into Flight, a gun into
Weapons, a view into View Select); the rest of each map stays. Close the game first. The GUI's **Export now** on the
Tiny Combat Arena tile runs it.

```powershell
engine tinycombatarena export --mode clean --nuke-untouched-devices --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file"
```
:::

:::layer advanced
It edits the player's `ControllerMap` values under `HKCU\Software\TinyCombat\Arena`. Tiny Combat Arena is a flight-only
game: every assignable action counts as JoyPro's.

| `--mode` | In the maps of your devices |
|---|---|
| `add` | nothing of the game changes; a bind whose input drives another action (or the other pole of an axis) is listed |
| `merge` | an action on an input you give another action loses it; the action keeps its other inputs |
| `merge-overwrite` (engine default) | as `merge`, and every action you bind leaves its other inputs on the connected devices JoyPro can read |
| `clean` (GUI default) | every other assignable action of the written devices is released, on every tab |

- `--nuke-untouched-devices`: the other connected devices (one of a model) lose those actions; a device without saved
  maps gets empty ones (not with `add`).
- No curves, keyboard binds or modifiers are written.
- Never touched: actions the player cannot assign, keyboard and mouse maps, calibration maps, controller assignments,
  `Controls.cfg`, devices that are not connected, two devices of one model, `--ignore-device(s)`, the `--vjoy-tunnel`
  device.
- Safety: refused while `Arena` runs; the player's controller-map values go into the backup store first (undo:
  [[cli:backups list]]); the profile is kept as "Before export to Tiny Combat Arena". `--dry-run` writes nothing.
:::

:::layer technical
- Output: JSON of the export result or a text report with `--text`; exit code `1` on an error (`Tiny Combat Arena is
  running (Arena). Close the game first; it overwrites its config on exit.`, an invalid `--mode`, no key found).
- Without a path it writes the key [[cli:tinycombatarena paths]] lists.
- After a real run: JoyPro's `.joypro-meta.json`, the watcher's fingerprint, the export marks
  ([[cli:export-marks list]]).
- Related: [[cli:tinycombatarena import]], [[cli:nuclearoption export]]; game page [[game:tiny-combat-arena]].
:::

<!-- entry: tinycombatarena paths -->
:::layer beginner
Shows where JoyPro finds Tiny Combat Arena's binds on this PC: the game's registry key `HKCU\Software\TinyCombat\Arena`,
which exists once you started the game. Scan, import and export use it when you give no path; **Detect** in the import
wizard's **Expert import** section runs it.

```powershell
engine tinycombatarena paths --text
```
:::

:::layer advanced
- No options. Listed: your own config location of [[cli:games config set]] first, then the registry key when it
  exists. An empty list means the game has not saved any settings yet.
- Read-only; also works when the game is not installed or switched off.
:::

:::layer technical
- Output: JSON (`Game`, `DisplayName`, `ConfigPaths`, `Warnings`), or with `--text` `Tiny Combat Arena Config Paths`,
  `Paths: <n>` and the locations. Exit code `0`; an unknown option fails with `Unknown TinyCombatArena paths
  argument(s): ...`.
- Related: [[cli:tinycombatarena scan]], [[cli:games paths]]; game page [[game:tiny-combat-arena]].
:::

<!-- entry: tinycombatarena catalog -->
:::layer beginner
Lists the Tiny Combat Arena actions JoyPro knows, with axis or button and category (Flight Control, Weapon Control,
View Control, Camera Control, Map Control, System ...): the actions you can bind for the game.

```powershell
engine tinycombatarena catalog --text
```
:::

:::layer advanced
- No options; read-only, also when the game is not installed.
- Only actions the player can assign in the game are in the list; each axis action also has the button rows
  `<action>.Positive` and `<action>.Negative`.
- A fresh install has no list yet; the first scan, import, export or [[cli:tinycombatarena refresh-db]] builds it.
  Until then the command lists no action and warns `Tiny Combat Arena action catalog not found: <path>`.
:::

:::layer technical
- Output: JSON (`Game`, `Source`, `Actions` with `Id`, `Title`, `IsAxis`, `Category`, `Warnings`), or with `--text`
  one line per action `<id> | axis|button | <category> | <title>`. Exit code `0`.
- File: `%LOCALAPPDATA%\JoyPro2\GameData\DB\TinyCombatArena\actions.tsv` (fifth column: the game's numeric action id).
- Related: [[cli:tinycombatarena refresh-db]], [[cli:nuclearoption catalog]]; game page [[game:tiny-combat-arena]].
:::

<!-- entry: tinycombatarena scan -->
:::layer beginner
Shows the joystick binds Tiny Combat Arena has for your connected devices, on every tab of its Control Mapper, without
changing your profile or the game. **Look first** in the import wizard runs it.

```powershell
engine tinycombatarena scan --text
```
:::

:::layer advanced
- Reads what [[cli:tinycombatarena import]] reads: the player's `ControllerMap` values under
  `HKCU\Software\TinyCombat\Arena` (axes with invert, buttons, the four main hat directions, buttons on an axis). A path
  (positional or `--path`) may name that key.
- `--ignore-device <name>` / `--ignore-devices <a;b>` leave the binds of those devices out (a name without `{GUID}`
  means every device of that name); a step line counts them.
- Not read, with a warning: half-axis binds, maps of two devices of one model, devices that are not connected. No
  keyboard binds, and not the curves of `Controls.cfg`.
- Safety: reads the game only, also while it runs; a fresh install first builds and saves JoyPro's command list.
  Refused when the game is not installed or switched off in Settings > Games.
:::

:::layer technical
- Output: JSON (`Game`, `Action` `scan`, `ConfigPath`, `BindingsRead`, `Bindings`, `Steps`, `Warnings`, `Errors`), or
  a text report with `--text` (`<action> <- <device> <input>` per bind).
- Exit code `0`; `1` on an error (`No Tiny Combat Arena config file found. Pass the path explicitly.`, `Tiny Combat
  Arena has no settings yet (HKCU\Software\TinyCombat\Arena is missing): start the game once.`, a list without action
  ids). Unknown options fail with `Unknown TinyCombatArena scan argument(s): ...`.
- Related: [[cli:tinycombatarena import]], [[cli:tinycombatarena catalog]]; game page [[game:tiny-combat-arena]].
:::

<!-- entry: tinycombatarena refresh-db -->
:::layer beginner
Builds JoyPro's Tiny Combat Arena command list from the installed game's own action list. Run it after a game update;
**Update** on the Tiny Combat Arena card of the Start page runs it.

```powershell
engine tinycombatarena refresh-db --text
```
:::

:::layer advanced
- Reads the action list from the game's data folder `Arena_Data`: the `sharedassets*.assets` files first, then
  `resources.assets` and `globalgamemanagers.assets`; the first that holds the list wins. A path (positional or
  `--path`) may be the game folder, the data folder or one `.assets` file (**DB Source** with **Refresh DB** in the
  import wizard's **Expert import** section).
- Actions the player cannot assign are left out (a step line counts them). The list is rebuilt from the game; an
  action the game no longer has drops out with a warning.
- `--dry-run` writes nothing. Otherwise the list is written only when it changed.
- Refused when Tiny Combat Arena is not installed or switched off in Settings > Games; the game may run.
:::

:::layer technical
- Output: JSON of the refresh (`DryRun`, `FilesWritten`, `Steps` with `Actions: <old> -> <new>`, `Warnings`,
  `Errors`), or a text report with `--text`.
- Exit code `0`; `1` when the list was not found (`Tiny Combat Arena's Rewired InputManager was not found. Pass --path
  with the game folder, its data folder or the .assets file.`); the list stays unchanged.
- Writes `%LOCALAPPDATA%\JoyPro2\GameData\DB\TinyCombatArena\actions.tsv` and records the game's Steam build for
  [[cli:db check-updates]].
- Related: [[cli:tinycombatarena catalog]], [[cli:nuclearoption refresh-db]]; game page [[game:tiny-combat-arena]].
:::

<!-- entry: wardogs import -->
:::layer beginner
Reads the binds of WARDOGS' HOTAS page into your JoyPro profile: pitch, roll, yaw, the collective, the look axes and the
flight actions, each with invert, sensitivity and dead zone. Nothing in the game changes. The GUI runs it with
**Import** on the WARDOGS card of the Start page.

```powershell
engine wardogs import --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" --text
```
:::

:::layer advanced
It reads the section `[/Script/WDJoystick.WDJoystickSettings]` of
`%LOCALAPPDATA%\Wardogs\Saved\Config\WindowsClient\GameUserSettings.ini`.

- Default: adds to the open profile (`--merge` means the same); `--replace` replaces every relation of the profile by
  what the file has.
- Keyboard binds (in the same file and in `EnhancedInputUserSettings.sav`) are not read; `--include-keyboard` changes
  nothing.
- Sensitivity and dead zone come in as the relation's WARDOGS tuning, so your other games keep their own curves.
- Safety: reads only, not refused while the game runs; the profile is kept as "Before import from WARDOGS" first.
- `--ignore-device <name>` / `--ignore-devices <a;b>` leave the binds of those devices out (a name without
  `{GUID}` means every device of that name); a step line says how many binds were left out.
:::

:::layer technical
- Output: JSON of the import result or a text report with `--text`; exit code `1` on an error (`No WARDOGS config file
  found. Pass the path explicitly.`).
- Without a path it reads the file [[cli:wardogs paths]] lists first. The command list is built from your settings file
  plus JoyPro's axes ([[cli:wardogs refresh-db]]).
- With `--profile` the profile is saved afterwards; a game JoyPro never exported to is watched from now on.
- Related: [[cli:wardogs scan]], [[cli:wardogs export]]; game page [[game:wardogs]].
:::

<!-- entry: wardogs export -->
:::layer beginner
Writes your helicopter binds into WARDOGS' settings file and switches **Enable HOTAS** on, so the game uses them. Close
the game first, and open its HOTAS page once before the first export. The GUI's **Export now** on the WARDOGS tile
runs it.

```powershell
engine wardogs export --mode clean --nuke-untouched-devices --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file"
```
:::

:::layer advanced
The game uses one bind per action. Every mode works only on JoyPro's cataloged helicopter actions in the joystick
section of `GameUserSettings.ini`.

| `--mode` | In the file |
|---|---|
| `add` | a bind goes in only where the action's slot is free and no other cataloged action uses the input; a bound slot is never replaced |
| `merge` | your bind takes the action's slot; another cataloged action on the same input loses it; a second line of the action stays |
| `merge-overwrite` (engine default) | as `merge`, and extra lines of the action go, so a moved action fully moves |
| `clean` (GUI default) | the devices you write keep only your binds of the cataloged actions |

- `--nuke-untouched-devices`: connected devices without binds in your profile lose their cataloged binds (not with
  `add`).
- Per axis: invert, sensitivity (0.1 to 2.0) and dead zone (0 to 0.5); without WARDOGS values they are fitted from your
  base curve. **Enable HOTAS** and **Self-Centering Collective** are written as you stored them (only when the file has
  the key); a profile without WARDOGS binds writes only those.
- Without a joystick section in the file the export stops and asks you to open the HOTAS page in the game.
- Never touched: keyboard and mouse binds, the keyboard save file, other sections, actions JoyPro does not catalog,
  unknown fields of a bind line, devices that are not connected, `--ignore-device(s)`, the `--vjoy-tunnel` device.
- Safety: refused while `WardogsClient-Win64-Shipping` or `WardogsClient` runs; backed up first (`.jpbak` and the backup
  store, undo: [[cli:backups list]]); the profile is kept as "Before export to WARDOGS". `--dry-run` writes nothing.
:::

:::layer technical
- Output: JSON of the export result or a text report with `--text`; exit code `1` on an error (`WARDOGS is running
  (...). Close the game first; it overwrites its config on exit.`, an invalid `--mode`, no file or no joystick
  section).
- Without a path it writes the file [[cli:wardogs paths]] lists first.
- After a real run: `GameUserSettings.ini.joypro-meta.json`, the watcher's fingerprint, the export marks
  ([[cli:export-marks list]]).
- Related: [[cli:wardogs import]], [[cli:ffb set]] (`enableHotas`); game page [[game:wardogs]].
:::

<!-- entry: wardogs paths -->
:::layer beginner
Lists where JoyPro finds the settings file of WARDOGS on this PC. The first one is what [[cli:wardogs scan]],
[[cli:wardogs import]] and [[cli:wardogs export]] use when you give no path. **Detect** in the **Other Games** panel
(Start page, **Expert import**) runs it and puts the first one into **File / Folder**.

```powershell
engine wardogs paths --text
```
:::

:::layer advanced
- `%LOCALAPPDATA%\Wardogs\Saved\Config\WindowsClient\GameUserSettings.ini`, listed when the file exists (the game
  writes it).
- A config location you set yourself ([[cli:games config set]], Settings > Games) comes first.
- No options. Reads only; it also works while the game runs or when it is not installed.
:::

:::layer technical
- Output: JSON (`Game`, `DisplayName`, `ConfigPaths`, `Warnings`), or text with `--text` ("WARDOGS Config Paths",
  "Paths: <n>" and one line per location). Exit code `0`, also when nothing was found (empty list).
- `engine wardogs` without an action runs `paths`.
- Related: [[cli:wardogs scan]], [[cli:games paths]], [[cli:games install set]]; game page [[game:wardogs]].
:::

<!-- entry: wardogs catalog -->
:::layer beginner
Lists the WARDOGS actions JoyPro manages (its command list): id, axis or button, category and title. An export writes
only these actions, and the relation editor and the search list them.

```powershell
engine wardogs catalog --text
```
:::

:::layer advanced
- The list is `%LOCALAPPDATA%\JoyPro2\GameData\DB\Wardogs\actions.tsv` (when that folder does not exist, the
  `DB\Wardogs` folder next to the engine).
- It is built from your own `GameUserSettings.ini` by [[cli:wardogs refresh-db]], or by the first scan, import or
  export.
- `catalog` only shows the list; it never builds it. Titles that look like ids are shown readable.
- No options. Reads only; works without the game installed.
:::

:::layer technical
- Output: JSON (`Game`, `Source`, `Actions` with `Id`, `Title`, `IsAxis`, `Category`, `Warnings`), or text with
  `--text` ("WARDOGS Action Catalog", "Source:", "Actions: <n>", then `<id> | axis|button | <category> | <title>`
  per action).
- Without a list: the warning `WARDOGS action catalog not found: <path>` and an empty list, exit code `0`.
- Related: [[cli:wardogs refresh-db]], [[cli:wardogs export]], [[cli:db games]]; game page [[game:wardogs]].
:::

<!-- entry: wardogs scan -->
:::layer beginner
Shows the joystick binds WARDOGS has now, without changing your profile or the game. Use it to look before an import.
**Look first** on the game's card of the Start page and **Scan** in the **Other Games** panel run it.

```powershell
engine wardogs scan --text
```
:::

:::layer advanced
- It reads the section `[/Script/WDJoystick.WDJoystickSettings]`: the six axes (pitch, throttle, roll, yaw, look
  pitch, look yaw) with invert, sensitivity and dead zone, the axes' button halves, and the action binds. A warning
  says when the game's HOTAS option is off.
- Path (positional or `--path`): another `GameUserSettings.ini`.
- `--ignore-device <name>` / `--ignore-devices <a;b>` (separated by `|` or `;`, `--ignore-devices` also by commas) leave the binds of those devices out
  (a name without `{GUID}` means every device of that name); a step line names them and counts the binds.
- Keyboard binds are not read in this game.
- Safety: reads only and is not refused while the game runs. It is refused when the game is not installed (set its
  folder: [[cli:games install set]]) or switched off in Settings > Games.
- The command list is built from your settings file the first time (a step line says so).
:::

:::layer technical
- Output: JSON (`ConfigPath`, `BindingsRead`, `Bindings` with `ActionId`, `Device`, `Input`, `IsAxis`, `Reformers`,
  `Inverted`, then `Steps`, `Warnings`, `Errors`), or text with `--text` ("WARDOGS Scan", "Config:", "Bindings
  Read:" and the list).
- Exit code `0`; `1` when the result has errors, for example `No WARDOGS config file found. Pass the path explicitly.`
- Unknown options fail with `Unknown Wardogs scan argument(s): ...`; MSFS options (`--game-profile`,
  `--include-disconnected`, `--include-sim-defaults`) are refused.
- Binds of the vJoy device JoyPro forwards into (`--vjoy-tunnel`) are left out.
- Related: [[cli:wardogs import]], [[cli:wardogs paths]], [[cli:wardogs catalog]]; game page [[game:wardogs]].
:::

<!-- entry: wardogs refresh-db -->
:::layer beginner
Builds JoyPro's WARDOGS command list from your own WARDOGS settings file, or adds the actions that are new. Run it
after a game update, or when JoyPro says the list is missing. **Refresh DB** in the **Other Games** panel (its **DB
Source** box is the path) and the Start page's notice "Command lists to build or update" run it.

```powershell
engine wardogs refresh-db --dry-run --text
```
:::

:::layer advanced
- It reads `GameUserSettings.ini` (the file [[cli:wardogs paths]] lists, or the file given as the path): every action
  its `ActionBindings` lines name comes in with category Flight. Only actions that have a line in the file can be
  found, so bind them in the game once.
- The six axes and their positive and negative button halves come from JoyPro itself, so they are in a list built from
  nothing.
- Known actions keep their entry.
- `--dry-run` reads everything and writes nothing (step "Dry run: nothing was written."). Without it the list is
  written only when it changed.
- Safety: only JoyPro's list changes, never a game file; it is refused when the game is not installed or switched off
  in Settings > Games. The GUI can cancel it while the game's files are read; nothing is written then.
:::

:::layer technical
- Output: JSON of the refresh (`DryRun`, `FilesWritten`, `Steps` with `Actions: <old> -> <new>`, `Warnings`,
  `Errors`), or text with `--text`.
- Exit code `0`; `1` when nothing could be read: `No WARDOGS actions could be read; the catalog was left unchanged.`
  or the file's read error; the list stays unchanged.
- Writes `%LOCALAPPDATA%\JoyPro2\GameData\DB\Wardogs\actions.tsv` (with a header line that has the date). After a real
  run the game's build is recorded for [[cli:db check-updates]].
- Related: [[cli:wardogs catalog]], [[cli:wardogs scan]], [[cli:db check-updates]]; game page [[game:wardogs]].
:::

<!-- entry: grebels import -->
:::layer beginner
Reads G-Rebels' controls into your JoyPro profile: the axis role of every axis of each channel (pitch, roll, yaw,
thrust, side strafe, up / down) with invert, and the button binds JoyPro knows. Nothing in the game changes. The GUI
runs it with **Import** on the G-Rebels card of the Start page.

```powershell
engine grebels import --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" --text
```
:::

:::layer advanced
It reads `%LOCALAPPDATA%\G_Rebels\Saved\SaveGames\SaveOptions.sav` (devices with their channels, the axis rows) and
`EnhancedInputUserSettings.sav` in the same folder (the button binds).

- Default: adds to the open profile (`--merge` means the same); `--replace` replaces every relation of the profile by
  what the game has.
- No keyboard or modifier binds; `--include-keyboard` changes nothing.
- A channel maps to your device through the device's HID path in the save file.
- Safety: reads only, not refused while the game runs; the profile is kept as "Before import from G-Rebels" first.
- `--ignore-device <name>` / `--ignore-devices <a;b>` leave the binds of those devices out (a name without
  `{GUID}` means every device of that name); a step line says how many binds were left out.
:::

:::layer technical
- Output: JSON of the import result or a text report with `--text`; exit code `1` on an error (`No G-Rebels config
  file found. Pass the path explicitly.`).
- Without a path it reads the file [[cli:grebels paths]] lists first; the command list is JoyPro's own table of the
  game's roles and buttons.
- With `--profile` the profile is saved afterwards; a game JoyPro never exported to is watched from now on.
- Related: [[cli:grebels scan]], [[cli:grebels export]]; game page [[game:g-rebels]].
:::

<!-- entry: grebels export -->
:::layer beginner
Sets the axis roles and button binds on the channels of your devices in G-Rebels. It never adds a channel or a device:
switch each device on once in the game. Close the game first. The GUI's **Export now** on the G-Rebels tile runs it.

```powershell
engine grebels export --mode clean --nuke-untouched-devices --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file"
```
:::

:::layer advanced
One role per axis and one key per action and channel; the same rules hold for roles and buttons.

| `--mode` | In the save files |
|---|---|
| `add` | a role goes only onto an axis without one; an axis with another role gets a mark "Not written to the game" |
| `merge` | the profile's role goes onto its axis (the role there goes); the role stays on its other axes; a button replaces another action's mapping on the key |
| `merge-overwrite` (engine default) | as `merge`, and the role's other axes on connected channels get **NONE** |
| `clean` (GUI default) | every other role of a written channel becomes **NONE** |

- `--nuke-untouched-devices`: the same on the enabled, connected channels JoyPro does not write (not with `add`).
- Invert is written with the role; hats, dead zone and sensitivity are not written. Axis rows the game has not saved
  yet are not added: change one role on the device's JST tab in the game first.
- A device without a channel is skipped ("switch it on in OPTIONS > INPUT > DEVICES").
- Never touched: keyboard, gamepad, mouse, the game's other options, channels of devices that are not connected, the
  game's device list, `--ignore-device(s)`, the `--vjoy-tunnel` device.
- Safety: refused while `G_Rebels-Win64-Shipping` or `G_Rebels` runs; both save files backed up first (undo:
  [[cli:backups list]]); the profile is kept as "Before export to G-Rebels". `--dry-run` writes nothing.
:::

:::layer technical
- Output: JSON of the export result or a text report with `--text`; exit code `1` on an error (the game runs, an
  invalid `--mode`, no file found).
- Without a path it writes the file [[cli:grebels paths]] lists first.
- After a real run: `SaveOptions.sav.joypro-meta.json`, the watcher's fingerprint, the export marks
  ([[cli:export-marks list]]).
- Related: [[cli:grebels import]], [[cli:vjoy mode]] (one virtual device needs one channel); game page
  [[game:g-rebels]].
:::

<!-- entry: grebels paths -->
:::layer beginner
Shows where JoyPro finds G-Rebels' controls on this PC: `%LOCALAPPDATA%\G_Rebels\Saved\SaveGames\SaveOptions.sav`.
**Detect** in the import wizard's **Expert import** section runs it.

```powershell
engine grebels paths --text
```
:::

:::layer advanced
- No options. Listed: your own config location of [[cli:games config set]] first, then `SaveOptions.sav` when it
  exists. The button binds are read from `EnhancedInputUserSettings.sav` in the same folder; that file is not listed.
- Read-only; also works when the game is not installed or switched off.
:::

:::layer technical
- Output: JSON (`Game`, `DisplayName`, `ConfigPaths`, `Warnings`), or with `--text` `G-Rebels Config Paths`,
  `Paths: <n>` and the files. Exit code `0`; an unknown option fails with `Unknown GRebels paths argument(s): ...`.
- Related: [[cli:grebels scan]], [[cli:games paths]]; game page [[game:g-rebels]].
:::

<!-- entry: grebels catalog -->
:::layer beginner
Lists the G-Rebels actions JoyPro knows: the six axis roles of a joystick tab (Yaw, Thrust, Roll, Pitch, Side strafe,
Up / down) and the button actions JoyPro knows (Primary Fire).

```powershell
engine grebels catalog --text
```
:::

:::layer advanced
- No options; read-only, also when the game is not installed.
- The list is JoyPro's own table, not read from the game. It is saved on the first scan, import, export or
  [[cli:grebels refresh-db]]; before that the command lists no action and warns `G-Rebels action catalog not found:
  <path>`.
:::

:::layer technical
- Output: JSON (`Game`, `Source`, `Actions` with `Id`, `Title`, `IsAxis`, `Category`, `Warnings`), or with `--text`
  one line per action `<id> | axis|button | <category> | <title>` (categories `Axis roles` and `Buttons`). Exit code
  `0`.
- File: `%LOCALAPPDATA%\JoyPro2\GameData\DB\GRebels\actions.tsv` (fifth column: the role number, `role:0` to `role:5`,
  or the game's mapping name of a button).
- Related: [[cli:grebels refresh-db]]; game page [[game:g-rebels]].
:::

<!-- entry: grebels scan -->
:::layer beginner
Shows the axis roles and button binds G-Rebels has for your connected devices, without changing your profile or the
game. **Look first** in the import wizard runs it.

```powershell
engine grebels scan --text
```
:::

:::layer advanced
- Reads what [[cli:grebels import]] reads: the devices switched on in the game with their channels and the axis rows
  (role, invert) of `SaveOptions.sav`, and the button binds `<ACTION>JST<channel>` of `EnhancedInputUserSettings.sav`
  in the same folder. A path (positional or `--path`) names another `SaveOptions.sav`.
- A channel belongs to your device through the device's HID path; a channel whose device is not connected is skipped
  with a warning.
- `--ignore-device <name>` / `--ignore-devices <a;b>` leave the binds of those devices out (a name without `{GUID}`
  means every device of that name).
- Safety: reads only, also while the game runs; a fresh install first saves JoyPro's table of the game. Refused when
  the game is not installed or switched off in Settings > Games.
:::

:::layer technical
- Output: JSON (`Game`, `Action` `scan`, `ConfigPath`, `BindingsRead`, `Bindings`, `Steps`, `Warnings`, `Errors`), or
  a text report with `--text`.
- Exit code `0`; `1` on an error (`No G-Rebels config file found. Pass the path explicitly.`, an unreadable save
  file). Unknown options fail with `Unknown GRebels scan argument(s): ...`.
- Related: [[cli:grebels import]], [[cli:grebels catalog]]; game page [[game:g-rebels]].
:::

<!-- entry: grebels refresh-db -->
:::layer beginner
Writes JoyPro's own G-Rebels table (the six axis roles and the known button actions) as the game's command list. The
game's files are not read. **Update** on the G-Rebels card of the Start page runs it.

```powershell
engine grebels refresh-db --text
```
:::

:::layer advanced
- Rows the list already has stay as they are; only rows of JoyPro's table that are missing are added (a step line
  counts them: `JoyPro's own G-Rebels table: <n> new action(s); the game's input assets are not read.`).
- A path (positional or `--path`) is accepted and not used.
- `--dry-run` writes nothing. Otherwise the list is written only when it changed.
- Works also when G-Rebels is not installed or switched off in Settings > Games, since nothing of the game is read.
:::

:::layer technical
- Output: JSON of the refresh (`DryRun`, `FilesWritten`, `Steps` with `Actions: <old> -> <new>`, `Warnings`,
  `Errors`), or a text report with `--text`; `The catalog already has every action the source lists; nothing was
  written.` when nothing changed. Exit code `0`.
- Writes `%LOCALAPPDATA%\JoyPro2\GameData\DB\GRebels\actions.tsv` and records the game's Steam build for
  [[cli:db check-updates]].
- Related: [[cli:grebels catalog]], [[cli:grebels export]]; game page [[game:g-rebels]].
:::

<!-- entry: ac7 import -->
:::layer beginner
Reads the flight-stick binds of ACE COMBAT 7: Skies Unknown from its `Input.ini` into your JoyPro profile. Nothing in
the game changes. Start the game once with every device connected first, so it writes a section per device. The GUI
runs it with **Import** on the ACE COMBAT 7 card of the Start page.

```powershell
engine ac7 import --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" --text
```
:::

:::layer advanced
It reads `%LOCALAPPDATA%\BANDAI NAMCO Entertainment\ACE COMBAT 7\Config\Input.ini`: one `[Joystick-<product GUID>]`
section per device model with its `Flight_*` keys.

- Default: adds to the open profile (`--merge` means the same); `--replace` replaces every relation of the profile by
  what the file has.
- No keyboard or modifier binds; `--include-keyboard` changes nothing. Invert (`:R`) comes in, no curves.
- Two sticks of the same model share one section.
- Safety: reads only, not refused while the game runs; the profile is kept as "Before import from ACE COMBAT 7: Skies
  Unknown" first.
- `--ignore-device <name>` / `--ignore-devices <a;b>` leave the binds of those devices out (a name without
  `{GUID}` means every device of that name); a step line says how many binds were left out.
:::

:::layer technical
- Output: JSON of the import result or a text report with `--text`; exit code `1` on an error (`No ACE COMBAT 7: Skies
  Unknown config file found. Pass the path explicitly.`).
- Without a path it reads the file [[cli:ac7 paths]] lists first. Before the file exists JoyPro lists its own 20 flight
  actions; [[cli:ac7 refresh-db]] builds the list from the file.
- With `--profile` the profile is saved afterwards; a game JoyPro never exported to is watched from now on.
- Related: [[cli:ac7 scan]], [[cli:ac7 export]], [[cli:ac8 import]]; game page [[game:ace-combat]].
:::

<!-- entry: ac7 export -->
:::layer beginner
Writes your binds into ACE COMBAT 7's `Input.ini`, one section per device model. Close the game first. The GUI's
**Export now** on the ACE COMBAT 7 tile runs it.

```powershell
engine ac7 export --mode merge-overwrite --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" --text
```
:::

:::layer advanced
One input per action and device. ACE COMBAT 7 counts as a flight-only game.

| `--mode` | In `Input.ini` |
|---|---|
| `add` | an action that already has a value on that device keeps it; a bind on an input the game uses for another action gets a mark "Not written to the game" |
| `merge` | the profile's value is written; another action on the same input is released; the action's values on other devices stay |
| `merge-overwrite` (engine default) | as `merge`, and every action the profile binds is cleared on the other connected devices |
| `clean` (GUI default) | the sections JoyPro writes keep only your readable values; a written axis is still cleared on the other connected devices |

- `--nuke-untouched-devices`: the readable values of the other connected devices are cleared (not with `add`).
- The game averages one axis action over all devices: `merge` reports such an axis; `merge-overwrite` clears it on the
  others.
- A device without a section is skipped ("start the game once with this device connected").
- Never touched: the keyboard, menu keys (`UI_*`), combinations JoyPro does not read, sections of devices that are not
  connected, `--ignore-device(s)`, the `--vjoy-tunnel` device; comments, order, encoding and line ends.
- Safety: refused while `Ace7Game` or `Ace7Game-Win64-Shipping` runs; backed up first (undo: [[cli:backups list]]);
  the profile is kept as "Before export to ACE COMBAT 7: Skies Unknown". `--dry-run` writes nothing.
:::

:::layer technical
- Output: JSON of the export result or a text report with `--text`; exit code `1` on an error (the game runs, an
  invalid `--mode`, no file found).
- Without a path it writes the file [[cli:ac7 paths]] lists first.
- After a real run: `Input.ini.joypro-meta.json`, the watcher's fingerprint, the export marks
  ([[cli:export-marks list]]).
- Related: [[cli:ac7 import]]; game page [[game:ace-combat]].
:::

<!-- entry: ac7 paths -->
:::layer beginner
Shows where JoyPro finds ACE COMBAT 7's binds on this PC:
`%LOCALAPPDATA%\BANDAI NAMCO Entertainment\ACE COMBAT 7\Config\Input.ini`, which the game writes when it closes with
your devices connected. **Detect** in the import wizard's **Expert import** section runs it.

```powershell
engine ac7 paths --text
```
:::

:::layer advanced
- No options. Listed: your own config location of [[cli:games config set]] first, then `Input.ini` when it exists.
- Read-only; also works when the game is not installed or switched off.
:::

:::layer technical
- Output: JSON (`Game`, `DisplayName`, `ConfigPaths`, `Warnings`), or with `--text` `ACE COMBAT 7: Skies Unknown
  Config Paths`, `Paths: <n>` and the file. Exit code `0`; an unknown option fails with `Unknown AceCombat7 paths
  argument(s): ...`.
- Related: [[cli:ac7 scan]], [[cli:games paths]]; game page [[game:ace-combat]].
:::

<!-- entry: ac7 catalog -->
:::layer beginner
Lists the ACE COMBAT 7 flight actions JoyPro knows (the `Flight_*` keys of `Input.ini`), with axis or button and
category.

```powershell
engine ac7 catalog --text
```
:::

:::layer advanced
- No options; read-only, also when the game is not installed.
- Categories: `Flight axes`, `Flight` and `Hat`. Before a list was built from your `Input.ini`, JoyPro lists its own
  20 flight actions; [[cli:ac7 refresh-db]], or the first scan, import or export, builds the list from the file.
:::

:::layer technical
- Output: JSON (`Game`, `Source`, `Actions` with `Id`, `Title`, `IsAxis`, `Category`, `Warnings`), or with `--text`
  one line per action `<id> | axis|button | <category> | <title>`. Exit code `0`.
- File: `%LOCALAPPDATA%\JoyPro2\GameData\DB\AceCombat7\actions.tsv`.
- Related: [[cli:ac7 refresh-db]], [[cli:ac8 catalog]]; game page [[game:ace-combat]].
:::

<!-- entry: ac7 scan -->
:::layer beginner
Shows the flight-stick binds ACE COMBAT 7 has in its `Input.ini` for your connected devices, without changing your
profile or the game. **Look first** in the import wizard runs it.

```powershell
engine ac7 scan --text
```
:::

:::layer advanced
- Reads the `[Joystick-<product GUID>]` sections of the file [[cli:ac7 paths]] lists, or of the file given (positional
  or `--path`): axes with invert (`:R`), buttons and hat directions.
- A section of a model that is not connected is skipped with a warning; two devices of one model share a section and
  are read for the first. Combinations and half axes (`:P`, `:N`, `:C`) are not read (warning each).
- `--ignore-device <name>` / `--ignore-devices <a;b>` leave the binds of those devices out.
- Safety: reads only, also while the game runs; a fresh install first builds and saves the command list. Refused when
  the game is not installed or switched off in Settings > Games.
:::

:::layer technical
- Output: JSON (`Game`, `Action` `scan`, `ConfigPath`, `BindingsRead`, `Bindings`, `Steps`, `Warnings`, `Errors`), or
  a text report with `--text`.
- Exit code `0`; `1` on an error (`No ACE COMBAT 7: Skies Unknown config file found. Pass the path explicitly.`).
  Unknown options fail with `Unknown AceCombat7 scan argument(s): ...`.
- Related: [[cli:ac7 import]], [[cli:ac7 catalog]]; game page [[game:ace-combat]].
:::

<!-- entry: ac7 refresh-db -->
:::layer beginner
Builds JoyPro's ACE COMBAT 7 command list from your `Input.ini`: every `Flight_*` key of the device sections. Start the
game once with your devices connected first. **Update** on the ACE COMBAT 7 card of the Start page runs it.

```powershell
engine ac7 refresh-db --text
```
:::

:::layer advanced
- Reads the file [[cli:ac7 paths]] lists; a path (positional or `--path`) may name another `Input.ini`, a folder means
  the default file.
- The list is rebuilt from the file. `--dry-run` writes nothing; otherwise the list is written only when it changed.
- Refused when the game is not installed or switched off in Settings > Games; the game may run.
:::

:::layer technical
- Output: JSON of the refresh (`DryRun`, `FilesWritten`, `Steps` such as `Read <n> flight action(s) from <file>.` and
  `Actions: <old> -> <new>`, `Warnings`, `Errors`), or a text report with `--text`.
- Exit code `0`; `1` when nothing could be read: `Input.ini not found (<path>): start ACE COMBAT 7 once with your
  devices connected.` or `<path> has no device section with Flight_ binds yet: ...`; the list stays unchanged.
- Writes `%LOCALAPPDATA%\JoyPro2\GameData\DB\AceCombat7\actions.tsv`.
- Related: [[cli:ac7 catalog]], [[cli:ac8 refresh-db]]; game page [[game:ace-combat]].
:::

<!-- entry: ac8 import -->
:::layer beginner
ACE COMBAT 8: Wings of Theve stores no joystick binds, so this import brings no relations; it only reports that. Your
HOTAS reaches the game through JoyPro's virtual Xbox pad instead. The GUI's **Import** on the ACE COMBAT 8 card runs
it.

```powershell
engine ac8 import --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" --text
```
:::

:::layer advanced
- Nothing is read into the profile: the step line says the game "stores no joystick binds" and names
  [[cli:vjoy mode]] `AceCombat8 xbox`.
- The pad keys you set in the game (`%LOCALAPPDATA%\BANDAI NAMCO Entertainment\ACE COMBAT 8\Saved\SaveGames\System.sav`,
  read only) go into the command list with [[cli:ac8 refresh-db]], not into the profile.
- `--replace` starts over from an empty import: every relation of the open profile would be replaced by nothing. Keep
  the default (add).
- Safety: reads nothing of the game; the profile is still kept as "Before import from ACE COMBAT 8: Wings of Theve"
  first.
- `--ignore-device <name>` / `--ignore-devices <a;b>` leave the binds of those devices out (a name without
  `{GUID}` means every device of that name); a step line says how many binds were left out.
:::

:::layer technical
- Output: JSON of the import result (`BindingsRead` 0) or a text report with `--text`; exit code `0`, `1` when no
  config file is found.
- Refused when the game is not installed; unknown options fail with `Unknown AceCombat8 import argument(s): ...`.
- Related: [[cli:ac8 export]], [[cli:vjoy mode]], [[cli:ac7 import]]; game page [[game:ace-combat]].
:::

<!-- entry: ac8 export -->
:::layer beginner
Writes nothing into ACE COMBAT 8: Wings of Theve and says so: your binds reach the game through the virtual Xbox pad,
which **Start** on the Virtual Joystick page builds from your relations. The GUI's **Export now** on the ACE COMBAT 8
tile runs it.

```powershell
engine ac8 export --dry-run --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" --text
```
:::

:::layer advanced
- The step line counts your gamepad action binds and names [[cli:vjoy mode]] `AceCombat8 xbox`; after you change a pad
  key in the game, run [[cli:ac8 refresh-db]] and [[cli:vjoy map]].
- A bind on an action that is not one of the catalog's gamepad actions is skipped with a warning.
- `--mode`, `--nuke-untouched-devices` and `--include-keyboard` have no effect: no game file is written.
- The game reads no joystick: outside the virtual mode, binds on a joystick get the warning "... reads no joystick
  ..." with the command that switches the game to the virtual pad.
- Safety: the export is still refused while `AceCombat8`, `AceCombat8-Win64-Shipping` or `start_protected_game` runs
  (not with `--dry-run`), and the profile is kept as "Before export to ACE COMBAT 8: Wings of Theve".
:::

:::layer technical
- Output: JSON of the export result (`BindingsWritten` 0, `BindingsSkipped`) or a text report with `--text`; exit code
  `0`, `1` when the game runs or the mode is invalid.
- The help note says "writes nothing": the game reads only Xbox pads and checksums its settings, so nothing is
  released or written.
- Related: [[cli:ac8 import]], [[cli:vjoy mode]], [[cli:vjoy status]]; game page [[game:ace-combat]].
:::

<!-- entry: ac8 paths -->
:::layer beginner
Shows where JoyPro finds ACE COMBAT 8's settings on this PC:
`%LOCALAPPDATA%\BANDAI NAMCO Entertainment\ACE COMBAT 8\Saved\SaveGames\System.sav`. JoyPro reads only the gamepad
keys you set from it. **Detect** in the import wizard's **Expert import** section runs it.

```powershell
engine ac8 paths --text
```
:::

:::layer advanced
- No options. Listed: your own config location of [[cli:games config set]] first, then `System.sav` when it exists.
- Read-only; also works when the game is not installed or switched off.
:::

:::layer technical
- Output: JSON (`Game`, `DisplayName`, `ConfigPaths`, `Warnings`), or with `--text` `ACE COMBAT 8: Wings of Theve
  Config Paths`, `Paths: <n>` and the file. Exit code `0`; an unknown option fails with `Unknown AceCombat8 paths
  argument(s): ...`.
- Related: [[cli:ac8 refresh-db]], [[cli:games paths]]; game page [[game:ace-combat]].
:::

<!-- entry: ac8 catalog -->
:::layer beginner
Lists the ACE COMBAT 8 actions JoyPro knows: the controls of the Xbox pad the game reads, each with the pad control it
is on. Bind your HOTAS to them; the virtual Xbox pad passes them on ([[cli:vjoy mode]] `AceCombat8 xbox`).

```powershell
engine ac8 catalog --text
```
:::

:::layer advanced
- No options; read-only, also when the game is not installed.
- Fixed in the game: pitch and roll (left stick) and the camera (right stick). Set in the game: yaw left and right,
  throttle up and down, gun, missile, change weapon, switch target and radar map.
- Before [[cli:ac8 refresh-db]] read your pad keys, the list shows the game's default layout.
:::

:::layer technical
- Output: JSON (`Game`, `Source`, `Actions` with `Id`, `Title`, `IsAxis`, `Category`, `Warnings`), or with `--text`
  one line per action `<id> | axis|button | Gamepad | <title>` (ids `Pad_...`). Exit code `0`.
- File: `%LOCALAPPDATA%\JoyPro2\GameData\DB\AceCombat8\actions.tsv`; the fifth column holds the pad control
  (`Gamepad_LeftY`, `Gamepad_FaceButton_Bottom` ...), which the virtual pad uses.
- Related: [[cli:ac8 refresh-db]], [[cli:vjoy map]]; game page [[game:ace-combat]].
:::

<!-- entry: ac8 scan -->
:::layer beginner
ACE COMBAT 8: Wings of Theve stores no joystick binds, so a scan shows none; it only reports that. Your HOTAS reaches
the game through the virtual Xbox pad. **Look first** in the import wizard runs it.

```powershell
engine ac8 scan --text
```
:::

:::layer advanced
- The step line says the game "stores no joystick binds" and names [[cli:vjoy mode]] `AceCombat8 xbox`. The pad keys
  you set in the game go into the command list with [[cli:ac8 refresh-db]].
- A path (positional or `--path`) and `--ignore-device(s)` are accepted and change nothing.
- Refused when the game is not installed or switched off in Settings > Games.
:::

:::layer technical
- Output: JSON (`BindingsRead` 0, `Steps`, `Warnings`, `Errors`), or a text report with `--text`.
- Exit code `0`; `1` when `System.sav` is not found (`No ACE COMBAT 8: Wings of Theve config file found. Pass the path
  explicitly.`) or for an unknown option (`Unknown AceCombat8 scan argument(s): ...`).
- Related: [[cli:ac8 import]], [[cli:vjoy status]]; game page [[game:ace-combat]].
:::

<!-- entry: ac8 refresh-db -->
:::layer beginner
Reads the gamepad keys you set in ACE COMBAT 8 into JoyPro's command list, so the virtual Xbox pad puts each HOTAS
input on the control the game uses. Run it after you change a key in the game, then [[cli:vjoy map]]. **Update** on the
ACE COMBAT 8 card of the Start page runs it.

```powershell
engine ac8 refresh-db --text
```
:::

:::layer advanced
- Reads the `<action>Key_Controller` entries (and the control type) of `System.sav`; a path (positional or `--path`)
  may name another `System.sav`, a folder means the default file.
- A key you did not set keeps the default; a pad key JoyPro has no row for gets a row of its own. Without a readable
  save the default layout is used (a step line says so).
- The game file is only read, never written. `--dry-run` writes nothing; otherwise the list is written only when it
  changed.
- Refused when the game is not installed or switched off in Settings > Games; the game may run.
:::

:::layer technical
- Output: JSON of the refresh (`DryRun`, `FilesWritten`, `Steps` such as `Read <n> gamepad key(s) from <file> (control
  type ...).` and `Actions: <old> -> <new>`, `Warnings`), or a text report with `--text`.
- Exit code `0`; `1` only for a refusal or an unknown option.
- Writes `%LOCALAPPDATA%\JoyPro2\GameData\DB\AceCombat8\actions.tsv`.
- Related: [[cli:ac8 catalog]], [[cli:vjoy map]], [[cli:ac7 refresh-db]]; game page [[game:ace-combat]].
:::

<!-- entry: pw import -->
:::layer beginner
Reads Project Wingman's joystick binds of the flight actions into your JoyPro profile, with invert, curve and dead
zone. Nothing in the game changes. The GUI runs it with **Import** on the Project Wingman card of the Start page.

```powershell
engine pw import --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" --text
```
:::

:::layer advanced
It reads `%LOCALAPPDATA%\ProjectWingman\Saved\Config\WindowsNoEditor\Input.ini` (`ActionMappings`, `AxisMappings`)
and the curves and dead zones per axis action from `...\Saved\SaveGames\savegame.sav`.

- Default: adds to the open profile (`--merge` means the same); `--replace` replaces every relation of the profile by
  what the game has.
- No keyboard or modifier binds; `--include-keyboard` changes nothing.
- The game numbers a device by its place in its device list: JoyPro maps it with the devices connected now and warns
  when a device's number moved.
- Safety: reads only, not refused while the game runs; the profile is kept as "Before import from Project Wingman"
  first.
- `--ignore-device <name>` / `--ignore-devices <a;b>` leave the binds of those devices out (a name without
  `{GUID}` means every device of that name); a step line says how many binds were left out.
:::

:::layer technical
- Output: JSON of the import result or a text report with `--text`; exit code `1` on an error (`No Project Wingman
  config file found. Pass the path explicitly.`).
- Without a path it reads the file [[cli:pw paths]] lists first; a fresh install builds the command list from the
  game's `pakchunk0-WindowsNoEditor.pak` first.
- With `--profile` the profile is saved afterwards; a game JoyPro never exported to is watched from now on.
- Related: [[cli:pw scan]], [[cli:pw export]]; game page [[game:project-wingman]].
:::

<!-- entry: pw export -->
:::layer beginner
Writes your binds into Project Wingman's `Input.ini` and the curves and dead zones into its save game. Close the game
first, and export again after you add or remove a controller. The GUI's **Export now** on the Project Wingman tile
runs it.

```powershell
engine pw export --mode clean --nuke-untouched-devices --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file"
```
:::

:::layer advanced
Only joystick mappings of cataloged flight actions are removed. Project Wingman counts as a flight-only game.

| `--mode` | In `Input.ini` |
|---|---|
| `add` | a bind on an input the game uses for another flight action gets a mark "Not written to the game" |
| `merge` | also removes the game's mapping on an input the profile gives another action; the action's other mappings stay |
| `merge-overwrite` (engine default) | also removes the other joystick mappings of every action the profile binds |
| `clean` (GUI default) | the exported devices lose their other joystick mappings |

- `--nuke-untouched-devices`: the same on the other connected devices (not with `add`).
- A written list replaces the game's defaults, so JoyPro writes the defaults of a missing list first. Curve (0.00 to
  2.00) and dead zone (0 to 100 %) per axis action go into `savegame.sav`, for all devices together.
- A hat bind is refused: the game binds no hats (set **Hat Function** in its controls).
- Never touched: keyboard, mouse and pad mappings, menu rows, devices that are not connected, `--ignore-device(s)`, the
  `--vjoy-tunnel` device, every other line of `Input.ini` and byte of `savegame.sav`.
- Safety: refused while `ProjectWingman-Win64-Shipping` or `ProjectWingman` runs; backed up first (undo:
  [[cli:backups list]]); the profile is kept as "Before export to Project Wingman". `--dry-run` writes nothing.
:::

:::layer technical
- Output: JSON of the export result or a text report with `--text`; exit code `1` on an error (the game runs, an
  invalid `--mode`, no file found).
- Without a path it writes the file [[cli:pw paths]] lists first. Device numbers come from the devices connected at the
  export.
- After a real run: `Input.ini.joypro-meta.json`, the watcher's fingerprint, the export marks
  ([[cli:export-marks list]]).
- Related: [[cli:pw import]]; game page [[game:project-wingman]].
:::

<!-- entry: pw paths -->
:::layer beginner
Shows where JoyPro finds Project Wingman's binds on this PC:
`%LOCALAPPDATA%\ProjectWingman\Saved\Config\WindowsNoEditor\Input.ini`. **Detect** in the import wizard's **Expert
import** section runs it.

```powershell
engine pw paths --text
```
:::

:::layer advanced
- No options. Listed: your own config location of [[cli:games config set]] first, then `Input.ini`. The file is listed
  once `%LOCALAPPDATA%\ProjectWingman` exists, also before the game wrote it (the game writes it at the first change in
  its controls menu); an export then starts from the game's defaults.
- The curves and dead zones are read from `...\Saved\SaveGames\savegame.sav`; that file is not listed.
- Read-only; also works when the game is not installed or switched off.
:::

:::layer technical
- Output: JSON (`Game`, `DisplayName`, `ConfigPaths`, `Warnings`), or with `--text` `Project Wingman Config Paths`,
  `Paths: <n>` and the file. Exit code `0`; an unknown option fails with `Unknown ProjectWingman paths argument(s):
  ...`.
- Related: [[cli:pw scan]], [[cli:games paths]]; game page [[game:project-wingman]].
:::

<!-- entry: pw catalog -->
:::layer beginner
Lists the Project Wingman actions JoyPro knows: the game's flight, weapon and view actions, with axis or button.

```powershell
engine pw catalog --text
```
:::

:::layer advanced
- No options; read-only, also when the game is not installed.
- Categories: `Flight axes`, `Weapons`, `View` and `Misc`; menu rows and the mouse axes are not in the list.
- A fresh install has no list yet; the first scan, import, export or [[cli:pw refresh-db]] builds it. Until then the
  command lists no action and warns `Project Wingman action catalog not found: <path>`.
:::

:::layer technical
- Output: JSON (`Game`, `Source`, `Actions` with `Id`, `Title`, `IsAxis`, `Category`, `Warnings`), or with `--text`
  one line per action `<id> | axis|button | <category> | <title>`. The ids are the game's action names (`Gun Fire`,
  `Pitch Axis` ...). Exit code `0`.
- File: `%LOCALAPPDATA%\JoyPro2\GameData\DB\ProjectWingman\actions.tsv`.
- Related: [[cli:pw refresh-db]]; game page [[game:project-wingman]].
:::

<!-- entry: pw scan -->
:::layer beginner
Shows the joystick binds Project Wingman has for your connected devices, with invert, without changing your profile or
the game. **Look first** in the import wizard runs it.

```powershell
engine pw scan --text
```
:::

:::layer advanced
- Reads the `ActionMappings` and `AxisMappings` lines with a `Joystick_...` key of the file [[cli:pw paths]] lists, or
  of the file given (positional or `--path`).
- The game numbers a device by its place in its device list: JoyPro maps the number to the devices connected now and
  warns when a device's number moved. Keyboard, mouse and pad mappings are not shown.
- The curve and dead zone of each axis action are read from `savegame.sav`; the scan output does not list them.
- `--ignore-device <name>` / `--ignore-devices <a;b>` leave the binds of those devices out.
- Safety: reads only, also while the game runs; a fresh install first builds and saves the command list. Refused when
  the game is not installed or switched off in Settings > Games.
:::

:::layer technical
- Output: JSON (`Game`, `Action` `scan`, `ConfigPath`, `BindingsRead`, `Bindings`, `Steps`, `Warnings`, `Errors`), or
  a text report with `--text`.
- Exit code `0`, also when `Input.ini` does not exist yet (warning `Input.ini does not exist yet (<path>): the game has
  its default binds, none on a joystick.`); `1` on an error (no config location, an unreadable file). Unknown options
  fail with `Unknown ProjectWingman scan argument(s): ...`.
- Related: [[cli:pw import]], [[cli:pw catalog]]; game page [[game:project-wingman]].
:::

<!-- entry: pw refresh-db -->
:::layer beginner
Builds JoyPro's Project Wingman command list from the game's default controls and your `Input.ini`. **Update** on the
Project Wingman card of the Start page runs it.

```powershell
engine pw refresh-db --text
```
:::

:::layer advanced
- Reads the default input file inside `ProjectWingman\Content\Paks\pakchunk0-WindowsNoEditor.pak` of the detected
  installation, then the actions of your `Input.ini`. A path (positional or `--path`) may be the game folder, the
  `.pak` file or another `Input.ini`.
- Menu rows and the mouse axes are left out. The list is rebuilt from these sources.
- `--dry-run` writes nothing; otherwise the list is written only when it changed.
- Refused when the game is not installed or switched off in Settings > Games; the game may run.
:::

:::layer technical
- Output: JSON of the refresh (`DryRun`, `FilesWritten`, `Steps` such as `Read <n> flight action(s) from <pak> and
  <ini>.` and `Actions: <old> -> <new>`, `Warnings`, `Errors`), or a text report with `--text`.
- Exit code `0`; `1` when nothing could be read (`Neither the game's pak nor an Input.ini was found: install Project
  Wingman (or pass its folder) and start it once.`); the list stays unchanged.
- Writes `%LOCALAPPDATA%\JoyPro2\GameData\DB\ProjectWingman\actions.tsv`.
- Related: [[cli:pw catalog]], [[cli:pw export]]; game page [[game:project-wingman]].
:::

<!-- entry: hllv import -->
:::layer beginner
Reads the keyboard pilot keys of Hell Let Loose: Vietnam into your JoyPro profile, when you ask for the keyboard. The
game stores no joystick binds, so without `--include-keyboard` nothing comes in. Nothing in the game changes. The GUI
runs it with **Import** on the Hell Let Loose: Vietnam card of the Start page.

```powershell
engine hllv import --include-keyboard --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" --text
```
:::

:::layer advanced
It reads the keys you changed in `%LOCALAPPDATA%\HLLVietnam\Saved\SaveGames\EnhancedInputUserSettings.sav`; the pad
layout (`SelectedGamepadLayout` in `GameUserSettings.ini`) is read for the virtual pad, not imported.

- Default: adds to the open profile (`--merge` means the same). `--replace` replaces every relation of the profile by
  what comes in, which is nothing without `--include-keyboard`.
- The helicopter on the pad needs no import: bind your HOTAS to the pad actions and use [[cli:vjoy mode]]
  `HLLVietnam xbox`.
- Safety: reads only, not refused while the game runs; the profile is kept as "Before import from Hell Let Loose:
  Vietnam" first.
- `--ignore-device <name>` / `--ignore-devices <a;b>` leave the binds of those devices out (a name without
  `{GUID}` means every device of that name); a step line says how many binds were left out.
:::

:::layer technical
- Output: JSON of the import result or a text report with `--text` (a step counts the keyboard binds left out); exit
  code `1` on an error (`No Hell Let Loose: Vietnam config file found. Pass the path explicitly.`).
- Without a path it reads the file [[cli:hllv paths]] lists first; the command list is JoyPro's table of the three pad
  layouts and the pilot keys ([[cli:hllv refresh-db]], [[cli:hllv oodle]]).
- With `--profile` the profile is saved afterwards; a game JoyPro never exported to is watched from now on.
- Related: [[cli:hllv scan]], [[cli:hllv export]], [[cli:vjoy mode]]; game page [[game:hll-vietnam]].
:::

<!-- entry: hllv export -->
:::layer beginner
Writes the keyboard pilot keys of your profile into Hell Let Loose: Vietnam, only with `--include-keyboard`. Pad binds
are never written: the virtual Xbox pad carries them. Close the game first. The GUI's **Export now** on the Hell Let
Loose: Vietnam tile runs it.

```powershell
engine hllv export --mode merge --include-keyboard --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" --text
```
:::

:::layer advanced
It writes `%LOCALAPPDATA%\HLLVietnam\Saved\SaveGames\EnhancedInputUserSettings.sav`, one key per pilot action. A
mouse button on a pilot action always stays; infantry and common keys never change.

| `--mode` | Pilot keys |
|---|---|
| `add` | the game's key stays; a key another pilot action has gets a mark "Not written to the game" |
| `merge`, `merge-overwrite` | the profile's key is set; another pilot action on that key loses it (the game then uses its default there) |
| `clean` (GUI default) | the other pilot keys you changed in the game go back to the game's defaults |

- `--nuke-untouched-devices`: nothing to clear, the game stores no joystick binds.
- Without `--include-keyboard` no key is written; gamepad action binds are counted in a step line ("the game's gamepad
  layouts are fixed, so nothing is written for them").
- The game reads no joystick: outside the virtual mode, binds on a joystick get the warning "... reads no joystick
  ..." with the command that switches it to the virtual pad.
- Never touched: the pad layouts, infantry and vehicle keys, mouse buttons, every other byte of the save file,
  `GameUserSettings.ini`.
- Safety: refused while `HLLVietnam-Win64-Shipping` or `HLLVietnam` runs; backed up first (undo:
  [[cli:backups list]]); the profile is kept as "Before export to Hell Let Loose: Vietnam". `--dry-run` writes nothing.
:::

:::layer technical
- Output: JSON of the export result or a text report with `--text`; exit code `1` on an error (the game runs, an
  invalid `--mode`, the save file cannot be read).
- Without a path it writes the file [[cli:hllv paths]] lists first.
- After a real run: `EnhancedInputUserSettings.sav.joypro-meta.json`, the watcher's fingerprint, the export marks
  ([[cli:export-marks list]]).
- The help note says "writes only the keyboard pilot keys, with --include-keyboard"; the gamepad binds reach the game
  through the virtual Xbox pad.
- Related: [[cli:hllv import]], [[cli:vjoy mode]]; game page [[game:hll-vietnam]].
:::

<!-- entry: hllv paths -->
:::layer beginner
Shows where JoyPro finds Hell Let Loose: Vietnam's key binds on this PC:
`%LOCALAPPDATA%\HLLVietnam\Saved\SaveGames\EnhancedInputUserSettings.sav`.
**Detect** in the import wizard's **Expert import** section runs it.

```powershell
engine hllv paths --text
```
:::

:::layer advanced
- No options. Listed: your own config location of [[cli:games config set]] first, then the file when it exists.
- The gamepad layout you picked (`SelectedGamepadLayout` in `...\Saved\Config\Windows\GameUserSettings.ini`) is read
  for the virtual pad; that file is not listed.
- Read-only; also works when the game is not installed or switched off.
:::

:::layer technical
- Output: JSON (`Game`, `DisplayName`, `ConfigPaths`, `Warnings`), or with `--text` `Hell Let Loose: Vietnam Config
  Paths`, `Paths: <n>` and the files. Exit code `0`; an unknown option fails with `Unknown HLLVietnam paths
  argument(s): ...`.
- Related: [[cli:hllv scan]], [[cli:games paths]]; game page [[game:hll-vietnam]].
:::

<!-- entry: hllv catalog -->
:::layer beginner
Lists the Hell Let Loose: Vietnam actions JoyPro knows: the keyboard pilot keys of the helicopter and the helicopter
actions of the gamepad, each with the pad control it has in the layouts A, B and C.

```powershell
engine hllv catalog --text
```
:::

:::layer advanced
- No options; read-only, also when the game is not installed.
- Two categories: `Helicopter keyboard` (the pilot keys, written with `--include-keyboard`) and `Helicopter gamepad`
  (ids `Pad_...`; your HOTAS reaches them through the virtual Xbox pad, [[cli:vjoy mode]] `HLLVietnam xbox`).
- A fresh install has no list yet; the first scan, import, export or [[cli:hllv refresh-db]] builds it. Until then the
  command lists no action and warns `Hell Let Loose: Vietnam action catalog not found: <path>`.
:::

:::layer technical
- Output: JSON (`Game`, `Source`, `Actions` with `Id`, `Title`, `IsAxis`, `Category`, `Warnings`), or with `--text`
  one line per action `<id> | axis|button | <category> | <title>`. Exit code `0`.
- File: `%LOCALAPPDATA%\JoyPro2\GameData\DB\HLLVietnam\actions.tsv`; the fifth column of a pad action holds its pad
  control per layout (`A:Gamepad_LeftY|B:Gamepad_RightY|C:Gamepad_LeftY`), which the virtual pad uses.
- Related: [[cli:hllv refresh-db]], [[cli:vjoy map]]; game page [[game:hll-vietnam]].
:::

<!-- entry: hllv scan -->
:::layer beginner
Shows the keyboard pilot keys Hell Let Loose: Vietnam has, without changing your profile or the game. The game stores
no joystick binds, so keys are all a scan can show. **Look first** in the import wizard runs it.

```powershell
engine hllv scan --text
```
:::

:::layer advanced
- Reads the keys of the pilot actions you changed in `EnhancedInputUserSettings.sav`; a path (positional or `--path`)
  names another file. The keys are listed without `--include-keyboard` (the import takes them only with it).
- A key JoyPro has no name for is skipped with a warning.
- `--ignore-device Keyboard` leaves the keys out.
- Safety: reads only, also while the game runs; a fresh install first builds and saves JoyPro's command list. Refused
  when the game is not installed or switched off in Settings > Games.
:::

:::layer technical
- Output: JSON (`Game`, `Action` `scan`, `ConfigPath`, `BindingsRead`, `Bindings` on device `Keyboard`, `Steps`,
  `Warnings`, `Errors`), or a text report with `--text`.
- Exit code `0`; `1` on an error (`No Hell Let Loose: Vietnam config file found. Pass the path explicitly.`, an
  unreadable save file). Unknown options fail with `Unknown HLLVietnam scan argument(s): ...`.
- Related: [[cli:hllv import]], [[cli:hllv catalog]], [[cli:vjoy mode]]; game page [[game:hll-vietnam]].
:::

<!-- entry: hllv refresh-db -->
:::layer beginner
Builds JoyPro's Hell Let Loose: Vietnam command list: from the game's input files when JoyPro can read them, else from
JoyPro's own table of the pilot keys and the three default gamepad layouts. **Update** on the game's card of the Start
page runs it and asks you first whether JoyPro may use another game's Oodle library ([[cli:hllv oodle]]).

```powershell
engine hllv refresh-db --text
```
:::

:::layer advanced
- Reads the input container `HLLVietnam\Content\Paks\HLLVietnam-Windows.utoc` of the detected installation; a path
  (positional or `--path`) may be the game folder or the `.utoc` file.
- The container can only be read with an Oodle library: the game's own `oo2core_*_win64.dll` when its folder has one,
  else the one `--oodle-dll <path>` names (an existing `oo2core_<n>_win64.dll`, used where it is, never copied, only
  for this run). Without either, or when reading fails, JoyPro's built-in table is used and a step line says so.
- `--oodle-dll` with a wrong file gives the warning `--oodle-dll must name an existing oo2core_<n>_win64.dll; JoyPro's
  own list is used.`
- The list is rebuilt each time. `--dry-run` writes nothing; otherwise the list is written only when it changed.
- Refused when the game is not installed or switched off in Settings > Games; the game may run.
:::

:::layer technical
- Output: JSON of the refresh (`DryRun`, `FilesWritten`, `Steps` such as `Catalog read from <utoc> (...; Oodle:
  <dll>).` or `Catalog: JoyPro's built-in table of the default helicopter gamepad layouts A, B, C ...`, `Actions: <old>
  -> <new>`, `Warnings`), or a text report with `--text`.
- Exit code `0` (the built-in table always gives a list); `1` only for a refusal or an unknown option.
- Writes `%LOCALAPPDATA%\JoyPro2\GameData\DB\HLLVietnam\actions.tsv`. After a pad layout change in the game, run
  [[cli:vjoy map]].
- Related: [[cli:hllv oodle]], [[cli:hllv catalog]], [[cli:vjoy mode]]; game page [[game:hll-vietnam]].
:::

<!-- entry: bf6 import -->
:::layer beginner
Reads the jet and helicopter binds of Battlefield 6 from its settings profile into your JoyPro profile: keyboard keys
when you ask for them, and joystick binds (with a warning). Nothing in the game changes. Start the game once before,
so the profile exists. The GUI runs it with **Import** on the Battlefield 6 card of the Start page.

```powershell
engine bf6 import --include-keyboard --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" --text
```
:::

:::layer advanced
It reads `%USERPROFILE%\Documents\Battlefield 6\settings\steam\PROFSAVEbf6mp_profile` (the EA app version has no
`steam` folder): the `GstKeyBinding.<category>.<Concept>.<slot>` lines of the jet and helicopter categories.

- Default: adds to the open profile (`--merge` means the same); `--replace` replaces every relation of the profile by
  what the file has.
- `--include-keyboard` also reads the keys of the jet and helicopter button actions.
- Joystick binds are read with a warning: the game's joystick numbering is not confirmed yet.
- Before the first import JoyPro lists its own table of the jet and helicopter actions; the import builds the list from
  your profile.
- Safety: reads only, not refused while the game runs; the profile is kept as "Before import from Battlefield 6" first.
- `--ignore-device <name>` / `--ignore-devices <a;b>` leave the binds of those devices out (a name without
  `{GUID}` means every device of that name); a step line says how many binds were left out.
:::

:::layer technical
- Output: JSON of the import result or a text report with `--text`; exit code `1` on an error (`No Battlefield 6
  config file found. Pass the path explicitly.`).
- Without a path it reads the file [[cli:bf6 paths]] lists first.
- With `--profile` the profile is saved afterwards; a game JoyPro never exported to is watched from now on.
- Related: [[cli:bf6 scan]], [[cli:bf6 export]], [[cli:bf2042 import]], [[cli:vjoy mode]]; game page
  [[game:battlefield]].
:::

<!-- entry: bf6 export -->
:::layer beginner
Writes the keyboard keys of your jet and helicopter relations into Battlefield 6's settings profiles, with
`--include-keyboard`. Joystick binds are listed as not written until the game's numbering is confirmed; bind the vJoy
device on the game's joystick page yourself. Close the game first. The GUI's **Export now** on the Battlefield 6 tile
runs it.

```powershell
engine bf6 export --mode clean --include-keyboard --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" --text
```
:::

:::layer advanced
It writes every `*_profile` of `Documents\Battlefield 6\settings\steam\` that holds binds (campaign and multiplayer
match). Only jet and helicopter slots change.

| `--mode` | In the profiles |
|---|---|
| `add` | a bind whose input another action of the same vehicle uses gets a mark "Not written to the game"; no slot is freed |
| `merge` | the other action's slot on the profile's input is freed; the action's bound slots stay |
| `merge-overwrite` (engine default) | as `merge`, and the action's other slots (keys with `--include-keyboard`) are freed |
| `clean` (GUI default) | the flight slots of what the export writes are freed: the keyboard keys when keys are exported |

- `--nuke-untouched-devices`: no other device to clear, the game reads one joystick.
- Joystick binds: the device with the most binds is kept, the others are skipped ("use the virtual joystick"); those
  binds are then not written either, with a warning, while the numbering is unconfirmed.
- Never touched: infantry, ground vehicle, transport and gunner binds, mouse and pad slots, keys on axis actions, keys
  without `--include-keyboard`, joystick slots, the binary `PROFSAVE` files.
- Safety: refused while `bf6` runs; backed up first (`.jpbak` and the backup store, undo: [[cli:backups list]]); the
  profile is kept as "Before export to Battlefield 6". `--dry-run` writes nothing.
:::

:::layer technical
- Output: JSON of the export result or a text report with `--text`; exit code `1` on an error (the game runs, an
  invalid `--mode`, a profile that cannot be written).
- Without a path it writes the profiles in the folder of the file [[cli:bf6 paths]] lists first.
- After a real run: `<profile>.joypro-meta.json`, the watcher's fingerprint, the export marks
  ([[cli:export-marks list]]).
- Related: [[cli:bf6 import]], [[cli:vjoy mode]] (`Battlefield6 virtual`), [[cli:bf2042 export]]; game page
  [[game:battlefield]].
:::

<!-- entry: bf6 paths -->
:::layer beginner
Shows where JoyPro finds Battlefield 6's binds on this PC: the settings profiles under
`%USERPROFILE%\Documents\Battlefield 6\settings\`, for example `steam\PROFSAVEbf6mp_profile`. **Detect** in the import
wizard's **Expert import** section runs it and takes the first file.

```powershell
engine bf6 paths --text
```
:::

:::layer advanced
- No options. Listed: your own config location of [[cli:games config set]] first, then every `*_profile` file in the
  settings folder and its subfolders that has key bind lines (`GstKeyBinding.`), the multiplayer profile first, then
  the newest.
- Read-only; also works when the game is not installed or switched off.
:::

:::layer technical
- Output: JSON (`Game`, `DisplayName`, `ConfigPaths`, `Warnings`), or with `--text` `Battlefield 6 Config Paths`,
  `Paths: <n>` and the files; a folder that cannot be listed gives a warning. Exit code `0`; an unknown option fails
  with `Unknown Battlefield6 paths argument(s): ...`.
- Related: [[cli:bf6 scan]], [[cli:bf2042 paths]], [[cli:games paths]]; game page [[game:battlefield]].
:::

<!-- entry: bf6 catalog -->
:::layer beginner
Lists the Battlefield 6 actions JoyPro knows: the jet and helicopter controls (`jet.ConceptPitch`,
`helicopter.ConceptFire` ...), each with axis or button.

```powershell
engine bf6 catalog --text
```
:::

:::layer advanced
- No options; read-only, also when the game is not installed.
- Before a list was built from your settings profile, JoyPro lists its own table: per category the throttle, pitch,
  roll, yaw and look axes (the helicopter also brake) and six buttons (fire, zoom, camera, seat and the two gadgets).
  [[cli:bf6 refresh-db]], or the first scan, import or export, replaces it with the controls of your profile.
:::

:::layer technical
- Output: JSON (`Game`, `Source`, `Actions` with `Id`, `Title`, `IsAxis`, `Category`, `Warnings`), or with `--text`
  one line per action `<id> | axis|button | <category> | <title>` (categories `Jet` and `Helicopter`). Exit code `0`.
- File: `%LOCALAPPDATA%\JoyPro2\GameData\DB\Battlefield6\actions.tsv` (`Source` names it also while the built-in
  table is shown).
- Related: [[cli:bf6 refresh-db]], [[cli:bf2042 catalog]]; game page [[game:battlefield]].
:::

<!-- entry: bf6 scan -->
:::layer beginner
Shows the jet and helicopter binds of Battlefield 6's settings profile, joystick binds and keyboard keys, without
changing your profile or the game. **Look first** in the import wizard runs it.

```powershell
engine bf6 scan --text
```
:::

:::layer advanced
- Reads the `GstKeyBinding.<category>.<Concept>.<slot>` lines of the jet and helicopter categories of the file
  [[cli:bf6 paths]] lists first, or of the file given (positional or `--path`).
- Keyboard keys of button actions are listed without `--include-keyboard` (the import takes them only with it).
- The game stores no device in its binds: joystick binds are shown on the vJoy device in virtual mode, else on the
  first connected game controller (a warning when several are connected). JoyPro's joystick numbering for the game is
  provisional, so joystick binds come with a warning; a code JoyPro's table does not know is skipped.
- `--ignore-device <name>` / `--ignore-devices <a;b>` leave the binds of those devices out.
- Safety: reads only, also while the game runs; a fresh install first builds and saves the command list from your
  profile. Refused when the game is not installed or switched off in Settings > Games.
:::

:::layer technical
- Output: JSON (`Game`, `Action` `scan`, `ConfigPath`, `BindingsRead`, `Bindings`, `Steps`, `Warnings`, `Errors`), or
  a text report with `--text`.
- Exit code `0`; `1` on an error (`No Battlefield 6 config file found. Pass the path explicitly.`). Unknown options
  fail with `Unknown Battlefield6 scan argument(s): ...`.
- Related: [[cli:bf6 import]], [[cli:vjoy mode]]; game page [[game:battlefield]].
:::

<!-- entry: bf6 refresh-db -->
:::layer beginner
Builds JoyPro's Battlefield 6 command list from your own settings profile: every jet and helicopter control it has.
Start the game once first, so the profile exists. **Update** on the Battlefield 6 card of the Start page runs it.

```powershell
engine bf6 refresh-db --dry-run --text
```
:::

:::layer advanced
- Reads the jet and helicopter concepts of the file [[cli:bf6 paths]] lists first, or of a `PROFSAVE..._profile` file
  given as path (positional or `--path`). A folder given as path is searched (with its subfolders) for the profile
  with binds, the multiplayer one first.
- A control counts as an axis when one of its slots holds the second half of an axis or a mouse axis.
- The list is rebuilt from the profile. `--dry-run` writes nothing; otherwise the list is written only when it changed.
- Refused when Battlefield 6 is not installed or switched off in Settings > Games; the game may run.
:::

:::layer technical
- Output: JSON of the refresh (`DryRun`, `FilesWritten`, `Steps` such as `Read the jet and helicopter controls from
  <file> (<n> action(s)).` and `Actions: <old> -> <new>`, `Warnings`, `Errors`), or a text report with `--text`.
- Exit code `0`; `1` when no profile is found (`No Battlefield 6 settings profile found (start the game once, or pass
  the PROFSAVE..._profile file).`); the list stays unchanged.
- Writes `%LOCALAPPDATA%\JoyPro2\GameData\DB\Battlefield6\actions.tsv`.
- Related: [[cli:bf6 catalog]], [[cli:bf2042 refresh-db]]; game page [[game:battlefield]].
:::

<!-- entry: bf2042 import -->
:::layer beginner
Reads the jet and helicopter binds of Battlefield 2042 from its settings profile into your JoyPro profile: keyboard keys
when you ask for them, and joystick binds (with a warning). Nothing in the game changes. The GUI runs it with
**Import** on the Battlefield 2042 card of the Start page.

```powershell
engine bf2042 import --include-keyboard --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file" --text
```
:::

:::layer advanced
It reads `%USERPROFILE%\Documents\Battlefield 2042\settings\PROFSAVE_profile`, the same line format as Battlefield 6.

- Default: adds to the open profile (`--merge` means the same); `--replace` replaces every relation of the profile by
  what the file has.
- `--include-keyboard` also reads the keys of the jet and helicopter button actions; joystick binds come with a
  warning (numbering not confirmed yet).
- Safety: reads only, not refused while the game runs; the profile is kept as "Before import from Battlefield 2042"
  first.
- `--ignore-device <name>` / `--ignore-devices <a;b>` leave the binds of those devices out (a name without
  `{GUID}` means every device of that name); a step line says how many binds were left out.
:::

:::layer technical
- Output: JSON of the import result or a text report with `--text`; exit code `1` on an error (`No Battlefield 2042
  config file found. Pass the path explicitly.`).
- Without a path it reads the file [[cli:bf2042 paths]] lists first; before the first import JoyPro lists its own
  table of the actions.
- With `--profile` the profile is saved afterwards; a game JoyPro never exported to is watched from now on.
- Related: [[cli:bf2042 scan]], [[cli:bf2042 export]], [[cli:bf6 import]]; game page [[game:battlefield]].
:::

<!-- entry: bf2042 export -->
:::layer beginner
Writes the keyboard keys of your jet and helicopter relations into Battlefield 2042's settings profile, with
`--include-keyboard`. Joystick binds are listed as not written until the game's numbering is confirmed. Close the game
first. The GUI's **Export now** on the Battlefield 2042 tile runs it.

```powershell
engine bf2042 export --mode clean --include-keyboard --profile "C:\Users\Pilot\Documents\JoyPro\My Sticks.pr0file"
```
:::

:::layer advanced
It writes every `*_profile` of `Documents\Battlefield 2042\settings\` that holds binds. Only jet and helicopter slots
change.

| `--mode` | In the profiles |
|---|---|
| `add` | a bind whose input another action of the same vehicle uses gets a mark "Not written to the game" |
| `merge` | the other action's slot on the profile's input is freed; the action's bound slots stay |
| `merge-overwrite` (engine default) | as `merge`, and the action's other slots (keys with `--include-keyboard`) are freed |
| `clean` (GUI default) | the flight slots of what the export writes are freed |

- `--nuke-untouched-devices`: no other device to clear, the game reads one joystick.
- Joystick binds are not written while the numbering is unconfirmed (warning); only the device with the most binds
  would count.
- Never touched: infantry, ground vehicle, transport and gunner binds, mouse and pad slots, keys on axis actions, keys
  without `--include-keyboard`, joystick slots, the binary `PROFSAVE` files.
- Safety: refused while `BF2042` runs; backed up first (undo: [[cli:backups list]]); the profile is kept as "Before
  export to Battlefield 2042". `--dry-run` writes nothing.
:::

:::layer technical
- Output: JSON of the export result or a text report with `--text`; exit code `1` on an error (the game runs, an
  invalid `--mode`, a profile that cannot be written).
- Without a path it writes the profiles in the folder of the file [[cli:bf2042 paths]] lists first.
- After a real run: `<profile>.joypro-meta.json`, the watcher's fingerprint, the export marks.
- Related: [[cli:bf2042 import]], [[cli:vjoy mode]] (`Battlefield2042 virtual`), [[cli:bf6 export]]; game page
  [[game:battlefield]].
:::

<!-- entry: bf2042 paths -->
:::layer beginner
Shows where JoyPro finds Battlefield 2042's binds on this PC: the settings profiles under
`%USERPROFILE%\Documents\Battlefield 2042\settings\`, normally `PROFSAVE_profile`. **Detect** in the import wizard's
**Expert import** section runs it.

```powershell
engine bf2042 paths --text
```
:::

:::layer advanced
- No options. Listed: your own config location of [[cli:games config set]] first, then every `*_profile` file in the
  settings folder and its subfolders that has key bind lines, a multiplayer profile first, then the newest.
- Read-only; also works when the game is not installed or switched off.
:::

:::layer technical
- Output: JSON (`Game`, `DisplayName`, `ConfigPaths`, `Warnings`), or with `--text` `Battlefield 2042 Config Paths`,
  `Paths: <n>` and the files. Exit code `0`; an unknown option fails with `Unknown Battlefield2042 paths argument(s):
  ...`.
- Related: [[cli:bf2042 scan]], [[cli:bf6 paths]]; game page [[game:battlefield]].
:::

<!-- entry: bf2042 catalog -->
:::layer beginner
Lists the Battlefield 2042 actions JoyPro knows: the jet and helicopter controls, each with axis or button.

```powershell
engine bf2042 catalog --text
```
:::

:::layer advanced
- No options; read-only, also when the game is not installed.
- Before a list was built from your settings profile, JoyPro lists its own table of the jet and helicopter controls
  (the same as for Battlefield 6); [[cli:bf2042 refresh-db]], or the first scan, import or export, replaces it.
:::

:::layer technical
- Output: JSON (`Game`, `Source`, `Actions` with `Id`, `Title`, `IsAxis`, `Category`, `Warnings`), or with `--text`
  one line per action `<id> | axis|button | <category> | <title>`. Exit code `0`.
- File: `%LOCALAPPDATA%\JoyPro2\GameData\DB\Battlefield2042\actions.tsv`.
- Related: [[cli:bf2042 refresh-db]], [[cli:bf6 catalog]]; game page [[game:battlefield]].
:::

<!-- entry: bf2042 scan -->
:::layer beginner
Shows the jet and helicopter binds of Battlefield 2042's settings profile, joystick binds and keyboard keys, without
changing your profile or the game. **Look first** in the import wizard runs it.

```powershell
engine bf2042 scan --text
```
:::

:::layer advanced
- Reads the jet and helicopter lines of `PROFSAVE_profile` (the file [[cli:bf2042 paths]] lists first) or of the file
  given (positional or `--path`); the line format is the same as Battlefield 6's.
- Keyboard keys of button actions are listed without `--include-keyboard`. Joystick binds are shown on the vJoy device
  in virtual mode, else on the first connected game controller, with a warning (the numbering is provisional).
- `--ignore-device <name>` / `--ignore-devices <a;b>` leave the binds of those devices out.
- Safety: reads only, also while the game runs; a fresh install first builds and saves the command list. Refused when
  the game is not installed or switched off in Settings > Games.
:::

:::layer technical
- Output: JSON (`Game`, `Action` `scan`, `ConfigPath`, `BindingsRead`, `Bindings`, `Steps`, `Warnings`, `Errors`), or
  a text report with `--text`.
- Exit code `0`; `1` on an error (`No Battlefield 2042 config file found. Pass the path explicitly.`). Unknown options
  fail with `Unknown Battlefield2042 scan argument(s): ...`.
- Related: [[cli:bf2042 import]], [[cli:bf6 scan]]; game page [[game:battlefield]].
:::

<!-- entry: bf2042 refresh-db -->
:::layer beginner
Builds JoyPro's Battlefield 2042 command list from your own settings profile: every jet and helicopter control it has.
**Update** on the Battlefield 2042 card of the Start page runs it.

```powershell
engine bf2042 refresh-db --text
```
:::

:::layer advanced
- Reads the file [[cli:bf2042 paths]] lists first, or a `PROFSAVE..._profile` file given as path; a folder given as
  path is searched (with its subfolders) for the profile with binds.
- The list is rebuilt from the profile. `--dry-run` writes nothing; otherwise the list is written only when it changed.
- Refused when Battlefield 2042 is not installed or switched off in Settings > Games; the game may run.
:::

:::layer technical
- Output: JSON of the refresh (`DryRun`, `FilesWritten`, `Steps` with `Actions: <old> -> <new>`, `Warnings`,
  `Errors`), or a text report with `--text`.
- Exit code `0`; `1` when no profile is found (`No Battlefield 2042 settings profile found (start the game once, or
  pass the PROFSAVE..._profile file).`).
- Writes `%LOCALAPPDATA%\JoyPro2\GameData\DB\Battlefield2042\actions.tsv`.
- Related: [[cli:bf2042 catalog]], [[cli:bf6 refresh-db]]; game page [[game:battlefield]].
:::

