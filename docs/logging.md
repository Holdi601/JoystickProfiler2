# Background logs and problem reports

JoyPro2 writes a detailed log in the background so that a problem can be understood afterwards from the log alone:
the user creates a problem report, attaches it to an issue, and the developer sees which files were read and written, which game folders were found, which export mode was used, what the engine
printed and where it failed. Logs delete themselves after a few days.

## Where

All of them write into one folder:

| Program | File |
|---|---|
| GUI (`JoyPro.Gui.exe`) | `gui-<yyyyMMdd>.log` |
| Engine (`engine.exe`, also when run from a console) | `engine-<yyyyMMdd>.log` |
| Telemetry force feedback (`JoyPro.Ffb.exe`) | `ffb-<yyyyMMdd>.log` |
| Tray host (`tray\JoyPro.Tray.exe`, the config watcher while the GUI is closed) | `tray-<yyyyMMdd>.log` |

The folder is `<data>\Logs`:

1. `JOYPRO_LOG_DIR`, when set, is the folder itself (the smoke test uses it).
2. else `<JOYPRO_DATA_DIR>\Logs` (tests and portable setups),
3. else `%LOCALAPPDATA%\JoyPro2\Logs`.

It sits next to `CrashReports` (`%LOCALAPPDATA%\JoyPro2\CrashReports`). The engine and JoyPro.Ffb keep their other data
in `%LOCALAPPDATA%\JoyPro2`; the logs go to the `JoyPro2` folder for all three so the GUI finds them for a report.
Settings > Report a Problem > `Open Log Folder` opens it.

