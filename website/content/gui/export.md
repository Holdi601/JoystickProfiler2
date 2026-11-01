---
title: Export
status: full
summary: The Export page: check, choose how JoyPro writes, export the ticked games, undo, game config backups.
---
The Export page writes your profile into the games. It is three numbered steps, then the expert options and the
game config backups. The full story of the modes and what they do in each game is on
[[page:flows/export.html|Export to your games]].

:::video export-page "The Export page"
:::

![The Export page](shot:export)

:::layer beginner
## The page from top to bottom

1. **Check your profile**: **Check now** runs the [[page:flows/check.html|Check]]; its cards appear further down.
2. **How JoyPro writes into the games**: four choices (Replace on my devices, Update keep the game's binds, Update my
   profile wins, Only add) and the tick **Also clear my other devices**.
3. **Export to games**: one tile per game with binds in your profile. Tick tiles and press **Export ticked games**,
   or **Export now** on one tile. **Undo last export** on a tile restores that game's files.

Below: **Options and paths (for experts)**, then **Problems found by the check**, **Same button used twice** and
**Game config backups**.
:::

:::layer advanced
## Tiles and remembered choices

- A tile starts ticked only when the game has bound relations and was started once (its control files exist). A tick
  you change stays, per profile.
- The DCS tile has **Aircraft...**: every aircraft (and input layer) of the profile with its picture and how many of
  its relations are bound; only ticked aircraft are written (**All** / **None**).
