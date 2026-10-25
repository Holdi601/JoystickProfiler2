# Game integration reference

One file per game with everything known about its control files: locations, format, device identification, input encoding, which entries are flight related, and pitfalls. The notes come from research on 2026-09-30.

Evidence tags used in these files:
- **[SRC]**: official documentation or the game's own source/scripts.
- **[OBS]**: seen in real user files.
- **[INF]**: inferred from real files.
- **[UNVERIFIED]**: needs a check on a real install before relying on it.

**Which games JoyPro supports and how far: [support.md](support.md).** It is the full list (every version separately,
and the titles JoyPro only documents) with a feature matrix (import, export, keyboard, modifiers, list
refresh, watcher, axis tuning, settings, force feedback, vJoy / Xbox pad, virtual functions, setup wizard, cockpits,
cheat sheets, in-game test) and per title the bind file, the command-list source, device limits, anti-cheat, the
in-game test result and the open points. The page is generated (`tools/game_support.ps1`, `engine games support`):
the marks come from the code, the test results and footnotes from [support-notes.json](support-notes.json); the
engine smoke test fails when it is out of date. In the app: Start page, `Supported games`.

Games that read only one device or only XInput (Battlefield, Rising Storm 2, ACE COMBAT 8, H.A.W.X ...) and the survey
of the other requested games (Squadrons, Flyout, KSP, G-Rebels, No Man's Sky, Project Wingman):
[one-device-games.md](one-device-games.md).

Every integration supports `paths`, `catalog`, `scan`, `import`, `export` (modes clean/add/merge/merge-overwrite, `--dry-run`) and, where a source exists, `refresh-db [<path>] [--dry-run]`, which adds the game's new actions, keeps the id, title and category of known ones and writes the catalog only when it changes. Scan, import, export and refresh-db need the game installed or its folder set ([installed games](../installs.md)). `engine games integrations` lists them, `engine games devices` shows the device data they use.

DB refresh of the games outside the registry: DCS `engine dcs refresh` ([dcs.md](dcs.md)), Star Citizen `engine db refresh-starcitizen` ([starcitizen.md](starcitizen.md)) and IL-2 Great Battles `engine db refresh-il2 [<install, data\input folder or .actions file>] [--db-path <folder>] [--dry-run]` (also `engine il2 refresh-db`; GUI: Database And Paths > Refresh IL-2 DB). The IL-2 refresh reads the key mapping files the game writes to `data\input` (`global.actions` and other `*.actions`, not JoyPro's `jp.actions`): one line per action with its input and the English description as a comment (`rpc_pitch, joy0_axis_y, 0| // Plane control: pitch`). Actions that `DB\IL2\IL2.actions` lacks are appended in its column format with that description (`//a` for an axis: a key pair or axis input, compiled `<id>_p`/`_n` halves or a response curve in `current.map`, or a `rpc_` plane control; else `//b`); existing lines stay byte for byte, so relation names made from the titles do not change, except a title the game left as its string-table key (`// inprb_pitch`, 21 actions: `rb_*`, `mc_*`, `vc_brake`, `cam_switch_mc_*`, `ext_action0-9`, `cam_rotate_gopro_*`): that one gets JoyPro's own name from `DB\Functions\ActionTexts\IL2Game.tsv` (new actions too; `Renamed Titles:` in the output). The game's own English text is what its Key Mapping menu shows (checked on a screenshot of the menu: "Plane control: pitch", "Rudder trim axis"), so it stays; the menu's tabs (Service, Camera controls, Pilot head control, Plane controls, Plane engine controls, Weapons controls, Flight leader orders, Tank controls) follow the order of `global.actions` and are the catalog categories. `IL2.map` is not touched. Run against the installed game on 2026-10-10: `global.actions` (written by the game 2026-10-04) has 375 actions, all in the catalog (382; the 7 extra are older entries the game no longer lists), so nothing was added. The packed `.gtp` archives were not read.

## Command lists on a fresh install

A release package ships no game data (THIRD_PARTY_NOTICES.md, section 1.4): JoyPro builds each game's command list on the user's
PC from the user's own installation, after the first-start setup, from the Start page notice ("Build List", "Build All
Missing Lists") or with Update on the game's card. `engine db check-updates` reports such a game as `no-catalog`.
The lists go to the game-data folder `%LOCALAPPDATA%\JoyPro\GameData` (`DB\<game>`, DCS's `CleanProfile` and
`KeyboardCleanProfile`), never into the program folder, so an update or rebuild of JoyPro keeps them
([installs.md](../installs.md#game-data); `engine db paths` shows where each list is read from).
Every game needs to be installed (or its folder linked); what else each build needs:

Measured with a release-like engine folder (game data removed by `tools/ReleaseContent.psm1`) and an empty
`JOYPRO_DATA_DIR`, one `refresh-db` per game, as the GUI's "Build All Missing Lists" runs them. "Before" = the build of
2026-10-11, "after" = 2026-10-12 (this change), "dev" = the development list in `EngineOnly_Runtime`.

| Game | Built by | Source on the user's PC | Before | After | Dev | Gaps left |
|---|---|---|---|---|---|---|
| DCS World | `dcs refresh id` + `dcs refresh clean` (headless Lua) | the DCS install's input Lua files, run by its own `luae.exe` in a temporary Saved Games folder | 92 html, 1027 / 89 clean profiles | (not rerun, unchanged) | 119 html, 1145 / 118 | only the installed modules get lists (correct) |
| IL-2 Great Battles | `db refresh-il2` | `data\input\*.actions` the game writes | 376 | 376 | 450 | the game writes only actions that have an input; the 74 others are only in the maintainer's development list (not shipped) and are not rebuilt |
| IL-2 Series: Korea | `il2korea refresh-db` | `general.actions`; titles from IL-2 GB's built list or, without it, IL-2 GB's own `data\input\*.actions`; JoyPro's own wording for the rest | 318 (91 titles were ids) | 318 (the cryptic ids get JoyPro's wording) | 378 | Korea's own texts are in its `.gtp` archives, which JoyPro does not read; 60 actions without an input are not in `general.actions` |
| Star Citizen | `db refresh-starcitizen` | `Data.p4k` (defaultProfile.xml) and user profiles | 1103 | 1103 | 1103 | — |
| War Thunder | `warthunder refresh-db` | `gui.vromfs.bin` / `lang.vromfs.bin` **by default** (user decision 2026-10-12); `--no-game-archives` (Settings > Games) = JoyPro's own list | 116 | 290 | 290 | no base preset table (exports write tuned attributes explicitly) |
| X-Plane 12 / 11 | `xplane12` / `xplane11 refresh-db` | `Resources\plugins\Commands.txt` | 2779 / 2066 | same | same | — |
| MSFS 2024 / 2020 | `msfs2024` / `msfs2020 refresh-db` | Asobo's preset files, **every flight event `fs-base\en-US.locPak` names** (JoyPro's rules pick the flight ones and their context), installed aircraft | 360 / 355 | 1739 / 1580 | 1378 / 1378 | 35 (2024) / 183 (2020) dev ids the sim does not name (dev list mixed both sims); context matches the dev list for 91 % of the locPak-only events |
| Falcon BMS | `bms refresh-db` | `BMS - Full.key` | 939 | 939 | 939 | — |
| X4: Foundations | `x4 refresh-db` | `X4.exe` name tables, `.cat/.dat` archives | 128 | 128 | 128 | key names: JoyPro's `TOOLS\Conversions\X4Keys.tsv` |
| Elite Dangerous | `elite refresh-db` | stock and user `.binds`; `DeviceMappings.xml` read from the install | 201 | 201 | 201 | key names: JoyPro's `TOOLS\Conversions\EliteKeys.tsv` |
| Arma 3 / Arma 2 OA / Arma 2 | `arma3` / `arma2oa` / `arma2 refresh-db` | **the game config** (`bin.pbo` > `config.bin`: `CfgDefaultKeysPresets` / `CfgDefaultKeysMapping`) with the English names of `languagecore`'s stringtable, no first start needed; the user's profile when there is one; JoyPro's axis relations | 37 / 20 / 19 | 51 / 68 / 66 | 51 / 68 / 66 | — (every dev id is rebuilt) |
| Arma Reforger | `reforger refresh-db` | `.pak` files + localization | 15 | 15 | 15 | — |
| Nuclear Option | `nuclearoption refresh-db` | `resources.assets` | 95 | 95 | 95 | — |
| Tiny Combat Arena | `tca refresh-db` | `sharedassets` (Rewired) | 97 | 97 | 97 | — |
| VTOL VR native | `vtolvr-native refresh-db` | `level0` | 3 | 3 | 3 | — |
| VTOL VR (BYOJoystick) | `vtolvr refresh-db` | **the installed mod's `BYOJoystick.dll`** (Steam Workshop item; GUI: Install BYOJoystick on the VTOL VR card), else the user's own `BYOJ_*.xml` | failed | failed (the mod is not installed on a test PC) | 433 | the full list after the user installs the mod; the reader is tested with a stand-in DLL only |
| WARDOGS | `wardogs refresh-db` | the user's `GameUserSettings.ini` + JoyPro's axes | 28 | 28 | 28 | — |
| Hell Let Loose: Vietnam | `hllv refresh-db` | **JoyPro's own table** of the three pad layouts and pilot keys; the game's files only with an Oodle library (the game ships none; another installed game's only after the user's yes, asked when the list is built) | 28 (read silently with Battlefield 6's Oodle) | 28 (own table, no yes given) | — | the game's own wording only with the user's consent to another game's Oodle library |
| G-Rebels | `grebels refresh-db` | **JoyPro's own table** (axis roles, buttons) | failed (no refresh) | 7 | 7 | — |
| Battlefield 6 / ACE COMBAT 8 | `bf6` / `ac8 refresh-db` | the user's profile / `System.sav` | 65 / 13 | same | — | — |
| ACE COMBAT 7 | `ac7 refresh-db` | `Input.ini` the game writes | failed | failed | — | the game must have been started once |
| IL-2 Sturmovik: 1946 | `il21946 refresh-db` | **JoyPro's own command list** + the commands `Users\default.ini`, `conf.ini` and the pilots' `settings.ini` name | 187 | 187 | 125 | titles of commands JoyPro does not list are humanized ids |
| Cliffs of Dover / Blitz | `il2cod` / `il2codblitz refresh-db` | **JoyPro's own command list** + the user's and the install's `confUser.ini` | 227 / 228 | same | 106 | as above |
| FSX / FSX-SE / Prepar3D v4-v6 | `fsx` / `fsxse` / `p3d4` / `p3d5` / `p3d6 refresh-db` | **JoyPro's own event list** (public SDK ids, own titles) + the events of the user's control files, FSX's install template, Prepar3D's `Standard.xmlx` | 149 own (not installed) | same | 149 | — |

`tools\build_local.ps1 -ReleaseData` builds a test copy without game data to try the first start like a user.

Rules that apply to every exporter:
1. Only flight actions from the game's catalog are written. Every other line, block or byte of the file stays as it was.
2. Refuse to write while the game is running (the games rewrite their files on exit).
3. Call `GameExportSafety.BackupBeforeOverwrite` before the first write to each file: it copies the file to `<file>.jpbak` and into the versioned backup store (`GameBackups`, see [../backups.md](../backups.md)). Registry games (Nuclear Option) put a snapshot of the values they may change into the same store (`GameBackups.ProtectRegistry`).
4. Never guess identifiers. If a device, action or field shape cannot be matched, skip it with a warning.
5. Clean and nuke (`--mode clean`, `--nuke-untouched-devices`):
   - Flight-only games (DCS, IL-2, IL-2 Korea, IL-2 1946, Cliffs of Dover, Falcon BMS, X-Plane, MSFS, FSX, Prepar3D): JoyPro takes over whole devices. Clean clears every other bind of the devices the export writes; nuke clears the devices it does not use (DCS: all of the plane's default binds are removed as well).
   - Games with other vehicles or on-foot play (Star Citizen, War Thunder, Arma 3, Arma Reforger, WARDOGS, Nuclear Option (its "Flight Controls" category)): clean and nuke only release cataloged aircraft, helicopter or spaceship actions, on the written devices and the unused devices respectively.
6. Keyboard binds are imported and exported only with `--include-keyboard` (GUI: "Include keyboard binds"), and only for flight actions.

Local research samples (third-party configs, not versioned) are in `research-samples/` when present. The author's
research notes and helper scripts (per-game surveys, in-game test notes) are private and not part of the repository
(`private/`, ignored).