The code is one file, `engine\JoyProLog.cs` (plain C# 7.3): the engine compiles it, `JoyPro.Ffb.Core` links it, and
the GUI and JoyPro.Ffb use it through `JoyPro.Ffb.Core`. Nothing is written before `JoyProLog.Start`, so library code
and unit tests that never start it log nothing.

## Format

```
2026-10-11 10:44:12.345 INFO  engine#4711 s:3f9a2c1d c:gui-17 t:1 [Backups] Game file about to change (DCS): ...
```

- local time with milliseconds, level (`DEBUG`, `INFO`, `WARN`, `ERROR`), program and process id;
- `s:` the session: the GUI makes one per start and passes it to the engine and JoyPro.Ffb it starts
  (`JOYPRO_LOG_SESSION`); a program started on its own makes its own;
- `c:` the call that started the process (`JOYPRO_LOG_PARENT`): the GUI logs `Call gui-17: engine ...` when it starts
  the engine, and every line of that engine run carries `c:gui-17`; `-` for a process nobody started;
- `t:` the managed thread id, `[category]` the area (`Engine`, `Status`, `Backups`, `Export`, `Import`, `Installs`,
  `Paths`, `Sync`, `Profile`, `Devices`, `DbUpdate`, `Output`, `Watcher`, `Ffb`, `FfbHost`, `Report`, `Crash`, ...);
- continuation lines (stack traces, captured output) are indented by four spaces.

To follow one GUI action: find `Call gui-N` in the GUI log, then `c:gui-N` in the engine log of the same day.

## What is logged

Each start writes a header: program and version, process id, session, the caller, OS, 32/64-bit, .NET version, CPU
count, culture and time zone, the command line and the log settings.

- **Engine**: the parsed command (area, action, profile path and size, output options, load corrections), every timed
  step (`Starting/Finished <step> in N ms`, also without `--status`), the progress messages of imports, exports and
  the DCS DB refresh, the structured progress of long commands (`[Progress] 23/59 DCS World, reading inputs: F-16C_50`:
  the first and last item of every phase and the ones in between at most twice a second; `--progress`), a cancel by the
  user (`Command cancelled by the user`), profile load/save with relation and bind counts, install detection per game (folder, how it was
  found, user overrides, hidden games) and config paths, the DCS/IL-2/SC paths found, every game file before it is
  changed (size, backup set) and backup restores, export options per game (mode, keyboard, nuke, aircraft, bind
  counts), what an import read, sync status per game and recorded exports, DB update checks, the connected devices with
  VID:PID and HID name (once per run), failures (`Command failed: ...`), unexpected exceptions with stack trace, what
  the run printed (stderr up to 64 KB, stdout up to 16 KB) and the exit code with the run time.
- **GUI**: install, data and settings folders, every engine and JoyPro.Ffb call (arguments, call id, pid, exit code,
  duration, stderr), requirements and crash notices at startup, update checks, the game config watcher (watch list
  changes, a change seen while the game runs, a change reported after the game closed, watcher errors), log setting
  changes, problem reports and crashes (with stack trace).
- **JoyPro.Ffb**: the run options and settings file, the chosen device (name, VID/PID, force axes, effects), the graph
  per aircraft, every status change (waiting / active / faulted, reason, aircraft, frames, endpoint), faults with
  their exception and the retry, game start/exit, GUI commands, STOP ALL at the end, the exit reason and code.

### Game runs (since 2.2.7)
Finding a game problem needs the exact environment and the files JoyPro worked on, so every game run (scan, import,
export, refresh, restore, `sync diff`, a changed `sync status`) logs at debug level (`engine\EngineDiagnostics.cs`,
fed by `GameBackups.Begin` / `GameBackups.Protect` and the game integrations):
- once per run and game, `[Diagnostics] Environment of <game>`: install kind (Steam, Microsoft Store, standalone),
  install folder and how it was found, the config folders and files with folder/file, attributes (Encrypted (EFS),
  ReadOnly, Hidden, Compressed, ReparsePoint, CloudPlaceholder), drive, file system (NTFS, exFAT, network share), drive
  type and free space, and the connected devices (name, VID:PID, instance GUID);
- per file JoyPro reads for a game (MSFS profiles) `Read: <path> (<what>) | size, written, attributes, sha <12 hex>`,
  per file it changes `Before write:` and, after the command, `After write:` with the same stamp;
- a failed run (exit code not 0) logs the whole snapshot as a warning and prints it to stderr
  (`Diagnostics (environment and files of this run):`, in the GUI's output panel); an engine crash report carries it.
- MSFS: the source folders searched and the files they gave, every profile read (device, GUID, category, bound and
  listed actions), which profile was taken per device and category and why, the sim's records per device and category
  (slots, votes, shipped presets), every file an export wrote (package preset, profile, import file) with the changed
  actions.
- The watcher (`[Sync]`, `[Watcher]`): status per game, a changed config's files (recorded and current stamp), an
  unchanged one's compared files (every 10 minutes at most), the diff's changes (or why there are none); in the GUI and
  the tray host every file event of a watched folder (kind, path, time; one line per file and kind every 2 s), the
  game process seen (pid, start time) and its exit, the wait decision, failed status/diff runs, and whether the user
  was asked (notice / notification) or the state was accepted without asking.

### Support file
The engine's part of Settings > Report a problem > **Create Report** (see "Problem report" below; engine: `engine
support bundle [--game <id>] [--days N] [--include-profile true|false] [--history-cap-mb N] [--list] --out <zip>`,
`engine\EngineSupportBundle.cs` and `EngineSupportBundle.Profile.cs`). It holds everything a maintainer needs, so a
tester never has to send a second file. The GUI first lists what the zip will hold (`--list`, nothing is written: every
part with its size and what is trimmed), the user confirms, then the engine saves it as `Documents\JoyPro2\Problem
Reports\JoyPro2-report-<time>.zip`, the GUI adds `report.txt` and shows it in Explorer. The engine makes it in its own process (the GUI stays usable) and reports its progress in the busy
area; a typical file of 40 MB takes about 5 seconds. Nothing is uploaded.

The one box of the section, **Include my open profile and its history (your binds and device names)**, on by default
and remembered (`ui-settings.json` `supportIncludeProfile`), decides the personal parts: the GUI passes `--profile <open
profile> --include-profile true`, or `--include-profile false`. On the command line `--include-profile` defaults to true
when `--profile` is given, else false.

| Entry | Content |
|---|---|
| `summary.txt` | versions, system, JoyPro's folders, the log window, the open profile (file name, size, number of history versions, which were left out), per installed game (or `--game`) the environment as above, the connected devices, then every part with its number of files and size and **what was trimmed or left out** |
| `logs/*.log` | all of JoyPro's logs (GUI, engine, tray and watcher, force feedback) of at least the last 14 days (`--days`) and back to the oldest history version of the open profile, at most 100 MB; the oldest are left out first (named in summary.txt) |
| `crash-reports/`, `errors/last-errors.txt` | the crash reports of that time (the newest 40) and the last 50 warnings and errors with a stack trace from the logs |
| `state/settings/` | the app's settings folder (`ui-settings.json`, `ui-layout.json`, `hud.json`, `export-settings.json`, ...; a long text log only its last 512 KB) |
| `state/data/` | the data folder's state files (`data-migration.json`, `game-installs.json`, `games-disabled.json`, ...), `Logs\log-settings.txt`, the update records, device reference, local AI, force feedback, theme and watcher settings, which game files JoyPro owns (`Ownership`) |
| `state/versions.txt` | the game lists in use (`GameData\DB\<game>\source-version.txt`, the record of `engine db check-updates`) and every installed cockpit, photo and 3D pack (`pack.json`) |
| `state/data-folder.txt` | files and size per folder of the data folder |
| `devices/devices.txt`, `devices/vjoy-hidhide.txt` | the devices (VID:PID, instance GUIDs, HID names) and `engine vjoy status` (vJoy, ViGEm and HidHide installed, their devices and state, JoyPro's HidHide session) |
| `profile/<file>.pr0file` | personal: the open profile (only that one) |
| `profile/history/` | personal: its history as `engine profile history` reads it: `history.json` (the whole index), `drafts.json`, `blobs/<hash>.gz` and `.items.gz` of the newest versions up to 50 MB of stored copies (`--history-cap-mb`); the versions left out are named in summary.txt |
| `joypro1/<file>.pr0file` | personal (2.5.0): JoyPro 1's profiles, `Documents\JoyPro\*.pr0file` with its `last.pr0file` (a tester who comes from JoyPro 1), cleaned like the open profile, newest first up to 50 MB (the others are named in summary.txt); the open profile is not added twice. `JOYPRO_JOYPRO1_DIR` points elsewhere (tests; under the test sandbox `<root>\Documents\JoyPro`) |
| `games/<id>/sync-record.json`, `backups.txt`, `backup-<set>/` | the watcher's record, the list of JoyPro's backups (set, time, command, files) and the newest set: JoyPro's own copies of the small text config files (at most 512 KB each) an export changed, and its manifest |
| `games/<id>/current/`, `current-files.txt` | personal: the game's bind files as they are now, the files JoyPro imports from and exports to (DCS `Config\Input\<aircraft>\{joystick,keyboard,mouse}\*.diff.lua` and `modifiers.lua`, IL-2's `input` files and `startup.cfg`, Star Citizen's actionmaps, the other games' files, a registry game's values as text) and JoyPro's files next to them (`.jp`, `.joypro-meta.json`, `.joypro.json`); text up to 4 MB each, at most 50 MB per game (newest first), and the list of all of them with size, time and whether copied. A file that changes while it is read is not copied |
| `games/MSFS2024/profiles/` | the input profiles JoyPro reads (Steam remote folder, Microsoft Store save; decoded text, a header line with the source path, size and time) |
| `games/MSFS2024/package/`, `import-files/` | JoyPro's preset package and the import files |
| `games/MSFS2024/sim-records.txt` | the sim's device records as JoyPro reads them (not the save file) |

Every text goes through `LogScrubber` (the user folder becomes `%USERPROFILE%`, see below); other binary files are left
out. JoyPro keeps no export or import reports of its own: the logs hold what every export and import did. The profile
and its history are binary: the copies get the same cleaning on the strings inside them (a picture path, a DCS folder:
each string record is rewritten with its new length) and are read back with the profile's own reader before they go
in; a copy that does not read back is left out and summary.txt says so. A cleaned version gets the hash of its new
content (its blob, its items and `history.json` follow), so the history in the zip still restores. Only copies are
changed; the originals and the game files are only read.

**Ask a tester for the support file first**; it holds the logs, the profile with its history and the games' files, so
no second request is needed.

#### Restoring a version from a support file (maintainers)

The history folder's name comes from the profile's full path, so `engine support unpack` puts both where `engine
profile history` finds them, in an isolated data folder (nothing of this PC's JoyPro is touched):

```powershell
engine support unpack 'C:\Cases\JoyPro2-support-20261026-170635.zip' --to 'C:\Cases\case1' --text
$env:JOYPRO_DATA_DIR = 'C:\Cases\case1\data'
engine --profile 'C:\Cases\case1\profile\<file>.pr0file' profile history list --text
engine --profile 'C:\Cases\case1\profile\<file>.pr0file' profile history show 12 --text
engine --profile 'C:\Cases\case1\profile\<file>.pr0file' profile history restore 12 --text
```

`unpack` writes the whole zip to `<folder>\support\`, the profile to `<folder>\profile\` and its history to
`<folder>\data\ProfileHistory\<id of that path>\` (`history.json` then names the copy). After `restore` the copy is
that version; `undo` goes back. A version left out by the size cap is listed but says "The stored copy of this version
is missing." Keep `JOYPRO_DATA_DIR` set for every command on the copy.

Never per frame or per tick: the force loop and the watcher's idle time log nothing. JoyPro.Ffb writes a one-line
summary once a minute (state, frames, memory, GC, handles, updates); repeated warnings use `JoyProLog.ShouldLog(key,
interval)`.

## Size and retention

- Lines are queued and written at most twice a second (warnings and errors at once, everything at exit). Files are
  opened per write under a named mutex, so several engine processes can append to one file.
- A file is rolled at 10 MB (`engine-20261011-1.log`, ...); at most 5 files per program and day (50 MB). The rest of
  such a day is dropped, so a runaway loop cannot fill the disk.
- Logs older than the retention are deleted: default **30 days** (up to 2.4.2: 7), 1-365, set in the GUI (Settings >
  Report a Problem > `Keep background logs for N days`), so the report can go back to the oldest saved version of a
  profile. The value is stored in `<Logs>\log-settings.txt` (`retention-days=30`, `retention-chosen=true` once the user
  set it, `level=debug`), which all three programs read at start. A file of an older version with the old default
  (`retention-days=7` without `retention-chosen`) reads as 30; any other value, and 7 chosen in 2.4.3 or later, stays. `JOYPRO_LOG_RETENTION_DAYS` and `JOYPRO_LOG_LEVEL`
  (`debug`, `info`, `warn`, `error`, `off`) override it.
- The cleanup runs at a program start at most once an hour (`<Logs>\.last-cleanup`), costs one file time read
  otherwise, and also removes logs over 200 MB in total (oldest first). Crash reports in the `CrashReports` folder are
  kept at least 30 days (the GUI shows unseen ones at its next start).

## Privacy

The repository and the program are public, so logs and reports must not carry personal data. Every log line and every
text file of a problem report goes through `LogScrubber.Scrub`:

| Data | Becomes |
|---|---|
| the user folder (`C:\Users\<name>`, also as `C:/Users/...` and JSON-escaped `C:\\Users\\...`) | `%USERPROFILE%` |
| the Windows user name (whole word, any case; e.g. `Arma 3\<name>.Arma3Profile`) | `<user>` |
| the PC name | `<pc>` |
| e-mail addresses | `<email>` |
| `password=`, `pwd:`, `secret=` values; `token=`, `api_key:`, `authorization:`, `cookie=` values of 12+ characters; `Bearer` tokens | `<redacted>` |
| Steam ids (`7656119...`, `userdata\<account id>`) | `steam#` + 8 hex digits |
| game account folders (War Thunder `Saves\<id>`, X4 `Egosoft\X4\<id>`) | `id#` + 8 hex digits |

The hash is SHA-256 over the id and a salt made of the PC and user name (which never reach a log): the same id gives
the same hash on one PC, so lines stay comparable, and it cannot be looked up. JoyPro does not read e-mail addresses,
passwords or tokens itself; the rules are a safety net for text that passes through (engine output, game files,
exception messages). The copies of the profile and its history in a report are cleaned inside their strings as well
(see "Support file" above); only the GUI's own report (when the engine cannot run) has no profile.

## Problem report

Settings > Report a Problem, for users without technical knowledge, one flow (`MainWindowViewModel.AppServices.cs`
`CreateProblemReportAsync`, `Services\ProblemReporter.cs`):

1. `What happened?`: the user describes the problem.
2. The box `Include my open profile and its history (your binds and device names)` (on by default, remembered; it
   replaced the report's `Attach my profile` of 2.4.2, which was never saved, so there was nothing to carry over).
3. `Create Report`: JoyPro lists what goes in (`engine support bundle --list`), the user confirms, the engine writes the
   support file (above) to `Documents\JoyPro2\Problem Reports\JoyPro2-report-<time>.zip`, and the GUI adds
   `report.txt` and `engine-output.log` (`ProblemReporter.AddReport`) and shows the zip in Explorer. The status line
   says what to do next.
4. `Open GitHub Issue Page` opens a prefilled issue (title and description, scrubbed) in the browser, where the user
   drags the zip in; `Show Report` shows the zip again.

The reports' folder is `Documents\JoyPro2\Problem Reports` since 2.5.0 (next to the profiles; tester wish). Up to 2.4.x
it was `Documents\JoyPro2 Problem Reports`: at the first start of 2.5.0 (and before a report is made) its files move
into the new folder once (`ProblemReporter.MigrateOldFolder`); a file whose name the new folder already has stays in
the old one, the old folder goes when it is empty, and the GUI log says how many moved and stayed.

Nothing is uploaded automatically and JoyPro has no upload endpoint. The zip is the support file (table above) plus:

| Entry | Content |
|---|---|
| `report.txt` | the description, time and UTC offset, version, OS, system and .NET, diagnostics (engine version, `engine games devices`, requirements, DB notices, the current page, recent engine output), the parts of the zip |
| `engine-output.log` | the full engine output of this GUI session |

When the engine cannot run, the GUI makes its own smaller zip instead (`ProblemReporter.Create`,
`JoyPro2-problem-<time>.zip`): `report.txt`, the logs of the last 3 days (at most 60 MB), up to 10 crash reports and
`engine-output.log`, without the profile.

All text entries are scrubbed. Reading a report: start with `report.txt` (what the user saw), then the GUI log around
the report time, follow `Call gui-N` into the engine log (`c:gui-N`), and check `WARN`/`ERROR` lines and the
`[Output] stderr` blocks.

## Tests

- `gui\JoyPro.Ffb.Core.Tests\LoggingTests.cs`: retention (old files deleted, recent kept, settings files kept), the
  total size cap, the clamp, the once-an-hour cleanup, crash report retention, settings round trip and overrides, the
  header and line format with correlation ids, level filtering, rolling at the size cap and the daily limit, rate
  limiting, and the scrubbing rules (paths, user and PC name, e-mail, secrets kept apart from ordinary words, Steam
  and account ids).
- `gui\JoyPro.Gui.Tests\AppServicesTests.cs`: the problem report zip (recent logs only, crash reports, engine output,
  optional profile, scrubbed contents, the issue link without secrets).
- `engine\tests\engine_smoke_test.ps1` sends every engine and JoyPro.Ffb log of the run to a temp folder
  (`JOYPRO_LOG_DIR`) and checks the start header, the exit line and that the user folder never appears.

Tests never write into the user's folders: the GUI tests set `JOYPRO_DATA_DIR` to a temp folder, the logging tests use
temp folders and `JOYPRO_LOG_DIR`.
