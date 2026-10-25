# GUI / CLI parity

Question: can everything the engine command line does also be done in the GUI, well and simply, by the least
technical user imaginable?

Short answer: yes for everything a normal user needs. Every engine command a player uses has a button, picker or
wizard in the GUI with plain words, safe defaults, a question before anything that removes binds or changes files,
and a way back (backups, "Undo export", a copy of the profile before a merge, device removal or a start-over
import). What is left on the command line only are developer tools (fake servers, test helpers, wiki generation)
and expert listings that a player never needs. A raw "Advanced console" box exists but is **not** counted as GUI
support anywhere in this table.

Checked against `engine/docs/ENGINE_CLI_WIKI.md` and the usage text of `engine/Program.cs` (engine at `7a2b84a`),
GUI after the novice pass (Start page game cards and import wizard, export in three steps, device cards, icons).

## How each command was judged

| Rating | Meaning |
|---|---|
| **FULL** | Findable from a page or card, labelled in plain words, no typed paths, ids or JSON, safe default, feedback (status symbol or text), a question and backup/undo where something is removed. Was already so before this pass. |
| **IMPROVED** | Was in the GUI but technical (typed paths/names, raw keywords, hidden, no confirmation or undo); fixed in this pass. Now FULL. |
| **EXPERT** | In the GUI, in an "(for experts)" expander or the expert import form: works without typing commands, but uses technical words or typed paths. Only experts need it; the normal path is FULL elsewhere. |
| **CLI-ONLY** | Not in the GUI (the Advanced console does not count). The reason is given. |

Where the GUI parts live: the page navigation at the bottom (Start, Relations, Modifiers, Binds, Layout, Export, HUD,
Force Feedback, Settings, Advanced), the profile bar at the top, and the confirmation dialog (`ConfirmWindow`).

## Profile

| CLI | GUI place | Rating | Novice notes |
|---|---|---|---|
| `profile new <path>` | Top bar: **New profile** (no dialog: an automatic name in Documents\JoyPro2\profiles, **More > Rename...**) | FULL | Also made automatically by the first import. |
| `profile load <path>` | Top bar: **Open profile...** (file picker), **More > Open last profile**; the last profile opens by itself at start (Settings > Startup) | FULL | The open profile's name is shown as a card; the path is a tooltip (was a typed path box: IMPROVED). |
| `profile save` / `save-last` | Autosave (top bar chip `Saving…` / `Saved ✓`, Ctrl+S), **More > Save as...**; "Save as last profile" in Engine options (advanced) | FULL | Every change is saved by itself, atomically ([profile history](profile-history.md)); save-last is in the expert options. |
| `profile history list / show / diff` | Top bar: **History** panel (versions with what changed, **Details**, **Compare**) | FULL | Plain words ("Relation 'Throttle ALL' created, 42 actions added", "Before export to DCS"). |
| `profile history undo / redo / restore` | Top bar: **Undo** / **Redo** (Ctrl+Z / Ctrl+Y); History **Restore** | FULL | Across sessions; a restore asks first and can be undone. |
| `profile history keep / prune / usage` | History: **Keep** per version, **Clean up**, the space used in the panel's first line | FULL | The clean-up also runs by itself in the background. |
| `profile history checkpoint`, `--history-note` | Automatic: a version before every export and import; the GUI names every change | FULL | |
| `profile summary`, `summary` (engine summary) | Start: "Information for experts" | EXPERT | Output panel text. |
| `profile validate` | Export step 1 **Check now**; bottom bar **Check** | FULL | Problems listed in words on the Export page. |
| `profile games` | Export step 3: one tile per game with cover and bound count | FULL | |
| `profile merge <left> <right> --mode --save` | Top bar: **Add another profile...** (file picker) + "The other profile wins / My profile wins" | IMPROVED | Was two typed paths and `right/left`. Now asks first and keeps `name.before-merge-<time>.pr0file`. |
| Global `--load-corrections`, `--auto-add-db-items`, `--remap-missing-devices`, `--correction-mode`, `--correction-auto-threshold`, `--correction-report`, `--status`, `--benchmark` | Top bar: Engine options (advanced) | EXPERT | Safe defaults are on; nobody needs to touch them. `--status`/`--progress` lines always feed the busy area; the option only keeps the status lines in the output pane. |
| Global `--out`, `--editor`, `--text` | Advanced page | EXPERT | Only meaningful for typed commands. |

