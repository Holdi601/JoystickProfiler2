---
title: Start
status: full
summary: The Start page: your profile at a glance, next steps, your games with the import wizard, your devices, recent activity and the watcher.
---
The Start page is where JoyPro opens. It shows what is set up, what to do next, and your games: **Import** takes
the binds you made in a game into JoyPro; **Export** writes them back. The import step by step:
[[page:flows/import.html|Import a game]].

:::video start-page "The Start page"
:::

![The Start page](shot:start)

:::layer beginner
## From top to bottom

- **Notices**: missing requirements, command lists to build or update, a crash report to send, an update.
- **Profile at a glance**: relations (and how many are axes), how many are bound, modifiers, devices connected,
  games installed.
- **Next steps**: what JoyPro suggests now ("3 relations have no button yet: Bind them", "VPforce Rhino FFB Base is
  not used yet: Auto Assign", "Write your binds into your games: Export").
- **Your games**: one card per game with **Import**, **Export**, **Update** (its command list), **Undo export**,
  **Watch for changes** and a hide button. **Set up my devices automatically...** runs the setup wizard.
- **Your devices**: the connected devices with their pictures; a click opens the Layout page. An orange **Which grip?**
  or **Which model?** on a device means JoyPro cannot tell which one it is: a click on it opens the Layout page with the
  choice open, and your answer stays.
- **Recent activity** per game and the game watcher switch; the **HUD** card.
:::

:::layer advanced
## The import wizard

1. Pick a game (**Import** on its card).
2. Choose how: **Add to my profile (recommended)** or **Start over from this game**, the keyboard, devices that are
   not connected, the DCS aircraft. **Look first** shows what the game has in the Output panel without changing
   anything.
3. **Import now**: the result stays on top of the cards ("Done: your profile now has 214 relations") with **See my
   relations**.

Below the cards, the **expert import** offers every option per game (DCS, IL-2, Star Citizen, and every other game
from **Load Games**) with Scan / Import / Export.
:::

:::layer technical
## Behind the page

`MainWindowViewModel.GameHub.cs` (cards, wizard), `MainWindowViewModel.Design.cs` (dashboard),
`MainWindowViewModel.DbUpdates.cs` (command lists), `MainWindowViewModel.DeviceCenter.cs` (devices),
`MainWindowViewModel.GameSync.cs` (watcher). Engine: [[cli:games installs]], [[cli:db check-updates]],
[[cli:hud devices]], [[cli:sync status]], [[cli:dcs import]] / [[cli:dcs scan]] and the other games' import and scan.
:::

<!-- entry: StartImportWizardCommand -->
Starts the import wizard for this game: step 2 asks how to import. Nothing is read or changed yet.

<!-- entry: RunImportWizardCommand -->
Imports the game with the choices of step 2. It first asks if you changed binds in JoyPro that this game does not
have yet (an import would bring the old ones back), and before **Start over** it keeps a copy of your profile. The
game's files are only read. Engine: `engine dcs import --merge ...`, `engine il2 import`, `engine starcitizen import`
or `engine <game> import`.

<!-- entry: PreviewImportWizardCommand -->
"Look first": runs the game's scan and shows what it has in the Output panel. Nothing changes in the profile or the
game. Engine: `engine <game> scan`.

<!-- entry: bind:ImportWizardAddToProfile -->
Adds the game's binds to the open profile (the safe default). Relations with the same name or input are merged.

<!-- entry: bind:ImportWizardStartOver -->
Replaces the relations of the open profile with what the game has. JoyPro asks first and keeps a copy of the profile.

<!-- entry: ExportGameCardCommand -->
Exports only this game with the Export page's mode (the same questions as on the Export page).

<!-- entry: UndoGameCardExportCommand -->
Restores this game's files from before the last export, byte for byte (`engine backups restore latest --game <id>`).

<!-- entry: UpdateGameCommandListCommand -->
Builds or updates this game's command list from your installation (DCS: the command list and the clean profiles,
run with DCS's own Lua, no game window). Needed after a game update adds aircraft or commands.

<!-- entry: BuildMissingCommandListsCommand -->
Builds every missing command list, one game after the other, with progress and Cancel.

<!-- entry: bind:Watch -->
Watch this game's control files: after the game closes, JoyPro asks whether to take changes you made in the game into
your profile. Off: changes in this game are ignored.

<!-- entry: OpenSetupWizardCommand -->
Opens the setup wizard: your games, your devices and their roles, your priorities, then a plan of relations and
binds you review before it is applied.

<!-- entry: RefreshGameCardsCommand -->
Detects the installed games again (after you installed one) and rebuilds the cards (`engine games installs`).

<!-- entry: HideGameCardCommand -->
Hides this game everywhere in JoyPro (it counts as not installed). Your relations for it stay; Settings > Games >
Show again brings it back.

<!-- entry: InstallMissingRequirementsCommand -->
Downloads and installs the missing components the notice lists (for example a .NET runtime the engine needs), one after
the other, then checks again whether the engine starts. Errors are shown in the notice.

<!-- entry: ReportLastCrashCommand -->
Opens Settings > Report a problem with a text started for the last crash ("JoyPro2 ran into an error. What I was
doing: ..."). Describe what you did and press **Create Report**; the crash details are added by themselves.

<!-- entry: DismissCrashReportsCommand -->
Hides the crash notice; the crash counts as seen and is not offered again.

<!-- entry: OpenReleasePageCommand -->
"What's New": opens the release page of the new version on GitHub with its notes.

<!-- entry: CheckDbUpdatesCommand -->
Checks again which games were updated since their command list was built (`engine db check-updates`) and refreshes the
notice: each game with an older list gets **Refresh** or **Keep DB**.

<!-- entry: Command -->
The button of a next step ("Bind them", "Auto Assign", "Export"): it opens the page or runs the action the step names.

<!-- entry: FinishImportWizardCommand -->
"See my relations": closes the import result and opens the Relations page with what the import brought in.

<!-- entry: ImportWizardBackCommand -->
Back to the import options after an import failed (or from step 2 back to the game cards), to change a choice and try
again. Nothing was changed by the failed import.

<!-- entry: GoExportCommand -->
"Export to games": opens the Export page to write the imported (or changed) binds back into your games.

<!-- entry: CloseByojCommand -->
Closes the BYOJoystick panel of VTOL VR without installing anything.

<!-- entry: bind:ByojConsent -->
Tick to confirm that you read what BYOJoystick is and want to install it from the Steam Workshop; **Install
BYOJoystick** works only then. JoyPro backs up your BYOJ files and opens Steam; Steam downloads the mod. Without the mod
VTOL VR binds only rudder and toe brakes; with it JoyPro writes the binds of stick, throttle and switches per aircraft
and seat.

<!-- entry: TurnOnWatchingFromImportCommand -->
"Turn on watching for <game>": switches on the game watcher for this game (and the watcher itself if it was off). From
then on, binds you change in the game are offered to your profile after the game closes.

<!-- entry: HideWatcherHintForGoodCommand -->
Hides the hint about the game watcher on the import page for good (Settings > Confirmations shows it again).

<!-- entry: bind:ImportWizardKeyboard -->
Also imports the game's keyboard keys of flight commands (device binds are always imported). Off: only devices.

<!-- entry: bind:ImportAllDcsPlanes -->
Imports every DCS aircraft that has binds. Off: only the aircraft ticked in the list below.

<!-- entry: bind:IsSelected -->
Ticks this DCS aircraft for the import (used when **All my DCS aircraft** is off).

<!-- entry: bind:ImportUseSelectedDevices -->
Expert import: imports only the devices ticked in the list below (`--device` per device). Off: every device.

<!-- entry: bind:ImportMerge -->
Expert import: adds the game's binds to the open profile (`--merge`). Off: the profile's relations are replaced by the
import.

<!-- entry: bind:ImportKeyboardCommands -->
Expert DCS import: also reads the keyboard binds (`--include-keyboard true`).

<!-- entry: bind:ImportStarCitizenIncludeKeyboard -->
Expert Star Citizen import: also reads the keyboard binds of ship controls (`--include-keyboard true`).

<!-- entry: bind:ImportIgnoreEasyPlanes -->
Expert DCS import: leaves out the `*_easy` variants of the aircraft (game-flight-mode copies), so they do not double
the relations (`--ignore-easy-planes true`).

<!-- entry: bind:ImportInverted -->
Expert import: takes the axis "invert" setting from the game into the bind's tuning (`--import-inverted`).

<!-- entry: bind:ImportSlider -->
Expert import: takes the axis "slider" setting from the game (`--import-slider`).

<!-- entry: bind:ImportCurvature -->
Expert import: takes the axis curvature from the game (`--import-curvature`).

<!-- entry: bind:ImportDeadzone -->
Expert import: takes the axis dead zone from the game (`--import-deadzone`).

<!-- entry: bind:ImportSaturationX -->
Expert import: takes the axis saturation X from the game (`--import-saturation-x`).

<!-- entry: bind:ImportSaturationY -->
Expert import: takes the axis saturation Y from the game (`--import-saturation-y`). Binds JoyPro exported itself always
come back with their curves, whatever these boxes say.

<!-- entry: RefreshDcsWorkspaceCommand -->
Reads the connected devices and the DCS aircraft again (`engine devices list`, `engine db planes`) for the expert
import's lists.

<!-- entry: ScanDcsCommand -->
Shows what DCS has (aircraft, devices, binds) in the Output panel without changing anything (`engine dcs scan`).

<!-- entry: ImportDcsCommand -->
Imports from DCS with every option of the expert import (`engine dcs import` with path, aircraft, devices, ignored
devices, keyboard and axis values). Asks first when JoyPro has changes DCS does not have yet. DCS's files are only read.

<!-- entry: ExportDcsCommand -->
Exports to DCS with the Export page's mode (`engine dcs export`). In the game: the aircraft's control files hold your
binds.

<!-- entry: RefreshIl2WorkspaceCommand -->
Reads the connected devices again for the IL-2 expert import (`engine devices list`).

<!-- entry: ScanIl2Command -->
Shows what IL-2 has in the Output panel without changing anything (`engine il2 scan`).

<!-- entry: ImportIl2Command -->
Imports from IL-2 with the expert options (`engine il2 import`). IL-2's files are only read.

<!-- entry: ExportIl2Command -->
Exports to IL-2 with the Export page's mode (`engine il2 export`). In the game: `data\input\global.actions` holds your binds (Settings > Key mapping shows them).

<!-- entry: RefreshStarCitizenWorkspaceCommand -->
Reads Star Citizen's device list and the connected devices again (`engine starcitizen list-devices`, `engine devices
list`).

<!-- entry: ScanStarCitizenCommand -->
Shows what Star Citizen has in the Output panel without changing anything (`engine starcitizen scan`).

<!-- entry: ImportStarCitizenCommand -->
Imports from Star Citizen with the expert options (`engine starcitizen import`). The game's files are only read.

<!-- entry: ExportStarCitizenCommand -->
Exports to Star Citizen with the Export page's mode (`engine starcitizen export`). In the game: the bindings of your
profile are in place at the next start of the game.

<!-- entry: DetectRegistryGamePathCommand -->
Looks for the picked game's control file (`engine <game> paths`) and fills **File / Folder** with it (the newest when
there are several; the others are in the Output panel).

<!-- entry: RefreshRegistryGamesCommand -->
Loads the list of the other games JoyPro reads (`engine games integrations`): WARDOGS, Arma, War Thunder, X-Plane,
MSFS, Falcon BMS, IL-2 Korea, X4, Elite Dangerous, VTOL VR, Nuclear Option and more.

<!-- entry: ScanRegistryGameCommand -->
Shows what the picked game's control file has in the Output panel without changing anything (`engine <game> scan`).

<!-- entry: ImportRegistryGameCommand -->
Imports the picked game's binds into the profile (`engine <game> import`). The game's files are only read.

<!-- entry: ExportRegistryGameCommand -->
Exports to the picked game with the Export page's mode (`engine <game> export`; with the dry run box only shows what
would change). Only flight controls are written; everything else in the game's file stays.

<!-- entry: bind:RegistryGameDryRun -->
The export of the picked game only shows what would change (`--dry-run`); nothing is written.

<!-- entry: bind:ExportNukeUntouchedDevices -->
With the expert export of a game: connected devices that have no bind in your profile lose all their binds in that game
(`--nuke-untouched-devices`). The same setting as **Also clear my other devices** on the Export page.

<!-- entry: OpenMsfsImportFilesFolderCommand -->
Opens the folder with the MSFS 2024 import files (one per device and category), for profiles JoyPro could not write
itself. Import them in MSFS 2024 under Settings > Controls.

<!-- entry: RefreshRegistryGameDbCommand -->
Rebuilds the picked game's command list from its files (`engine <game> refresh-db`, from **DB Source** when set). Games
whose list ships with JoyPro say so.

<!-- entry: OpenDeviceCardInLayoutCommand -->
Opens the Layout page with this device's picture.

<!-- entry: AnswerDeviceCardQuestionCommand -->
The orange **Which grip?** or **Which model?** on a device card: opens the Layout page with this device and its choice
open. **Which grip?**: a base does not say which grip is on it. **Which model?**: several products report the same USB
id. Your answer is kept for this device, also after a restart, and the chip goes away.

<!-- entry: CardPhotoNoCommand -->
No maker's photo for this device; JoyPro's drawing stays. Asked again only from Settings > Privacy.

<!-- entry: CardPhotoAlwaysCommand -->
Downloads the maker's photo of every device from now on without asking (Settings > Privacy: always).

<!-- entry: CardPhotoNeverCommand -->
Never downloads makers' photos of devices; JoyPro's drawings are shown (Settings > Privacy changes it back).

<!-- entry: AcceptDevice3DCommand -->
Downloads the 3D views of the connected devices from JoyPro's releases; the Layout page and the Bind page's picture
then have **3D view**.

<!-- entry: DismissDevice3DCommand -->
Not now: the 3D views are not downloaded; the offer comes back in a later session.

<!-- entry: AcceptReferencePhotosCommand -->
Downloads every reference photo JoyPro knows for the connected devices, in the background; each is checked against
JoyPro's record. It counts as your yes for these devices.

<!-- entry: DismissReferencePhotosCommand -->
Not now: no photos are downloaded; JoyPro asks again in the next session.

<!-- entry: ToggleOverlayCommand -->
Shows or hides the HUD overlay over your game (the label says which). It shows your devices live as designed on the HUD
page.

<!-- entry: GoHudCommand -->
Opens the HUD designer (the HUD page).

<!-- entry: RunEngineSummaryCommand -->
Prints the engine's summary in the Output panel (`engine summary`): version, data folders, command lists.

<!-- entry: RunProfileSummaryCommand -->
Prints the open profile's summary in the Output panel (`engine profile summary`): relations, binds, modifiers, games.

<!-- entry: RefreshDevicesCommand -->
Lists the connected devices with their names and IDs in the Output panel (`engine devices list`).

<!-- entry: RefreshDbGamesCommand -->
Lists the games of JoyPro's command lists in the Output panel (`engine db games`).

<!-- entry: RefreshDbPlanesCommand -->
Lists the aircraft of the command lists in the Output panel (`engine db planes`).

<!-- entry: CancelCommand -->
**Cancel** in the busy area: asks the running engine command to stop at its next safe point; the label turns to
**Cancelling...**. JoyPro offers it only while the command still reads, so nothing has been written when it stops.

<!-- entry: RefreshDashboardCommand -->
Reads your connected devices and the recent activity per game again, for example after you plugged in a stick or
exported to a game. Nothing is changed. Engine: [[cli:sync status]], [[cli:hud devices]].

<!-- entry: CheckRequirementsCommand -->
**Check Again** in the "Components missing" notice: checks whether the components the engine needs are installed, then
whether the engine really starts, as at program start. Use it after you installed a missing component yourself.

<!-- entry: InstallUpdateCommand -->
Downloads the new version the notice names, checks the download, then closes JoyPro, installs it and starts it again.
If the download or the check fails, your installed version stays as it is and the dialog says why.

<!-- entry: RefreshDbUpdateCommand -->
**Refresh DB** (**Build List** when the game has no command list yet): reads the game's commands from its own files
into JoyPro's command list, then checks the notice again. DCS is read through its own Lua files without starting the
game; its first list also builds the clean profiles. Your profile and the game's files are not changed. Engine:
[[cli:dcs refresh id]], [[cli:db refresh-il2]], [[cli:db refresh-starcitizen]] or `engine <game> refresh-db`.

<!-- entry: AcceptDbUpdateCommand -->
**Keep DB**: this game's current build counts as known and its command list stays as it is; the game leaves the
notice. Use it when the game update added nothing you need. Engine: [[cli:db check-updates]] `--accept <game>`.

<!-- entry: AcceptDbUpdatesCommand -->
Marks the current build of every listed game as known without refreshing any command list, and hides the notice.
Engine: [[cli:db check-updates]] `--accept-all`.

<!-- entry: OpenSupportedGamesCommand -->
Opens the list of every game JoyPro knows in your browser: what it can do for each (import, export, curves, settings,
virtual joystick ...) and how it was tested. The engine writes the page into your temp folder; nothing else changes.
Engine: [[cli:games support]] `--html`.

<!-- entry: bind:WatchAllGames -->
Ticks or unticks **Watch for changes** on every installed game card at once (mixed when only some are ticked). Saved
at once and kept across restarts; it only has an effect while the **Game watcher** switch is on.

<!-- entry: RestartImportWizardCommand -->
**Close** on the import result: hides the result and goes back to the game cards (step 1). The import itself stays in
your profile.

<!-- entry: InstallByojCommand -->
Works only after you ticked the consent box. JoyPro backs up your BYOJ files (Export > Game config backups) and opens
Steam: install the VTOL VR Mod Loader if asked, then press Subscribe on the BYO Joystick page; Steam downloads the
mod. The consent counts once: a second install asks again. Engine: `engine vtolvr mod prepare`.

<!-- entry: CheckByojCommand -->
Looks again whether BYOJoystick is installed (`engine vtolvr mod status`); once it is, builds JoyPro's VTOL VR command
list from the mod. Press it after Steam finished the download. Engine: [[cli:vtolvr refresh-db]].

<!-- entry: OpenByojCommand -->
On the VTOL VR card: opens the BYOJoystick panel with what the mod is, its official source, its size and license, and
what is on this PC already (`engine vtolvr mod status`). Nothing is installed or changed yet.

<!-- entry: OpenSettingsSectionCommand -->
Opens Settings with its section open: from the hidden games note the **Games** section, which lists your hidden
games (**Show again** there brings a game back on the cards, in the export and in the watcher); **Manage** next to Your
devices opens the **Devices** section, where binds move to another device and an old device is removed.

<!-- entry: ReportWatcherProblemCommand -->
Opens Settings > Report a problem with the description started as "Watcher: ...". Describe what the watcher did and
create the report there.

<!-- entry: bind:ImportIncludeDisconnected -->
MSFS import (wizard step 2 and the expert import): also takes the binds of devices that are not connected now. MSFS
keeps the binds of every device it ever saw; off, only the connected devices are imported (`--include-disconnected`).
Remembered with the import and export settings.

<!-- entry: bind:ImportIncludeSimDefaults -->
MSFS import: devices you never changed in the sim run on a preset MSFS ships. JoyPro takes it when the sim records
that it uses it; on, also when that is not known, which can add binds the sim does not have
(`--include-sim-defaults`). Remembered with the import and export settings.

<!-- entry: bind:ImportDcsSameNameAsOne -->
DCS import (on by default): Windows gives a device a new ID now and then. On, the binds of its IDs are read as one
device, on the connected ID (else the newest); two identical devices plugged in at once stay apart
(`--merge-same-name`). Saved in the settings.

<!-- entry: bind:ImportDcsOnlyConnected -->
DCS import: on, devices that are not plugged in now are skipped and counted. Off (the default), they are imported too,
useful for a stick you connect later (`--only-connected`). Saved in the settings.

<!-- entry: bind:ImportIncludeDefaults -->
Expert DCS import and scan (on by default): also takes the binds DCS gives by default to actions you have not bound:
the keyboard keys and DCS's binds for your exact device model, only for aircraft and devices you already have binds
for. Your own binds win; DCS's general joystick defaults are left out (`--include-defaults`). Remembered.

<!-- entry: bind:RegistryGameIncludeKeyboard -->
Expert import and export of the picked game: also reads or writes the keyboard binds of flight actions
(`--include-keyboard`). Off: only device binds. Remembered per game.

<!-- entry: bind:MsfsWriteUserProfiles -->
MSFS 2024 export (on by default): writes the binds into the profile the sim uses for each device and category (Steam
or Microsoft Store), backed up first. Off: JoyPro writes only import files (`--user-profiles false`). The same setting
as on the Export page. In the game: your MSFS 2024 controls profiles hold the binds.

<!-- entry: bind:MsfsAlwaysImportFiles -->
MSFS 2024 export: also writes an import file for every device and category (`--import-files`). Off: import files only
where JoyPro could not write your profile. Import them in MSFS 2024 under Settings > Controls.

<!-- entry: CardPhotoYesCommand -->
**Yes** on a device card: downloads the maker's photo of this device model from the maker's site and shows it on the
card. The answer is kept for this model; Settings > Privacy changes it.

<!-- entry: AlwaysReferencePhotosCommand -->
**Always download**: downloads the offered reference photos now and, from now on, the photos of every device that
appears, in the background. It also allows the photos to be shown (Settings > Privacy switches it off).

<!-- entry: NeverReferencePhotosCommand -->
**No** on the reference photo offer: no photos for these devices, JoyPro's drawings stay. The answer is kept per
device model; Settings > Privacy forgets the answers.

<!-- entry: bind:WatchGameConfigs -->
The game watcher's main switch (on by default, kept across restarts): after a game closes, JoyPro asks whether to take
the binds you changed in the game into your profile. Only games ticked **Watch for changes** are watched; off, none
is. The same as Settings > Games > Watch game configs.
