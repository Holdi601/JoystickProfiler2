# Telemetry recording

JoyPro can record every round, mission or flight of a game with all the telemetry the game sends, like Tacview does
for DCS, for every game JoyPro reads telemetry of. A recording can be exported for Tacview (ACMI 2.2) or as CSV.

**Off by default.** Nothing is recorded, polled or looked up until the user switches it on (Settings > Telemetry
recording, or `engine telemetry config set --param enabled=true`).

**Privacy: everything stays on this PC.** Recordings are files in a folder the user picks; nothing is uploaded and
nothing is sent anywhere. JoyPro only listens on 127.0.0.1 (or reads local files / shared memory) and only while the
game runs. War Thunder's in-game chat is never read; kill and HUD messages (which name other players, as the HUD does)
are recorded as events. See also [privacy.md](privacy.md).

## Games

| Game | Reads | Recorded | Tacview (ACMI) | Not recorded, and why |
|---|---|---|---|---|
| DCS World | JoyPro's hook `Saved Games\DCS*\Scripts\Hooks\JoyProFfb.lua` (v2), UDP 41992 | every frame (up to 100/s of model time): air data, G, angular rates, position (lat/lon/alt), true heading/pitch/bank, map coordinates, velocity vector, gear struts, cannon shells; 10/s: engines, fuel (kg), control surfaces, mechanics, stores, chaff/flares, warning flags, navigation/ILS, wind, autopilot requirements, locked target; 1/s: route, wingmen; facts: map, mission, date/time, pilot, unit, group, coalition, version; up to 600 other objects (Tacview types) 1/s; events: takeoff, landing, kill, crash, eject, pause, mode changes, chat | yes, with the other objects | cockpit/draw arguments (module specific: switches, lamps, real throttle position, trim); unguided munitions (too heavy); multiplayer servers decide (allow ownship / object / sensor export); full-fidelity modules report little through LoGetMCPState / navigation / payload |
| IL-2 Great Battles, IL-2 Korea | the game's telemetry output (`startup.cfg` address JoyPro adds), UDP 41994 | every indicator of the telemetry device (engines, gear, EAS, AoA/AoS, acceleration, cockpit shake, AGL, flaps, air brakes, vertical speed), events (gun fire, hits, damage, explosions, bombs, rockets), aircraft (SET_FOCUS), server; the motion and FFB outputs when they are sent to the same port | no | no world position in IL-2's UDP outputs; attitude only from the motion device, which JoyPro does not set up (one target, used by motion rigs) |
| War Thunder | the game's local web server 127.0.0.1:8111 | /state 20/s and /indicators 10/s (every flight-model and cockpit value), and while recording: /map_obj.json 2/s (player position in metres, heading, all map objects with stable ids, Tacview types, coalition by colour), /map_info.json every 10 s, /hudmsg 1/s (kill and damage messages), /mission.json every 5 s; one session per sortie | yes, flat world (U/V metres around 0/0), other objects without altitude | chat (on purpose); true geo position (the game has none); other objects' altitude and attitude (2D map); tank battles (/state invalid outside aircraft); the map's real name (an id from its extent) |
| X-Plane 12, X-Plane 11 | UDP RREF subscriptions to 127.0.0.1:49000 | 257 datarefs (214 at the sample rate, 43 twice a second): time, position, attitude, air data, G, controls and surfaces, gear, engines 1..4, fuel tanks, autopilot, weapons, warnings, lights, weather; aircraft name, ICAO, tail number; up to 19 TCAS targets as objects 2/s | yes, with the TCAS targets | engines 5..16, wheels 7..10, weapon stations 9..25, traffic slots 20..63, radios, electrics and hydraulics (more packets); every value arrives as a float (positions about 0.5 m resolution); X-Plane 12 needs Settings > Network > "Accept incoming connections" |
| MSFS 2024, MSFS 2020 | SimConnect, read only, through the user's own `SimConnect.dll` (never shipped with JoyPro) | 213 simvars (98 at sim-frame rate thinned to the sample rate, 115 once a second): position, attitude, velocities, accelerations, G, air data, warnings, surfaces, pilot inputs, trim, configuration, ground contact, engines 1..4, fuel and weight, autopilot, lights, systems, weather, simulation state; 8 strings (title, ATC id ...); events: sim start/stop, pause, crash, flight and aircraft loaded; aircraft and helicopters within 50 km 1/s as objects | yes, with the traffic | lateral/longitudinal G (signs unverified), rotor values, transponder (BCD); ground vehicles and boats (too many); Steam MSFS 2024 ships no `SimConnect.dll` (install the MSFS 2024 SDK, or have MSFS 2020); **not run against a live sim yet** |
| Falcon BMS | BMS's shared memory (its official export interface, `Tools\SharedMem\FlightData.h`), read only, 20/s | ~330 values of FlightData and FlightData2 (position, lat/lon, attitude, air data, engines, fuel, gear, lights and every decoded bit, instruments, trim, countermeasures, RWR contacts, radios, electrics), derived heading/track/AGL/bullseye; events: DED and PFL text, the multiplayer pilot list; objects: bullseye, steerpoints, pre-planned threats; a session = the time in 3D | yes | other aircraft, ground units and weapons (BMS exports no world objects; RWR contacts have only a bearing); throttle and stick positions (not exported) |
| Elite Dangerous | the journal folder (`Saved Games\Frontier Developments\Elite Dangerous`), read only, up to 10/s | Status.json (all values, every Flags/Flags2 bit as its own channel, pips, fuel, cargo, legal state, planet lat/lon/alt/heading, on-foot values), every journal line as an event (`ed.<event>`), companion files on change; facts: ship, commander, star system, game mode, version | no | no position on Earth (planet coordinates); attitude, velocity, G and throttle are not in the journal API; other ships (only text facts) |