## Import, export, backups (per game)

| CLI | GUI place | Rating | Novice notes |
|---|---|---|---|
| `dcs import`, `il2 import`, `starcitizen import`, `<game> import` (all 17 game areas) | Start: **Your games** card > **Import** > wizard (1 pick game, 2 choose how, 3 done) | IMPROVED | Was a form of typed paths and 12 check boxes. Wizard: "Add to my profile (recommended)" = `--merge`, "Start over from this game" = replace (asks, keeps a profile copy), "Also take the keyboard keys", DCS aircraft ticks. Result with a symbol and the relation count, shown over the game cards (they stay on the page after an import, a failed one or an error; leaving the page closes the result). |
| `dcs scan`, `il2 scan`, `starcitizen scan`, `<game> scan` | Import wizard **Look first** | IMPROVED | Read-only; now also works before a profile exists. |
| Import options `--path`, `--device(s)`, `--ignore-device(s)`, `--include-defaults`, `--import-inverted/slider/curvature/deadzone/saturation-x/-y`, `--ignore-easy-planes`, `--planes` CSV, `--source`, `--game-profile` | Start: **Expert import** expander | EXPERT | Paths are found automatically; the expert form is for portable installs and partial imports. |
| `dcs export`, `il2 export`, `starcitizen export`, `<game> export` | Export page step 3: game tiles, **Export now** / **Export ticked games**; Start card **Export** | FULL | DCS tile opens its aircraft with pictures. Tiles show a green tick or a red mark with the result. |
| `--mode clean\|add\|merge\|merge-overwrite` | Export page step 2: four described choices | IMPROVED | Was a raw keyword box (twice). "Update, keep the game's binds" (merge, default), "Update, my profile wins", "Only add", "Replace on my devices" (clean, warning symbol). Clean asks before every export. |
| `--nuke-untouched-devices` | Export step 2: "Also clear my other devices" | IMPROVED | Warning symbol; asks before the export. |
| `--dry-run` (registry games) | Tile **Preview** | IMPROVED | Shows the changes in the output panel, writes nothing. |
| `--keep-keyboard-defaults`, `--clean-all-aircraft`, `--prefer-local-order`, `--include-keyboard`, `--aircraft`, `--path`, `--source` | Export: **Options and paths (for experts)** | EXPERT | Defaults are the safe ones. |
| `backups list` / `show` | Export: **Game config backups** (game picker, table, Show Files) | FULL | |
| `backups restore <id> [--dry-run]` | Export: Preview Restore / **Restore** | IMPROVED | Now asks first (close the game, can be undone). |
| `backups restore latest --game <id>` | Tile **Undo last export**; Start card **Undo export** | IMPROVED | New: one click per game, asks first. |
| `games installs` | Settings > Games (cards with status symbols); Start cards | FULL | |
| `games install set/clear` | Settings > Games: **Choose folder...** (folder picker), **Use detected** | FULL | |
| `games install disable/enable` | Start page: hide button on a game card, "Hidden games" > **Show again**; Settings > Games: **Hide** / **Show again**; first-start setup | FULL | |
| `games config set/clear` | Settings > Games > **Run setup again** (game setup window, Browse) | FULL | |
| `games paths`, `<game> paths` | Settings: "Command lists (DB) and game paths" expander; Expert import **Detect** | EXPERT | The paths are used automatically. |
| `games integrations` | Used automatically for the Start cards; Expert import **Load Games** | FULL | |
| `games support` | Start: **Supported games** next to "Your games" (the page opens in the browser) | FULL | `--markdown` / `--notes` are for the maintainer's page (tools/game_support.ps1). |
| `starcitizen list-devices` | Expert import **Refresh Star Citizen** | EXPERT | |

## Game command lists (DB)

