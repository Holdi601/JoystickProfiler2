# Usage statistics, ranking and the Statistics page

JoyPro notes which games, aircraft, ships and vehicles you use. It reads what the games themselves write (logs,
journals, logbooks, replays, savegames) and Steam's playtime. It keeps a small summary in its own data folder and never
sends anything anywhere. The recording always runs, so the ranking and the layout features have data. The Statistics
page that shows it all is opt-in.

- GUI: Settings > **Usage statistics** (ranking with Pin / Exclude, "Lay out new aircraft like my most used one",
  "Show the Statistics page in the bottom bar", "Read the games' logs now"); the **Statistics** page (bottom bar, once
  switched on; Settings > Pages hides and moves it like any page).
- CLI: `engine usage harvest | sources | ranking | stats | pin | unpin | exclude | include | backups | restore`, the
  global option `--usage-style on|off` ([CLI wiki](../engine/docs/ENGINE_CLI_WIKI.md)).
- Code: `engine/Usage/*.cs` (store, parsers, aggregation, ranking, statistics; plain C# 7.3, also compiled into the GUI
  tests), `engine/EngineUsage.cs` (CLI, where the games keep their files, the style reference),
  `gui/JoyPro.Watch/UsageLogTailer.cs` (live tail), `gui/JoyPro.Gui/ViewModels/MainWindowViewModel.Usage.cs`,
  `StatisticsViewModel.cs`, `Views/StatisticsView.axaml`, `Views/Controls/UsageCharts.cs`.

## Sources

Read only, every file opened with shared read/write/delete, so a game that writes or rotates its log is never disturbed.

| Game | Source | Gives |
|---|---|---|
| DCS World | `Saved Games\DCS*\Logs\dcs.log` (+ `dcs.log.old`) | every game start (time in the game, menus included); the aircraft from `spawnLocalPlayer <id>,<type>` until the next aircraft, the menu (`enterToState_:3`) or the end |
| | `MissionEditor\logbook.lua` | the pilots' totals: flight hours, landings, kills, deaths + ejections (per pilot, not per aircraft) |
| | `Tracks\**\*.trk` | the player's aircraft of each track (`["skill"] = "Player"/"Client"` in the mission); a flight without hours |
| DCS, Falcon BMS, IL-2 | Tacview `.acmi` / `.zip.acmi` (`Documents\Tacview`, BMS `User\Acmi`) | the game (DataSource), the player's aircraft (Pilot = Author), its time, take-offs, landings, its destruction |
| Falcon BMS | `User\Config\<pilot>.lbk` | the pilot logbook: flight hours (F-16), kills, deaths, missions; read only when the decoded name or callsign is the file's name |
| IL-2 Great Battles / Korea | `data\missionReports\missionReport(<time>)[n].txt` | per mission: the player's plane (AType:10), kills, take-offs, landings. IL-2 writes them only with `mission_text_log = 1` in `data\startup.cfg`; JoyPro says so and never changes the game's files |
| X-Plane 12 / 11 | `Output\logbooks\X-Plane Pilot.txt` | one line per flight: day, aircraft, hours, landings |
| | `Log.txt` | each start with the aircraft loaded (`Loading airplane number 0 with ...acf`) |
| MSFS 2020 / 2024 | - | the logbook is kept online: Steam's playtime only |
| War Thunder | `Replays\*.wrpl` (also `Documents\My Games\WarThunder\Replays`) | the header only: start time, battle; its length from the file's time; no vehicle |
| Elite Dangerous | `Saved Games\Frontier Developments\Elite Dangerous\Journal.*.log` | per session: the ship (LoadGame, Loadout, ShipyardSwap/New), jumps and light years (FSDJump), landings (Docked, Touchdown), kills (Bounty, FactionKillBond, PVPKill, CapShipBond), deaths |
| Star Citizen | `<channel>\Game.log` and `logbackups\*.log` (LIVE, PTU, EPTU, TECH-PREVIEW, HOTFIX) | per start: the ship from `granted control token` to `releasing control token`, kills and deaths (`<Actor Death>` of the local client), quantum travel arrivals as jumps |
| X4: Foundations | `Documents\Egosoft\X4\<id>\save\*.xml.gz` | the `<info>` block only: play time per playthrough |
| every game on Steam | `<Steam>\userdata\<account>\config\localconfig.vdf` | playtime and last played per app (all accounts of the PC add up) |