Games without telemetry JoyPro can read (no recording): Star Citizen (no export), Arma, Nuclear Option,
VTOL VR, Ace Combat, Project Wingman, KSP and the other titles of [games/support.md](games/support.md) without a mark
in the "Telemetry recording" column. FSX / Prepar3D have SimConnect too, but with their own older client libraries:
not implemented. The support matrix is generated from the code (`engine/EngineGameSupport.cs`, column
"Telemetry recording": ✓ with ACMI, ◐ CSV only).

Setup: DCS needs JoyPro's hook and IL-2 JoyPro's address in `startup.cfg`; both are set up on the Force Feedback page
(`engine ffb telemetry setup dcs|il2|il2korea`), the same as for telemetry force feedback. An older DCS hook (v1) shows
as "outdated" there: "Set up" replaces it (DCS loads hooks when it starts). Everything else needs nothing.

## How it runs

```
game ──UDP / HTTP / shared memory / SimConnect / files──> feed (TelemetryFeeds.Create) ──> source (frames, events, raw input, session facts)
                                                                                              └─> TelemetrySessionRecorder ──> <folder>\2026-10-18_14-03-22_DCS.jptel
```

- **Who records.** One process owns a game's telemetry port. While telemetry force feedback runs for a game, its
  `JoyPro.Ffb run` records too. For every other game the GUI starts `JoyPro.Ffb record --game G --game-pid P
  --parent-pid GUI --protocol` when the game runs: every 5 s it looks for the games recording is on for, only while
  recording is on (nothing at all while it is off). The process follows the game through its handle and ends with it;
  the session is finished then. When force feedback starts for a game a record process holds, the record process is
  stopped first. Settings changes reach running processes through "reload".
- **Sessions.** A session starts with the first frame and ends when the game says so (DCS mission end, BMS leaving 3D,
  MSFS SimStop, Elite main menu, War Thunder no flight for 10 s, X-Plane new flight), after the session timeout without
  telemetry (default 60 s: a longer pause starts a new session), or when the game or JoyPro exits.
- **Cheap and never in the way.** The source threads only thin out (sample rate) and enqueue; compression and disk I/O
  run on one background thread of below-normal priority, so the force loop and the HUD never wait for the disk. Up to
  20000 items wait for the writer; beyond that frames are dropped and counted. War Thunder's extra requests run only
  while recording. Recording stops when the drive has less than 256 MB free. Measured 2026-10-18 (Debug build,
  `JoyPro.Ffb record` fed by the synthetic DCS stream at 60 datagrams a second, every frame and raw data kept, 40 s):
  about 1.7 % of one core, private memory flat at about 15 MB, handles and threads flat, 130 KB written.
