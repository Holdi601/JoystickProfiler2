# Testing JoyPro2

All scripted, none needs a person once started:

| Layer | Command | What it covers | Time |
|---|---|---|---|
| Engine smoke tests | `powershell -File engine\tests\engine_smoke_test.ps1` | every CLI command and game integration on fixtures | ~10 min |

Auto Assign and Lay out with AI: `AutoAssignMatrixTests` (three device sets and the same plan twice always; the full
combination matrix with a report via `JOYPRO_AUTOASSIGN_MATRIX=<folder>`, docs/auto-assign.md#combination-matrix),
`LayoutWithAiTests` (phrases on four device sets, the same phrases through the real engine, the Binds page panel; the
author's pit on a copy of a real profile via `JOYPRO_LAYOUT_BIG_PROFILE`, pictures via `JOYPRO_LAYOUT_AI_OUT`) and the
smoke part `engine\tests\smoke\autoassignmatrix.ps1`.

Telemetry force feedback (docs/ffb-telemetry.md) is tested without moving a stick: `dotnet test
gui\JoyPro.Ffb.Core.Tests` (graph, runtime safety rules, the synthetic flight through the DCS, IL-2 and War Thunder
sources, loopback UDP host, War Thunder HTTP stub) and the smoke part `engine\tests\smoke\ffbtelemetry.ps1` (CLI
settings and game setup on temp copies, `JoyPro.Ffb run --sink dry` against `JoyPro.Ffb simulate` per game, telemetry
timeout, game exit, and a 60 s CPU/memory/handle resource run, ~2 min).

Usage statistics (docs/usage-statistics.md): `UsageStoreTests` (parsers on the synthetic fixtures of
`engine\tests\fixtures\usage\root`, dedupe, retention, ranking), `UsageRecordingTests` (live tail incl. a long resource
run, tray host, the engine's usage commands in a process and the server, the Statistics page rendered at 1280 / 2267 in
Light and Dark with `JOYPRO_USAGE_RENDER_OUT`), `UsageSettingsTests` and the smoke part `engine\tests\smoke\usage.ps1`.
The GUI tests point `JOYPRO_USAGE_ROOT` at an empty folder, so no test reads this PC's games.

Check page with thousands of findings: `CheckResponsivenessTests` (5000 relations, 3000 errors, 900 duplicate cards in a
HiddenWindow; a UI-thread probe fails any block over 400 ms; `JOYPRO_CHECK_REPLAY=<folder>` replays real engine outputs,
`JOYPRO_CHECK_REPORT=<file>` writes the timings). DCS import with "include defaults": the smoke part
`engine\tests\smoke\dcsincludedefaults.ps1`.

## Speed

Nothing a person waits for should take more than a few seconds (interactive actions ≤ 2 s); the budgets are in
`gui\JoyPro.Gui.Tests\QualityGate\Budgets.cs` and checked by the quality gate's I7.

- **Engine server.** The app runs its quick commands (relation editor, catalog, search, binds, layout titles, device and
  game lists, the DCS export: `Services\EngineServerCommands.cs`) in one long-running `engine serve` (engine `EngineServer.cs`, app
  `Services\EngineServerPool.cs`). Every command runs as in a process of its own: own arguments, environment variables,
  output and exit code, and a fresh copy of the profile as the file has it (`EngineProfileCache`: the profile is read
  once and copied per command by `ObjectGraphCloner`, 15 ms instead of 0.4-1.3 s; after the server's own save the copy is
  made "as serialized", as a read of the file would give it). A profile older than the DB is corrected on load; the
  state the DB part of that correction made is kept too (`EngineLoadCorrectionCache`, per file, DB fingerprint, options
  and rules version), so the first change after "profile load" does not correct again; the device remap against the
  connected devices runs every time (`serve --self-test` compares a kept correction with one that runs and checks that a
  changed file or DB list runs it again). Kept between commands: the game lists, function map, search
  and suggestion indexes and the load corrections' lists; before every command the server checks the files they come
  from (`DataStamp`: name, size and time of the DB, function and layout catalog files) and answers "stale" when they
  changed, then the app starts a new one. The app also starts a new server after a refresh or another command that can
  change game data. Per-run state (backups, diagnostics, detent warnings, device and install detection) is reset before
  every command. The server warms up as the app starts, ends with the app (closed stdin, the app's process gone) or after
  20 idle minutes (`JOYPRO_SERVER_IDLE_MINUTES`). `JOYPRO_ENGINE_SERVER=off` runs every call in a process.
- **Checks.** `gui\JoyPro.Gui.Tests\EngineServerTests.cs` runs a sequence of 20 commands (add, show, rename, group, bind,
  search, browse, suggest, remove, save) once in processes and once in the server and compares output, exit code and
  the saved profile byte for byte (`JOYPRO_SERVER_TEST_PROFILE=<profile>` also on a copy of a real profile), a game list
  changed on disk (a new server answers), and that no server outlives its use. `smoke\server.ps1` checks
  `engine serve --self-test` (the profile copy serializes to the same bytes as the profile read from the file, also after
  a save with removed entries and new ones afterwards), the same sequence through `engine serve --script` and the
  protocol. Development tools: `engine serve --script <file>` (one JSON argument array per line, each command timed;
  `JOYPRO_SCRIPT_OUT=<folder>` writes every result), `engine serve --diff-profiles <a> <b>` (where two profiles differ,
  also in shared instances) and `JOYPRO_SAMPLE_OUT=<file>` (a sampling profiler of the engine's main thread).
- **DCS export.** The input files are read once, several at once (`RunFileCache`: a file the run wrote is taken from
  what it wrote, every hit checked by size and time stamp), parsed in parallel and merged in the usual order; the backup
  manifest is written once per run (a journal in between, read back after a crash); the unique relation name of an
  import is one lookup. The output is byte for byte the old engine's (checked on a copy of a real Saved Games folder in
  merge, merge-overwrite and clean + nuke, and loaded with DCS's own `luae.exe`).
- **Load corrections.** The DB fingerprint hashes only files new or changed since the last fingerprint
  (`<data>\Cache\db-file-hashes.tsv`, size and time stamp). A profile saved with an older DB searches a replacement
  for every action the DB no longer has (`CoreInternalDataManagement.cs`, `FindBestReplacement`): each distinct title of
  the DB is scored once, titles that cannot reach the best so far are dropped by their length and letter histogram,
  containment only where the letters allow it, and the edit distance is bit-parallel (Myers / Hyyroe). The engine server
  keeps the prepared lists and every result per DB, so a profile's corrections are searched once (profile load) and not
  again by the commands after it (Lay out with AI's preview, the Setup wizard's plan, sync diff). The result is exactly
  the old full search: `JOYPRO_CORRECTION_REFERENCE=1` scores every candidate the old way, and the correction reports
  (`--correction-report`) of both are compared on copies of real profiles (2298 + 228 replacement searches identical;
  the user's March 2025 profile 80 s -> 2 s, the first Lay out with AI preview ~100 s -> under 3 s).

## Quality gate

`powershell -File tools\quality_gate.ps1 -Profile <profile.pr0file> [-Out <folder>] [-Data user|shipped] [-Only I1,I5]`
(opt-in, `JOYPRO_QUALITY_GATE=1`; about an hour on a big profile) runs invariant checks on real data through the real
view model and the real engine, in `gui\JoyPro.Gui.Tests\QualityGate`: the game lists of this PC (a copy of
`%LOCALAPPDATA%\JoyPro2\GameData`) or only the shipped ones (`-Data shipped`), and always a copy of the profile. It renders
its pages in roots that are never shown (HiddenWindow: no window on the desktop, no focus taken) and writes only into `-Out`; exports run
into copies of the game's config or as dry runs, and the real config files are fingerprinted before and after.

| Check | What must hold |
|---|---|
| I1 | the catalog shows actions for every game and every DCS aircraft (as many as its overview counts); every game chip and DCS aircraft filter of the search shows rows |
| I2 | no catalog title is empty, the id, a lua key, an encoded/hex id, a snake_case identifier, has an unexplained abbreviation ("Rb pitch", `ActionTexts.UnexplainedAbbreviation`) or is only a word and an axis letter; every action has a category, categories are words (DCS abbreviations are the module's own: a note) |
| I3 | every game of the Start page and Settings > Games has catalog actions, and a title of its own catalog finds it in the search |
| I4 | pitch, roll, throttle, flaps, gear, engine rpm, fire, view, zoom find readable rows (no unexplained abbreviation outside DCS) in every game whose catalog has the word |
| I5 | suggestions of typical relations (pitch, roll, throttle, gear, flaps over two games) are there after the second game's action and after the profile is opened again |
| I6 | every relation of the profile shows readable titles in the editor, the binds peek and the layout table; the check's problems show no raw id (the profile's own relation and device names, whole or quoted, are the user's data; a raw id injected next to one is still caught) |
| I7 | engine server start (a fresh server after the gate's first start; the first start right after the build has its own cold-start budget), profile load, the first Lay out with AI preview after it (rules only), relation open, 200 actions at once, save, suggestions, catalog, search, export per game, the Statistics page's `usage stats` (1 s) stay within the budgets of `QualityGate\Budgets.cs` |
| I8 | texts of the main pages have at least 3:1 contrast to their background in Light and Dark |
| I9 | every game the UI names (games list, supported games page, badges, trailer) has an integration, catalog actions and a function map; labels are names, not abbreviations |
| I10 | every page and the relation editor (empty, normal, large relation) at MinWidth, 1280x720 and 1920x1080, 100 % and 150 %, Light and Dark: no overlapping controls or chips, no text drawn over a control or chip it does not belong to, no cut-off text, no pre-filled search that finds nothing; also at 150 % and 200 % text size (Settings > Appearance) and with a serif and a wide font at 150 % text, each with 150 % Windows scaling, in Dark; the Layout page also with the profile's devices as own layouts (long names, "Your layout · N markers" chips; the header names the picture in words, match at most 100 %) |
| I11 | every phrase of `engine/tests/fixtures/phrases/phrases.json` (names, synonyms, slang, paraphrases, everyday sentences, German) finds its function offline: search box 95 %, catalog filter 90 %, JoyPro assistant 95 %, Manage / Edit with AI 90 %, By description 90 %, search top-row precision 90 %; the app's catalog filter on one phrase and one sentence per function 85 %; failures in `phrase-failures.txt` |

Output: `report.md` (findings by check, rule and game; timings), `findings.json`, `timings.csv`, `engine-calls.csv` (every
engine run with its time) and the pictures of the contrast and layout checks. A finding fails its check; the script's
exit code is 0 only without findings.

The engine smoke test dot-sources one part per game from `engine\tests\smoke\*.ps1`. The DB refresh parts (IL-2 Great
Battles `il2.ps1`, Elite `elite.ps1`, War Thunder, MSFS, Nuclear Option, Arma Reforger `reforger.ps1`) build small synthetic game files, run the
refresh in a temporary JoyPro folder (`JOYPRO_DATA_DIR`, install set with `JOYPRO_INSTALL_DETECTION=off`) and check
that new actions come in (in `<JOYPRO_DATA_DIR>\GameData`), known titles stay and the shipped catalog and the program
folder stay untouched (IL-2 and Elite also: a dry run and a second refresh write nothing). The whole smoke test runs
with a temporary `JOYPRO_DATA_DIR` from its first command on: the engine copies the program folder's game lists into
`<data>\GameData` on its first start, which must never be the user's `%LOCALAPPDATA%\JoyPro2`. `gamedata.ps1` covers that
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
`--progress true` (the engine runs several luae.exe at once: the fake one takes only its run's profiles,
`assignedProfiles`, and the count is summed over the runs; with `FAKE_LUAE_CRASH_ONCE` its first run crashes and
both refreshes must still write every list); a cancel through `JOYPRO_CANCEL_FILE` mid-run exits with
code 3, stops every luae.exe and writes no command list; a War Thunder `refresh-db` reports its three steps and a cancel leaves its catalog untouched. On the GUI side
`BusyProgressTests` covers the parser, the busy view model (count, bar, estimate, details, cancel), a fake engine that
streams its lines while the view model is watched, and an offscreen render of the busy area that checks that no text
overlaps; with `JOYPRO_BUSY_PREVIEW=<repo>\docs\ui` it also renders the whole window (Start page area and the compact
row above the status line) into `docs\ui\busy-progress-*.png`.

Themes, text size and fonts (docs/ui/themes.md): `ThemeTests`, `TextScaleLayoutTests` (`JOYPRO_TEXTSCALE_OUT`),
`AppearanceRenderTests` (`JOYPRO_APPEARANCE_OUT`), `LayoutDeviceCardTests` (`JOYPRO_LAYOUT_CARDS_OUT`).

Readable colors: `ThemeContrastTests` renders every main page, the engine output panel, every settings section open and
the dialogs (confirm, game setup, replace device, every setup wizard step) in the light and the dark theme and a theme of the user's own (`ThemeTests.SampleTheme`) in a hidden
root and measures every visible text (also inside buttons, toggle buttons, chips and key caps) against the color the
render shows behind it: at least 4.5:1, 3:1 for large text (24 px, or 18.66 px from SemiBold up); disabled controls are
left out. `JOYPRO_CONTRAST_OUT=<folder>` keeps the renders, `contact-light.png` / `contact-dark.png` and
`contrast-report.txt`. Colors come from the theme tokens in `Styles/Design.axaml` (`JP.WarnTextBrush`,
`JP.DangerTextBrush`, `JP.OkTextBrush`, ...), never as fixed colors on text; the navy banner keeps the dark theme in both
themes (`ThemeVariantScope`).

Cheat sheet style (docs/ui/layout-redesign.md "Sheet style"): `SheetStyleTests` (presets and the base palettes, JSON and
the sheet's `"style"`, the engine options, the global style and own presets surviving a restart, a device's own style
saved in its sheet and removed again, "Share layout..." round trip, the real engine's sheet and the panel's preview
sampled for header row, table and marker colours, no overlap of the panel at 1280x800 and 2267x1250 in both themes with
a big sheet font and 1.5x UI text; `JOYPRO_SHEET_STYLE_OUT` keeps the renders) and the smoke part
`engine\tests\smoke\sheetstyle.ps1`.

Back / Forward (docs/ui/navigation.md): `NavigationHistoryTests` covers the history class (push, back, forward, the cap,
duplicates, no push while restoring), the view model (page changes and "Open both relations" push; Back puts page,
search, open cards and relation back) and a navigation render test in a hidden root: the Check list scrolled to a card,
"Open both relations", the list scrolled to the top while away, Back -> the same card inside the scroll viewport again.
It also renders the header at 1280x800, 1200x760 and 2267x1250 in both themes (no overlap, every button inside the
header). `JOYPRO_NAV_OUT=<folder>` keeps the renders.

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
`MsfsStoreWatcherEndToEndTests` runs the config watcher end to end on such a Store save: import / export record the
sim, the GUI's own watcher, a stand-in sim process (a copy of `PING.EXE` in `<home>\XboxGames\Microsoft Flight
Simulator 2024\Content`, or named by `JOYPRO_TEST_GAME_PROCESSES`), the sim's save (new GUID blob, `container.<n+1>`,
`containers.index` deleted and written again, old files deleted), the prompt and the take-over (added and removed binds).

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

In-game tests of the integrations (each game started, its own controls menu checked) are run before a release; their results are in each game's page ([games](games/README.md)).