| CLI | GUI place | Rating | Novice notes |
|---|---|---|---|
| `db check-updates [--accept ...]` | Start: "Command lists to build or update" notice (Build List / Refresh DB / Keep DB per game, Build All Missing Lists); Start card "No command list yet" / "Game updated: update its command list"; after the first-start setup the missing lists are built | FULL | `no-catalog` = no list yet (fresh install ships none). |
| `dcs refresh id` (headless) | Start card **Update** (DCS stays closed) | IMPROVED | Was in Settings with paths and legacy options. |
| `db refresh-il2`, `db refresh-starcitizen`, `<game> refresh-db` | Start card **Update** | IMPROVED | One button per game; result on the card; DCS also builds its clean profiles the first time. |
| `warthunder refresh-db [--no-game-archives]` | Settings > Games: "War Thunder: read the full command list from the game's archives" (on by default; unticked passes `--no-game-archives`) | FULL | |
| `hllv oodle`, `hllv refresh-db --oodle-dll <path>` | Building / updating the HLL Vietnam list asks once whether another game's Oodle library may be used (Yes / No, remembered); Settings > Games: "use the Oodle library of another installed game", "Ask again" | FULL | Without a yes JoyPro's own list. |
| `vtolvr mod status`, `vtolvr mod prepare --accept-license` | VTOL VR card: **Install BYOJoystick** (what it is, source, size, license, consent tick; Steam opens the Mod Loader / Workshop page), **Check again** (status, SHA-256, then `vtolvr refresh-db`) | FULL | The mod comes from the Steam Workshop; JoyPro ships nothing of it. |
| `dcs refresh clean`, `--plane(s)`, `--dry-run`, SC `--branch`, registry DB source path | Settings: "Command lists of DCS, Star Citizen and IL-2 (for experts)"; Expert import "DB Source" | EXPERT | |
| `dcs refresh preview-clicks`, `--ocr/--launch/--keep-running/--clicks-xml` | Same expert section, legacy click mode | EXPERT | Old way; headless is the default. |
| `db summary/list/check/games/planes/fingerprint` | Settings: "Command lists (DB) and game paths: information for experts" | EXPERT | Listings for support. |
| `<game> catalog` | Not a button: the relation node search reads the catalogs directly | CLI-ONLY | A raw listing; its content is searchable on the Relations page. |

## Devices

| CLI | GUI place | Rating | Novice notes |
|---|---|---|---|
| `devices list [--connected] [--profile-devices]` | Settings > **Devices** cards (connected and/or used in the profile) | IMPROVED | Were two plain name lists. |
| `games devices`, `hud devices` | Device cards (product picture, axes, buttons, hats); HUD page | IMPROVED | Pictures of the exact model. |
| `devices exchange <old> <new>` | Settings > Devices > "Device lists and names (for experts)" (Exchange Old/New) | EXPERT | Renames the device only; the inputs stay. |
| `devices replace <old> <new> [--mode] [--map] [--merge] [--dry-run]` | **Replace device** window (device card **Replace...**, Settings > Devices, Layout device list, Binds > More, the remap notice): preview per input, changeable, merge choice | IMPROVED | Moves binds input by input (same ids or by layout); one Undo. |
| `devices auto-remap [--dry-run]` | Runs when a profile opens (Engine options: Remap Missing Devices); notice with Undo | IMPROVED | Devices with a new ID (new PC) get their binds by themselves. |
| `devices remove <device>` | Device card **Remove** | IMPROVED | Was a typed name. Asks first, keeps a profile copy. |
| `devices inputs` | Settings > Devices > "Device lists and names (for experts)" | EXPERT | |
| `layout identify` | Layout page (identify per device) | FULL | |
| `devices kb list/show/identify` | Relations > Auto Assign (devices, grips, catalog search, device layout) | FULL | |

## Relations, binds, modifiers

