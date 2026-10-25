# Game config watcher

When you change controls in a game that JoyPro manages, JoyPro asks after the game has closed whether to take the
changes into your profile. Where several relations use that button (same device, input and modifiers), it asks which
relation the change belongs to, or whether to make a new one. Only what JoyPro manages counts.

Settings > Games > **Watch game configs** (on by default) switches it off; then nothing is watched at all. Per game, the
Start page's game cards have a **Watch for changes** box, see [Which games are watched](#which-games-are-watched).

The GUI watches while it is open. To keep watching after it closed (or from Windows sign-in on), a tiny tray host takes
over (Settings > Run in the background): **Start with Windows (in the tray)** and **Close to tray**, see
[Tray and autostart](#tray-and-autostart).

## How it works

1. **Every JoyPro export records the game's state.** Right after a successful export (DCS, IL-2, Star Citizen and every
   registry game, also exports that only write game-wide settings) the engine stores a fingerprint of the files (or
   registry values) JoyPro manages in `<data>\Sync\<game>.json` (`<data>` = `%LOCALAPPDATA%\JoyPro2`, or
   `JOYPRO_DATA_DIR`): size, write time and SHA-256 per file, and a *baseline*, i.e. hashes of every bind (plane, action,
   device, input, modifiers) and axis tuning the file holds, read with the game's own import. JoyPro's own exports
   therefore never ask anything. A file Windows refuses to read (a Microsoft Store install keeps its data EFS-encrypted
   and refuses a file without the EFS mark) is stamped by size and write time instead of its hash; it used to
   break the record, and MSFS 2024 stayed untracked (2.2.6 report). An import of a game that has no record yet records
   it too, so a game you only imported is watched from then on. Only games JoyPro exported to or imported from (or whose state was accepted) are watched, every config
   it exported to (also one exported to a custom path, and the config location you set with `engine games config set`),
   and never a game you marked as one you do not have (`engine games install disable`) or unticked on the Start page
   ([Which games are watched](#which-games-are-watched)).
2. **The GUI (or, while it is closed, the tray host) watches those files** (`gui\JoyPro.Watch\GameConfigWatcher.cs`): one `FileSystemWatcher` per config folder,
   filtered to the managed file names (LastWrite, Size, FileName), or a registry change notification for games that
   keep their controls in the registry. A change starts a 2 s debounce (a game's `SettleSeconds` from `sync status`
   when it needs longer: the MSFS Microsoft Store save folder 10 s).
3. **After the game has closed.** When the debounce ends and one of the game's processes runs (by process name, or any
   exe under the game's `ProcessFolders`: MSFS Store `XboxGames\<game>\Content\`, `WindowsApps\<family>_`), the watcher
   waits for its `Exited` event (no polling; only when Windows refuses that wait it looks again every 10 s), then waits
   3 s more (games write their config while closing; `SettleSeconds` when longer).
4. **What changed?** `engine sync status --game <id>` compares the fingerprint (a file whose content is unchanged is no
   change, even with a new write time). If it changed, `engine sync diff --game <id>` reads the config into a scratch
   profile (the loaded profile is never touched) and compares it with the profile. No changes (only formatting, or only
   things JoyPro does not manage): the new state is accepted silently. Otherwise a dialog lists the changes.
5. **The dialog**: one line per change with a tick box and a choice: one of the candidate relations, *(new relation)*
   with an editable name, or *(ignore)*. It comes with a notice in the window ("A game changed its controls", an
   information, not a warning); when JoyPro's window is not the active one (the user is on the desktop or already in
   another program), its taskbar button flashes until the user switches to it. The focus is never taken. **Take over** runs `engine sync apply` (the profile is saved), **Not now** keeps
   the changes for later (JoyPro asks again after the next change or start), **Ignore these** runs `engine sync accept`
   (the profile stays; the next export writes it over the game's changes).
6. Changes made while JoyPro was closed are offered at the next start (once the game is not running).
7. With the GUI closed, the tray host checks the same way and shows a notification ("A game changed its controls");
   clicking it (or the tray menu's Review entry) opens JoyPro2 with this dialog. See [Tray host](#tray-host).

## Which games are watched

Every game card on the Start page ("Your games") has a **Watch for changes** box ("JoyPro notices when you change binds
in this game and offers to take them over"); the section header has **Watch all** (ticked: all installed games are
watched, empty: none, mixed: some; a click on a mixed box unticks all).

- **Default**: ticked for every installed game; a game JoyPro does not find has the box unticked and greyed (nothing
  to watch).
- **Only explicit choices are stored**, so a game installed later is ticked by itself, and a game the user unticked
  stays unticked, also after it was uninstalled and installed again. Ticking it again stores "watched".
- **Where**: `ui-settings.json` next to `watchGameConfigs` (`%APPDATA%\JoyPro2`, tests `<JOYPRO_DATA_DIR>\settings`):
  `"watchedGames": { "WarThunder": false, "DCS": true }`, keyed by the game ids of `engine games installs` /
  `engine sync status`. The GUI writes it at each tick; the tray host reads it (and keeps it when its own Watching
  switch rewrites `watchGameConfigs`). Code: `gui\JoyPro.Watch\GameWatchChoices.cs`.
- **Not "I do not have this game"**: that is `games-disabled.json` of the engine, which hides the game everywhere and
  blocks import and export. An unwatched game keeps its card and still imports and exports; only the watcher leaves
  it alone. The engine is not involved (it never watches), so there is no engine command for it.
- **The master switch** (Settings > Games > Watch game configs, also the Start page's Game watcher switch and the tray
  menu) still wins: off, nothing is watched whatever the ticks say; the ticks can still be changed and apply when it
  is on again (the Start page says so above the cards).
- **Live**: the GUI applies a tick at once without asking the engine again: it filters the last `sync status`
  (`GameWatchChoices.Filter`) and hands the result to `GameConfigWatcher.Update`, which releases the unticked game's
  `FileSystemWatcher`s / registry wait at once and creates them for a ticked one. A game ticked again is checked once
  for what it changed while it was not watched (after it has closed, as always). Settings > Games shows "Not watched
  (unticked on the Start page): ...". The tray host is paused while the GUI runs (the only place to tick) and reads
  the ticks again whenever the GUI closes, so it never needs a restart either; a late event of a game unticked
  meanwhile is dropped by both.

## Tray and autostart

The watcher only works while something runs. After the GUI closes, a separate small process keeps watching: the
**tray host** (`<install>\tray\JoyPro.Tray.exe`, WinForms `NotifyIcon`, no Avalonia, no render loop). The GUI itself
always ends when its window closes, so its memory goes back to Windows.

Settings > **Run in the background (system tray)**:

| Setting | Default | What it does |
|---|---|---|
| Start with Windows (in the tray) | off | writes the value `JoyPro2` = `"<install>\tray\JoyPro.Tray.exe" --tray` to `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` (per user, no admin); unticking removes it. Ticking it also ticks Close to tray. |
| Close to tray | off | the tray host runs while the GUI is open (paused, its icon is there) and goes on watching after the GUI closed; off: closing the GUI ends the watching too (a tray host started with Windows ends with the GUI) |

Both are stored in `ui-settings.json` (`startWithWindows`, `closeToTray`; `%APPDATA%\JoyPro2`, tests
`<JOYPRO_DATA_DIR>\settings`). The tray host reads `watchGameConfigs`, `watchedGames`, `closeToTray` and `lastProfilePath` there and
writes only `watchGameConfigs` (its menu switch, only while the GUI is closed), keeping every other field. The Run entry
is the tray host next to the running GUI, so a portable copy starts from where it is. At every GUI start with the
setting on, the entry is written again when it is missing, starts a JoyPro2 that no longer exists (a moved portable
folder) or starts `JoyPro.Gui.exe` (the first tray mode); an entry for another existing install's tray host is left
alone (tick the box again to switch to this copy). If Windows' own Settings > Apps > Startup has JoyPro2 switched off
(`StartupApproved\Run`), the setting says so; JoyPro2 does not change Windows' switch. Code:
`gui\JoyPro.Gui\Services\AutostartService.cs` (`IRunKeyStore`; the tests use an in-memory store, set in
`TestEnvironment`, and check that the real key is unchanged).

### Tray host

`gui\JoyPro.Tray` (the exe, a thin WinForms shell) and `gui\JoyPro.Watch` (net9.0 library shared with the GUI:
`GameConfigWatcher`, `SyncOutput` for the engine's sync JSON, `TrayWatcherHost` with the logic, `GuiPresence`,
`SingleInstanceGuard`, `UiSettingsFileStore`). The GUI builds it and copies it to `<output>\tray\` (publish:
`<publish>\tray\`).

- **Watching**: `engine sync status` (with the GUI's common options) lists the configs, of which it watches the games
  ticked on the Start page (`watchedGames`); the same `GameConfigWatcher` as the GUI. At its own start it offers what
  changed while nothing ran.
- **After a change** (the game has closed): `sync status --game <id> --path <config>`, then `--profile <last profile>
  sync diff`. No change JoyPro manages: `sync accept`, silently. Otherwise a balloon notification (Windows shows it as a
  toast; it never takes the focus): "A game changed its controls - DCS World changed 2 controls JoyPro manages. Click
  here to take them over or ignore them in JoyPro2.", and the menu gets "Review the changes made in DCS World (2)".
  Without a last profile nothing is asked (as in the GUI; the config stays changed for later).
- **Review** (the notification, the menu entry, or a left click while changes wait): starts `JoyPro.Gui.exe --review
  <game>|<config> ...`. The GUI loads the last profile if none is loaded and shows the existing dialog (Take over / Not
  now / Ignore these). A GUI that already runs gets the list through `<settings>\gui-request.txt` and its activation
  event, and comes to the front.
- **Menu**: *Open JoyPro2* (also a left click), *Review ...*, *Watching game configs* (checked = on; greyed while the
  GUI is open), *Exit*.
- **Log**: `<data>\Logs\tray-<date>.log` ([logging](logging.md)) says what it decided after a game closed
  (`notification shown`, `accepted without asking`, `content is unchanged`) and when it paused for the GUI; the GUI
  logs the same decisions (`offering N change(s) in the dialog`, `took over`, `ignored`) in its own log. The
  notification's sender is "JoyPro2" (the tray exe's file description).
- **Hand-over, no double prompts**: the GUI announces itself after it took its single-instance mutex (writes
  `<settings>\gui.pid`, sets the event `Local\JoyPro2.Gui.<hash>.Started`). The tray host waits for that event on the
  thread pool, then disposes its watcher and forgets its pending reviews (the GUI watches and offers them itself), and
  follows the GUI process's `Exited` event. When the GUI ends it reloads the settings: Close to tray on -> watch again
  (without offering again what the user postponed with "Not now"), off -> the tray host ends.
- **One tray host** per user and settings folder (`Local\JoyPro2.Tray.<hash>.Running`); a second start ends at once.
  The GUI starts it (through `ExternalProcess`, never in the tests) when Close to tray is ticked, at GUI start with Close
  to tray on and when the GUI closes with it on (not when an update closes the GUI; the update script stops the tray
  host, the restarted GUI starts it again).
- **Usage recording**: while it watches, the tray host also catches up with the games' logs (`engine usage harvest`),
  follows them live and harvests after a game closed ([usage statistics](usage-statistics.md#live-tail)); it hands this
  over to the GUI like the watcher.
- **Small**: workstation GC without the background GC thread, no tiered PGO; after startup and after each check one
  compacting GC and `SetProcessWorkingSetSize(-1, -1)` give the pages back. No timer, no polling.
- **GUI**: one GUI per user (mutex `Local\JoyPro2.Gui.<hash>.Running`, event `...Activate`); a second start brings the
  running one to the front and hands over its `--review` list. `JoyPro.Gui.exe --tray` (the first tray mode's Run
  entry) starts the tray host and ends. A test run (`JOYPRO_DATA_DIR`) has its own names and never meets the user's
  JoyPro2. Code: `gui\JoyPro.Gui\AppShell.cs`, `Services\TrayHostLauncher.cs`, `ViewModels\MainWindowViewModel.Tray.cs`.

Tests: `gui\JoyPro.Gui.Tests\TrayHostTests.cs` (the tray host with fakes: offering what changed meanwhile, one
notification, silent accept, no profile, Review / Open arguments, pause while the GUI runs and resume without asking
again, ending with Close to tray off, the Watching switch, unsubscribing), `TrayHostResourceTests` (the real watcher:
1000 writes -> 1 notification, handles 630 -> 630, managed heap +0.8 MB; 200 GUI start/exit hand-overs: handles
630 -> 633), `TrayModeTests.cs` (Run command and repair, settings shared through ui-settings.json, `--review` / `--tray`,
the review hand-over, single instance, `GuiPresence` with a real mutex and event, the sync output parser).

Measured 2026-10-11, Release build of `JoyPro.Tray.exe --tray` with its own `JOYPRO_DATA_DIR` and five watched games
(DCS, IL-2, War Thunder, X-Plane, Falcon BMS; 5 config folders):

| | Tray host | (the GUI kept in the tray, previous version) |
|---|---|---|
| idle CPU, 60 s | **0 ms** (0 ms/s) | 7-30 ms/s (Avalonia's compositor) |
| working set after start | 11-12.5 MB | ~156 MB |
| private bytes | 13 MB | ~130 MB |
| handles / threads | 358 -> 354 over 60 s / 21 | 627-651 / 58 |
| second start | ends after 101 ms | |

A process standing in for the GUI (its mutex, pid file and event): the tray host's handles 354 -> 349 while it ran
(watchers released), 357 after it ended (watching again); with Close to tray off the tray host ended itself.

## What counts as a change

Only binds and axis tuning JoyPro manages:

- devices the profile uses (a device the profile never used is not JoyPro's), the keyboard only when the profile has
  keyboard binds for that game (the export rules, see [export](export.md));
- actions JoyPro writes: registry games their catalog (`DB\<Game>\actions.tsv`), Star Citizen the ship action maps;
- DCS: only the aircraft whose files changed (`Config\Input\<aircraft>\...`);
- values are compared as the game uses them, rounded to 4 decimals, after the game's own import: Star Citizen writing
  0.21 as 0.20999999, DCS writing 0.2 as 0.20000000298023, War Thunder reordering keys or writing machine.blk as a diff
  against its preset, other whitespace or element spelling are no change;
- the baseline makes the diff report what the *game* changed: a bind JoyPro could not write (or one you added in
  JoyPro since the export) is not reported as removed, and a bind the file already had is not reported as new;
- DCS: what counts is the device's defaults plus its diff.lua, not the diff alone. DCS saves the diff against the
  defaults of the device type (e.g. the T-Pendular-Rudder has its rudder on JOY_Z by default) and drops a bind equal to
  such a default; JoyPro's .jp copy keeps it, and it stays active unless the diff has a "removed" entry for it. The
  defaults are the ones of the DCS list refresh: `CleanProfile\DCS\<plane>\<device type>.cf` for a type with defaults
  of its own, else the plane's generic `<plane>.cf` (smoke: `engine\tests\smoke\sync_dcsdefaults.ps1`).

| Kind | Meaning | Choices |
|---|---|---|
| `added` | the game binds an action to a device/input/modifiers the profile does not have for it | a relation that binds that input in this game (or holds the action), a new relation, ignore |
| `removed` | a bind the profile has (and the file had) is gone | the relation that holds it (the action leaves it; DCS: only that aircraft), ignore |
| `changed-tuning` | an axis' dead zone, saturation, curve, invert or game parameter changed | the relation (gets the value as its game tuning; DCS: the aircraft's, or the base when the relation only flies that aircraft), a new relation (the axis moves there), ignore |
| `changed-modifiers` | same action and input, other modifiers | the relation (its modifiers change for all its games), another relation with these modifiers or a new one (the action moves), ignore |
| `changed-setting` | a game-wide setting the profile holds for the game (`ffb set --device *`) has another value in the game | take over (the value becomes the profile's setting for the game), ignore |

A relation that is rebound by a choice ("now bound to ...") changes for every game it has, as everywhere in JoyPro.
Game-wide settings count only for the keys the profile holds for the game (those JoyPro writes), compared rounded to 4
decimals and against the baseline (IL-2 writes 0.333 as 0.33: no change). They are read from: War Thunder `settings{}`
and the other blocks of machine.blk, Falcon BMS (`Falcon BMS User.cfg`, `Feedback.ini`, the pilot file), Star Citizen
`attributes.xml`, IL-2 `data\input\current.devices` (then `data\startup.cfg`), X-Plane's Joysticks.prf, Elite's
.binds, the Arma 3 profile (RTD options, vibrations; Arma 2 has none), WARDOGS `GameUserSettings.ini`
(`[/Script/WDGame.WDUserSettings]`), X4 `inputmap.xml` `<config>` and `config.xml` next to it, Arma Reforger's
`ReforgerGameSettings.conf` / `GameUserSettings.conf` (the one the game saved last wins) and VTOL VR's `gameSettings.cfg`
next to `inputbindings.cfg` (settings of game `VTOLVR`, watched with `VTOLVRNative`).

## Games and what is watched

| Game | Watched | Processes |
|---|---|---|
| DCS | `Saved Games\DCS\Config\Input`, `*.diff.lua` and `modifiers.lua`, with subfolders | `DCS` |
| IL-2 Great Battles | `data\input`: `global.actions`, `devices.txt`, `current.responses`, `current.devices`; `data\startup.cfg` | `Il-2` |
| Star Citizen | the profile's `actionmaps.xml` and `attributes.xml` | `StarCitizen` |
| Registry games with a file (War Thunder `machine.blk`, X-Plane `.prf`, Elite `.binds`, X4 `inputmap.xml`, Arma profiles, Reforger `.conf`, WARDOGS ini and sav, IL-2 Korea) | that file, plus the files that hold its game-wide settings (`IGameSettingsSource`): X4 `config.xml`, Reforger's game settings in each `app*_user*\settings`, VTOL VR native `gameSettings.cfg` | the integration's `ProcessNames` (e.g. `aces`, `X-Plane`, `EliteDangerous64`, `X4`) |
| Registry games with a folder | Falcon BMS `User\Config\Setup.v100.*.xml`, `Falcon BMS User.cfg`, `Feedback.ini`, `*.pop`, VTOL VR `SaveData\BYOJ_*.xml`, MSFS profile folders (all files) | as above |
| MSFS 2020 / 2024 | JoyPro's package (2024) or the Steam remote folder, `inputprofile_*` of the Steam remote folders, the Store save folder's `containers.index` with subfolders (10 s settle) | `FlightSimulator2024` / `FlightSimulator`, or any exe in the Store install folders |
| Nuclear Option | `HKCU\Software\Shockfront\NuclearOption` (registry notification); the fingerprint covers Player0's Rewired controller maps only | `NuclearOption` |

JoyPro's own files next to the game's (`.jp`, `.joypro-meta.json`, `.joypro.json`, `.jpbak`) are never part of the
fingerprint. Status uses the config path discovery the export uses (with your config location from `engine games config
set` first) and adds every other config JoyPro recorded for the game (an export to a custom `--path`); the GUI watches
each of them. A disabled game shows `State: disabled` and has nothing to watch.

## CLI

```
engine sync status [--game <id>] [--path <config>]
engine sync accept --game <id> [--path <config>]
engine --profile <p> sync diff --game <id> [--path <config>]
engine --profile <p> sync apply --game <id> --choices <file.json> [--path <config>]
```

- `status` JSON: `{ DataFolder, Games: [{ Game, DisplayName, Installed, ConfigPath, Kind (files|registry), State
  (unchanged|changed|untracked|missing|not-installed|disabled), Disabled, Tracked, Changed, RecordedUtc, Reason (export|accept|apply),
  Fingerprint, Files, ChangedFiles, ChangedPlanes (DCS), Watch: [{ Directory, Filters, IncludeSubdirectories }],
  RegistryKey, ProcessNames, ProcessFolders (image path prefixes that count as the game), SettleSeconds (0: the
  watcher's default), Warnings }] }`. Tests: `JOYPRO_TEST_GAME_PROCESSES=MSFS2024=<name>[,<name>];<game>=...` replaces
  the reported process names (`MSFS2024=` for none), so a stand-in process (a copy of `PING.EXE`) plays the game and a
  game the developer plays meanwhile does not take part; with `JOYPRO_TEST_MSFS_HOME` the MSFS `ProcessFolders` are
  `<home>\XboxGames\<game>\Content\` only. Hashing only happens for files whose size or write time differ from the
  record (about 150 ms for every game on a PC with 18 games installed).
- `diff` JSON: `{ Game, DisplayName, ConfigPath, Tracked, Baseline, Planes, ProfileBinds, FileBinds, Changes: [{ Id,
  Kind, Game, Plane, ActionId, ActionTitle, IsAxis, Device, Input, Modifiers, ModifierStrings, OldModifiers, OldTuning,
  NewTuning, Relations, Candidates, Suggested, NewRelationName, SettingKey, SettingLabel, OldValue, NewValue,
  Description }], Warnings, Errors }` (OldValue / NewValue only for `changed-setting`). Without `--path` and `--game`
  status may list a game more than once, one entry per config. `Candidates`: the
  relations that hold the bind (removed / changed), then those that bind the device + input + modifiers in this game,
  then those that hold the action. `Suggested`: the relation to preselect, `new` or `ignore`. The ids are stable as
  long as the file and the profile do not change.
- `apply --choices`: `{ "Choices": [ { "Id": "c...", "Target": "<relation>" | "new" | "apply" (settings) | "ignore", "Name": "<new
  relation name>" } ] }` (or the bare list). Changes not listed are ignored. Afterwards the config counts as known, so
  ignored changes are not offered again. Output: `{ Game, ConfigPath, Applied, Ignored, NewRelations,
  ChangedRelations, Fingerprint, Steps, Warnings }`.
- `accept`: the current state counts as known; nothing is imported.

The whole journey in the games themselves (GUI or tray host running, a bind changed in the game's controls menu, the
dialog after the exit, Ignore / Take over, no dialog without a change) is tested in the games before a release.

## Resources

The watcher must never cost anything noticeable:

- no polling and no periodic timer: file events (overlapped I/O in the kernel), one registry wait on the thread pool,
  a one-shot debounce timer per game that only runs after a change, `Process.Exited` while a game runs;
- every `FileSystemWatcher`, timer, registry key, wait handle and `Process` is disposed and every event unsubscribed
  when a game is no longer watched, the setting is switched off or JoyPro closes; the only collections hold one entry
  per watched game;
- in the tray: not the GUI but the tray host (WinForms, no render loop): 0 ms CPU in 60 s idle, ~12 MB working set;
  its only waits are the GUI-started event and, while the GUI runs, the GUI process (both on the thread pool);
- engine calls only after a change (status ~150 ms; diff of one DCS aircraft ~0.2 s; the record after an export: 1.3 s
  for 180 DCS files / 9.5 MB, `Sync\DCS.json` ~1 MB for 54 000 binds). The GUI's background checks keep the output
  pane of the last command.

Measured by `gui\JoyPro.Gui.Tests\GameConfigWatcherTests.cs` (runs alone, `dotnet test gui\JoyPro.Gui.Tests --filter
GameConfigWatcherTests`), 2026-10-11:

| Test | Result |
|---|---|
| 1000 writes to a watched file (and 1000 to an unwatched one in the same folder) | 1 report (debounced); process handles 426 -> 428, managed heap 3.3 MB -> 3.3 MB, working set 68 -> 69 MB |
| idle 3 s with the watcher on | 0-16 ms CPU for the whole test process |
| 200 x watch / unwatch two games | handles 408 -> 406, managed heap flat, watcher holds 0 handles afterwards |
| change while the "game" runs (a renamed ping.exe) | no report until it exits, then one |
| registry value changed twice | two reports (the notification is armed again) |

The per-game ticks, `gui\JoyPro.Gui.Tests\GameWatchChoiceTests.cs`, 2026-10-12: `GameWatchChoiceTests` (defaults for
installed and missing games, untick saved and read back by a new GUI and by the tray host's store, tick again, Watch
all / none, a game installed later ticked by itself, an unticked game staying unticked after a new install, the tray
host watching only ticked games and taking new ticks after the GUI closed, the Start page render
`docs\ui\start-game-watch-dark.png` / `-light.png` with `JOYPRO_UI_DESIGN_OUT` set) and `GameWatchChoiceWatcherTests`
(real file watchers on temp folders):

| Test | Result |
|---|---|
| GUI: two tracked games, one unticked | only the ticked game has a `FileSystemWatcher`; the untick needs no engine call; master switch off -> 0 handles, on -> the ticked game only |
| GUI: 200 x untick / tick | process handles 831 -> 842, the watcher holds the same 4 handles as before, 0 after stop |
| tray host, real watcher: write to the unticked and the ticked game's config | only the ticked game is reported; after the GUI ran and the user ticked the other game, its write is reported too; every watcher holds 0 handles after the pause and the end |