- Each tile shows its last result ("Exported 14:02" or the engine's error).
- Games not installed get no tile; the page names them with a link to Settings > Games.
- **Preview** on a tile (where offered) runs the export as a dry run and shows what would change.
:::

:::layer technical
## Behind the page

View model: `MainWindowViewModel.ExportGames.cs` (tiles, export, undo), `MainWindowViewModel.ExportDefaults.cs`
(mode defaults, marks), `MainWindowViewModel.Backups.cs`. Engine: [[cli:profile games]] fills the tiles;
[[cli:dcs export]], [[cli:il2 export]], [[cli:starcitizen export]] and `engine <game> export` write;
[[cli:backups list]] / [[cli:backups restore]] for undo and the backups list; [[cli:profile game-state]] for the
"Import first?" question.
:::

<!-- entry: bind:ExportNukeUntouchedDevices -->
Connected devices that have **no bind in your profile** lose all their binds in the exported aircraft (DCS: their
DCS defaults too). Devices that are not connected, the import's ignored devices and the vJoy device JoyPro tunnels
into are never cleared. Not used with **Only add**. Engine: `--nuke-untouched-devices true`. In the game: a second
stick you never bound in JoyPro is empty in those aircraft after the export.

<!-- entry: ExportSelectedGamesCommand -->
Exports every ticked tile, one game after the other, with the mode chosen above. Before it, each game exported for
the first time by this profile asks "Import first?", and clean or nuke ask once whether binds may be removed. The
status line ends with "Exported 5 of 6 games, 1 failed: <game> (<reason>)" and notes binds the games did not take.
In the game: the game's control files hold your binds; start the game to see them.

<!-- entry: ExportGameNowCommand -->
Exports only this tile's game, with the same questions as **Export ticked games**. In the game: as above, for this
game only.

<!-- entry: UndoLastExportCommand -->
Restores the newest backup of this game: every file the last export changed comes back byte for byte, files it
created are removed. The restore itself is backed up first, so it can be undone too. Refused while the game runs.
Engine: `engine backups restore latest --game <id>`. In the game: the controls are exactly as before the export.

<!-- entry: RefreshExportGamesCommand -->
Reads the profile's games and their bound relations again (`engine profile games`) and rebuilds the tiles.

<!-- entry: PreviewExportGameCommand -->
Runs this game's export without writing (a dry run) and lists what would change in the Output panel. Nothing is
written and no backup is made.

<!-- entry: ValidateProfileCommand -->
Runs the Check: `engine profile validate`, the duplicate and conflict reports and the export marks; the cards appear
under **Problems found by the check**. Changes nothing.

<!-- entry: RestoreGameBackupCommand -->
Puts the selected backup set back (any set, not only the newest). Use **Preview Restore** first to see which files
change. The current files are backed up before the restore.

<!-- entry: bind:ExportDcsCleanAllAircraft -->
DCS only, off by default: with **Replace on my devices**, the written devices also lose their binds and DCS defaults
in **every other installed aircraft**, not only the exported ones (input layers such as UiLayer and Camera are not
touched). Engine: `--clean-all-aircraft true`.

<!-- entry: bind:ExportKeepKeyboardDefaults -->
DCS only, on by default: the keyboard keeps DCS's default keys. Off: the profile's keyboard binds are written and
only the DCS default keys they take are removed. Engine: `--keep-keyboard-defaults`.

<!-- entry: bind:RegistryGameDryRun -->
For the games outside DCS, IL-2 and Star Citizen: the export only shows what would change (`--dry-run`), nothing is
written.

<!-- entry: bind:ProfileOwnPaths -->
Gives the open profile its own game paths and ignored devices instead of the PC's. Opening another profile brings
the PC's paths back.

<!-- entry: OpenMsfsImportFilesFolderCommand -->
Microsoft Flight Simulator 2024 only. Where JoyPro could not write your own MSFS 2024 profile, the export writes your
binds as one file per device and category; this opens that folder in Explorer. In the sim: Settings > Controls, pick
the device and the category (Airplanes or General), the cogwheel of your profile, **Import**, and choose the file of
that device and category. Import a file again only when JoyPro's export says it changed.

<!-- entry: GoSettingsCommand -->
Shown when some games of your profile are not installed (they get no tile): opens Settings > Games, where you can
link a portable install or another folder so the game counts as installed.

<!-- entry: SelectNoPlanesCommand -->
In the **Aircraft...** list of the DCS tile: unticks every aircraft (**All** ticks them again). Only ticked aircraft are
written by the export; the others keep their DCS files as they are.

<!-- entry: ExportIl2Command -->
Expert option: exports IL-2 Great Battles alone, with the path typed in the box next to it (empty: the detected
installation) and the mode chosen above. The same as **Export now** on the IL-2 tile. Engine: [[cli:il2 export]]. In
the game: IL-2 keeps its controls in the installation (`data\input`); start IL-2 to see the binds.

<!-- entry: ExportStarCitizenCommand -->
Expert option: exports Star Citizen alone, with the path and optional source in the boxes above, the mode chosen above
and the two Star Citizen ticks. Engine: [[cli:starcitizen export]]. In the game: the binds are in the game's
`actionmaps.xml`; the game reads it at start.

<!-- entry: ExportRegistryGameCommand -->
Expert option: exports the game picked in **Other Game** (every game outside DCS, IL-2 and Star Citizen), to the file or
folder in the box (empty: the detected one), with the mode chosen above; with **Dry run** ticked it only shows what
would change. Engine: `engine <game> export`.

<!-- entry: bind:ExportStarCitizenKeepKeyboardDefaults -->
Star Citizen only, on by default: the keyboard keeps the game's default keys. Off: the profile's keyboard binds are
written and the default keys they take are released. Engine: `--keep-keyboard-defaults`.

<!-- entry: bind:ExportStarCitizenPreferLocalOrder -->
Star Citizen only, on by default: a device keeps the joystick slot (js1, js2, ...) the game already gave it in its own
file; other devices fill the free slots. Off: the slots follow the order Windows reports the devices in, which can move
every bind of a device to another slot. Engine: `--prefer-local-order`.

<!-- entry: ExportDcsCommand -->
Expert option: exports DCS alone, every aircraft of the profile, to the DCS path in the box (empty: the detected Saved
Games folder), with the mode and the DCS ticks chosen here. **Export now** on the DCS tile does the same but only for
the aircraft ticked under **Aircraft...**. Engine: [[cli:dcs export]]. In the game: each aircraft's
`Config\Input\<aircraft>\joystick\<device>.diff.lua` holds your binds; DCS reads them when the aircraft loads.

<!-- entry: OpenIssueContextCommand -->
Opens the relation named in the selected line of the check's issue list ("The Relations it was found in: ...") on the
Relations page.

<!-- entry: RefreshGameBackupsCommand -->
Reads the list of game config backups again ([[cli:backups list]]) for the game picked on the left (or all). Backups
of games you hid in Settings > Games are left out. Also updates which tiles offer **Undo last export**.

<!-- entry: ShowGameBackupCommand -->
Lists the files of the selected backup set (what the export changed and where each copy is) below the table.
Engine: [[cli:backups show]].

<!-- entry: PreviewRestoreGameBackupCommand -->
Shows which files **Restore** would put back, without writing anything ([[cli:backups restore]] `--dry-run`). Close the
game before the real restore.

<!-- entry: bind:MsfsWriteUserProfiles -->
Microsoft Flight Simulator 2024 only, on by default: the export writes your binds straight into the profile MSFS 2024
uses for each device and category (Steam or Microsoft Store version), after a backup. The box below the tiles then
says per device which profile got them. Off: JoyPro writes only import files (`--user-profiles false`). Remembered for
the next start. In the sim: your own controls profile holds the binds; no import is needed for those devices.

<!-- entry: bind:MsfsAlwaysImportFiles -->
Microsoft Flight Simulator 2024 only, off by default: the export also writes an import file for every device and
category (`--import-files`), not only where JoyPro could not write your profile. **Open folder** in the MSFS box shows
the files. Remembered for the next start.

<!-- entry: bind:IsSelected -->
The tick of a game tile: ticked tiles are exported by **Export ticked games**. A tile starts ticked when the game has
bound relations and was started once; a tick you change is remembered per profile. It cannot be ticked when the game
has no export. Under **Aircraft...**, the same tick on an aircraft decides whether that aircraft is written.

<!-- entry: ToggleExpandedCommand -->
**Aircraft...** / **Hide aircraft** on a tile with aircraft (DCS): opens the list of the profile's aircraft with their
pictures and how many of their relations are bound. Only the ticked aircraft are written by the export; your ticks are
remembered per profile.

<!-- entry: SelectAllPlanesCommand -->
In the **Aircraft...** list of a tile: ticks every aircraft, so the export writes all of them (**None** unticks them).