| CLI | GUI place | Rating | Novice notes |
|---|---|---|---|
| `relations list/show/add` | Relations page (list, the relation editor, New relation / Start relation from a search hit, Press a button) | FULL | |
| `relations group list/add/remove/rename` | Relations: Group Management, group chips | FULL | |
| `relations item add/remove/enable/disable` | Relations: the editor's actions (remove); adds go through `suggest apply` | FULL | enable/disable: "dismissed" is the switched-off state. |
| `relations search` (`--plane`, `--axis/--button`, `--limit`) | Relations: Add actions (search as you type, tick rows, Add selected / Create relation; game chips, Axes/Buttons, aircraft) | FULL | `--game` and `--category` are CLI-only: the page filters the games of the result with its chips. |
| `relations assistant` (`--relation`, `--llm`, `--max-steps`, `--show-prompt`) | Relations: JoyPro assistant, Expand with AI (applies at once, one Undo per answer) | FULL | `--show-prompt` and `--max-steps` are CLI-only (for experts). |
| `relations rename` | Relations: the open relation's name, edited in place | FULL | |
| `relations chat` (`--llm`, `--show-prompt`, `--apply`) | (the JoyPro assistant replaced the chat box) | CLI-ONLY | The diff-then-apply chat of one relation stays on the CLI. |
| `relations resolve`, `relations functions` | Relations: the JoyPro assistant's `resolve` tool and its offline brain use them | CLI-ONLY | `relations functions` is for maintainers; `--per-game` and `--batch` of `search` are for tools. |
| `relations mass delete/duplicate/merge/split/add-prefix/add-postfix/remove-part` | Relations: Mass Operations (selection in the list) | FULL | Collapsed by default. |
| `relations mass export`, `relations export/import <json>` | Relations: Import/Export Relations JSON... (file pickers) | FULL | |
| `relations edit`, `binds edit` (text editor round trip) | Advanced page buttons | EXPERT | Editing JSON by hand is for experts by nature. |
| `relations suggest` (`--game`) / `suggest apply` (`--add/--never/--remove/--move/--take`) / `suggest --all` | Relations: Suggested for this relation / Suggest missing (sections, Add ticked, Add, Add all, dismiss, Restore, Move here, Looks wrong; More > Next relation with suggestions) | FULL | |
| `relations suggest --all --missing [--per-relation N]` | Relations: More > Suggest missing for every relation... (review, tick, Add ticked, Open) | FULL | |
| `relations browse` (`catalog`) `[--game g --plane p] [--axis/--button]` | Relations: Open catalog / Catalog tab (game, aircraft, categories, used and bound marks, Add selected) | FULL | |
| `relations auto-group` | Relations: Auto-build relations from all my actions > By function (presets in words, preview, apply) | FULL | |
| `relations auto-assign` | Binds: Put relations on my buttons automatically (devices, grips, modifiers, mode switch, keep my layout, explain) | FULL | |
| `arrange ask/apply/prefs` | Binds: Lay out with AI... (words, examples, preview grouped with ticks, Apply, Undo, kept preferences, forget) | FULL | `--forget-saved`, `--games`, `--dry-run` are CLI-only. |
| `setup devices/aircraft/plan/render/apply` | Start: Set up my devices automatically... / Binds: Set up my devices automatically... (Setup wizard: roles, priorities, modifiers, mode switch, realism, review, apply) | FULL | The GUI runs the same commands; `--seed`, `--replace-existing`, `--force` are CLI-only. |
| `setup check` | (the Setup wizard's plan keeps these rules by itself) | CLI-ONLY | For tests and the combination matrix. |
| `relations from-text`, `from-text --check` | Relations: Auto-build relations from all my actions > By description | FULL | `--check` is CLI-only. |
| `llm status/recommend/download/remove/enable/disable/cancel` | Relations: Local AI model (optional) | FULL | Same: parallel work, not changed here. |
| `binds list/show/set/clear` | Binds page (record a button, quick bind, clear) | FULL | "Pick the input by hand" lists only real game controllers (the device cards: no driver entries, no vJoy, one entry per device, names without GUID unless two are alike) and the picked device's inputs that fit the relation (axis: its axes; button: its buttons and hat directions); the keyboard only for a button relation. After every bind change the bound counts (Bind Studio, Start page, next steps, device cards) follow and the status line names the bind. |
| `binds modifier list/add/remove/rename` | Binds: Bind Modifiers; Modifiers page | FULL | |
| `binds unbind` | Binds: Unbind Selected Input; "Unbind Input (advanced)" with device picker | FULL | |
| `binds export/import <json>` | Binds: Import/Export Binds JSON... (file pickers) | FULL | |
| `binds tuning models/show/set/clear` (base, game, aircraft, family scopes) | Binds: **Axis Tuning** (curve editor per scope, live preview) | FULL | |
| `binds tuning preview` | Axis Tuning live curve (computed in the GUI with the engine's own models) | FULL | |
| Legacy `binds set --inverted/--slider/--curvature/--deadzone/--saturation-*` | Binds: Bind Behavior (Advanced) | EXPERT | Axis Tuning is the simple way. |

## Force feedback, HUD, layouts

| CLI | GUI place | Rating | Novice notes |
|---|---|---|---|
| `ffb models/show/set/clear` (game FFB settings per device and aircraft) | Settings: "Force feedback settings of the games" (game, device and aircraft pickers, sliders, Apply/Revert/Clear) | FULL | |
| `dcs ffb list/set/clear` | Settings: "DCS force feedback: swap and invert per device" (table with Apply/Clear per row) | IMPROVED | Plain hint; **Clear All** now asks first. |
| `ffb telemetry status/effects/config show/set/clear` | **Force Feedback** page: Settings tab (game, aircraft, effects with sliders), Graph editor, Telemetry inspector | FULL | |
| `ffb telemetry setup dcs\|il2\|il2korea\|warthunder [--remove] [--dry-run]` | Force Feedback page: per game Set up / Remove / Check | FULL | Backed up by the engine. |
| `telemetry games / config show / config set` | **Settings > Telemetry recording**: the switch, folder (picker), values per second, space, delete after, raw data, one switch per game with what it reads | FULL | Off by default. |
| `telemetry sessions / show / preview / export / delete` | Settings > Telemetry recording: the sessions list, a timeline of the selected session, Export for Tacview (ACMI), Export CSV, Delete (asks first) | FULL | |
| `telemetry record`, `telemetry recover`, `telemetry prune` | automatic: the GUI records while a game runs and recording is on; cut-off sessions are finished and the retention runs by themselves | FULL | `record` also records when the settings say off (for experts). |
| `overlay dcs-hook status/install/remove` | HUD page: Install DCS hook / Remove / Check | FULL | |
| `overlay map`, `hud devices` | HUD page and the running HUD (automatic) | FULL | |
| `layout render` | Layout page > Cheat sheets (devices, games, aircraft, what to make incl. one page with all devices and white page, look, output folder, preview gallery, open folder) | FULL | Not on Export: it changes no game. Sheets with tables and lines are the Layout page's saved callout sheets (`--layout-map <device>=<callouts .json>`), the same picture as `layout callouts --png`. |
| `layout callouts` | Layout page > Device pictures > Tables (auto arrange, edit, save, share) | FULL | `--check-all` is a maintainer check (no GUI). |
| `layout titles` | Layout page > Device pictures > the preview box next to Tables | FULL | |
| `layout suggest`, `template-inspect`, `template-save` | Layout page > Device pictures (devices list, own picture, View / Edit markers / Bind on the picture canvas, tables and lines, save) | FULL | Marker editing is visual (drag with snapping, undo/redo); see docs/ui/layout-redesign.md. |
| `dcs strip-defaults apply/restore/status/auto`, `dcs device-luas list/remove/restore` | Settings > "DCS: default binds": the two switches, Preview files / Preview device files (dry run), Apply now, Restore DCS files, Check again; `auto` runs at every start | IMPROVED | Both off by default; each change asks first and shows the server integrity-check warning; moving device files rebuilds the DCS clean profiles. |
| `dcs defaults cut/restore` | Settings > "DCS: default binds" > Folders and older JoyPro backups: "Put back files an older JoyPro moved" (restore); cut is replaced by the device-files switch | IMPROVED | |
| `dcs verify-clean` | CLI only | CLI-ONLY | Maintainer check with DCS's own Lua (docs/games/dcs.md). |
| `media [games\|aircraft\|sources] [--no-internet-pictures]` | Automatic: covers on game cards and tiles (downloads follow Settings > Privacy "Game covers from the internet"), aircraft pictures; `sources` is CLI only | FULL | |
| `hud devices [--no-device-photos]` | Automatic (Start page device cards, Layout page, HUD); `--no-device-photos` while Settings > Privacy "Makers' product photos" is Never | FULL | The makers' photos are downloaded by the GUI per device after the user's yes (docs/privacy.md). |

## Watcher and sync

| CLI | GUI place | Rating | Novice notes |
|---|---|---|---|
| `sync status` | Settings > Games: "Watch game configs" and its status line | FULL | The watcher settings (tray, autostart) are owned by the parallel watcher work; not changed here. |
| `cockpits list` / `search` | Cockpits page: search box and filters (Kind, Game, Era, Role, Nation), aircraft list | FULL | |
| `cockpits get` | Cockpits page: the variant's drawings, tables, panels, sources; "Use this layout" (setup wizard) | FULL | |
| `cockpits render` | Cockpits page: the same sheets on screen (the PNG is CLI only) | PARTIAL | the GUI shows, the CLI writes the picture |
| `cockpits compare` | Cockpits page: "Compare with my setup" | FULL | |
| `cockpits photos` | Cockpits page: "Photos" (links; download after consent) | FULL | |
| `cockpits check` / `coverage` / `drawings` | (maintainer tools, `tools/cockpit_db.py`) | CLI ONLY | data checks |
| `sync diff` / `sync apply` / `sync accept` | The "take these changes?" dialog after a game closes (Take over / Not now / Ignore these, relation pickers per change) | FULL | |
| `vjoy functions list` / `add` / `set` / `remove` | Virtual Joystick page, Functions tab: type list + "Add function", the editor (inputs, outputs, edges on the axis bar, settings, steps), Save, On / off, Remove (asks first) | FULL | Outputs are picked by JoyPro; "Try it" previews without vJoy. |
| `vjoy functions test` | Functions tab editor: "Try it" (same evaluation as the forwarder) and the LIVE state during Test/Start | FULL | |
| `vjoy limits` | Virtual Joystick page, Overview > Games: "Reads of a joystick" and the orange limit warnings per game | FULL | |
| `vjoy configure-vjoy --max` | Virtual Joystick page, Overview > Status: "Set up vJoy for maximum" (asks first, says Windows must restart) | FULL | |

## App services (GUI only, no CLI command)

| Feature | GUI place | Rating |
|---|---|---|
| Problem report (zip with versions, devices, output, crash reports) | Settings: "Report a problem" | FULL |
| Crash notice | Start page notice: Report It / Dismiss | FULL |
| Updates of JoyPro (docs/updates.md) | Settings: "About JoyPro and updates"; More menu: Check for updates; the update dialog; the version in the corner | FULL |
| Required components | Settings: "Components" > "Needed by JoyPro"; Start notice | FULL |
| Optional components (vJoy, HidHide, ViGEmBus; GUI only, docs/components.md) | Settings: "Components" > "Optional"; Virtual Joystick page: "What this page needs" | FULL |
| `version` | The window's corner and Settings: "About JoyPro and updates" (version, build date, commit) | FULL |

## Left on the command line on purpose

| CLI | Why it stays CLI-only |
|---|---|
| `llm fake-server` | Developer test server for the local AI model; never needed by players. |
| `<game> catalog` | Raw action list (also in the DB table above); the GUI searches the same catalogs on the Relations page. |
| Wiki and benchmark generation (`engine/docs`, `tools/`), smoke/e2e helpers (`tools/e2e`) | Developer tools outside the engine's user commands. |

vJoy virtualization is not an engine command yet; its GUI page is being built by parallel work.

## Counts

Counted per row of the tables above (a row may group several subcommands or options of one feature):

| Rating | Rows |
|---|---|
| FULL (already simple before this pass) | 46 |
| IMPROVED in this pass (now simple) | 16 |
| EXPERT (in the GUI, expert area; the normal path is simple elsewhere) | 13 |
| CLI-ONLY (developer tools or a raw listing, reasons above) | 4 |

So 62 of 79 rows are simple for a novice (46 + 16), 13 are reachable without typing commands but meant for
experts, and 4 stay on the command line on purpose.

## What the novice pass changed (summary)

- **Icons everywhere**: `gui/JoyPro.Gui/Styles/Icons.axaml` is one shared icon set (Material-style path data, no
  downloaded images) with status colors; `Styles/IconStyles.axaml` gives icon buttons (`Classes="icon"`,
  `Tag="{StaticResource Icon.X}"`), the icon navigation, status symbols (`PathIcon.ok/.warn/.error/.info`), cards
  and wizard step numbers. Other pages (watcher settings, Group by description, vJoy) can reuse them.
- **Start page**: "Your games" cards with cover, installed / game-updated symbols and Import, Export, Update, Undo
  export; the import wizard; the old form is "Expert import".
- **Export page**: three numbered steps, the export mode as described choices, tiles with status symbols, Preview and
  Undo last export.
- **Settings**: device cards with product pictures, pickers instead of typed device names, plain DCS sections, expert
  listings in expanders.
- **Safety**: a confirmation dialog before restore/undo, clean or nuke exports, device remove/exchange, profile
  merge, start-over import, DCS defaults and clearing all DCS FFB settings; profile copies before profile-changing
  merges, device changes and start-over imports.

## Open points

- The offscreen render tests show layout, not exact colors (text and plain icons are drawn oddly offscreen); check
  the real window once on screen in light and dark mode.
- Registry games with a DB source other than the game's own file still take a typed source path (expert form).
- The Relations, Binds and Layout pages got icons on their titles only; their inner panels are unchanged (the
  Relations "Group by description / LLM" area belongs to parallel work).
- Localization (release plan) is not part of this pass; all new texts are English and short.