- **Crash safe.** Data reaches the disk at least every 2 s; the next recorder that starts finishes sessions that were
  cut off (`telemetry recover` does it by hand): the damaged tail is cut and the summary written from the data.
- **Retention.** Before every new session and at its end: sessions older than "Delete recordings after" go, then the
  oldest until the folder fits "Space for recordings". The session being written is never deleted.

### Settings (`<data>\Telemetry\telemetry-recording.json`)

`<data>` = `JOYPRO_DATA_DIR`, else `%LOCALAPPDATA%\JoyPro2`. Written by the GUI and by `engine telemetry config set`,
read by JoyPro.Ffb at start and on "reload".

| Key | Default | Meaning |
|---|---|---|
| `enabled` | false | the global switch |
| `folder` | "" | where sessions go ("" = `<data>\Telemetry\Recordings`), any drive |
| `maxTotalMb` | 4096 | size cap of the folder (0 = none) |
| `keepDays` | 0 | delete sessions older than this (0 = keep) |
| `sampleHz` | 20 | frames per second written (0 = every frame the game sends); events are always kept |
| `keepRaw` | true | keep the raw input too (datagrams, HTTP answers, shared memory snapshots, journal lines) |
| `sessionTimeoutSeconds` | 60 | a session ends after this long without telemetry |
| `games.<id>.enabled` | true | per game; a game without an entry is recorded when the global switch is on |

## The session file (`*.jptel`)

One file per session, append-only, readable by the engine (net48) and JoyPro.Ffb (net9) through the same plain C# 7.3
code (`engine/TelemetryRecording/`). Blocks: `"JPTEL1\r\n"` then blocks of `"PBLK" | kind | raw length | stored length |
crc32 | deflate data`. Kinds: header (key=value: game, title, created, sample rate, `info.*` session facts, `nav.*`
mappings), data (text lines), raw (binary records: stream name, time, bytes), summary (ended, duration, frames,
events, raw, channels, position frames, aircraft, maps, end reason).

Data lines (tab separated, texts escaped): `C idx key unit title category min max` (a channel), `K t idx=value ...`
(key frame, the first frame of each block: every value present), `F t idx=value ...` (the values that changed;
`idx=` = the value is gone), `E t kind text` (an event). `t` = seconds since the session start on the receive clock,
so pauses and gaps are kept. The derived force feedback model (`ac.*`) is not stored: replay derives it again.

The `nav.*` header entries tell the exporter where a game's position and attitude are (`nav.lat=dcs.lat*1+0`,
`nav.roll=dcs.true_bank*57.29577951+0`): the export has no per-game code. Other objects come as `objects` events
(one snapshot per line set: `id|type|name|coalition|lat|lon|alt|roll|pitch|yaw|u|v`).

Size: about 10 to 50 MB per hour at 20 values a second with raw data (DCS with objects and raw at the upper end).

## Export

- **Tacview ACMI 2.2** (`.zip.acmi`: a zip with one `.txt.acmi`, or `.txt.acmi`): `FileType=text/acmi/tacview`,
  `FileVersion=2.2`, global object 0 (ReferenceTime in whole seconds, RecordingTime, DataSource, DataRecorder, Title,
  ReferenceLongitude/Latitude as whole degrees), `#<seconds>` time frames, the player as object `A0001` with
  `T=lon|lat|alt|roll|pitch|yaw` relative to the reference, IAS/TAS/Mach/AOA/AOS/AGL/HDG/Throttle/LandingGear/Flaps/
  AirBrakes/FuelWeight/VerticalGForce/LongitudinalGForce/LateralGForce when they change, other objects with their own
  hex ids, Type, Name, Coalition, Color, `-<id>` when gone, messages as `Event=Message`, aircraft and map changes as
  `Event=Bookmark`. UTF-8 with BOM, commas in texts escaped. Flat-world games (War Thunder) are placed around 0/0 with
  `T=lon|lat|alt|roll|pitch|yaw|u|v|heading`. Default at most 20 frames a second (`--max-hz`). Only for sessions with
  frames that have a position.
- **CSV** (a folder): `<session>.csv` (time + every channel as a column, one row per frame), `<session>-events.csv`,
  `<session>-channels.csv` (key, title, unit, category, range) and `<session>-channels\<key>.csv` (time and value at
  every change). Comma separated, `.` as the decimal point, UTF-8 (RFC 4180).