`engine usage sources` lists what exists on this PC; the harvest's report (`engine usage harvest --text`, the
Statistics page's "Where the numbers come from") says what each source gave.

## The store

`<data>\Usage` (data = `JOYPRO_DATA_DIR` or `%LOCALAPPDATA%\JoyPro2`, so a portable setup with its own data folder carries
it along):

| File | What |
|---|---|
| `sessions.jsonl` | one session per line: game, source, start, length, units (aircraft / ship / vehicle with start, length, counters), counters |
| `totals.json` | totals the games keep themselves (Steam, logbooks, savegames); the highest value ever read stays |
| `seen.json` | the files already read: size, write time, hash of the first 4 KB |
| `exports.jsonl` | one line per export JoyPro made (game, time) |
| `overrides.json` | pinned and excluded units and games |
| `Live\<game>\` | the live copies the watcher writes while a game runs |
| `Backups\<day>\` | a copy of the files above before the first change of a day; the newest 7 days stay (`engine usage backups`, `restore <day>`) |
| `last-harvest.json` | what each source gave at the last harvest |
| `Cache\growth-<profile>.json` | relations and binds per profile version (the profile's growth) |

- **Nothing counts twice.** A session has a key (game, source family, start). Read again (the log grew, the live copy, the
  same log moved into a backup folder) it replaces its record; the more complete read wins. A file whose size and write
  time are known is skipped; a copy of a known file (same first bytes and size) too.
- **History survives** a game deleting or overwriting its logs: the store keeps the sessions; totals are never lowered.
- **Small and append-friendly:** one harvest appends its new and changed sessions in one write (under a lock file);
  `sessions.jsonl` is compacted when more than half of its lines are replaced reads.
- **When:** at JoyPro's start (catching up, in a process of its own beside the user's commands), after each game closed
  (the live tail's game exit), and "Read the games' logs now". The tray host does the same while JoyPro's window is closed.

## Live tail

While a game runs, `UsageLogTailer` follows its log or journal (`engine usage sources` → `Tail`: DCS `dcs.log`, Star
Citizen `Game.log`, Elite journals, X-Plane `Log.txt`, IL-2 mission reports): a `FileSystemWatcher` per folder (only the
log's file names), a one-shot timer that coalesces a burst of writes, and per change only the bytes appended since the
last offset (plus the first 256 bytes to notice an overwritten log). They go into
`Live\<game>\<kind>~<file>~<ticks>.live`. A log written from the start again, or rotated away (`dcs.log` → `dcs.log.old`),
finishes the copy (`.done`) and starts a new one. The game's process is looked up once when its log first changes; its
`Exited` event finishes the copies and starts a harvest of that game, which removes finished copies once their sessions
are stored (a copy left behind is finished after two days). No polling, nothing read twice, every watcher, timer and
process released on update and close: the same rules as the [config watcher](sync-watcher.md). In the GUI and the tray
host; `JOYPRO_USAGE_RECORDING=off` stops it (support cases only).

## Names

No raw ids on screen: `ranking` and `stats` give every unit the name a person reads. A name the log brings stays
(Star Citizen ships "RSI Polaris", Elite "Python Mk II"); an id gets the cockpit database's variant name
(`F-16C_50` -> "F-16C Block 50/52", `FA-18C_hornet` -> "F/A-18C Hornet Lot 20"), else DCS's own module name of this PC
(entry.lua `displayName`), else the id made readable (`Cessna_172SP` -> "Cessna 172SP"). The ids stay in `Unit` / `Id`
(pin, exclude). `UsageRecordingTests` scans the rendered page and the ranking for raw ids (the quality gate's I6 rules
plus any word with an underscore).

## Ranking

`engine usage ranking`, Settings > Usage statistics:

- **Units** (aircraft, ships, vehicles; per game and across games): hours, sessions, last used. Pinned first, then units
  with logged time (sources overlap: a unit segment counts unless more than half of it lies in a segment of a more
  trusted source; a logbook line counts unless a timed flight of that unit was logged that day), then units the open
  profile only binds (a weak fallback, by bind count), excluded ones last.
- **Games**: hours = the higher of logged time (union of the sessions) and Steam's playtime / the game's own totals.
- **Pin** makes a unit rank first and the model of its class; **Exclude** keeps it out of the ranking and never a model.

## Style reference

New or unused aircraft (below half an hour of logged time) get the layout logic of the most used aircraft of the same
game and class (fixed wing, rotary wing, spaceship; DB\Functions\aircraft.json), or of the pinned one:

- **Auto Assign**: an unbound relation whose DCS aircraft are all unused gets the control that the relation of the same
  function in the reference aircraft is bound to (counts like its own previous control). The result says how many.
- **Auto Group**: with the presets wing / era / purist an unused aircraft is grouped like the reference, so its
  functions join the reference's relations (and binds). `UsageReferences` lists them.
- **Setup wizard / Lay out with AI**: every function the reference has on a control of the setup gets a bonus there for
  its class; bound relations keep their binds as before.

Only recorded use counts (never the bind fallback); with nothing recorded nothing changes. Settings > Usage statistics >
"Lay out new aircraft like my most used one" (on by default, `ui-settings.json` `usageStyle`) off: the GUI passes
`--usage-style off`.

## Statistics page

Opt-in: Settings > Usage statistics > "Show the Statistics page in the bottom bar" (`ui-settings.json`
`showStatisticsPage`, off by default). `engine usage stats` (with `--profile` for the profile's part) gives:

- tiles: hours in total and logged, sessions, games and units, days played, longest and current streak;
- hours per game, top 10 aircraft / ships / vehicles, hours per weekday, sessions per hour of the day, hours per month;
- counters where the games log them: landings, take-offs, kills, deaths, jumps, light years;
- fun facts (ids with values, sentences in the user's language: "You flew the F-14B more than all helicopters together");
- the profile: binds per device, the oldest bind whose aircraft or game shows up in no recorded session, relations and
  binds over time (profile history, 40 versions), exports per game;
- where the numbers come from (each source, what it gave, followed live or not).

Charts draw only their marks (the theme's accent; rounded data ends, 2 px gaps, a recessive baseline); every label is a
text in the theme's text colours, so Light and Dark are checked by the contrast tests.

**Most-pressed buttons and most-moved axes are not counted.** JoyPro reads the devices only while its window (Binds,
live input) or the HUD is open, so such counts would describe JoyPro's own pages rather than flights; counting during the
games would need an input reader running in the background all the time, which JoyPro does not do.

## Tests

`gui/JoyPro.Gui.Tests/UsageStoreTests.cs` (every parser on the synthetic fixtures in `engine/tests/fixtures/usage/root`,
harvest, dedupe and idempotence, moved and grown logs, live copies, retention, compaction, backups, ranking,
statistics), `UsageRecordingTests.cs` (live tail: appended bytes only, overwrite, rotation, game exit, release on
update, a long run with flat handles and memory; the tray host part; the engine's commands in a process and through the
server, `usage stats` under 1 s; the page rendered at 1280 and 2267, Light and Dark, `JOYPRO_USAGE_RENDER_OUT`),
`UsageSettingsTests` (the settings survive a restart), the smoke part `engine/tests/smoke/usage.ps1` and the quality gate
(I7 budget "usage stats", the page in I8 and I10).
