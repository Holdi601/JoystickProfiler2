# Testing JoyPro2

All scripted, none needs a person once started:

| Layer | Command | What it covers | Time |
|---|---|---|---|
| Engine smoke tests | `powershell -File engine\tests\engine_smoke_test.ps1` | every CLI command and game integration on fixtures | ~10 min |

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