Default place: `<recordings folder>\Exports\`.

## GUI (Settings > Telemetry recording)

The switch, the folder (Choose... on any drive, Default folder, Open folder), values per second, space for
recordings, delete after, raw data, one switch per game (what JoyPro reads, ACMI yes/no, what needs setting up), the
current state ("Recording DCS World - F-16C_50: <file> (1,234 frames)"), the recorded sessions (date, game, aircraft,
duration, size, "recording" / "unfinished"), Show in folder, Export for Tacview (ACMI), Export CSV, Delete (asks
first), and a small timeline of three channels of the selected session (altitude, speed, G or pitch; min and max per
time bucket, so peaks stay). Code: `ViewModels/MainWindowViewModel.TelemetryRecording.cs`,
`Views/Controls/TelemetryRecordingPanel.axaml`, `Views/Controls/TelemetryTimelineView.cs`.

## Command line

```
engine telemetry games                                   the games, what JoyPro listens to, ACMI possible, recorded now
engine telemetry config show
engine telemetry config set --param enabled=true --param folder=E:\Recordings --param sample-hz=20
engine telemetry config set --game IL2Game --param enabled=false
engine telemetry sessions [--game G] [--folder F]        newest first
engine telemetry show <session>                          facts, channels by category, event counts
engine telemetry export <session> --format acmi|acmi-zip|csv [--to <file or folder>] [--max-hz N] [--no-per-channel]
engine telemetry preview <session> [--channel KEY]... [--points N]
engine telemetry delete <session>...
engine telemetry recover | prune
engine telemetry record --game G [--seconds N] [--folder F] [--sample-hz N] [--no-raw] [--no-game-check]
JoyPro.Ffb record --game G [--force] [--folder F] [--duration S] [--protocol] ...   (what the GUI and "telemetry record" start)
```

`<session>` = a path, a file name in the folder, `latest`, or a number of the list (1 = newest). `telemetry record`
records now even when the settings say off (it runs `JoyPro.Ffb record --force`, found at `<engine>\..\ffb\` or
`JOYPRO_FFB_EXE`; Ctrl+C ends it). The global `--out` is the engine's output file as everywhere; the export target is
`--to`.

## Tests

- `gui/JoyPro.Ffb.Core.Tests/TelemetrySessionTests.cs`: the file format (round trip across many blocks, deltas, gone
  values, events, raw, compression), cut-off files and recovery, the store (list, resolve, in-use protection,
  retention), ACMI validated against the format (mandatory header lines first, ReferenceTime/Longitude/Latitude, time
  frames never going back, hex object ids, T with 3/5/6/9 fields, escaped commas, objects appearing and removed, zip
  with one `.txt.acmi`, flat world, refused without position), CSV, preview.
- `TelemetryRecorderTests.cs`: the recorder (session facts and events before the first frame, raw only with
  `keepRaw`, decimation, session end by timeout / game / new session, recovery at start, never blocking the caller),
  the record host with fixture streams of the parsers (DCS UDP lines incl. position -> ACMI, IL-2 packets, War Thunder
  JSON through a stub server, polling only while recording), the force feedback host recording alongside, the settings.
- Per source: `DcsRecordingTelemetryTests.cs` (incl. the hook run in DCS's own `luae.exe` with stubbed APIs when DCS
  is installed), `XPlaneTelemetryTests.cs` (a fake X-Plane on loopback), `MsfsTelemetryTests.cs` (a fake SimConnect
  client), `BmsTelemetryTests.cs` (test-named shared memory; offsets checked against the installed `FlightData.h`),
  `EliteTelemetryTests.cs` (a temp journal folder), `WarThunderRecordingTests.cs` (stub server).
- `gui/JoyPro.Gui.Tests/TelemetryRecordingTests.cs`: the settings section, the process manager (which game, not for
  games force feedback covers, reload, stop), sessions list, preview, exports, delete. Journey scenario
  `telemetry-recording` (`UserJourneyTests`) renders the section at 2267x1250 and 1280x800.
- Live: `JoyPro.Ffb simulate --game DCS --port N` feeds a synthetic flight to `JoyPro.Ffb record --game DCS --port N
  --no-game-check --force` (no game needed).
