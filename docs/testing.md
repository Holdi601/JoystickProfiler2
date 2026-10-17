# Testing JoyPro2

Three layers, all scripted, none needs a person or an LLM once started:

| Layer | Command | What it covers | Time |
|---|---|---|---|
| Engine smoke tests | `powershell -File engine\tests\engine_smoke_test.ps1` | every CLI command and game integration on fixtures | ~10 min |
| E2E kit self-test | `powershell -File tools\e2e\SelfTest.ps1` | the safety guard and the image measurements, no game | ~10 s |
| Release smoke test | `powershell -File tools\e2e\RunAll.ps1` | build, both of the above, the offline game matrix and every in-game test | hours |

Telemetry force feedback (docs/ffb-telemetry.md) is tested without moving a stick: `dotnet test
gui\JoyPro.Ffb.Core.Tests` (graph, runtime safety rules, the synthetic flight through the DCS, IL-2 and War Thunder
sources, loopback UDP host, War Thunder HTTP stub) and the smoke part `engine\tests\smoke\ffbtelemetry.ps1` (CLI
settings and game setup on temp copies, `JoyPro.Ffb run --sink dry` against `JoyPro.Ffb simulate` per game, telemetry
timeout, game exit, and a 60 s CPU/memory/handle resource run, ~2 min).

The engine smoke test dot-sources one part per game from `engine\tests\smoke\*.ps1`. The DB refresh parts (IL-2 Great
Battles `il2.ps1`, Elite `elite.ps1`, War Thunder, MSFS, Nuclear Option, Arma Reforger `reforger.ps1`) build small synthetic game files, run the
refresh in a temporary JoyPro folder (`JOYPRO_DATA_DIR`, install set with `JOYPRO_INSTALL_DETECTION=off`) and check
that new actions come in (in `<JOYPRO_DATA_DIR>\GameData`), known titles stay and the shipped catalog and the program
folder stay untouched (IL-2 and Elite also: a dry run and a second refresh write nothing). The whole smoke test runs
with a temporary `JOYPRO_DATA_DIR` from its first command on: the engine copies the program folder's game lists into
`<data>\GameData` on its first start, which must never be the user's `%LOCALAPPDATA%\JoyPro`. `gamedata.ps1` covers that
migration, a refresh into the game-data folder only, a simulated rebuild with older lists (the refreshed ones stay in
use) and a newer program-folder list replacing the game-data copy (the old one kept under `GameData\Replaced`). `legacyids.ps1` loads a profile written before MSFS and X-Plane became one game per
version (`tests\fixtures\legacy-ids\legacy-ids.pr0file`, saved by the engine of commit 70b83ec) and checks the migration
of nodes, tuning scopes and game settings; `xplane.ps1` also covers X-Plane 11 and the install folders of both versions.

The function map (docs/relation-builder.md#function-map) is tested on fixtures by `smoke\functionmap.ps1`: a few DCS,
Star Citizen, War Thunder, Elite and Tiny Combat Arena actions in `tests\fixtures\function-map\DB` replace the game lists
(`JOYPRO_ACTION_INDEX_DIR`), so it needs no real game data; a query table for `relations search` / `resolve` (names,
synonyms, game terms, directions, descriptions, a typo), the map cache, suggestions from the map and Auto Group's map
rows. On the real game lists `tools\relations\check-function-map.ps1` reports coverage, search quality and mapping
precision (not part of the smoke test).

Live progress of long commands (`--status`/`--progress`, engine/EngineProgress.cs): `smoke\progress.ps1` builds a fake
DCS `bin\luae.exe` from `tests\fixtures\fake-luae\FakeLuae.cs` with the .NET Framework `csc.exe` and runs the headless
`dcs refresh id` and `dcs refresh clean` on three modules with four profiles: one `[engine-progress]` line per module,
profile and written file with an increasing count and the right total, none in plain output, only those with
`--progress true`; a cancel through `JOYPRO_CANCEL_FILE` mid-run exits with code 3, stops luae.exe and writes no command
list; a War Thunder `refresh-db` reports its three steps and a cancel leaves its catalog untouched. On the GUI side
`BusyProgressTests` covers the parser, the busy view model (count, bar, estimate, details, cancel), a fake engine that
streams its lines while the view model is watched, and an offscreen render of the busy area that checks that no text
overlaps; with `JOYPRO_BUSY_PREVIEW=<repo>\docs\ui` it also renders the whole window (Start page area and the compact
row above the status line) into `docs\ui\busy-progress-*.png`.

`RunAll.ps1` options: `-Only msfs,dcs` (names in the script's list), `-Offline` (no game is started), `-IdleMinutes 3`
(an in-game test only starts after that long without user input and while no video or full-screen program is in
front, so it never takes the screen from someone using the PC; `Wait-E2EIdle` in E2E.psm1). Results: `<root>\runs\<stamp>-release\summary.md` (root: the `JOYPRO_E2E_ROOT` environment variable, else `C:\Temp\jp2-e2e`; it also holds the vJoy feeder and the MSFS SimVar helper), one folder per test with `run.log`, `results.jsonl` and
screenshots. Exit code = number of failed tests.

The catalog refresh (`engine <game> refresh-db`) is tested on synthetic game files, each in a temporary JoyPro folder
so the shipped catalog stays untouched: War Thunder (`smoke\warthunder.ps1`, vromfs archives), MSFS (`smoke\msfs.ps1`,
presets + locPak; the aircraft list from `tests\fixtures\msfs\aircraft-install`: a 2020 Official package with
`[FLTSIM.n]` titles, AI variant and livery, Community2024 packages (one packed, listed in `layout.json`, one 2024
modular SimObject), a 2024 `Content.xml` with switched-off, livery, AI and shared packages; then `db planes`, a relation
limited to an aircraft (`--node MSFS2024:<action>:<aircraft>`) that export skips without and writes with `--aircraft`), Nuclear Option (`smoke\nuclearoption.ps1`, resources.assets), Tiny Combat Arena (`smoke\tinycombatarena.ps1`: a synthetic `sharedassets1.assets` with the game's newer InputManager layout, plus scan / import / clean / nuke / merge on a fake registry built in the shape the game wrote: player "Player", one map per device and mapper tab, buttons with axisRange 1, a hat element), G-Rebels (`smoke\grebels.ps1`: a synthetic UE 5.8 `SaveOptions.sav` with
UE 5.4+ property tags, built by the script; scan, an in-place export that changes exactly three bytes, import, clean,
nuke and a button in `EnhancedInputUserSettings.sav`), X4 (`smoke\x4.ps1`, .cat/.dat
archives and extracted files), VTOL VR native and BYOJoystick (`smoke\vtolvr.ps1`, level0 and BYOJ_<seat>.xml, plus an
export of the new action/target; there a second refresh must write nothing; then `vtolvr mod status|prepare` and the
targets read from a stand-in `BYOJoystick.dll` the script compiles from `tests\fixtures\vtolvr\byoj-fake` in a fresh
JoyPro folder), Arma 3 without a first start (`smoke\freshinstall.ps1`: a synthetic `Dta\bin.pbo` with a binarized
config and a `languagecore_f.pbo` stringtable), Arma Reforger (`smoke\reforger.ps1`: three
Enfusion PAK archives built by the script, stored and zlib entries, with chimeraInputCommon.conf, keyBindingMenu.conf
and a string table with a continued text; plus a user preset found through `games config set`. Checks the flight
contexts only, ActionRefs, the up/down rows of a stepping action, a whole-axis action, localized and rule titles, known
rows unchanged, the shipped catalog untouched, a dry run and a second refresh writing nothing, and an export / scan of
the new rows with the game's own filter).

MSFS import of the user's own profiles (`smoke\msfsuserprofiles.ps1`, docs/games/msfs.md "Import of your own
profiles"): stand-in user folders (`JOYPRO_TEST_MSFS_HOME`) with Microsoft Store save containers built by the script
(index, container lists, GUID blobs: plain, zlib, an unreadable index), Steam remote folders and JoyPro's package; the
report's layout (Store, no package) runs in a release-like program folder with an empty data folder, so the command list
is built on demand. `gui\JoyPro.Gui.Tests\MsfsProfileImportTests.cs` covers the same with the real engine, and the
journey scenario `msfs-store-import` renders the import wizard (best with a release-like `JOYPRO_JOURNEY_ENGINE`).

IL-2 1946 (`smoke\il21946.ps1`), Cliffs of Dover / Blitz (`smoke\il2cod.ps1`) and FSX / FSX-SE / Prepar3D
(`smoke\fsx.ps1`) run refresh-db in a temporary data root (JoyPro's own DB folders only), then scan, import, a round
trip, merge / merge-overwrite / add / clean / nuke, the game's axis settings and a dry run on fixtures
(`tests\fixtures\il21946`, `il2cod`, `fsx`) with synthetic content. The IL-2 1946 and Cliffs of Dover fixtures have the
shape the installed games write (checked in the games 2026-10-12); the Cliffs of Dover part also checks Blitz's device
ids (hash of the interface path, `devices.tsv` carries the paths), an older id of a device being rewritten, and the
process guard (a `Launcher.exe` outside the game's folder, as Falcon BMS has one, does not block an export). FSX was
written without the game; see its page.

The game config watcher ([sync-watcher](sync-watcher.md)) is tested by `smoke\sync.ps1` for DCS (diff.lua), War Thunder
(machine.blk) and Star Citizen (actionmaps.xml): export, status unchanged, the file changed as the game would (a bind on
an input two relations share, an axis tuning, a removed bind, and float / order / whitespace changes that must not
count), diff with kinds and candidates, apply (existing relation, new relation, ignored), status unchanged and an empty
diff afterwards. War Thunder also covers a game-wide setting (`changed-setting`), the config exported to a custom path
in `sync status` and a disabled game; Nuclear Option (registry storage through its fake-registry .json) a bind moved to
another button; IL-2 Great Battles a new bind and the force feedback power in current.devices; Arma 3, WARDOGS, X4
(inputmap.xml and config.xml), Arma Reforger and VTOL VR native a game-wide setting each (settings-only export, an
empty diff right after it, the value changed as the game writes it, apply). Its resource use (1000 writes, 200 re-creations, idle CPU, process wait, registry notification) is
checked by `gui\JoyPro.Gui.Tests\GameConfigWatcherTests.cs`.

Autosave and the profile history ([profile-history](profile-history.md)): `smoke\history.ps1` runs the engine on temp
profiles in its own `JOYPRO_DATA_DIR` (versions with what changed and `--history-note`, no temp file left by the atomic
save, show / diff / undo / redo across runs, a change outside JoyPro kept as a version, restore and its undo, keep,
checkpoint, prune, usage, a version before a DCS export, `JOYPRO_PROFILE_HISTORY=off`, and the cost on a 450-relation
profile); `gui\JoyPro.Gui.Tests\ProfileHistoryTests.cs` covers the debounce, flush and atomic writes, the store with
simulated timestamps for the retention, the size caps, kept versions, a 3 MB profile, auto-open and the view model's
undo, History panel, drafts and profile switch. GUI tests set `JOYPRO_PROFILES_DIR` so "New profile" never writes to
Documents.

## In-game tests (`tools\e2e\games\*.ps1`)

Each script exports random binds and settings per run (`choices.json`), so only this run's export can pass (cloud copies
or caches cannot fake a result). It snapshots the game's files first and restores them at the end, starts the game,
checks in the game's own options menus that the binds and values show, closes the game and checks what it wrote back
(JoyPro imports the file the game rewrote). The smoke run never needs a flight: export -> options menu and game file
-> import are what it tests. Measuring what the game makes of an axis in a flight (SimConnect, X-Plane Web API, shared
memory, image shift) is a calibration of JoyPro's curve previews, run on demand with `-Flight` (MSFS, BMS) or the
`flight` stage (Elite, X4).

The watcher is also checked in the games themselves: a value is changed in the game's own menu, the game writes its
file, and after it closed `engine sync status` must say "changed", `sync diff` must report exactly that change,
`sync apply` must put it into the profile and a following diff must be empty (always before the snapshot comes back).
DCS goes through the running GUI as well: with the run's data folder and profile the GUI must offer the change in its
dialog at start ("changed while JoyPro was closed"), and Take over must put it into the relation. The GUI keeps its own
settings in `<JOYPRO_DATA_DIR>\settings` for that, never in the user's `%APPDATA%\JoyPro2`.

| Game | Changed in the game for the watcher check |
|---|---|
| DCS | the Pitch dead zone typed into Axis Tune (DCS saves it as a "changed" entry); GUI dialog and Take over |
| X4 | Auto-Roll (Settings > Game Settings, config.xml) |
| WARDOGS | Self-Centering Collective (its toggle; leaving the settings answers APPLY) |
| Elite Dangerous | the Roll Axis invert (a click on its value, APPLY): an axis tuning change |
| Arma 3 | the Y dead zone slider in Customize (dragged; Customize > OK also copies the + sensitivity to both halves) |
| War Thunder | WEP throttle hold on the Controls page (a game-wide setting the game drops at its default) |

| Game | Script | In-game checks |
|---|---|---|
| DCS | `DCS.ps1`, `DCSCurves.ps1` | binds in Options > Controls, one bind made in the game, Axis Tune values and live In/Out curve, the FF Tune panel (trimmer, shake, swap/invert) |
| IL-2 GB / Korea | `IL2Game.ps1`, `IL2GameCurves.ps1`, `IL2Korea*.ps1` | key mapping, a bind made in the game, response curves |
| IL-2 1946 | `IL21946.ps1` | vJoy button, hat and axis bound on the Controls page and a dead band / curve point set on the Input page (files, scan and import checked); exported random button, hat, axis and response shown on both pages; the game's rewrite imported |
| Elite Dangerous | `Elite.ps1`, `EliteSettings.ps1` | binds, axis settings (stage `flight`: pitch direction, image shift) |
| X4 | `X4.ps1`, `X4Settings.ps1` | binds, axis settings (stage `flight`: pitch direction, image shift) |
| WARDOGS | `Wardogs.ps1`, `WardogsSettings.ps1` | HOTAS binds and axis settings |
| X-Plane 12 | `XPlaneCurves.ps1` | main menu only: Settings > Joystick > vJoy shows each axis' use, Edit Response Curve the exported stops and interpolation, and its live "Current: in -> out" readout at fixed vJoy positions matches JoyPro's preview; the curves survive X-Plane's rewrite on quit (33/33 on 2026-10-11). `-Flight`: the old calibration through the Web API in a loaded flight |
| Arma 3 / Arma 2 | `Arma3Settings.ps1`, `Arma2Settings.ps1` | controller binds and axis curves |
| Falcon BMS | `BmsSettings.ps1` | launcher output, Setup > Controllers > Advanced (device, dead zone, saturation); `-Flight`: cockpit sweeps |
| MSFS 2020 | `MsfsCurves.ps1` | Controls list (axes and random buttons), Sensitivity page; `-Flight`: buttons change their SimConnect variable, axis curves match JoyPro's preview |
| MSFS 2024 | `Msfs2024Settings.ps1` | JoyPro's Community2024 preset package; Controls of the test aircraft (Cessna 185): random axes and buttons on vJoy, the per-action curve override ticked, and its Tweak action curve page shows the run's random sensitivity +/-, neutral, dead zone and extremity dead zone (26/26 on 2026-10-11; the page has no reactivity row) |
| Star Citizen | `StarCitizenSettings.ps1` | Advanced Controls Customisation (main menu only, EasyAntiCheat) |
| War Thunder | `WarThunderSettings.ps1` | vJoy binds, Axis setup dialogs (pitch, throttle) and Controls page values; restores the save folder with a raised sync version |
| Project Wingman | `ProjectWingmanSettings.ps1` | Controls > KEY BINDINGS / AXIS BINDINGS show the exported vJoy buttons and axes (slot crops read by OCR) and the Invert box, CURVES + DEADZONES Pitch's curve and dead zone; View Padlock X bound in the game; import after exit; Config and SaveGames restored |
| ACE COMBAT 7 | `AceCombat7Settings.ps1` | no input to the game: export into the vJoy section, start to the title, WM_CLOSE; the game's rewrite of Input.ini keeps every value; import after exit |
| Nuclear Option | `NuclearOptionSettings.ps1` | Control Mapper vJoy tab: element ids of Pitch/Roll/Yaw; import after exit; registry snapshot/restore |
| G-Rebels | `GRebelsSettings.ps1` | two starts: vJoy gets channel 0 in INPUT > DEVICES and JST0 is opened once; then JoyPro's random axis roles (Pitch/Roll/Yaw) and Primary Fire button show on the JST0 tab; import after exit; SaveGames restored |
| Hell Let Loose: Vietnam (virtual pad) | `HllVietnamXbox.ps1` | JoyPro's xbox mode with vJoy as the HOTAS stand-in: planner pins of layout A, XInput, the pad's A passes the title, B opens QUIT GAME, D-pad right moves the focus, A dismisses; menus only (EasyAntiCheat); vJoy/HidHide unchanged |
| ACE COMBAT 8 (virtual pad) | `AceCombat8Xbox.ps1` | the same pad chain: A passes the title and opens SYSTEM > CONTROLS > CONTROLLER, the stick moves the focus, A opens BUTTON MAPPING, B goes back; menus only (EasyAntiCheat) |
| Tiny Combat Arena | `TinyCombatArenaSettings.ps1` | Control Mapper vJoy tab, three map categories: random axes (Pitch/Roll/Yaw), buttons (Fire Gun on Weapons, Cockpit on View Select) and a hat direction (Throttle Up/Down); Throttle Axis bound in the game; import after exit reads all seven; registry snapshot/restore |

### Config watcher, live (`tools\e2e\games\Watchdog.ps1`)

The full watcher journey with the built GUI running, as a user has it: JoyPro2 is open (or only its tray host runs),
the game is started, a control is changed in the game's own controls menu, the game is closed, and the dialog must come
up by itself. The GUI is `gui\JoyPro.Gui\bin\Debug\net9.0\JoyPro.Gui.exe` of the checkout (build it first with
`-p:AutoStopRunningJoyProGuiOnBuild=false`), with the run's own data folder (`JOYPRO_DATA_DIR = <run>\jpdata`, settings
in `<run>\jpdata\settings`: the test profile opens at start, only the game under test is ticked), never the user's
JoyPro2.

```
powershell -ExecutionPolicy Bypass -File tools\e2e\games\Watchdog.ps1 -Game DCS
powershell -ExecutionPolicy Bypass -File tools\e2e\games\Watchdog.ps1 -Game Simulated   # no game, ~3 min
```

`-Game`: `Simulated` (a stand-in `DCS.exe` = a copy of ping.exe and a DCS Saved Games folder in the run folder; checks
the kit and the GUI without a game), `DCS`, `IL2Game`, `IL2Korea`, `Wardogs`, `XPlane12`, `NuclearOption`.
`-Sessions change|nochange|tray` (default all), `-RunDir` (default `E:\JoyProWork\watchdog\<game>\<stamp>`), `-NoLock`.
Each adapter uses the game's own e2e script for prepare (snapshot + export of the test binds), launch and restore, so
the game files come back as they were; DCS runs with its own write folder (`-w JoyProE2E`), the user's
`Saved Games\DCS\Config` is only hashed before and after. Games start one at a time under `E:\JoyProWork\game-lock.txt`
(created exclusively; a lock younger than 90 min or a running game means wait).

Sessions:

1. **change** (GUI open): no dialog while the game runs (the GUI log shows the watcher waiting for the game); after the
   exit the GUI offers the change (`offering N change(s)` in `<run>\jpdata\Logs\gui-*.log`, seconds after the exit), a
   desktop screenshot shows what the user sees, the dialog names the game and the change (OCR); **Ignore these** leaves
   the profile file byte-identical and `sync status` unchanged; the sync records and profile from before the answer
   are put back and a new GUI start offers the same change again ("changed while JoyPro was closed"); **Take over**
   puts it into the profile (`binds list` or the change's tuning) and `sync diff` is empty afterwards.
2. **nochange** (GUI open): the game is started and closed again (DCS: Options > Controls left with OK): no dialog,
   the log says why (content unchanged / nothing JoyPro manages), the profile is unchanged.
3. **tray** (DCS and the stand-in: a second, different change is needed): GUI closed, `tray\JoyPro.Tray.exe --tray`
   watches; 60 s idle (CPU, working set, handles), the second change in the game, the tray log says `notification
   shown`, the Windows notification is on a screenshot of the screen; `JoyPro.Gui.exe --review <game>|<config>` (what
   a click on it starts) shows the dialog; Take over; the tray host pauses while the GUI runs.

`-Simulate` (adapters with a Sim block: IL-2 GB, WARDOGS, X-Plane 12, Nuclear Option, IL-2 Korea) does not start the
game: a stand-in process with the game's process name runs, and "the game" rewrites the watched config (files or
registry) with JoyPro's own export of the profile plus one bind, prepared before with a scratch data folder so that
export is not recorded. The GUI, tray host, watcher and engine are the real ones; only the game is not.

Results of 2026-10-17 (run folders `E:\JoyProWork\watchdog\<game>\<run>`):

| Game | Run | Result |
|---|---|---|
| Stand-in (Simulated) | run7 | 43/43 (on the portrait 1440x2560 screen) |
| DCS | run1 | 38/39 in game: pop-up 3.6 s after DCS exited, Ignore, re-offer at start, Take over, no dialog without a change, tray notification + Review |
| IL-2 Great Battles | run3, sim6 | 27/29 in game (the misses: OCR of the dialog title, matched more loosely since); 45/45 simulated |
| WARDOGS | sim4 | 45/45 simulated; in game a season-end screen with in-game cash stopped the guard (not dismissed by the test) |
| Nuclear Option | sim5 | 45/45 simulated; in game Steam's launch option question waited (the kit answers it now with the preselected Play) |
| IL-2 Korea | - | not possible: the first start shows the user agreement; no saved key mapping (general.actions) to export to |
| X-Plane 12 | - | not run: never started on this PC (first start = tutorial flight, no preferences); the kit seeds it through the Web API now |
| MSFS 2024 | run3 | reached Controls, the rebind was not captured (two clicks needed, fixed); later runs: the screen was switched to a portrait 1440x2560 display |

Resources (Release-like Debug build): tray host idle 0 ms CPU in 60 s, 12-19 MB working set, handles 359 -> 357; over a
whole game session 0.2-11 ms/s. The GUI over a game session 10-27 ms/s while the game is in front (its window is
covered), up to 190 ms/s right after its own start; its handles fall back after each session (no leak).

Every step leaves screenshots (`shot-*.png`) in the run folder; `watchdog.md` / `watchdog.json` list the checks and the
resource numbers (GUI and tray host: CPU ms, working set, private bytes, handles, threads per session), `run.log` has
every PASS/FAIL line including the game script's own.

## Safety rules of the kit (`tools\e2e\E2E.psm1`)

- **No purchases.** Every mouse button and key press of every script goes through `E2EWin`, whose guard reads the
  foreground window (OCR) first. A store, price, wishlist, checkout or payment screen stops the run for good
  (`SAFETY-STOP.png`, no further input in that process). Lines a game always shows on its menu (IL-2's news panel) are
  excluded per script with `Set-E2EGuardIgnore`.
- **No EULAs.** A user agreement or license agreement on screen ("USER AGREEMENT", "ACCEPT AND CONTINUE", "End User
  License") stops the run the same way; a test never accepts one. Steam's launch option question is answered with the
  preselected first option (`Invoke-E2ESteamLaunchOption`, "Always use this option" stays unticked).
- **No input to other programs.** Input only goes to the processes a script got with `Get-GameWindow`; with another
  program in front the game is brought back or the input refused.
- **No blind clicks.** `Invoke-E2EStep` acts only when the expected page is on screen, clicks a text found on it, and
  waits for the page it should open. A click next to a shop entry (offers, cart, store, marketplace) is refused.
  MSFS takes a first click as "select" only: retries click again, never press Enter (the focused element is unknown).
- **Game files**: snapshot first, restored at the end (also when a step fails).
- **vJoy buttons** only where they cannot navigate menus (MSFS: only in a flight).

`SelfTest.ps1` checks the guard (drawn store and settings pages), the input refusal and the image measurements.

## Measured game facts from these tests

- MSFS 2020 Sensitivity curve: `docs/games/msfs.md` (preview section).
- The facts each script relies on (menu layouts, quirks) are in its header comment.
