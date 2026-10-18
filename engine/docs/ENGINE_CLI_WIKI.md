# JoyPro Engine CLI Wiki

This guide explains what JoyPro Engine is, why you would use it, and how to operate it for DCS, IL2, and Star Citizen control management.

`engine` is a command-line tool for building and maintaining one consistent control setup across games, aircraft, and joystick devices.

This page includes:
- A practical explanation of what problem JoyPro solves.
- A mental model of how the tool works internally.
- Plain command descriptions and workflows.
- Auto-generated command surface from current `engine --help`.

- Generated from current local `engine --help` output.
- Source of truth for command list: `engine --help`
- Regenerate: `powershell -ExecutionPolicy Bypass -File .\tools\update_cli_wiki.ps1`

## What The Program Does

DCS, IL2, and Star Citizen control files are powerful but hard to maintain long-term because:
- Controls are spread across many files per aircraft and per device.
- Device names and GUIDs can change when hardware or USB ports change.
- Rebinding and cleanup can be repetitive when you own multiple aircraft.
- Migrating setups between PCs or installs is error-prone.

JoyPro Engine solves this by giving you a single master mapping model and import/export tools around it:
- Import game bindings into one central file (`.pr0file`).
- Inspect and edit mappings structurally (relations, binds, modifiers, groups).
- Clean up or unbind in bulk using CLI operations.
- Export back to game-compatible formats when you are ready.

In short: JoyPro is useful when you want consistent controls over time, not one-off manual edits in game menus.

## Super Simple Explanation

Imagine each game control is a small toy block.

- A `node` is one toy block from one game and one plane.
- A `relation` is a box where you put toy blocks that mean the same thing (for example "Wheel Brake").
- A `bind` is a sticker on that box that says "press this joystick button".
- A `.pr0file` is your big toy shelf where all boxes and stickers are saved.

JoyPro helps you:
1. Collect toy blocks from games (import).
2. Put matching blocks into boxes (relations).
3. Put joystick stickers on boxes (binds/modifiers).
4. Send the result back into games (export).

## Core Concepts

- `Profile (.pr0file)`: Your master control library. It stores all logical actions and hardware assignments in one file.
- `Relation`: One logical action bucket that can group multiple equivalent game commands (`nodes`) across different planes and even different games.
- `Bind`: The physical input assignment attached to a relation (device + input token + optional modifiers). If a relation has a physical input attached, you effectively have a bind for that relation.
- `Node`: One concrete in-game command target. JoyPro models each game input command as a node, and a relation can contain multiple nodes.
- `Groups`: Simple tags for organizing relations (`Startup`, `Combat`, `Navigation`, etc.).
- `DB`: JoyPro's local reference data (games, aircraft/modules, metadata used by checks/listing).

How the tool works in principle:
1. Read game/profile data and model each mappable in-game command as a node.
2. Group nodes that mean the same action into one relation, including across multiple planes and games.
3. Attach hardware inputs to relations, creating/maintaining binds for those actions.
4. Manage and organize relations/binds (groups, enable/disable, add/remove/unbind) and save all of it in one `.pr0file`.
5. Export back to game-specific formats when you want to apply the setup in DCS/IL2.

## Quick Start (From Zero)

1. Verify engine and view command help:
```powershell
engine --help
engine summary
```

2. Check what devices are connected and what devices are already referenced:
```powershell
engine devices list --connected --text
engine devices list --profile-devices --text
engine devices inputs --text
```

3. Import controls from DCS or IL2:
```powershell
engine dcs import "C:\Users\<you>\Saved Games\DCS.openbeta" --text
engine il2 import "D:\IL-2 Sturmovik Great Battles" --text
```

4. Review what was created:
```powershell
engine relations list --text
engine binds list --text
```

5. Save your master control library:
```powershell
engine profile save "C:\profiles\my_setup.pr0file"
```

## Full Command Surface (Auto-Generated)

### Global Options
```text
  --profile <path>     Load/create this .pr0file and auto-save after mutating commands
  --out <path>         Write command output to file
  --editor <command>   Editor command for relations edit / binds edit (use {file} placeholder)
  --text               Text output instead of JSON for list/export/show/add
  --status true|false  Emit live status/progress updates to stderr (default: false)
  --progress true|false  Emit only the machine-readable progress lines to stderr: [engine-progress] <current>/<total> <phase>: <item> (default: false; --status true includes them)
  --benchmark true|false  Emit per-command timing summary to stderr (default: false)
  --load-corrections true|false   Apply load-time correction mechanics (default: true)
  --auto-add-db-items true|false  Auto-add corrected DB nodes during profile load (default: true)
  --remap-missing-devices true|false  Remap missing profile device names during load (default: true)
  --confirm-device-remap true|false   Ask before each load-time device remap (default: true)
  --correction-mode strict|balanced|aggressive  Confidence model for relation-item remaps (default: balanced)
  --correction-auto-threshold <0..200>  Minimum score to auto-apply relation-item remaps (default: mode-based)
  --confirm-corrections true|false   Ask for confirmation on medium-confidence item remaps (default: true)
  --correction-report <path>         Write load correction report (json, or text with .txt/.log)
  --history-note <text>  Name of this change in the profile history (default: the command's name)
```

### Commands
```text
  engine summary
  engine profile summary
  engine profile load <path.pr0file>
  engine profile save <path.pr0file>
  engine profile new <path.pr0file>
  engine profile save-last
  engine profile validate [--fail-on-warnings true|false]
  engine profile games   (games and DCS aircraft the profile has relations for, with how many are bound: what each export writes)
  engine profile merge <left.pr0file> <right.pr0file> [<more.pr0file> ...] [--mode left|right|ask] [--save <path.pr0file>]
  engine profile history [list]   (the versions of --profile, newest first: every save is one, kept in <data>\ProfileHistory; undo/redo state)
  engine profile history show <id> | diff <id> [<id>|current]   (what a version changed / the changes between two versions)
  engine profile history restore <id> | undo | redo   (writes a version back into the profile; that is a version too, so it can be undone)
  engine profile history keep <id> [on|off] | checkpoint [--label <text>]   (a kept version is never cleaned up; a checkpoint keeps the profile as it is now)
  engine profile history prune [--all] [--dry-run] | usage [--all]   (clean-up: all of 24 h, hourly 7 days, daily 60 days, weekly after; 200 MB per profile, 1 GB in total)
  engine dcs scan [<saved-games-path>] [--path <saved-games-path>] [--plane <name>] [--planes <csv>] [--device <name>] [--devices <csv>] [--ignore-device <name>] [--ignore-devices <csv>] [--include-keyboard true|false] [--ignore-easy-planes true|false] [--include-defaults] [--import-inverted] [--import-slider] [--import-curvature] [--import-deadzone] [--import-saturation-x] [--import-saturation-y] [--confirm true|false]
  engine dcs import [<saved-games-path>] [--path <saved-games-path>] [--merge] [--save-profile <path.pr0file>] [--save-last] [--plane <name>] [--planes <csv>] [--device <name>] [--devices <csv>] [--ignore-device <name>] [--ignore-devices <csv>] [--include-keyboard true|false] [--ignore-easy-planes true|false] [--include-defaults] [--import-inverted] [--import-slider] [--import-curvature] [--import-deadzone] [--import-saturation-x] [--import-saturation-y] [--confirm true|false]
  engine dcs export [<saved-games-path>] [--path <saved-games-path>] [--mode clean|add|merge|merge-overwrite] [--keep-keyboard-defaults true|false] [--nuke-untouched-devices true|false] [--clean-all-aircraft true|false] [--plane <name>] [--planes <csv>]   (only these aircraft; writes .diff.lua + .jp backup + modifiers.lua named as DCS names the device; clean: the written devices also lose binds and DCS defaults in every other aircraft unless --clean-all-aircraft false or a plane filter; nuke: devices without binds in an exported plane lose all binds there, DCS defaults included)
  engine dcs defaults cut [<saved-games-path>] [<dcs-install-path>] [--path <saved-games-path>] [--install-path <dcs-install-path>] [--backup-path <path>]   (moves stick-specific defaults from game folder into BACKUP_JOYSTICK_DEFAULTS)
  engine dcs defaults restore [<saved-games-path>] [<dcs-install-path>] [--path <saved-games-path>] [--install-path <dcs-install-path>] [--backup-path <path>]   (restores joystick defaults from BACKUP_JOYSTICK_DEFAULTS)
  engine dcs strip-defaults [apply|restore|status|auto] [--dry-run] [--restore] [--skip-ui-layer true|false] [--remember true|false] [--install-path <dcs-install-path>] [--saved-games <saved-games-path>] [--luae <luae.exe>] [--no-load-check]   (switches DCS's default joystick binds off in the DCS install: wraps every joystick profile lua, combos = nil, backup <file>.nocombo.bak; restore puts the originals back; auto re-applies after a DCS update; patched files fail server integrity checks)
  engine dcs device-luas [list|remove|restore] [--dry-run] [--remember true|false] [--install-path <dcs-install-path>] [--saved-games <saved-games-path>]   (moves the device-specific default files of the DCS install (<device type>.lua, template diffs) into JoyPro's backup folder so default.lua applies; restore puts them back)
  engine dcs verify-clean [<saved-games-path>] [--path <saved-games-path>] [--install-path <dcs-install-path>] [--plane <name>] [--planes <csv>] [--device <name>] [--devices <csv>] [--include-disconnected true|false] [--max-listed <n>]   (DCS's own Lua lists the default binds still active on the joysticks with the files of that Saved Games folder; read only)
  engine dcs refresh clean [<saved-games-path>] [<dcs-install-path>] [--path <saved-games-path>] [--install-path <dcs-install-path>] [--ocr true|false] [--launch true|false] [--keep-running true|false] [--headless-lua true|false] [--plane <name>] [--planes <csv>] [--dry-run] [--menu-wait-seconds <n>] [--ocr-menu-timeout-seconds <n>] [--settle-seconds <n>] [--default-stick <name>] [--clicks-xml <path>] [--clicks-dir <path>] [--dcs-exe <path>] [--preview-clicks] [--preview-image <path>] [--gui-scale <value>] [--window-left <n> --window-top <n> --window-width <n> --window-height <n>]
  engine dcs refresh id [<saved-games-path>] [<dcs-install-path>] [--path <saved-games-path>] [--install-path <dcs-install-path>] [--ocr true|false] [--launch true|false] [--keep-running true|false] [--headless-lua true|false] [--plane <name>] [--planes <csv>] [--dry-run] [--menu-wait-seconds <n>] [--ocr-menu-timeout-seconds <n>] [--modules-to-scan <n>] [--clicks-xml <path>] [--clicks-dir <path>] [--dcs-exe <path>] [--preview-clicks] [--preview-image <path>] [--gui-scale <value>] [--window-left <n> --window-top <n> --window-width <n> --window-height <n>]
  engine dcs refresh preview-clicks [--ocr true|false] [--path <saved-games-path>] [--install-path <dcs-install-path>] [--clicks-xml <path>] [--clicks-dir <path>] [--preview-image <path>] [--gui-scale <value>] [--window-left <n> --window-top <n> --window-width <n> --window-height <n>]
  engine dcs ffb list [--path <saved-games-path>] [--device <name>] [--devices <csv>] [--connected] [--profile-devices] [--confirm true|false]
  engine dcs ffb set <device-name> [--path <saved-games-path>] [--invert-x true|false] [--invert-y true|false] [--swap-axis true|false] [--confirm true|false]
  engine dcs ffb clear [<device-name>] [--path <saved-games-path>] [--device <name>] [--devices <csv>] [--all] [--confirm true|false]
  engine il2 scan [<il2-root-path>] [--path <il2-root-path>] [--device <name>] [--devices <csv>] [--ignore-device <name>] [--ignore-devices <csv>] [--import-inverted] [--import-curvature] [--import-deadzone] [--confirm true|false]
  engine il2 import [<il2-root-path>] [--path <il2-root-path>] [--merge] [--save-profile <path.pr0file>] [--save-last] [--device <name>] [--devices <csv>] [--ignore-device <name>] [--ignore-devices <csv>] [--import-inverted] [--import-curvature] [--import-deadzone] [--confirm true|false]
  engine il2 export [<il2-root-path>] [--path <il2-root-path>] [--mode clean|add|merge|merge-overwrite] [--nuke-untouched-devices true|false]   (conflict = same action on the same device; clean releases the other binds of the written devices, nuke the joysticks the profile does not use; keyboard lines stay)
  engine starcitizen scan [<sc-root-or-profile-path>] [--path <path>] [--source <path>] [--device <name>] [--devices <csv>] [--ignore-device <name>] [--ignore-devices <csv>] [--include-keyboard true|false] [--confirm true|false]
  engine starcitizen import [<sc-root-or-profile-path>] [--path <path>] [--source <path>] [--merge] [--save-profile <path.pr0file>] [--save-last] [--device <name>] [--devices <csv>] [--ignore-device <name>] [--ignore-devices <csv>] [--include-keyboard true|false] [--confirm true|false]
  engine starcitizen export [<sc-root-or-profile-path>] [--path <path>] [--source <path>] [--mode clean|add|merge|merge-overwrite] [--keep-keyboard-defaults true|false] [--prefer-local-order true|false] [--include-keyboard true|false] [--nuke-untouched-devices true|false]   (clean releases the ship binds of the written devices, nuke those of the devices the profile does not use; both switch off the game's default joystick binds on js1 when js1 is written (clean) or unused (nuke))
  engine starcitizen list-devices [<sc-root-or-profile-path>] [--path <path>] [--source <path>]   (aliases: devices, list)
  engine db summary [--keyboard] [--no-keyboard] [--strict-names]
  engine db list [filter] [--missing] [--keyboard] [--no-keyboard] [--strict-names]
  engine db check [--keyboard] [--no-keyboard] [--strict-names]
  engine db games [filter]
  engine db check-updates [--accept <game>] [--accept-all] [--no-record]   (games updated since their DB was built)
  engine db planes [filter] [--game <name>]
  engine db fingerprint
  engine db paths   (where the lists are read from: the game-data folder for everything built from your games, the program folder for JoyPro's own data)
  engine db refresh-il2 [<install, data\input folder or .actions file>] [--path <path>] [--db-path <folder>] [--dry-run]   (adds new actions of the game's key mapping files to DB\IL2\IL2.actions; also: engine il2 refresh-db)
  engine db refresh-starcitizen [<install-or-source-path>] [--install-path <path>] [--path <path>] [--source <path-to-source>] [--branch <name>] [--db-path <path>] [--include-user-profiles true|false] [--dry-run true|false]
  engine games installs [--game <id>]   (which supported games are installed and where; import/export/scan/refresh/restore are refused for the others)
  engine games install set <game> <folder> | clear <game>   (your installation folder of a game, e.g. a portable install; it then counts as installed and detection never replaces it; unset = clear)
  engine games install disable <game> [<game> ...] | enable <game> [<game> ...]   (games you do not have: they count as not installed and the GUI hides them; enable undoes it)
  engine games config set <game> <file or folder> | clear <game>   (your config location of a game, e.g. Saved Games\DCS.openbeta; it comes first wherever JoyPro looks for the game's controls; not for games that keep them in the installation)
  engine games paths [--game <name>] [--games <csv>]
  engine version
  engine games integrations
  engine games support [--markdown | --html | --json] [--notes <file>]   (how far JoyPro supports each game: the feature matrix from the code plus the verification notes, docs/games/support.md; exit 1 when the notes miss a game or claim more than the code)
  engine hud devices [--grip <device>=<catalog id>]... [--no-device-photos]   (connected devices for the HUD: product picture address, axes with their role, buttons, hats, layout picture with button positions; --no-device-photos leaves the makers' photo addresses out (the user said never); the engine downloads no device photo, the GUI does per device after the user's yes)
  engine overlay map [--game <id>] [--plane <name>] [--unit <DCS unit type>]   (bound buttons with modifiers and titles, for the overlay)
  engine overlay dcs-hook status|install|remove [--path <Saved Games\DCS>] [--dry-run]   (Export.lua addition that tells JoyPro the aircraft; backed up)
  engine media [games|aircraft|sources] [--no-internet-pictures]   (official pictures: game covers from Steam on this PC, else downloaded once from Steam's store or the publisher's site, else the game's executable icon; DCS aircraft pictures from the modules; sources = the web source per game)
  engine backups list [--game <id>] | show <id> | restore <id|latest> [--game <id>] [--dry-run]
  engine games devices   (connected devices with VID:PID, HID name and DirectInput axes/buttons/hats)
  engine sync status [--game <id>] [--path <config>]   (per game the config files / registry values JoyPro manages, their fingerprint and whether the game changed them since JoyPro's last export, accept or apply)
  engine sync accept --game <id> [--path <config>]   (the current config counts as known; nothing is imported)
  engine sync diff --game <id> [--path <config>]   (what the game changed that JoyPro manages, compared with the profile: added, removed, changed-tuning, changed-modifiers, with candidate relations)
  engine sync apply --game <id> --choices <file.json> [--path <config>]   (per change id: Target = relation name, "new" (+ Name) or "ignore"; then the config counts as known)
  engine vjoy status   (virtual joystick mode: vJoy and HidHide installed, vJoy devices, HidHide state and JoyPro's session, games native/virtual, the mapping table, what vJoy must offer; read-only)
  engine vjoy mode <game> native|virtual|xbox [--dry-run]   (per game: physical devices, the virtual joystick (vJoy, every used input gets a fixed vJoy slot) or the virtual Xbox 360 pad (ViGEmBus, for XInput-only or one-device games: the game's gamepad layout first, then sticks, triggers, buttons, D-pad))
  engine vjoy plan [--reset] [--prune]   (the mapping table and the vJoy configuration it needs; nothing is saved)
  engine vjoy map [--reset] [--prune] [--dry-run]   (saves the mapping table in the profile; slots never move unless --reset; --prune drops unused slots)
  engine vjoy settings [--first-device N] [--last-device N] [--auto-start on|off] [--hide-devices on|off] [--dry-run]   (which vJoy devices JoyPro may use, GUI auto start, HidHide on/off)
  engine vjoy exe <game> [--add <path>] [--remove <path>]   (the game executables HidHide blocks in virtual mode: found in the installation + your own)
  engine vjoy forwarder-config [--out <file>] [--xbox-game <game>] [--game <game>]   (the mapping table for JoyPro.VJoy.exe, the forwarding process; one Xbox game's pad table; with --game only that game's virtual functions)
  engine vjoy functions types   (the virtual function types: axis bands, button cycle, toggle, switch pulses, short/long/double press, shift layer, merge, split, curve, axis ends, axes to hat, buttons to axis, encoder, turbo, macro, hat to buttons, buttons to hat)
  engine vjoy functions list [--game <game>]   (the virtual functions per game with inputs, outputs (bind device and input), problems, limit warnings and the physical devices for inputs)
  engine vjoy functions add --game <game> --type <type> [--name <name>] [--input role=<device>|<input>]... [--output role=<target>[@<vJoy device>][:hold|enter|leave|both]]... [--param key=value]... [--edges 0.1,0.9] [--count n] [--tuning <axis tuning>] [--keyboard on|off] [--spec <json>|@<file>] [--no-auto-outputs] [--dry-run]   (adds a function; outputs left empty get free vJoy buttons/axes/hats (or Xbox pad controls), reserved in the table)
  engine vjoy functions set --game <game> --id <id> [same options as add] [--enabled on|off] [--auto-outputs] [--dry-run]   (changes a function; --spec replaces it)
  engine vjoy functions remove --game <game> --id <id> [--dry-run]   (removes a function; its outputs are free again)
  engine vjoy functions test (--game <game> --id <id> | --spec <json>|@<file>) [--stream "<ms>:<role>=<value>; ..."] [--rate Hz] [--duration ms]   (simulates the function with the forwarder's evaluation and lists every output change; nothing goes to vJoy, the pad or the keyboard)
  engine vjoy limits [--game <game>]   (what each game reads of one joystick: buttons, axes, hats, with confidence and source; vJoy offers 128 buttons, 8 axes, 4 hats)
  engine vjoy configure-vjoy [--max [--device N]] [--dry-run]   (runs vJoyConfig for the vJoy devices that lack axes, buttons or hats, or with --max sets 8 axes, 128 buttons, 4 hats; one administrator prompt; never shrinks a device; Windows must restart afterwards)
  engine vjoy apply-hidhide [--dry-run] [--owner-pid N] [--exe <game.exe>]... [--trusted <program.exe>]...   (hides the mapped physical devices from the virtual-mode games only; one administrator prompt; journal first)
  engine vjoy restore-hidhide [--dry-run]   (undoes exactly JoyPro's HidHide changes and restores the previous HidHide state; one administrator prompt)
  engine vjoy recover [--dry-run] [--force]   (restores a session whose owner (the GUI) is gone; --force: also while it runs)
  engine wardogs paths
  engine wardogs catalog
  engine wardogs scan [<config-path>] [--path <config-path>]
  engine wardogs import [<config-path>] [--path <config-path>] [--replace] [--include-keyboard]
  engine wardogs export [<config-path>] [--path <config-path>] [--mode clean|add|merge|merge-overwrite] [--dry-run] [--include-keyboard] [--nuke-untouched-devices]   (only aircraft/helicopter actions: clean and nuke release those on the written / unused devices)
  engine wardogs refresh-db [<source-path>] [--dry-run]   (builds the catalog from the installed game, or adds its new actions; writes it only when it changes)
  engine arma3 paths
  engine arma3 catalog
  engine arma3 scan [<config-path>] [--path <config-path>]
  engine arma3 import [<config-path>] [--path <config-path>] [--replace] [--include-keyboard]
  engine arma3 export [<config-path>] [--path <config-path>] [--mode clean|add|merge|merge-overwrite] [--dry-run] [--include-keyboard] [--nuke-untouched-devices]   (only aircraft/helicopter actions: clean and nuke release those on the written / unused devices)
  engine arma3 refresh-db [<source-path>] [--dry-run]   (builds the catalog from the installed game, or adds its new actions; writes it only when it changes)
  engine arma2oa paths
  engine arma2oa catalog
  engine arma2oa scan [<config-path>] [--path <config-path>]
  engine arma2oa import [<config-path>] [--path <config-path>] [--replace] [--include-keyboard]
  engine arma2oa export [<config-path>] [--path <config-path>] [--mode clean|add|merge|merge-overwrite] [--dry-run] [--include-keyboard] [--nuke-untouched-devices]   (only aircraft/helicopter actions: clean and nuke release those on the written / unused devices)
  engine arma2oa refresh-db [<source-path>] [--dry-run]   (builds the catalog from the installed game, or adds its new actions; writes it only when it changes)
  engine arma2 paths
  engine arma2 catalog
  engine arma2 scan [<config-path>] [--path <config-path>]
  engine arma2 import [<config-path>] [--path <config-path>] [--replace] [--include-keyboard]
  engine arma2 export [<config-path>] [--path <config-path>] [--mode clean|add|merge|merge-overwrite] [--dry-run] [--include-keyboard] [--nuke-untouched-devices]   (only aircraft/helicopter actions: clean and nuke release those on the written / unused devices)
  engine arma2 refresh-db [<source-path>] [--dry-run]   (builds the catalog from the installed game, or adds its new actions; writes it only when it changes)
  engine reforger paths
  engine reforger catalog
  engine reforger scan [<config-path>] [--path <config-path>]
  engine reforger import [<config-path>] [--path <config-path>] [--replace] [--include-keyboard]
  engine reforger export [<config-path>] [--path <config-path>] [--mode clean|add|merge|merge-overwrite] [--dry-run] [--include-keyboard] [--nuke-untouched-devices]   (only aircraft/helicopter actions: clean and nuke release those on the written / unused devices)
  engine reforger refresh-db [<source-path>] [--dry-run]   (builds the catalog from the installed game, or adds its new actions; writes it only when it changes)
  engine warthunder paths
  engine warthunder catalog
  engine warthunder scan [<config-path>] [--path <config-path>]
  engine warthunder import [<config-path>] [--path <config-path>] [--replace] [--include-keyboard]
  engine warthunder export [<config-path>] [--path <config-path>] [--mode clean|add|merge|merge-overwrite] [--dry-run] [--include-keyboard] [--nuke-untouched-devices]   (only aircraft/helicopter actions: clean and nuke release those on the written / unused devices)
  engine warthunder refresh-db [<source-path>] [--dry-run] [--no-game-archives]   (builds the catalog from the installed game, or adds its new actions; writes it only when it changes); War Thunder: the full list from gui.vromfs.bin, with --no-game-archives only JoyPro's own list
  engine xplane12 paths
  engine xplane12 catalog
  engine xplane12 scan [<config-path>] [--path <config-path>]
  engine xplane12 import [<config-path>] [--path <config-path>] [--replace] [--include-keyboard]
  engine xplane12 export [<config-path>] [--path <config-path>] [--mode clean|add|merge|merge-overwrite] [--dry-run] [--include-keyboard] [--nuke-untouched-devices]   (flight only: clean takes over the devices it writes, nuke clears unused devices)
  engine xplane12 refresh-db [<source-path>] [--dry-run]   (builds the catalog from the installed game, or adds its new actions; writes it only when it changes)
  engine xplane11 paths
  engine xplane11 catalog
  engine xplane11 scan [<config-path>] [--path <config-path>]
  engine xplane11 import [<config-path>] [--path <config-path>] [--replace] [--include-keyboard]
  engine xplane11 export [<config-path>] [--path <config-path>] [--mode clean|add|merge|merge-overwrite] [--dry-run] [--include-keyboard] [--nuke-untouched-devices]   (flight only: clean takes over the devices it writes, nuke clears unused devices)
  engine xplane11 refresh-db [<source-path>] [--dry-run]   (builds the catalog from the installed game, or adds its new actions; writes it only when it changes)
  engine msfs2024 paths
  engine msfs2024 catalog
  engine msfs2024 scan [<config-path>] [--path <config-path>] [--game-profile <name>]
  engine msfs2024 import [<config-path>] [--path <config-path>] [--replace] [--include-keyboard] [--game-profile <name>]
  engine msfs2024 export [<config-path>] [--path <config-path>] [--mode clean|add|merge|merge-overwrite] [--dry-run] [--include-keyboard] [--nuke-untouched-devices] [--game-profile <name>] [--aircraft <aircraft-key>]   (flight only: clean takes over the devices it writes, nuke clears unused devices)
  engine msfs2024 refresh-db [<source-path>] [--dry-run]   (builds the catalog from the installed game, or adds its new actions; writes it only when it changes)
  engine msfs2020 paths
  engine msfs2020 catalog
  engine msfs2020 scan [<config-path>] [--path <config-path>] [--game-profile <name>]
  engine msfs2020 import [<config-path>] [--path <config-path>] [--replace] [--include-keyboard] [--game-profile <name>]
  engine msfs2020 export [<config-path>] [--path <config-path>] [--mode clean|add|merge|merge-overwrite] [--dry-run] [--include-keyboard] [--nuke-untouched-devices] [--game-profile <name>] [--aircraft <aircraft-key>]   (flight only: clean takes over the devices it writes, nuke clears unused devices)
  engine msfs2020 refresh-db [<source-path>] [--dry-run]   (builds the catalog from the installed game, or adds its new actions; writes it only when it changes)
  engine bms paths
  engine bms catalog
  engine bms scan [<config-path>] [--path <config-path>]
  engine bms import [<config-path>] [--path <config-path>] [--replace] [--include-keyboard]
  engine bms export [<config-path>] [--path <config-path>] [--mode clean|add|merge|merge-overwrite] [--dry-run] [--include-keyboard] [--nuke-untouched-devices]   (flight only: clean takes over the devices it writes, nuke clears unused devices)
  engine bms refresh-db [<source-path>] [--dry-run]   (builds the catalog from the installed game, or adds its new actions; writes it only when it changes)
  engine il2korea paths
  engine il2korea catalog
  engine il2korea scan [<config-path>] [--path <config-path>]
  engine il2korea import [<config-path>] [--path <config-path>] [--replace] [--include-keyboard]
  engine il2korea export [<config-path>] [--path <config-path>] [--mode clean|add|merge|merge-overwrite] [--dry-run] [--include-keyboard] [--nuke-untouched-devices]   (flight only: clean takes over the devices it writes, nuke clears unused devices)
  engine il2korea refresh-db [<source-path>] [--dry-run]   (builds the catalog from the installed game, or adds its new actions; writes it only when it changes)
  engine x4 paths
  engine x4 catalog
  engine x4 scan [<config-path>] [--path <config-path>]
  engine x4 import [<config-path>] [--path <config-path>] [--replace] [--include-keyboard]
  engine x4 export [<config-path>] [--path <config-path>] [--mode clean|add|merge|merge-overwrite] [--dry-run] [--include-keyboard] [--nuke-untouched-devices]   (only aircraft/helicopter actions: clean and nuke release those on the written / unused devices)
  engine x4 refresh-db [<source-path>] [--dry-run]   (builds the catalog from the installed game, or adds its new actions; writes it only when it changes)
  engine elite paths
  engine elite catalog
  engine elite scan [<config-path>] [--path <config-path>]
  engine elite import [<config-path>] [--path <config-path>] [--replace] [--include-keyboard]
  engine elite export [<config-path>] [--path <config-path>] [--mode clean|add|merge|merge-overwrite] [--dry-run] [--include-keyboard] [--nuke-untouched-devices]   (only aircraft/helicopter actions: clean and nuke release those on the written / unused devices)
  engine elite refresh-db [<source-path>] [--dry-run]   (builds the catalog from the installed game, or adds its new actions; writes it only when it changes)
  engine vtolvr paths
  engine vtolvr catalog
  engine vtolvr scan [<config-path>] [--path <config-path>]
  engine vtolvr import [<config-path>] [--path <config-path>] [--replace] [--include-keyboard]
  engine vtolvr export [<config-path>] [--path <config-path>] [--mode clean|add|merge|merge-overwrite] [--dry-run] [--include-keyboard] [--nuke-untouched-devices]   (flight only: clean takes over the devices it writes, nuke clears unused devices)
  engine vtolvr refresh-db [<source-path>] [--dry-run]   (builds the catalog from the installed game, or adds its new actions; writes it only when it changes)
  engine vtolvr mod status|prepare [--accept-license]   (the BYOJoystick mod on this PC: Mod Loader, Workshop item, BYOJoystick.dll with SHA-256 and the targets JoyPro reads; prepare backs up the BYOJ_<seat>.xml files and returns the Steam pages to open (Mod Loader, Workshop item 3276352374); Steam downloads and installs the mod, JoyPro ships none of it; refresh-db then reads the targets from the installed mod)
  engine vtolvr-native paths
  engine vtolvr-native catalog
  engine vtolvr-native scan [<config-path>] [--path <config-path>]
  engine vtolvr-native import [<config-path>] [--path <config-path>] [--replace] [--include-keyboard]
  engine vtolvr-native export [<config-path>] [--path <config-path>] [--mode clean|add|merge|merge-overwrite] [--dry-run] [--include-keyboard] [--nuke-untouched-devices]   (flight only: clean takes over the devices it writes, nuke clears unused devices)
  engine vtolvr-native refresh-db [<source-path>] [--dry-run]   (builds the catalog from the installed game, or adds its new actions; writes it only when it changes)
  engine nuclearoption paths
  engine nuclearoption catalog
  engine nuclearoption scan [<config-path>] [--path <config-path>]
  engine nuclearoption import [<config-path>] [--path <config-path>] [--replace] [--include-keyboard]
  engine nuclearoption export [<config-path>] [--path <config-path>] [--mode clean|add|merge|merge-overwrite] [--dry-run] [--include-keyboard] [--nuke-untouched-devices]   (only aircraft/helicopter actions: clean and nuke release those on the written / unused devices)
  engine nuclearoption refresh-db [<source-path>] [--dry-run]   (builds the catalog from the installed game, or adds its new actions; writes it only when it changes)
  engine tinycombatarena paths
  engine tinycombatarena catalog
  engine tinycombatarena scan [<config-path>] [--path <config-path>]
  engine tinycombatarena import [<config-path>] [--path <config-path>] [--replace] [--include-keyboard]
  engine tinycombatarena export [<config-path>] [--path <config-path>] [--mode clean|add|merge|merge-overwrite] [--dry-run] [--include-keyboard] [--nuke-untouched-devices]   (flight only: clean takes over the devices it writes, nuke clears unused devices)
  engine tinycombatarena refresh-db [<source-path>] [--dry-run]   (builds the catalog from the installed game, or adds its new actions; writes it only when it changes)
  engine grebels paths
  engine grebels catalog
  engine grebels scan [<config-path>] [--path <config-path>]
  engine grebels import [<config-path>] [--path <config-path>] [--replace] [--include-keyboard]
  engine grebels export [<config-path>] [--path <config-path>] [--mode clean|add|merge|merge-overwrite] [--dry-run] [--include-keyboard] [--nuke-untouched-devices]   (flight only: clean takes over the devices it writes, nuke clears unused devices)
  engine grebels refresh-db [<source-path>] [--dry-run]   (builds the catalog from the installed game, or adds its new actions; writes it only when it changes)
  engine hllv paths
  engine hllv catalog
  engine hllv scan [<config-path>] [--path <config-path>]
  engine hllv import [<config-path>] [--path <config-path>] [--replace] [--include-keyboard]
  engine hllv export [<config-path>] [--path <config-path>] [--mode clean|add|merge|merge-overwrite] [--dry-run] [--include-keyboard] [--nuke-untouched-devices]   (only aircraft/helicopter actions: clean and nuke release those on the written / unused devices)
  engine hllv refresh-db [<source-path>] [--dry-run] [--oodle-dll <path>]   (builds the catalog from the installed game, or adds its new actions; writes it only when it changes)
  engine hllv oodle [--path <game folder>]   (the game's own Oodle library, or one another installed game ships; refresh-db --oodle-dll <path> uses that one only with the user's consent, from where it is, never copied; without it JoyPro's own list of the pad keys)
  engine bf6 paths
  engine bf6 catalog
  engine bf6 scan [<config-path>] [--path <config-path>]
  engine bf6 import [<config-path>] [--path <config-path>] [--replace] [--include-keyboard]
  engine bf6 export [<config-path>] [--path <config-path>] [--mode clean|add|merge|merge-overwrite] [--dry-run] [--include-keyboard] [--nuke-untouched-devices]   (only aircraft/helicopter actions: clean and nuke release those on the written / unused devices)
  engine bf6 refresh-db [<source-path>] [--dry-run]   (builds the catalog from the installed game, or adds its new actions; writes it only when it changes)
  engine bf2042 paths
  engine bf2042 catalog
  engine bf2042 scan [<config-path>] [--path <config-path>]
  engine bf2042 import [<config-path>] [--path <config-path>] [--replace] [--include-keyboard]
  engine bf2042 export [<config-path>] [--path <config-path>] [--mode clean|add|merge|merge-overwrite] [--dry-run] [--include-keyboard] [--nuke-untouched-devices]   (only aircraft/helicopter actions: clean and nuke release those on the written / unused devices)
  engine bf2042 refresh-db [<source-path>] [--dry-run]   (builds the catalog from the installed game, or adds its new actions; writes it only when it changes)
  engine ac8 paths
  engine ac8 catalog
  engine ac8 scan [<config-path>] [--path <config-path>]
  engine ac8 import [<config-path>] [--path <config-path>] [--replace] [--include-keyboard]
  engine ac8 export [<config-path>] [--path <config-path>] [--mode clean|add|merge|merge-overwrite] [--dry-run] [--include-keyboard] [--nuke-untouched-devices]   (only aircraft/helicopter actions: clean and nuke release those on the written / unused devices)
  engine ac8 refresh-db [<source-path>] [--dry-run]   (builds the catalog from the installed game, or adds its new actions; writes it only when it changes)
  engine ac7 paths
  engine ac7 catalog
  engine ac7 scan [<config-path>] [--path <config-path>]
  engine ac7 import [<config-path>] [--path <config-path>] [--replace] [--include-keyboard]
  engine ac7 export [<config-path>] [--path <config-path>] [--mode clean|add|merge|merge-overwrite] [--dry-run] [--include-keyboard] [--nuke-untouched-devices]   (flight only: clean takes over the devices it writes, nuke clears unused devices)
  engine ac7 refresh-db [<source-path>] [--dry-run]   (builds the catalog from the installed game, or adds its new actions; writes it only when it changes)
  engine pw paths
  engine pw catalog
  engine pw scan [<config-path>] [--path <config-path>]
  engine pw import [<config-path>] [--path <config-path>] [--replace] [--include-keyboard]
  engine pw export [<config-path>] [--path <config-path>] [--mode clean|add|merge|merge-overwrite] [--dry-run] [--include-keyboard] [--nuke-untouched-devices]   (flight only: clean takes over the devices it writes, nuke clears unused devices)
  engine pw refresh-db [<source-path>] [--dry-run]   (builds the catalog from the installed game, or adds its new actions; writes it only when it changes)
  engine il21946 paths
  engine il21946 catalog
  engine il21946 scan [<config-path>] [--path <config-path>]
  engine il21946 import [<config-path>] [--path <config-path>] [--replace] [--include-keyboard]
  engine il21946 export [<config-path>] [--path <config-path>] [--mode clean|add|merge|merge-overwrite] [--dry-run] [--include-keyboard] [--nuke-untouched-devices]   (flight only: clean takes over the devices it writes, nuke clears unused devices)
  engine il21946 refresh-db [<source-path>] [--dry-run]   (builds the catalog from the installed game, or adds its new actions; writes it only when it changes)
  engine il2codblitz paths
  engine il2codblitz catalog
  engine il2codblitz scan [<config-path>] [--path <config-path>]
  engine il2codblitz import [<config-path>] [--path <config-path>] [--replace] [--include-keyboard]
  engine il2codblitz export [<config-path>] [--path <config-path>] [--mode clean|add|merge|merge-overwrite] [--dry-run] [--include-keyboard] [--nuke-untouched-devices]   (flight only: clean takes over the devices it writes, nuke clears unused devices)
  engine il2codblitz refresh-db [<source-path>] [--dry-run]   (builds the catalog from the installed game, or adds its new actions; writes it only when it changes)
  engine il2cod paths
  engine il2cod catalog
  engine il2cod scan [<config-path>] [--path <config-path>]
  engine il2cod import [<config-path>] [--path <config-path>] [--replace] [--include-keyboard]
  engine il2cod export [<config-path>] [--path <config-path>] [--mode clean|add|merge|merge-overwrite] [--dry-run] [--include-keyboard] [--nuke-untouched-devices]   (flight only: clean takes over the devices it writes, nuke clears unused devices)
  engine il2cod refresh-db [<source-path>] [--dry-run]   (builds the catalog from the installed game, or adds its new actions; writes it only when it changes)
  engine fsx paths
  engine fsx catalog
  engine fsx scan [<config-path>] [--path <config-path>]
  engine fsx import [<config-path>] [--path <config-path>] [--replace] [--include-keyboard]
  engine fsx export [<config-path>] [--path <config-path>] [--mode clean|add|merge|merge-overwrite] [--dry-run] [--include-keyboard] [--nuke-untouched-devices]   (flight only: clean takes over the devices it writes, nuke clears unused devices)
  engine fsx refresh-db [<source-path>] [--dry-run]   (builds the catalog from the installed game, or adds its new actions; writes it only when it changes)
  engine fsxse paths
  engine fsxse catalog
  engine fsxse scan [<config-path>] [--path <config-path>]
  engine fsxse import [<config-path>] [--path <config-path>] [--replace] [--include-keyboard]
  engine fsxse export [<config-path>] [--path <config-path>] [--mode clean|add|merge|merge-overwrite] [--dry-run] [--include-keyboard] [--nuke-untouched-devices]   (flight only: clean takes over the devices it writes, nuke clears unused devices)
  engine fsxse refresh-db [<source-path>] [--dry-run]   (builds the catalog from the installed game, or adds its new actions; writes it only when it changes)
  engine p3d4 paths
  engine p3d4 catalog
  engine p3d4 scan [<config-path>] [--path <config-path>]
  engine p3d4 import [<config-path>] [--path <config-path>] [--replace] [--include-keyboard]
  engine p3d4 export [<config-path>] [--path <config-path>] [--mode clean|add|merge|merge-overwrite] [--dry-run] [--include-keyboard] [--nuke-untouched-devices]   (flight only: clean takes over the devices it writes, nuke clears unused devices)
  engine p3d4 refresh-db [<source-path>] [--dry-run]   (builds the catalog from the installed game, or adds its new actions; writes it only when it changes)
  engine p3d5 paths
  engine p3d5 catalog
  engine p3d5 scan [<config-path>] [--path <config-path>]
  engine p3d5 import [<config-path>] [--path <config-path>] [--replace] [--include-keyboard]
  engine p3d5 export [<config-path>] [--path <config-path>] [--mode clean|add|merge|merge-overwrite] [--dry-run] [--include-keyboard] [--nuke-untouched-devices]   (flight only: clean takes over the devices it writes, nuke clears unused devices)
  engine p3d5 refresh-db [<source-path>] [--dry-run]   (builds the catalog from the installed game, or adds its new actions; writes it only when it changes)
  engine p3d6 paths
  engine p3d6 catalog
  engine p3d6 scan [<config-path>] [--path <config-path>]
  engine p3d6 import [<config-path>] [--path <config-path>] [--replace] [--include-keyboard]
  engine p3d6 export [<config-path>] [--path <config-path>] [--mode clean|add|merge|merge-overwrite] [--dry-run] [--include-keyboard] [--nuke-untouched-devices]   (flight only: clean takes over the devices it writes, nuke clears unused devices)
  engine p3d6 refresh-db [<source-path>] [--dry-run]   (builds the catalog from the installed game, or adds its new actions; writes it only when it changes)
  engine layout render [--path <export-folder>] [--device <name>] [--devices <csv>] [--layout-map <joystick>=<path.layout>] [--game <name>] [--games <csv>] [--plane <name>] [--planes <csv>] [--include-overview true|false] [--include-planes true|false] [--skip-missing-layouts true|false] [--draw-markers true|false] [--draw-legend-table true|false] [--marker-size <n>] [--legend-width <n>] [--legend-max-rows <n>] [--legend-font-size <n>] [--modifier-colors <modifier=#RRGGBB,...>] [--button-symbol circle|square|triangle|diamond|bar] [--axis-symbol circle|square|triangle|diamond|bar] [--pov-symbol circle|square|triangle|diamond|bar] [--slider-symbol circle|square|triangle|diamond|bar] [--font <family>] [--font-size <n>] [--text-color <#RRGGBB|#AARRGGBB>] [--outline-color <color|none>] [--label-background <color|none>] [--style dark|light] [--print-width <px>] [--combine true|false] [--drawing-style lines|cel] [--drawing-preset joypro|manual|blueprint|print|custom] [--drawing-ink <#RRGGBB>] [--drawing-paper <#RRGGBB>] [--drawing-accent <#RRGGBB>]   (a --layout-map to a device layout .json with tables and lines draws JoyPro 1 style sheets; the --drawing-* options paint JoyPro's own drawings, default: the GUI's settings)
  engine layout callouts (--model <catalog id> | --layout <device-layout.json>) [--picture <file>] [--schematic] [--drawing-style lines|cel] [--drawing-preset <p>] [--drawing-ink|--drawing-paper|--drawing-accent <#RRGGBB>] [--arrange] [--device <name>] [--game <name> --plane <name>] [--sample] [--png <file>] [--light] [--print-width <px>] [--save <file.json>]   (tables around the device picture with orthogonal lines; --check-all [query] checks every model)
  engine layout titles --device <name>   (the game's own menu titles of the device's binds, per game and aircraft)
  engine layout suggest [--device <name>] [--devices <csv>] [--connected] [--profile-devices] [--legacy-layout-dir <path>] [--legacy-output-dir <path>] [--catalog <path.json>] [--apply true|false] [--apply-min-score <n>] [--backdrop <picture-file-or-image-url>]
  engine layout identify [--device <name>] [--devices <csv>] [--connected]   (vendor/model from VID:PID and HID name, picture search link)
  engine layout template-inspect --source <path.layout-or-image> [--max-markers <n>]
  engine layout template-save --source <path.layout-or-image> --out <path.layout> [--markers-json <file.json> | --markers <json-array-or-object>] [--tables-json <file.json> | --tables <json-array-or-object>] [--connectors-json <file.json> | --connectors <json-array-or-object>] [--replace-all true|false]
  engine devices list [--connected] [--profile-devices]
  engine devices inputs [--include-off]   (aliases: catalog, input-catalog)
  engine devices exchange <old-device> <new-device> [--old <name>] [--new <name>] [--confirm true|false]   (alias: swap; renames the device, inputs stay)
  engine devices replace <old-device> <new-device> [--mode auto|same|layout] [--map OLD=NEW]... [--merge add|keep|replace] [--grip <device>=<grip id>]... [--dry-run]   (moves binds and modifiers input by input: same ids, or by layout from the device catalog; --dry-run previews)
  engine devices auto-remap [--dry-run]   (alias: remap; a profile device that is not connected gets the connected device of the same product with a new GUID)
  engine devices remove <device-name> [--device <name>] [--confirm true|false]   (aliases: purge, clear)
  engine relations list [filter]
  engine relations show <relation-name>
  engine relations add <name> [--axis|--button|--type axis|button] [--node game:id[:plane1|plane2]] [--from relation] [--group g] [--device d --input i --modifier m]
  engine relations group list [relation-name]
  engine relations group add <relation-name> <group1> [group2] [...]
  engine relations group remove <relation-name> <group1> [group2] [...] [--all]
  engine relations group rename <old-group> <new-group>
  engine relations item add <relation-name> --node game:id[:plane1|plane2] [--node ...]  (or --id/--game/--planes)
  engine relations item remove <relation-name> --node game:id[:plane1|plane2] [--node ...] [--all]
  engine relations item enable <relation-name> --node game:id[:plane1|plane2] [--node ...]
  engine relations item disable <relation-name> --node game:id[:plane1|plane2] [--node ...]
  engine relations mass delete [--relation <name>] [--relations <csv>] [--query <text>] [--all]
  engine relations mass duplicate [--relation <name>] [--relations <csv>] [--query <text>] [--all]
  engine relations mass merge <name> [--relation <name>] [--relations <csv>] [--query <text>] [--all]
  engine relations mass split <postfix> [--plane <game:plane|plane>] [--planes <csv>] [--relation <name>] [--relations <csv>] [--query <text>] [--all]
  engine relations mass add-prefix <value> [--relation <name>] [--relations <csv>] [--query <text>] [--all]
  engine relations mass add-postfix <value> [--relation <name>] [--relations <csv>] [--query <text>] [--all]
  engine relations mass remove-part <value> [--relation <name>] [--relations <csv>] [--query <text>] [--all]
  engine relations mass export <path.rl|path.json> [--format rl|json] [--relation <name>] [--relations <csv>] [--query <text>] [--all]
  engine relations export <path.json>
  engine relations import <path.json>
  engine relations edit   (no args; exports temp relations JSON, opens editor, re-imports on close; uses --editor or notepad)
  engine relations suggest <relation>|--relation <relation> [--min high|medium|low] [--limit N] [--game g1|g2] | --all [--missing [--per-relation N]] [--min ...]
  engine relations suggest apply <relation> [--add game:id:plane1|plane2]... [--never game:id:plane1|plane2]... [--remove game:id:planes]... [--move game:id:planes=>relation]... [--take game:id:planes]...
  engine relations search "<words>" [--game g1|g2] [--plane p1|p2] [--category <name>] [--axis|--button] [--limit N] [--per-game N] [--batch <file>]
  engine relations browse [--game <game> [--plane p1|p2]] [--axis|--button]   (alias: relations catalog)
  engine relations resolve "<query>" [--game g1|g2] [--plane p1|p2] [--axis|--button] [--per-game N]
  engine relations functions [--category <id>] [--query "<words>"] [--coverage] [--dump <file.tsv>]
  engine relations chat <relation>|--relation <relation> "<message>" [--llm off|auto|on] [--show-prompt] [--apply]
  engine relations assistant "<message>" [--relation <name>] [--llm auto|on|off] [--max-steps N] [--show-prompt]
  engine relations rename <relation> <new name>
  engine relations aircraft <relation> [--node game:id]
  engine relations aircraft set <relation> --node game:id [--on p1|p2] [--off p3|p4]
  engine relations check-aircraft [--relation <name>] [--apply] [--include-same-function]
  engine relations meanings [--game g] [--top N]
  engine relations auto-group [--preset all|wing|era|purist] [--no-substitutes] [--planes p1|p2|Game] [--replace] [--dry-run] [--no-map]
  engine relations from-text "<request>" [--games g1|g2] [--planes p1|p2] [--llm off|auto|on] [--substitutes] [--select <relation>]... [--skip-node game:id:plane1|plane2]... [--name <name>] [--min-confidence 0..1] [--dry-run]
  engine relations from-text --check   (tests the vocabulary in DB/Functions/language.json against the catalog)
  engine relations from-text "<request>" --show-prompt [--prompt-model <model>|local]
  engine llm status|recommend [--ram-gb N] [--vram-gb N] [--gpu <name>] [--gpu-vendor nvidia|amd|intel] [--cores N] [--avx2 true|false]
  engine llm download [<model>] --accept-license [--runtime auto|cpu|vulkan|cuda] [--no-runtime]
  engine llm remove [<model>] [--all]   (--all also removes the runtime and switches the model off)
  engine llm enable [<model>|local] | engine llm disable   (use the local model for relations from-text)
  engine llm cancel   (stops a running download; the next download resumes it)
  engine llm find-local [--root <folder>]...
  engine llm use-local [--server <llama-server.exe>] [--model <file.gguf>] [--gpu-layers auto|all|N] [--context N] [--template server|chatml] | --clear
  engine llm fake-server [--port N] [--port-file <path>] [--response <file>] [--serve <folder>] [--log <file>] [--lifetime <s>]
  engine setup devices [--grip <device>=<grip id>]... [--role <device|VID:PID>=<role>]... [--add-device <catalog id>]...
  engine setup aircraft --games <g1,g2>   (the aircraft / games for the priority list)
  engine setup plan --games <g1,g2> [--priorities "A>B|C>D"] [--priority "B|C"]... [--modifiers 0..8] [--prefer-fewer|--use-all-modifiers]
  engine setup apply --plan <plan.json> [--dry-run] [--force]   (writes the plan's relations, binds and modifiers in one step)
  engine setup render --plan <plan.json> --out-dir <folder> [--aircraft <name>] [--light] [--width <px>]   (a callout sheet per device)
  engine relations auto-assign [--grip <device|VID:PID>=<grip id>]... [--add-device <catalog id>]... [--ignore-device <device>]... [--relation <name>]... [--max-modifiers 0|1|2] [--overwrite [--include-missing]] [--no-style] [--explain <relation>]... [--dry-run]
  engine devices kb [list [query] | show <id> | identify [--grip <device>=<grip id>]... [--add-device <id>]...]   (device catalog for Auto Assign)
  engine devices reference [list [query] | identify --vidpid <VID:PID> --name <HID name> [--instance <name>] [--buttons <n>] [--hats <n>] [--axes X,Y,RZ] | show <id> [--base <id>] [--svg <file>] [--light] [--no-badges] | check [query] | photos [query] [--coverage] | photo-add --model <id> --url <https photo on the maker's host> --view <front|side|back|top|base|three-quarter|sheet> [--page <url>] [--confidence <c>] [--shows a,b] [--sha256 <hex>] | layout-check | drawings [query] [--check] [--model <id> [--view <v>] --svg <file> | --png <file>] [--light] [--drawing-style lines|cel] [--drawing-preset <p>]]   (device reference pictures: catalog, identification and the question to ask, JoyPro's schematic with markers, photo references, curated marker positions, JoyPro's own drawings of the devices)
  engine cockpits [list [query] [--game G] [--era E] [--role R] [--nation N] [--category fixed-wing|helicopter|space|fiction|real] | search <text> [--limit n] | get <aircraft> [<variant>] [--json] | check | render <aircraft> [<variant>] --png <file> [--device <d>] [--style dark|light] [--print-width <px>] [--drawing-only] | compare <aircraft> [<variant>] [--game G] | photos [query] [--coverage] | coverage | drawings | pictures build [--game DCS|XPlane12|MSFS2020|IL2Game|NuclearOption|all] [--aircraft <name>,...] [--force] [--out <folder>] | pictures status | pictures check [--game G] [--aircraft <name>,...] [--write] [--drop]]
  engine binds list [filter]
  engine binds show <bind-or-relation>
  engine binds set <bind-or-relation> [--device <name>] [--input <value>] [--format dcs|il2] [--modifier <name>] [--clear-modifiers] [--clear-input] [--inverted true|false] [--slider true|false] [--curvature <csv>] [--deadzone <value>] [--saturation-x <value>] [--saturation-y <value>] [--double-tap true|false]
  engine binds clear <bind-or-relation> [--input-only]
  engine binds modifier list [bind-or-relation]
  engine binds modifier add <bind-or-relation> <mod1> [mod2] [...]
  engine binds modifier remove <bind-or-relation> <mod1> [mod2] [...] [--all]
  engine binds modifier rename <old-modifier> <new-modifier> [bind-or-relation]
  engine ffb models
  engine ffb show [--device <device>] [--game <game>] [--plane <plane>]
  engine ffb set --device <device>|* --game <game> [--plane <plane>] --param <name>=<value>...
  engine ffb clear --device <device>|* --game <game> [--plane <plane>] [--param <name>]...
  engine ffb telemetry status   (JoyPro's telemetry force feedback: settings, game setup, process, last status)
  engine ffb telemetry effects
  engine ffb telemetry config show
  engine ffb telemetry config set [--game DCS|IL2Game|IL2Korea|WarThunder] [--plane <aircraft>] [--effect <name>] | [--device {guid}] --param <key>=<value>...
  engine ffb telemetry config clear [--game <game>] [--plane <aircraft>] [--effect <name>] | [--device {guid}]
  engine ffb telemetry setup dcs|il2|il2korea|warthunder [--remove] [--dry-run] [--path <Saved Games\DCS | IL-2 folder | startup.cfg>]   (DCS Hooks script / IL-2 startup.cfg telemetry target; backed up)
  engine telemetry games   (telemetry recording, docs/telemetry-recording.md: the games, what JoyPro listens to, ACMI possible)
  engine telemetry config show
  engine telemetry config set [--game <game>] --param enabled|folder|max-total-mb|keep-days|sample-hz|keep-raw|session-timeout=<value>...
  engine telemetry sessions [--game <game>] [--folder <folder>]   (recorded sessions, newest first)
  engine telemetry show <session> [--folder <folder>]   (<session> = path, file name, latest or a number of the list)
  engine telemetry export <session> --format acmi|acmi-zip|csv [--to <file or folder>] [--max-hz <n>] [--no-per-channel]   (Tacview ACMI 2.2 / CSV)
  engine telemetry preview <session> [--channel <key>]... [--points <n>]
  engine telemetry delete <session>...
  engine telemetry recover | prune   (finish sessions cut off by a crash | apply the retention settings)
  engine telemetry record --game <game> [--seconds <n>] [--folder <folder>] [--sample-hz <n>] [--no-raw] [--no-game-check]   (records now; Ctrl+C ends it)
  engine binds tuning models
  engine binds tuning show <bind-or-relation> [--game <game>] [--plane <plane>]
  engine binds tuning set <bind-or-relation> --scope base|game:<game>|plane:<game>:<plane>|family:<family> [--param <name>=<value>]... [--inverted true|false] [--slider true|false] [--deadzone <value>] [--saturation-x <value>] [--saturation-y <value>] [--curvature <csv>] [--curve-points <x:y,...>|none]
  engine binds tuning clear <bind-or-relation> --scope game:<game>|plane:<game>:<plane>|family:<family>
  engine binds tuning preview <bind-or-relation> --game <game> [--plane <plane>] [--points <n>]
  engine binds detent show <bind-or-relation>   (detent mode: settings, the device axis calibration, the aircraft grouped by afterburner start, per game the curve it gets and how exactly)
  engine binds detent set <bind-or-relation> [--on|--off] [--no-ab full|linear] [--unknown full|linear] [--margin <0..0.1>] [--target <game>:<plane>=ab:<0..1>|full|linear|default]...
  engine binds detent calibrate <bind-or-relation> --idle <0..1> --detent <0..1> [--full <0..1>|none] [--cutoff <0..1>|none] [--source wizard|manual]   (raw axis positions of the bound device axis)
  engine binds detent uncalibrate <bind-or-relation>
  engine binds detent gates [--game <game>] [--plane <plane>]   (the shipped throttle gate data: afterburner start per aircraft, source, confidence)
  engine binds detent merge [--relations <csv>] [--into <relation>] [--ab-source profile|game] [--apply]   (per-aircraft throttle relations of one device axis into one relation in detent mode; without --apply only the plan)
  engine binds unbind <device::input|input> [--device <name>] [--input <value>] [--format dcs|il2] [--game DCS|IL2Game] [--all]
  engine binds export <path.json>
  engine binds import <path.json>
  engine binds edit   (no args; exports temp binds JSON, opens editor, re-imports on close; uses --editor or notepad)
```

## Global Options (Detailed)

### `--profile <path>`
What it does: Global option: load this .pr0file before running the command.
Value format: Filesystem path value. Example: C:\\JoyPro\\logs\\preview.png.

Example:
```powershell
engine --profile 'C:\JoyPro\profiles\pilot_main.pr0file' summary --text
```

### `--out <path>`
What it does: Global option: write result output to file path instead of terminal.
Value format: Filesystem path value. Example: C:\\JoyPro\\logs\\preview.png.

Example:
```powershell
engine db games --text --out 'C:\JoyPro\logs\db_games.txt'
```

### `--editor <command>`
What it does: Global option: editor command for relations edit and binds edit (use {file} placeholder).
Value format: Command argument. See examples for typical values.

Example:
```powershell
engine --editor 'code --wait {file}' relations edit
```

### `--text`
What it does: Global option: render human-readable text instead of JSON output.

How it interacts:
- Without this flag, output defaults to JSON.

Example:
```powershell
engine relations list --text
```

### `--status`
What it does: Global option: emit live status/progress updates to stderr while command runs (true/false).

How it interacts:
- Status lines are written to stderr so stdout payload format remains unchanged.
- Useful for GUI progress indicators on long-running commands.
- Also prints the machine-readable progress lines of --progress.

Example:
```powershell
engine --status true dcs import 'C:\Users\Pilot\Saved Games\DCS.openbeta' --text
```

### `--progress`
What it does: Global option: emit only the machine-readable progress lines of long commands to stderr (true/false).

How it interacts:
- Format: [engine-progress] <current>/<total> <phase>: <item>; current is the item being worked on (1-based), total is ? when unknown, the phase never contains a colon.
- Written by the DB refreshes (DCS per module, aircraft and written file; DCS clean profiles; IL-2; Star Citizen; every game's refresh-db), the DCS import (per file) and the DCS export (per aircraft).
- [engine-cancel] available|unavailable|cancelled: with JOYPRO_CANCEL_FILE set, a frontend may create that file to stop the command; the engine stops only before it writes (exit code 3, nothing written).
- The progress also goes into the background log (category Progress), with or without this option.

Example:
```powershell
engine --progress true dcs refresh id --headless-lua true --text
```

### `--benchmark`
What it does: Global option: emit timing summary lines for command phases to stderr (true/false).

How it interacts:
- Benchmark lines are written to stderr with per-phase timing in milliseconds.
- Also enables status output.

Example:
```powershell
engine --benchmark true dcs export 'C:\Users\Pilot\Saved Games\DCS.openbeta' --text
```

### `--load-corrections`
What it does: Global option: enable/disable load-time profile correction mechanics.

How it interacts:
- If set to false, correction behavior is disabled for that run.

Example:
```powershell
engine --load-corrections false profile load 'C:\JoyPro\profiles\legacy.pr0file'
```

### `--auto-add-db-items`
What it does: Global option: allow corrected DB nodes to be auto-added while loading profile.

How it interacts:
- Useful only when --load-corrections true.

Example:
```powershell
engine --auto-add-db-items false profile load 'C:\JoyPro\profiles\legacy.pr0file'
```

### `--remap-missing-devices`
What it does: Global option: allow missing profile device names to be remapped to closest matches.

How it interacts:
- Useful only when --load-corrections true.

Example:
```powershell
engine --remap-missing-devices true profile load 'C:\JoyPro\profiles\legacy.pr0file'
```

### `--confirm-device-remap`
What it does: Global option: prompt (true) or auto-accept (false) device remaps.

How it interacts:
- Set false for non-interactive runs that should auto-accept remaps.

Example:
```powershell
engine --confirm-device-remap false profile load 'C:\JoyPro\profiles\legacy.pr0file'
```

### `--correction-mode`
What it does: Global option: correction strictness (strict, balanced, aggressive).

How it interacts:
- Works with load corrections. strict is conservative, aggressive is more permissive.

Example:
```powershell
engine --correction-mode strict profile load 'C:\JoyPro\profiles\legacy.pr0file'
```

### `--correction-auto-threshold <0..200>`
What it does: Global option: numeric score threshold (0..200) to auto-apply relation-item remaps.
Value format: Integer score threshold from 0 to 200. Example: 98.

How it interacts:
- Overrides mode-based auto-apply score with explicit numeric threshold.
- Applies to relation-item remaps only; title/category refresh for existing IDs is always automatic.

Example:
```powershell
engine --correction-auto-threshold 98 profile load 'C:\JoyPro\profiles\legacy.pr0file'
```

### `--confirm-corrections`
What it does: Global option: prompt (true) or auto-accept (false) medium-confidence item remaps.

How it interacts:
- When false and no explicit auto-threshold is set, medium-confidence corrections are auto-applied.
- When --correction-auto-threshold is set, scores below that threshold are skipped unless confirmed interactively.

Example:
```powershell
engine --confirm-corrections false profile load 'C:\JoyPro\profiles\legacy.pr0file'
```

### `--correction-report <path>`
What it does: Global option: write load correction report to .json or .txt/.log path.
Value format: Filesystem path value. Example: C:\\JoyPro\\logs\\preview.png.

How it interacts:
- Use .json for machine-readable output and .txt/.log for readable summaries.

Example:
```powershell
engine --correction-report 'C:\JoyPro\logs\load_corrections.json' profile load 'C:\JoyPro\profiles\legacy.pr0file'
```

### `--history-note <text>`
What it does: Command argument. See examples for typical values.
Value format: Command argument. See examples for typical values.

Example:
```powershell
engine --help
```


## Command Reference (Every Command)

### `engine summary`
What it does: Shows a quick overview of the currently loaded profile state.

Arguments and example values:
- No command-specific arguments.

Examples:
```powershell
engine summary --text
```

### `engine profile summary`
What it does: Shows profile-focused counts and status.

Arguments and example values:
- No command-specific arguments.

Examples:
```powershell
engine --profile 'C:\JoyPro\profiles\pilot_main.pr0file' profile summary --text
```

### `engine profile load <path.pr0file>`
What it does: Loads a .pr0file into memory so later commands act on it.

Arguments and example values:
- `<path.pr0file>`: Path to profile file. Example: C:\\JoyPro\\profiles\\pilot_main.pr0file.

Examples:
```powershell
engine profile load 'C:\JoyPro\profiles\pilot_main.pr0file'
```

### `engine profile save <path.pr0file>`
What it does: Saves current in-memory profile state to a .pr0file.

Arguments and example values:
- `<path.pr0file>`: Path to profile file. Example: C:\\JoyPro\\profiles\\pilot_main.pr0file.

Examples:
```powershell
engine profile save 'C:\JoyPro\profiles\pilot_main.pr0file'
```

### `engine profile new <path.pr0file>`
What it does: Creates an empty profile and saves it to the given .pr0file (overwrites an existing file).

Arguments and example values:
- `<path.pr0file>`: Path to profile file. Example: C:\\JoyPro\\profiles\\pilot_main.pr0file.

Examples:
```powershell
engine profile new 'C:\JoyPro\profiles\new_setup.pr0file'
```

### `engine profile save-last`
What it does: Saves to JoyPro's last-used profile path.

Arguments and example values:
- No command-specific arguments.

Examples:
```powershell
engine profile save-last
```

### `engine profile validate [--fail-on-warnings true|false]`
What it does: Validates the loaded profile and reports both warnings and errors across relation items, binds, modifiers, and active-node states.

Arguments and example values:
- `--fail-on-warnings`: For profile validate: treat warnings as failure (non-zero exit) in addition to errors.

How arguments interact:
- Validation always reports both warnings and errors.
- Default exit behavior fails only when errors exist.
- Set --fail-on-warnings true for CI or strict runs where warnings should also fail the command.

Examples:
```powershell
engine --profile 'C:\JoyPro\profiles\pilot_main.pr0file' profile validate --text
engine --profile 'C:\JoyPro\profiles\pilot_main.pr0file' profile validate --fail-on-warnings true --text
```

### `engine profile games   (games and DCS aircraft the profile has relations for, with how many are bound: what each export writes)`
What it does: Runs this command operation.

Arguments and example values:
- No command-specific arguments.

Examples:
```powershell
engine --help
```

### `engine profile merge <left.pr0file> <right.pr0file> [<more.pr0file> ...] [--mode left|right|ask] [--save <path.pr0file>]`
What it does: Runs this command operation.

Arguments and example values:
- `<left.pr0file>`: Command argument. See examples for typical values.
- `<right.pr0file>`: Command argument. See examples for typical values.
- `<more.pr0file>`: Command argument. See examples for typical values.
- `--mode`: Export mode for DCS/IL2 export commands: clean, add, merge, merge-overwrite.
- `--save`: Command argument. See examples for typical values.
- `<path.pr0file>`: Path to profile file. Example: C:\\JoyPro\\profiles\\pilot_main.pr0file.

Examples:
```powershell
engine --help
```

### `engine profile history [list]   (the versions of --profile, newest first: every save is one, kept in <data>\ProfileHistory; undo/redo state)`
What it does: Runs this command operation.

Arguments and example values:
- `--profile`: Global option: load this .pr0file before running the command.
- `<data>`: Command argument. See examples for typical values.

Examples:
```powershell
engine --help
```

### `engine profile history show <id> | diff <id> [<id>|current]   (what a version changed / the changes between two versions)`
What it does: Runs this command operation.

Arguments and example values:
- `<id>`: Command argument. See examples for typical values.

Examples:
```powershell
engine --help
```

### `engine profile history restore <id> | undo | redo   (writes a version back into the profile; that is a version too, so it can be undone)`
What it does: Runs this command operation.

Arguments and example values:
- `<id>`: Command argument. See examples for typical values.

Examples:
```powershell
engine --help
```

### `engine profile history keep <id> [on|off] | checkpoint [--label <text>]   (a kept version is never cleaned up; a checkpoint keeps the profile as it is now)`
What it does: Runs this command operation.

Arguments and example values:
- `<id>`: Command argument. See examples for typical values.
- `--label`: Command argument. See examples for typical values.
- `<text>`: Command argument. See examples for typical values.

Examples:
```powershell
engine --help
```

### `engine profile history prune [--all] [--dry-run] | usage [--all]   (clean-up: all of 24 h, hourly 7 days, daily 60 days, weekly after; 200 MB per profile, 1 GB in total)`
What it does: Runs this command operation.

Arguments and example values:
- `--all`: Apply operation to all matches (or select all relations/devices where supported).
- `--dry-run`: Plan mode only: do not click, launch, or change files; report what would run.

Examples:
```powershell
engine --help
```

### `engine dcs scan [<saved-games-path>] [--path <saved-games-path>] [--plane <name>] [--planes <csv>] [--device <name>] [--devices <csv>] [--ignore-device <name>] [--ignore-devices <csv>] [--include-keyboard true|false] [--ignore-easy-planes true|false] [--include-defaults] [--import-inverted] [--import-slider] [--import-curvature] [--import-deadzone] [--import-saturation-x] [--import-saturation-y] [--confirm true|false]`
What it does: Parses DCS input files and previews what would be imported, without applying changes.

Arguments and example values:
- `<saved-games-path>`: Path to DCS Saved Games folder. Example: C:\\Users\\Pilot\\Saved Games\\DCS.openbeta.
- `--path`: Optional explicit path override. Use when you prefer option form over positional path.
- `--plane`: Include one plane/module per flag. Example: --plane A-10C_2.
- `<name>`: Name value for surrounding option. Example: A-10C_2 or LEFT VPC Throttle.
- `--planes`: Include planes/modules as CSV. Example: --planes A-10C_2,FA-18C_hornet.
- `<csv>`: Comma-separated values. Example: 0.1,0.2,0.3 or VKB,LEFT VPC Throttle.
- `--device`: Include one device per flag. Example: --device LEFT VPC Throttle.
- `--devices`: Include devices as CSV. Example: --devices VKB Gladiator NXT,LEFT VPC Throttle.
- `--ignore-device`: Exclude one device per flag. Example: --ignore-device Keyboard.
- `--ignore-devices`: Exclude devices as CSV. Example: --ignore-devices Keyboard,Mouse.
- `--include-keyboard`: Command argument. See examples for typical values.
- `--ignore-easy-planes`: Command argument. See examples for typical values.
- `--include-defaults`: Include defaults in DCS import/scan.
- `--import-inverted`: Include axis inversion settings.
- `--import-slider`: Include DCS slider axis setting.
- `--import-curvature`: Include axis curvature settings.
- `--import-deadzone`: Include axis deadzone settings.
- `--import-saturation-x`: Include DCS saturation X setting.
- `--import-saturation-y`: Include DCS saturation Y setting.
- `--confirm`: Controls suggestion confirmation (true) vs auto-accept (false).

How arguments interact:
- If no device include filter is given, all devices are considered.
- Ignore filters (--ignore-device, --ignore-devices) remove matches even if included.
- --confirm false auto-accepts closest suggested device names.

Examples:
```powershell
engine dcs scan 'C:\Users\Pilot\Saved Games\DCS.openbeta' --plane 'A-10C_2' --confirm false --text
```

### `engine dcs import [<saved-games-path>] [--path <saved-games-path>] [--merge] [--save-profile <path.pr0file>] [--save-last] [--plane <name>] [--planes <csv>] [--device <name>] [--devices <csv>] [--ignore-device <name>] [--ignore-devices <csv>] [--include-keyboard true|false] [--ignore-easy-planes true|false] [--include-defaults] [--import-inverted] [--import-slider] [--import-curvature] [--import-deadzone] [--import-saturation-x] [--import-saturation-y] [--confirm true|false]`
What it does: Parses DCS input files and applies results to the in-memory profile.

Arguments and example values:
- `<saved-games-path>`: Path to DCS Saved Games folder. Example: C:\\Users\\Pilot\\Saved Games\\DCS.openbeta.
- `--path`: Optional explicit path override. Use when you prefer option form over positional path.
- `--merge`: Merge import results into existing in-memory profile.
- `--save-profile`: After import, save profile to this .pr0file path.
- `<path.pr0file>`: Path to profile file. Example: C:\\JoyPro\\profiles\\pilot_main.pr0file.
- `--save-last`: After import, save to last profile path.
- `--plane`: Include one plane/module per flag. Example: --plane A-10C_2.
- `<name>`: Name value for surrounding option. Example: A-10C_2 or LEFT VPC Throttle.
- `--planes`: Include planes/modules as CSV. Example: --planes A-10C_2,FA-18C_hornet.
- `<csv>`: Comma-separated values. Example: 0.1,0.2,0.3 or VKB,LEFT VPC Throttle.
- `--device`: Include one device per flag. Example: --device LEFT VPC Throttle.
- `--devices`: Include devices as CSV. Example: --devices VKB Gladiator NXT,LEFT VPC Throttle.
- `--ignore-device`: Exclude one device per flag. Example: --ignore-device Keyboard.
- `--ignore-devices`: Exclude devices as CSV. Example: --ignore-devices Keyboard,Mouse.
- `--include-keyboard`: Command argument. See examples for typical values.
- `--ignore-easy-planes`: Command argument. See examples for typical values.
- `--include-defaults`: Include defaults in DCS import/scan.
- `--import-inverted`: Include axis inversion settings.
- `--import-slider`: Include DCS slider axis setting.
- `--import-curvature`: Include axis curvature settings.
- `--import-deadzone`: Include axis deadzone settings.
- `--import-saturation-x`: Include DCS saturation X setting.
- `--import-saturation-y`: Include DCS saturation Y setting.
- `--confirm`: Controls suggestion confirmation (true) vs auto-accept (false).

How arguments interact:
- If no device include filter is given, all devices are considered.
- Ignore filters (--ignore-device, --ignore-devices) remove matches even if included.
- --confirm false auto-accepts closest suggested device names.
- --save-profile and --save-last are valid on import commands only.

Examples:
```powershell
engine dcs import 'C:\Users\Pilot\Saved Games\DCS.openbeta' --device 'VKB Gladiator NXT' --merge --save-profile 'C:\JoyPro\profiles\dcs_import.pr0file' --text
```

### `engine dcs export [<saved-games-path>] [--path <saved-games-path>] [--mode clean|add|merge|merge-overwrite] [--keep-keyboard-defaults true|false] [--nuke-untouched-devices true|false] [--clean-all-aircraft true|false] [--plane <name>] [--planes <csv>]   (only these aircraft; writes .diff.lua + .jp backup + modifiers.lua named as DCS names the device; clean: the written devices also lose binds and DCS defaults in every other aircraft unless --clean-all-aircraft false or a plane filter; nuke: devices without binds in an exported plane lose all binds there, DCS defaults included)`
What it does: Writes current profile mappings back to DCS-compatible files.

Arguments and example values:
- `<saved-games-path>`: Path to DCS Saved Games folder. Example: C:\\Users\\Pilot\\Saved Games\\DCS.openbeta.
- `--path`: Optional explicit path override. Use when you prefer option form over positional path.
- `--mode`: Export mode for DCS/IL2 export commands: clean, add, merge, merge-overwrite.
- `--keep-keyboard-defaults`: DCS export only: keep existing keyboard defaults/keyboard diff files untouched (true/false).
- `--nuke-untouched-devices`: DCS export only: delete untouched device .diff.lua/.jp files under Config\\Input (true/false).
- `--clean-all-aircraft`: Command argument. See examples for typical values.
- `--plane`: Include one plane/module per flag. Example: --plane A-10C_2.
- `<name>`: Name value for surrounding option. Example: A-10C_2 or LEFT VPC Throttle.
- `--planes`: Include planes/modules as CSV. Example: --planes A-10C_2,FA-18C_hornet.
- `<csv>`: Comma-separated values. Example: 0.1,0.2,0.3 or VKB,LEFT VPC Throttle.

How arguments interact:
- Import-only filters/settings are not valid for export commands.
- --mode is valid for DCS export and IL2 export.
- --keep-keyboard-defaults controls whether keyboard files are preserved untouched.
- --nuke-untouched-devices deletes non-exported device files (except keyboard when keep-keyboard-defaults is true).

Examples:
```powershell
engine dcs export 'C:\Users\Pilot\Saved Games\DCS.openbeta' --text
```

### `engine dcs defaults cut [<saved-games-path>] [<dcs-install-path>] [--path <saved-games-path>] [--install-path <dcs-install-path>] [--backup-path <path>]   (moves stick-specific defaults from game folder into BACKUP_JOYSTICK_DEFAULTS)`
What it does: Backs up joystick defaults and removes stick-specific defaults from DCS game-folder aircraft input files.

Arguments and example values:
- `<saved-games-path>`: Path to DCS Saved Games folder. Example: C:\\Users\\Pilot\\Saved Games\\DCS.openbeta.
- `<dcs-install-path>`: Path to DCS install root (contains Mods\\aircraft). Example: D:\\Games\\Eagle Dynamics\\DCS World OpenBeta.
- `--path`: Optional explicit path override. Use when you prefer option form over positional path.
- `--install-path`: DCS install path override (root folder that contains Mods\\aircraft).
- `--backup-path`: Override backup folder path for dcs defaults cut/restore. Default: <saved-games>\\BACKUP_JOYSTICK_DEFAULTS.
- `<path>`: Filesystem path value. Example: C:\\JoyPro\\logs\\preview.png.

How arguments interact:
- If paths are omitted, the command tries to auto-detect DCS Saved Games and install locations.
- Copies default.lua to backup and moves non-default joystick files into backup.
- Default backup location is <saved-games>\\BACKUP_JOYSTICK_DEFAULTS unless --backup-path is provided.

Examples:
```powershell
engine dcs defaults cut 'C:\Users\Pilot\Saved Games\DCS.openbeta' 'D:\Games\Eagle Dynamics\DCS World OpenBeta' --text
```

### `engine dcs defaults restore [<saved-games-path>] [<dcs-install-path>] [--path <saved-games-path>] [--install-path <dcs-install-path>] [--backup-path <path>]   (restores joystick defaults from BACKUP_JOYSTICK_DEFAULTS)`
What it does: Restores joystick defaults from BACKUP_JOYSTICK_DEFAULTS back into DCS game-folder aircraft input files.

Arguments and example values:
- `<saved-games-path>`: Path to DCS Saved Games folder. Example: C:\\Users\\Pilot\\Saved Games\\DCS.openbeta.
- `<dcs-install-path>`: Path to DCS install root (contains Mods\\aircraft). Example: D:\\Games\\Eagle Dynamics\\DCS World OpenBeta.
- `--path`: Optional explicit path override. Use when you prefer option form over positional path.
- `--install-path`: DCS install path override (root folder that contains Mods\\aircraft).
- `--backup-path`: Override backup folder path for dcs defaults cut/restore. Default: <saved-games>\\BACKUP_JOYSTICK_DEFAULTS.
- `<path>`: Filesystem path value. Example: C:\\JoyPro\\logs\\preview.png.

How arguments interact:
- If paths are omitted, the command tries to auto-detect DCS Saved Games and install locations.
- Restores files by copying backup aircraft input folders back into Mods\\aircraft.
- Default backup location is <saved-games>\\BACKUP_JOYSTICK_DEFAULTS unless --backup-path is provided.

Examples:
```powershell
engine dcs defaults restore 'C:\Users\Pilot\Saved Games\DCS.openbeta' 'D:\Games\Eagle Dynamics\DCS World OpenBeta' --text
```

### `engine dcs strip-defaults [apply|restore|status|auto] [--dry-run] [--restore] [--skip-ui-layer true|false] [--remember true|false] [--install-path <dcs-install-path>] [--saved-games <saved-games-path>] [--luae <luae.exe>] [--no-load-check]   (switches DCS's default joystick binds off in the DCS install: wraps every joystick profile lua, combos = nil, backup <file>.nocombo.bak; restore puts the originals back; auto re-applies after a DCS update; patched files fail server integrity checks)`
What it does: Switches DCS's default joystick binds off in the DCS installation (every joystick profile lua wrapped, combos = nil, original kept as <file>.nocombo.bak), or restores the originals; status and auto (re-apply after a DCS update).

Arguments and example values:
- `--dry-run`: Plan mode only: do not click, launch, or change files; report what would run.
- `--restore`: dcs strip-defaults / device-luas: put the original DCS files back (same as the restore action).
- `--skip-ui-layer`: dcs strip-defaults: leave Config\\Input\\UiLayer unpatched (true/false; the flag alone means true).
- `--remember`: dcs strip-defaults / device-luas: keep the switch for automatic re-apply after DCS updates (default true).
- `--install-path`: DCS install path override (root folder that contains Mods\\aircraft).
- `<dcs-install-path>`: Path to DCS install root (contains Mods\\aircraft). Example: D:\\Games\\Eagle Dynamics\\DCS World OpenBeta.
- `--saved-games`: Saved Games\\DCS folder whose Mods are included (default: every Saved Games\\DCS* folder found).
- `<saved-games-path>`: Path to DCS Saved Games folder. Example: C:\\Users\\Pilot\\Saved Games\\DCS.openbeta.
- `--luae`: Path to DCS's luae.exe for the load check (default: the install's bin\\luae.exe).
- `<luae.exe>`: Command argument. See examples for typical values.
- `--no-load-check`: dcs strip-defaults: skip the check with DCS's own Lua loader after patching (tests only).
- `<file>`: Command argument. See examples for typical values.

How arguments interact:
- Off by default. Patched DCS files fail the integrity check of multiplayer servers that enforce it.
- Same marker (-- [[DCS_STRIP_JOYSTICK_DEFAULTS v1]]) and backup name as the community strip script; either one restores the other's files.
- apply remembers the switch; JoyPro re-applies it at its start (auto) and before every DCS list refresh. restore switches it off.
- Never touches Saved Games\\DCS\\Config\\Input. Refused while DCS or DCS_updater runs.

Examples:
```powershell
engine dcs strip-defaults apply --dry-run --text
engine dcs strip-defaults apply --skip-ui-layer --text
engine dcs strip-defaults status --text
engine dcs strip-defaults --restore --text
```

### `engine dcs device-luas [list|remove|restore] [--dry-run] [--remember true|false] [--install-path <dcs-install-path>] [--saved-games <saved-games-path>]   (moves the device-specific default files of the DCS install (<device type>.lua, template diffs) into JoyPro's backup folder so default.lua applies; restore puts them back)`
What it does: Moves the device-specific default files of the DCS installation into JoyPro's backup folder so DCS uses default.lua for every device, or puts them back.

Arguments and example values:
- `--dry-run`: Plan mode only: do not click, launch, or change files; report what would run.
- `--remember`: dcs strip-defaults / device-luas: keep the switch for automatic re-apply after DCS updates (default true).
- `--install-path`: DCS install path override (root folder that contains Mods\\aircraft).
- `<dcs-install-path>`: Path to DCS install root (contains Mods\\aircraft). Example: D:\\Games\\Eagle Dynamics\\DCS World OpenBeta.
- `--saved-games`: Saved Games\\DCS folder whose Mods are included (default: every Saved Games\\DCS* folder found).
- `<saved-games-path>`: Path to DCS Saved Games folder. Example: C:\\Users\\Pilot\\Saved Games\\DCS.openbeta.
- `<device type>`: Command argument. See examples for typical values.

How arguments interact:
- Files another Lua file of the module refers to stay; so does the UI layer (VR controllers).
- The moved files and manifest.json are in <data>\\DcsDefaults\\DeviceLuas. Rebuild the DCS clean profiles afterwards (the GUI does it).

Examples:
```powershell
engine dcs device-luas list --text
engine dcs device-luas remove --text
engine dcs device-luas restore --text
```

### `engine dcs verify-clean [<saved-games-path>] [--path <saved-games-path>] [--install-path <dcs-install-path>] [--plane <name>] [--planes <csv>] [--device <name>] [--devices <csv>] [--include-disconnected true|false] [--max-listed <n>]   (DCS's own Lua lists the default binds still active on the joysticks with the files of that Saved Games folder; read only)`
What it does: Runs DCS's own input code (luae.exe) on a copy of a Saved Games input folder and lists every DCS default bind still active on the joysticks.

Arguments and example values:
- `<saved-games-path>`: Path to DCS Saved Games folder. Example: C:\\Users\\Pilot\\Saved Games\\DCS.openbeta.
- `--path`: Optional explicit path override. Use when you prefer option form over positional path.
- `--install-path`: DCS install path override (root folder that contains Mods\\aircraft).
- `<dcs-install-path>`: Path to DCS install root (contains Mods\\aircraft). Example: D:\\Games\\Eagle Dynamics\\DCS World OpenBeta.
- `--plane`: Include one plane/module per flag. Example: --plane A-10C_2.
- `<name>`: Name value for surrounding option. Example: A-10C_2 or LEFT VPC Throttle.
- `--planes`: Include planes/modules as CSV. Example: --planes A-10C_2,FA-18C_hornet.
- `<csv>`: Comma-separated values. Example: 0.1,0.2,0.3 or VKB,LEFT VPC Throttle.
- `--device`: Include one device per flag. Example: --device LEFT VPC Throttle.
- `--devices`: Include devices as CSV. Example: --devices VKB Gladiator NXT,LEFT VPC Throttle.
- `--include-disconnected`: dcs verify-clean: also check devices that only have files, through a fake stick of their type (true/false).
- `--max-listed`: dcs verify-clean: how many remaining default binds are listed (counts are always complete).
- `<n>`: Integer numeric value. Example: 45.

How arguments interact:
- Read only: works on a temporary copy of Config\\Input. Needs the DCS installation (luae.exe and Scripts).
- Defaults Left On JoyPro Devices counts remaining defaults on files JoyPro wrote (.jp next to them); after an export it should be 0.

Examples:
```powershell
engine dcs verify-clean 'C:\Users\Pilot\Saved Games\DCS' --text
engine dcs verify-clean --planes 'FA-18C_hornet' --include-disconnected true --text
```

### `engine dcs refresh clean [<saved-games-path>] [<dcs-install-path>] [--path <saved-games-path>] [--install-path <dcs-install-path>] [--ocr true|false] [--launch true|false] [--keep-running true|false] [--headless-lua true|false] [--plane <name>] [--planes <csv>] [--dry-run] [--menu-wait-seconds <n>] [--ocr-menu-timeout-seconds <n>] [--settle-seconds <n>] [--default-stick <name>] [--clicks-xml <path>] [--clicks-dir <path>] [--dcs-exe <path>] [--preview-clicks] [--preview-image <path>] [--gui-scale <value>] [--window-left <n> --window-top <n> --window-width <n> --window-height <n>]`
What it does: Automates DCS Controls -> Clear All flow and rebuilds JoyPro clean-profile baseline files.

Arguments and example values:
- `<saved-games-path>`: Path to DCS Saved Games folder. Example: C:\\Users\\Pilot\\Saved Games\\DCS.openbeta.
- `<dcs-install-path>`: Path to DCS install root (contains Mods\\aircraft). Example: D:\\Games\\Eagle Dynamics\\DCS World OpenBeta.
- `--path`: Optional explicit path override. Use when you prefer option form over positional path.
- `--install-path`: DCS install path override (root folder that contains Mods\\aircraft).
- `--ocr`: Use OCR image matching click flow instead of fixed XML anchor click offsets (true/false).
- `--launch`: Start DCS automatically before refresh automation (true/false).
- `--keep-running`: Leave DCS running after refresh command completes (true/false).
- `--headless-lua`: Command argument. See examples for typical values.
- `--plane`: Include one plane/module per flag. Example: --plane A-10C_2.
- `<name>`: Name value for surrounding option. Example: A-10C_2 or LEFT VPC Throttle.
- `--planes`: Include planes/modules as CSV. Example: --planes A-10C_2,FA-18C_hornet.
- `<csv>`: Comma-separated values. Example: 0.1,0.2,0.3 or VKB,LEFT VPC Throttle.
- `--dry-run`: Plan mode only: do not click, launch, or change files; report what would run.
- `--menu-wait-seconds`: Non-OCR wait time before click automation starts. Example: --menu-wait-seconds 45.
- `<n>`: Integer numeric value. Example: 45.
- `--ocr-menu-timeout-seconds`: OCR max wait for DCS main menu template detection. Example: --ocr-menu-timeout-seconds 600.
- `--settle-seconds`: Post-click settle wait for DCS file writes. Example: --settle-seconds 60.
- `--default-stick`: Preferred joystick name for clean profile extraction if that .diff.lua exists.
- `--clicks-xml`: Path to Clicks.xml anchor file for non-OCR refresh clicks.
- `<path>`: Filesystem path value. Example: C:\\JoyPro\\logs\\preview.png.
- `--clicks-dir`: Folder containing OCR template images (cog1.0.png, controls1.0.png, etc.).
- `--dcs-exe`: Explicit DCS executable path override. Example: --dcs-exe 'D:\\Games\\DCS World\\bin\\DCS_updater.exe'.
- `--preview-clicks`: Generate click preview data/image while running refresh commands.
- `--preview-image`: Output .png path for click preview image overlay.
- `--gui-scale`: Override GUI scale multiplier used for high-resolution XML click anchor offsets.
- `<value>`: Generic value placeholder. Example for --input: JOY_BTN1 or joy0_b0.
- `--window-left`: Manual DCS window left coordinate override (integer).
- `--window-top`: Manual DCS window top coordinate override (integer).
- `--window-width`: Manual DCS window width override (integer > 0).
- `--window-height`: Manual DCS window height override (integer > 0).

How arguments interact:
- Requires DCS Saved Games and install paths (auto-detect is attempted when omitted).
- Without --ocr, XML click anchors from Clicks.xml are used; with --ocr, template image matching is used.
- --dry-run skips launch/click/file changes and reports planned steps only.
- --default-stick prefers <name>.diff.lua when rebuilding CleanProfile output.
- --window-left/top/width/height and --gui-scale control non-OCR click coordinate resolution.

Examples:
```powershell
engine dcs refresh clean 'C:\Users\Pilot\Saved Games\DCS.openbeta' 'D:\Games\Eagle Dynamics\DCS World OpenBeta' --ocr false --preview-clicks --preview-image 'C:\JoyPro\logs\dcs_refresh_clean_preview.png' --confirm false --text
```

### `engine dcs refresh id [<saved-games-path>] [<dcs-install-path>] [--path <saved-games-path>] [--install-path <dcs-install-path>] [--ocr true|false] [--launch true|false] [--keep-running true|false] [--headless-lua true|false] [--plane <name>] [--planes <csv>] [--dry-run] [--menu-wait-seconds <n>] [--ocr-menu-timeout-seconds <n>] [--modules-to-scan <n>] [--clicks-xml <path>] [--clicks-dir <path>] [--dcs-exe <path>] [--preview-clicks] [--preview-image <path>] [--gui-scale <value>] [--window-left <n> --window-top <n> --window-width <n> --window-height <n>]`
What it does: Automates DCS Controls -> Make HTML flow and refreshes DB HTML files from exported layouts.

Arguments and example values:
- `<saved-games-path>`: Path to DCS Saved Games folder. Example: C:\\Users\\Pilot\\Saved Games\\DCS.openbeta.
- `<dcs-install-path>`: Path to DCS install root (contains Mods\\aircraft). Example: D:\\Games\\Eagle Dynamics\\DCS World OpenBeta.
- `--path`: Optional explicit path override. Use when you prefer option form over positional path.
- `--install-path`: DCS install path override (root folder that contains Mods\\aircraft).
- `--ocr`: Use OCR image matching click flow instead of fixed XML anchor click offsets (true/false).
- `--launch`: Start DCS automatically before refresh automation (true/false).
- `--keep-running`: Leave DCS running after refresh command completes (true/false).
- `--headless-lua`: Command argument. See examples for typical values.
- `--plane`: Include one plane/module per flag. Example: --plane A-10C_2.
- `<name>`: Name value for surrounding option. Example: A-10C_2 or LEFT VPC Throttle.
- `--planes`: Include planes/modules as CSV. Example: --planes A-10C_2,FA-18C_hornet.
- `<csv>`: Comma-separated values. Example: 0.1,0.2,0.3 or VKB,LEFT VPC Throttle.
- `--dry-run`: Plan mode only: do not click, launch, or change files; report what would run.
- `--menu-wait-seconds`: Non-OCR wait time before click automation starts. Example: --menu-wait-seconds 45.
- `<n>`: Integer numeric value. Example: 45.
- `--ocr-menu-timeout-seconds`: OCR max wait for DCS main menu template detection. Example: --ocr-menu-timeout-seconds 600.
- `--modules-to-scan`: Additional module loop count for dcs refresh id export cycle. Example: --modules-to-scan 5.
- `--clicks-xml`: Path to Clicks.xml anchor file for non-OCR refresh clicks.
- `<path>`: Filesystem path value. Example: C:\\JoyPro\\logs\\preview.png.
- `--clicks-dir`: Folder containing OCR template images (cog1.0.png, controls1.0.png, etc.).
- `--dcs-exe`: Explicit DCS executable path override. Example: --dcs-exe 'D:\\Games\\DCS World\\bin\\DCS_updater.exe'.
- `--preview-clicks`: Generate click preview data/image while running refresh commands.
- `--preview-image`: Output .png path for click preview image overlay.
- `--gui-scale`: Override GUI scale multiplier used for high-resolution XML click anchor offsets.
- `<value>`: Generic value placeholder. Example for --input: JOY_BTN1 or joy0_b0.
- `--window-left`: Manual DCS window left coordinate override (integer).
- `--window-top`: Manual DCS window top coordinate override (integer).
- `--window-width`: Manual DCS window width override (integer > 0).
- `--window-height`: Manual DCS window height override (integer > 0).

How arguments interact:
- Requires DCS Saved Games and install paths (auto-detect is attempted when omitted).
- Exports per-module HTML from DCS and copies the largest non-keyboard/mouse/trackir HTML per plane into DB\\DCS.
- --modules-to-scan adds extra loop count on top of detected install/instance module counts.
- --dry-run skips launch/click/file changes and reports planned steps only.

Examples:
```powershell
engine dcs refresh id 'C:\Users\Pilot\Saved Games\DCS.openbeta' 'D:\Games\Eagle Dynamics\DCS World OpenBeta' --ocr true --modules-to-scan 0 --confirm false --text
```

### `engine dcs refresh preview-clicks [--ocr true|false] [--path <saved-games-path>] [--install-path <dcs-install-path>] [--clicks-xml <path>] [--clicks-dir <path>] [--preview-image <path>] [--gui-scale <value>] [--window-left <n> --window-top <n> --window-width <n> --window-height <n>]`
What it does: Shows where DCS XML/OCR click anchors resolve on your current screen and writes an annotated preview image.

Arguments and example values:
- `--ocr`: Use OCR image matching click flow instead of fixed XML anchor click offsets (true/false).
- `--path`: Optional explicit path override. Use when you prefer option form over positional path.
- `<saved-games-path>`: Path to DCS Saved Games folder. Example: C:\\Users\\Pilot\\Saved Games\\DCS.openbeta.
- `--install-path`: DCS install path override (root folder that contains Mods\\aircraft).
- `<dcs-install-path>`: Path to DCS install root (contains Mods\\aircraft). Example: D:\\Games\\Eagle Dynamics\\DCS World OpenBeta.
- `--clicks-xml`: Path to Clicks.xml anchor file for non-OCR refresh clicks.
- `<path>`: Filesystem path value. Example: C:\\JoyPro\\logs\\preview.png.
- `--clicks-dir`: Folder containing OCR template images (cog1.0.png, controls1.0.png, etc.).
- `--preview-image`: Output .png path for click preview image overlay.
- `--gui-scale`: Override GUI scale multiplier used for high-resolution XML click anchor offsets.
- `<value>`: Generic value placeholder. Example for --input: JOY_BTN1 or joy0_b0.
- `--window-left`: Manual DCS window left coordinate override (integer).
- `<n>`: Integer numeric value. Example: 45.
- `--window-top`: Manual DCS window top coordinate override (integer).
- `--window-width`: Manual DCS window width override (integer > 0).
- `--window-height`: Manual DCS window height override (integer > 0).

How arguments interact:
- When --ocr false, outputs XML-anchor click landing points for the active/overridden window.
- When --ocr true, attempts live template detection and reports found/missing click targets.
- Writes an annotated preview image (--preview-image or temp path).

Examples:
```powershell
engine dcs refresh preview-clicks --ocr false --window-left 0 --window-top 0 --window-width 2560 --window-height 1440 --preview-image 'C:\JoyPro\logs\dcs_click_preview.png' --text
```

### `engine dcs ffb list [--path <saved-games-path>] [--device <name>] [--devices <csv>] [--connected] [--profile-devices] [--confirm true|false]`
What it does: Lists DCS force-feedback inversion/swap settings per device from your profile/device context.

Arguments and example values:
- `--path`: Optional explicit path override. Use when you prefer option form over positional path.
- `<saved-games-path>`: Path to DCS Saved Games folder. Example: C:\\Users\\Pilot\\Saved Games\\DCS.openbeta.
- `--device`: Include one device per flag. Example: --device LEFT VPC Throttle.
- `<name>`: Name value for surrounding option. Example: A-10C_2 or LEFT VPC Throttle.
- `--devices`: Include devices as CSV. Example: --devices VKB Gladiator NXT,LEFT VPC Throttle.
- `<csv>`: Comma-separated values. Example: 0.1,0.2,0.3 or VKB,LEFT VPC Throttle.
- `--connected`: For devices list, show connected devices only.
- `--profile-devices`: For devices list, show profile-referenced devices only.
- `--confirm`: Controls suggestion confirmation (true) vs auto-accept (false).

How arguments interact:
- Without --device/--devices, lists all discovered profile/connected/FFB devices.
- --connected and --profile-devices mirror the device list filtering behavior.
- --confirm false auto-accepts closest device suggestion when a requested name is fuzzy.

Examples:
```powershell
engine dcs ffb list --text
engine dcs ffb list --device 'VKB Gladiator NXT' --confirm false --text
```

### `engine dcs ffb set <device-name> [--path <saved-games-path>] [--invert-x true|false] [--invert-y true|false] [--swap-axis true|false] [--confirm true|false]`
What it does: Creates or updates DCS force-feedback settings (invert X/Y, swap axis) for one device.

Arguments and example values:
- `<device-name>`: Device name token. Example: VKB Gladiator NXT {B4280440-0000-0000-0000-504944564944}.
- `--path`: Optional explicit path override. Use when you prefer option form over positional path.
- `<saved-games-path>`: Path to DCS Saved Games folder. Example: C:\\Users\\Pilot\\Saved Games\\DCS.openbeta.
- `--invert-x`: Set DCS FFB invert X flag (true/false).
- `--invert-y`: Set DCS FFB invert Y flag (true/false).
- `--swap-axis`: Set DCS FFB swap-axis flag (true/false).
- `--confirm`: Controls suggestion confirmation (true) vs auto-accept (false).

How arguments interact:
- At least one setting flag is required: --invert-x, --invert-y, or --swap-axis.
- If the target device setting does not exist yet, it is created.

Examples:
```powershell
engine dcs ffb set 'VKB Gladiator NXT' --invert-x true --invert-y false --swap-axis false --text
```

### `engine dcs ffb clear [<device-name>] [--path <saved-games-path>] [--device <name>] [--devices <csv>] [--all] [--confirm true|false]`
What it does: Clears DCS force-feedback settings for one or more devices (or all).

Arguments and example values:
- `<device-name>`: Device name token. Example: VKB Gladiator NXT {B4280440-0000-0000-0000-504944564944}.
- `--path`: Optional explicit path override. Use when you prefer option form over positional path.
- `<saved-games-path>`: Path to DCS Saved Games folder. Example: C:\\Users\\Pilot\\Saved Games\\DCS.openbeta.
- `--device`: Include one device per flag. Example: --device LEFT VPC Throttle.
- `<name>`: Name value for surrounding option. Example: A-10C_2 or LEFT VPC Throttle.
- `--devices`: Include devices as CSV. Example: --devices VKB Gladiator NXT,LEFT VPC Throttle.
- `<csv>`: Comma-separated values. Example: 0.1,0.2,0.3 or VKB,LEFT VPC Throttle.
- `--all`: Apply operation to all matches (or select all relations/devices where supported).
- `--confirm`: Controls suggestion confirmation (true) vs auto-accept (false).

How arguments interact:
- Use --all to remove every stored DCS FFB device setting.
- You can clear a single device by positional name or --device/--devices.

Examples:
```powershell
engine dcs ffb clear 'VKB Gladiator NXT' --text
engine dcs ffb clear --all --text
```

### `engine il2 scan [<il2-root-path>] [--path <il2-root-path>] [--device <name>] [--devices <csv>] [--ignore-device <name>] [--ignore-devices <csv>] [--import-inverted] [--import-curvature] [--import-deadzone] [--confirm true|false]`
What it does: Parses IL2 input files and previews import results.

Arguments and example values:
- `<il2-root-path>`: Path to IL2 root folder. Example: D:\\Games\\IL-2 Sturmovik Great Battles.
- `--path`: Optional explicit path override. Use when you prefer option form over positional path.
- `--device`: Include one device per flag. Example: --device LEFT VPC Throttle.
- `<name>`: Name value for surrounding option. Example: A-10C_2 or LEFT VPC Throttle.
- `--devices`: Include devices as CSV. Example: --devices VKB Gladiator NXT,LEFT VPC Throttle.
- `<csv>`: Comma-separated values. Example: 0.1,0.2,0.3 or VKB,LEFT VPC Throttle.
- `--ignore-device`: Exclude one device per flag. Example: --ignore-device Keyboard.
- `--ignore-devices`: Exclude devices as CSV. Example: --ignore-devices Keyboard,Mouse.
- `--import-inverted`: Include axis inversion settings.
- `--import-curvature`: Include axis curvature settings.
- `--import-deadzone`: Include axis deadzone settings.
- `--confirm`: Controls suggestion confirmation (true) vs auto-accept (false).

How arguments interact:
- If no device include filter is given, all devices are considered.
- Ignore filters (--ignore-device, --ignore-devices) remove matches even if included.
- --confirm false auto-accepts closest suggested device names.

Examples:
```powershell
engine il2 scan 'D:\Games\IL-2 Sturmovik Great Battles' --ignore-device 'Keyboard' --text
```

### `engine il2 import [<il2-root-path>] [--path <il2-root-path>] [--merge] [--save-profile <path.pr0file>] [--save-last] [--device <name>] [--devices <csv>] [--ignore-device <name>] [--ignore-devices <csv>] [--import-inverted] [--import-curvature] [--import-deadzone] [--confirm true|false]`
What it does: Parses IL2 input files and applies results to the in-memory profile.

Arguments and example values:
- `<il2-root-path>`: Path to IL2 root folder. Example: D:\\Games\\IL-2 Sturmovik Great Battles.
- `--path`: Optional explicit path override. Use when you prefer option form over positional path.
- `--merge`: Merge import results into existing in-memory profile.
- `--save-profile`: After import, save profile to this .pr0file path.
- `<path.pr0file>`: Path to profile file. Example: C:\\JoyPro\\profiles\\pilot_main.pr0file.
- `--save-last`: After import, save to last profile path.
- `--device`: Include one device per flag. Example: --device LEFT VPC Throttle.
- `<name>`: Name value for surrounding option. Example: A-10C_2 or LEFT VPC Throttle.
- `--devices`: Include devices as CSV. Example: --devices VKB Gladiator NXT,LEFT VPC Throttle.
- `<csv>`: Comma-separated values. Example: 0.1,0.2,0.3 or VKB,LEFT VPC Throttle.
- `--ignore-device`: Exclude one device per flag. Example: --ignore-device Keyboard.
- `--ignore-devices`: Exclude devices as CSV. Example: --ignore-devices Keyboard,Mouse.
- `--import-inverted`: Include axis inversion settings.
- `--import-curvature`: Include axis curvature settings.
- `--import-deadzone`: Include axis deadzone settings.
- `--confirm`: Controls suggestion confirmation (true) vs auto-accept (false).

How arguments interact:
- If no device include filter is given, all devices are considered.
- Ignore filters (--ignore-device, --ignore-devices) remove matches even if included.
- --confirm false auto-accepts closest suggested device names.
- --save-profile and --save-last are valid on import commands only.

Examples:
```powershell
engine il2 import 'D:\Games\IL-2 Sturmovik Great Battles' --devices 'VKB Gladiator NXT,LEFT VPC Throttle' --save-last --text
```

### `engine il2 export [<il2-root-path>] [--path <il2-root-path>] [--mode clean|add|merge|merge-overwrite] [--nuke-untouched-devices true|false]   (conflict = same action on the same device; clean releases the other binds of the written devices, nuke the joysticks the profile does not use; keyboard lines stay)`
What it does: Exports current profile mappings to IL2-compatible files.

Arguments and example values:
- `<il2-root-path>`: Path to IL2 root folder. Example: D:\\Games\\IL-2 Sturmovik Great Battles.
- `--path`: Optional explicit path override. Use when you prefer option form over positional path.
- `--mode`: Export mode for DCS/IL2 export commands: clean, add, merge, merge-overwrite.
- `--nuke-untouched-devices`: DCS export only: delete untouched device .diff.lua/.jp files under Config\\Input (true/false).

How arguments interact:
- Import-only filters/settings are not valid for export commands.
- --mode is valid for DCS export and IL2 export.

Examples:
```powershell
engine il2 export 'D:\Games\IL-2 Sturmovik Great Battles' --mode merge-overwrite --text
```

### `engine starcitizen scan [<sc-root-or-profile-path>] [--path <path>] [--source <path>] [--device <name>] [--devices <csv>] [--ignore-device <name>] [--ignore-devices <csv>] [--include-keyboard true|false] [--confirm true|false]`
What it does: Reads the game's control file and shows the joystick binds it contains, without changing the profile.

Arguments and example values:
- `<sc-root-or-profile-path>`: Command argument. See examples for typical values.
- `--path`: Optional explicit path override. Use when you prefer option form over positional path.
- `<path>`: Filesystem path value. Example: C:\\JoyPro\\logs\\preview.png.
- `--source`: Command argument. See examples for typical values.
- `--device`: Include one device per flag. Example: --device LEFT VPC Throttle.
- `<name>`: Name value for surrounding option. Example: A-10C_2 or LEFT VPC Throttle.
- `--devices`: Include devices as CSV. Example: --devices VKB Gladiator NXT,LEFT VPC Throttle.
- `<csv>`: Comma-separated values. Example: 0.1,0.2,0.3 or VKB,LEFT VPC Throttle.
- `--ignore-device`: Exclude one device per flag. Example: --ignore-device Keyboard.
- `--ignore-devices`: Exclude devices as CSV. Example: --ignore-devices Keyboard,Mouse.
- `--include-keyboard`: Command argument. See examples for typical values.
- `--confirm`: Controls suggestion confirmation (true) vs auto-accept (false).

Examples:
```powershell
engine starcitizen scan --text
```

### `engine starcitizen import [<sc-root-or-profile-path>] [--path <path>] [--source <path>] [--merge] [--save-profile <path.pr0file>] [--save-last] [--device <name>] [--devices <csv>] [--ignore-device <name>] [--ignore-devices <csv>] [--include-keyboard true|false] [--confirm true|false]`
What it does: Imports the game's joystick binds into the loaded profile (merged by default; --replace starts from an empty relation list).

Arguments and example values:
- `<sc-root-or-profile-path>`: Command argument. See examples for typical values.
- `--path`: Optional explicit path override. Use when you prefer option form over positional path.
- `<path>`: Filesystem path value. Example: C:\\JoyPro\\logs\\preview.png.
- `--source`: Command argument. See examples for typical values.
- `--merge`: Merge import results into existing in-memory profile.
- `--save-profile`: After import, save profile to this .pr0file path.
- `<path.pr0file>`: Path to profile file. Example: C:\\JoyPro\\profiles\\pilot_main.pr0file.
- `--save-last`: After import, save to last profile path.
- `--device`: Include one device per flag. Example: --device LEFT VPC Throttle.
- `<name>`: Name value for surrounding option. Example: A-10C_2 or LEFT VPC Throttle.
- `--devices`: Include devices as CSV. Example: --devices VKB Gladiator NXT,LEFT VPC Throttle.
- `<csv>`: Comma-separated values. Example: 0.1,0.2,0.3 or VKB,LEFT VPC Throttle.
- `--ignore-device`: Exclude one device per flag. Example: --ignore-device Keyboard.
- `--ignore-devices`: Exclude devices as CSV. Example: --ignore-devices Keyboard,Mouse.
- `--include-keyboard`: Command argument. See examples for typical values.
- `--confirm`: Controls suggestion confirmation (true) vs auto-accept (false).

Examples:
```powershell
engine --profile 'C:\JoyPro\profiles\pilot_main.pr0file' starcitizen import --text
```

### `engine starcitizen export [<sc-root-or-profile-path>] [--path <path>] [--source <path>] [--mode clean|add|merge|merge-overwrite] [--keep-keyboard-defaults true|false] [--prefer-local-order true|false] [--include-keyboard true|false] [--nuke-untouched-devices true|false]   (clean releases the ship binds of the written devices, nuke those of the devices the profile does not use; both switch off the game's default joystick binds on js1 when js1 is written (clean) or unused (nuke))`
What it does: Writes the profile's binds for this game. Only the flight actions JoyPro manages are changed, the game must be closed, and the file is backed up to <file>.jpbak first.

Arguments and example values:
- `<sc-root-or-profile-path>`: Command argument. See examples for typical values.
- `--path`: Optional explicit path override. Use when you prefer option form over positional path.
- `<path>`: Filesystem path value. Example: C:\\JoyPro\\logs\\preview.png.
- `--source`: Command argument. See examples for typical values.
- `--mode`: Export mode for DCS/IL2 export commands: clean, add, merge, merge-overwrite.
- `--keep-keyboard-defaults`: DCS export only: keep existing keyboard defaults/keyboard diff files untouched (true/false).
- `--prefer-local-order`: Command argument. See examples for typical values.
- `--include-keyboard`: Command argument. See examples for typical values.
- `--nuke-untouched-devices`: DCS export only: delete untouched device .diff.lua/.jp files under Config\\Input (true/false).

Examples:
```powershell
engine --profile 'C:\JoyPro\profiles\pilot_main.pr0file' starcitizen export --dry-run --text engine --profile 'C:\JoyPro\profiles\pilot_main.pr0file' starcitizen export --mode merge-overwrite --text
```

### `engine starcitizen list-devices [<sc-root-or-profile-path>] [--path <path>] [--source <path>]   (aliases: devices, list)`
What it does: Runs this command operation.

Arguments and example values:
- `<sc-root-or-profile-path>`: Command argument. See examples for typical values.
- `--path`: Optional explicit path override. Use when you prefer option form over positional path.
- `<path>`: Filesystem path value. Example: C:\\JoyPro\\logs\\preview.png.
- `--source`: Command argument. See examples for typical values.

Examples:
```powershell
engine --help
```

### `engine db summary [--keyboard] [--no-keyboard] [--strict-names]`
What it does: Shows DB health/coverage summary for clean profile data.

Arguments and example values:
- `--keyboard`: Include keyboard clean-profile checks for supported DB commands.
- `--no-keyboard`: Disable keyboard clean-profile checks for supported DB commands.
- `--strict-names`: For DB summary/list/check: require exact clean filename match (no alias fallback accepted).

How arguments interact:
- Alias-matched clean files are shown separately from missing files.
- Use --strict-names to fail if any clean files only match through legacy alias mapping.

Examples:
```powershell
engine db summary --keyboard --text
```

### `engine db list [filter] [--missing] [--keyboard] [--no-keyboard] [--strict-names]`
What it does: Lists DB entries, optionally filtered.

Arguments and example values:
- `filter`: Text filter (case-insensitive substring). Example values: 'A-10', 'Hornet', 'VKB'.
- `--missing`: For db list, show only missing entries.
- `--keyboard`: Include keyboard clean-profile checks for supported DB commands.
- `--no-keyboard`: Disable keyboard clean-profile checks for supported DB commands.
- `--strict-names`: For DB summary/list/check: require exact clean filename match (no alias fallback accepted).

How arguments interact:
- --missing narrows list to missing clean-profile entries.
- --strict-names marks alias-matched clean files as mismatches in db check and surfaces alias states in output.

Examples:
```powershell
engine db list 'A-10' --missing --text
```

### `engine db check [--keyboard] [--no-keyboard] [--strict-names]`
What it does: Validates DB completeness and reports missing required data.

Arguments and example values:
- `--keyboard`: Include keyboard clean-profile checks for supported DB commands.
- `--no-keyboard`: Disable keyboard clean-profile checks for supported DB commands.
- `--strict-names`: For DB summary/list/check: require exact clean filename match (no alias fallback accepted).

How arguments interact:
- Alias-matched clean files are shown separately from missing files.
- Use --strict-names to fail if any clean files only match through legacy alias mapping.

Examples:
```powershell
engine db check --text
```

### `engine db games [filter]`
What it does: Lists available game names from DB catalog.

Arguments and example values:
- `filter`: Text filter (case-insensitive substring). Example values: 'A-10', 'Hornet', 'VKB'.

Examples:
```powershell
engine db games --text
```

### `engine db check-updates [--accept <game>] [--accept-all] [--no-record]   (games updated since their DB was built)`
What it does: Runs this command operation.

Arguments and example values:
- `--accept`: Command argument. See examples for typical values.
- `<game>`: Command argument. See examples for typical values.
- `--accept-all`: Command argument. See examples for typical values.
- `--no-record`: Command argument. See examples for typical values.

Examples:
```powershell
engine --help
```

### `engine db planes [filter] [--game <name>]`
What it does: Lists plane/module names from DB catalog.

Arguments and example values:
- `filter`: Text filter (case-insensitive substring). Example values: 'A-10', 'Hornet', 'VKB'.
- `--game`: Game filter value. Example: DCS or IL2Game.
- `<name>`: Name value for surrounding option. Example: A-10C_2 or LEFT VPC Throttle.

How arguments interact:
- --game is valid only with db planes.

Examples:
```powershell
engine db planes --game DCS 'A-10' --text
```

### `engine db fingerprint`
What it does: Runs this command operation.

Arguments and example values:
- No command-specific arguments.

Examples:
```powershell
engine --help
```

### `engine db paths   (where the lists are read from: the game-data folder for everything built from your games, the program folder for JoyPro's own data)`
What it does: Shows the program folder (JoyPro's own data), the game-data folder <data>\GameData (lists built from your games) and, per game folder, where it is read from (game-data or program) and its recorded game build (docs/installs.md, Game data).

Arguments and example values:
- No command-specific arguments.

Examples:
```powershell
engine db paths --text
```

### `engine db refresh-il2 [<install, data\input folder or .actions file>] [--path <path>] [--db-path <folder>] [--dry-run]   (adds new actions of the game's key mapping files to DB\IL2\IL2.actions; also: engine il2 refresh-db)`
What it does: Runs this command operation.

Arguments and example values:
- `<install, data\input folder or .actions file>`: Command argument. See examples for typical values.
- `--path`: Optional explicit path override. Use when you prefer option form over positional path.
- `<path>`: Filesystem path value. Example: C:\\JoyPro\\logs\\preview.png.
- `--db-path`: Command argument. See examples for typical values.
- `<folder>`: Command argument. See examples for typical values.
- `--dry-run`: Plan mode only: do not click, launch, or change files; report what would run.

Examples:
```powershell
engine --help
```

### `engine db refresh-starcitizen [<install-or-source-path>] [--install-path <path>] [--path <path>] [--source <path-to-source>] [--branch <name>] [--db-path <path>] [--include-user-profiles true|false] [--dry-run true|false]`
What it does: Runs this command operation.

Arguments and example values:
- `<install-or-source-path>`: Command argument. See examples for typical values.
- `--install-path`: DCS install path override (root folder that contains Mods\\aircraft).
- `<path>`: Filesystem path value. Example: C:\\JoyPro\\logs\\preview.png.
- `--path`: Optional explicit path override. Use when you prefer option form over positional path.
- `--source`: Command argument. See examples for typical values.
- `<path-to-source>`: Command argument. See examples for typical values.
- `--branch`: Command argument. See examples for typical values.
- `<name>`: Name value for surrounding option. Example: A-10C_2 or LEFT VPC Throttle.
- `--db-path`: Command argument. See examples for typical values.
- `--include-user-profiles`: Command argument. See examples for typical values.
- `--dry-run`: Plan mode only: do not click, launch, or change files; report what would run.

Examples:
```powershell
engine --help
```

### `engine games installs [--game <id>]   (which supported games are installed and where; import/export/scan/refresh/restore are refused for the others)`
What it does: Runs this command operation.

Arguments and example values:
- `--game`: Game filter value. Example: DCS or IL2Game.
- `<id>`: Command argument. See examples for typical values.

Examples:
```powershell
engine --help
```

### `engine games install set <game> <folder> | clear <game>   (your installation folder of a game, e.g. a portable install; it then counts as installed and detection never replaces it; unset = clear)`
What it does: Runs this command operation.

Arguments and example values:
- `<game>`: Command argument. See examples for typical values.
- `<folder>`: Command argument. See examples for typical values.

Examples:
```powershell
engine --help
```

### `engine games install disable <game> [<game> ...] | enable <game> [<game> ...]   (games you do not have: they count as not installed and the GUI hides them; enable undoes it)`
What it does: Runs this command operation.

Arguments and example values:
- `<game>`: Command argument. See examples for typical values.

Examples:
```powershell
engine --help
```

### `engine games config set <game> <file or folder> | clear <game>   (your config location of a game, e.g. Saved Games\DCS.openbeta; it comes first wherever JoyPro looks for the game's controls; not for games that keep them in the installation)`
What it does: Runs this command operation.

Arguments and example values:
- `<game>`: Command argument. See examples for typical values.
- `<file or folder>`: Command argument. See examples for typical values.

Examples:
```powershell
engine --help
```

### `engine games paths [--game <name>] [--games <csv>]`
What it does: Discovers install/config paths for DCS, IL2, and Star Citizen on this machine.

Arguments and example values:
- `--game`: Game filter value. Example: DCS or IL2Game.
- `<name>`: Name value for surrounding option. Example: A-10C_2 or LEFT VPC Throttle.
- `--games`: Game filter values as CSV. Example: DCS,IL2Game,StarCitizen.
- `<csv>`: Comma-separated values. Example: 0.1,0.2,0.3 or VKB,LEFT VPC Throttle.

How arguments interact:
- Without filters, reports all supported games.
- Use --game/--games to narrow output to selected game(s).

Examples:
```powershell
engine games paths --text
```

### `engine version`
What it does: Runs this command operation.

Arguments and example values:
- No command-specific arguments.

Examples:
```powershell
engine --help
```

### `engine games integrations`
What it does: Lists the games supported through the game registry (id, aliases, experimental flag, DB refresh support).

Arguments and example values:
- No command-specific arguments.

Examples:
```powershell
engine --help
```

### `engine games support [--markdown | --html | --json] [--notes <file>]   (how far JoyPro supports each game: the feature matrix from the code plus the verification notes, docs/games/support.md; exit 1 when the notes miss a game or claim more than the code)`
What it does: How far JoyPro supports each game (docs/games/support.md): one row per title with the feature marks taken from the code (import, export, keyboard, modifiers, list refresh, watcher, axis tuning, settings, force feedback, vJoy / Xbox modes, virtual functions, function tables, cockpits, cheat sheets) and the in-game test results, footnotes and per-title facts of docs/games/support-notes.json (built into engine.exe). JSON by default; --markdown writes the page (tools/game_support.ps1), --html the page the GUI opens. Exit 1 when the notes miss a registered game, name an unknown game or cell, claim more than the code or leave a partial mark without a reason.

Arguments and example values:
- `--markdown`: For games support: print the Markdown page (docs/games/support.md) instead of JSON.
- `--html`: For games support: print the page as one HTML file (what the GUI's Supported games opens).
- `--json`: Command argument. See examples for typical values.
- `--notes`: For games support: read the verification notes from this JSON file instead of the copy built into engine.exe. Example: docs\games\support-notes.json.
- `<file>`: Command argument. See examples for typical values.

Examples:
```powershell
engine games support --text
engine games support --markdown --notes docs\games\support-notes.json --out docs\games\support.md
engine games support --html --out 'C:\JoyPro\supported-games.html'
```

### `engine hud devices [--grip <device>=<catalog id>]... [--no-device-photos]   (connected devices for the HUD: product picture address, axes with their role, buttons, hats, layout picture with button positions; --no-device-photos leaves the makers' photo addresses out (the user said never); the engine downloads no device photo, the GUI does per device after the user's yes)`
What it does: Runs this command operation.

Arguments and example values:
- `--grip`: Command argument. See examples for typical values.
- `<device>`: Command argument. See examples for typical values.
- `<catalog id>`: Command argument. See examples for typical values.
- `--no-device-photos`: Command argument. See examples for typical values.

Examples:
```powershell
engine --help
```

### `engine overlay map [--game <id>] [--plane <name>] [--unit <DCS unit type>]   (bound buttons with modifiers and titles, for the overlay)`
What it does: Runs this command operation.

Arguments and example values:
- `--game`: Game filter value. Example: DCS or IL2Game.
- `<id>`: Command argument. See examples for typical values.
- `--plane`: Include one plane/module per flag. Example: --plane A-10C_2.
- `<name>`: Name value for surrounding option. Example: A-10C_2 or LEFT VPC Throttle.
- `--unit`: Command argument. See examples for typical values.
- `<DCS unit type>`: Command argument. See examples for typical values.

Examples:
```powershell
engine --help
```

### `engine overlay dcs-hook status|install|remove [--path <Saved Games\DCS>] [--dry-run]   (Export.lua addition that tells JoyPro the aircraft; backed up)`
What it does: Runs this command operation.

Arguments and example values:
- `--path`: Optional explicit path override. Use when you prefer option form over positional path.
- `<Saved Games\DCS>`: Command argument. See examples for typical values.
- `--dry-run`: Plan mode only: do not click, launch, or change files; report what would run.

Examples:
```powershell
engine --help
```

### `engine media [games|aircraft|sources] [--no-internet-pictures]   (official pictures: game covers from Steam on this PC, else downloaded once from Steam's store or the publisher's site, else the game's executable icon; DCS aircraft pictures from the modules; sources = the web source per game)`
What it does: Runs this command operation.

Arguments and example values:
- `--no-internet-pictures`: Command argument. See examples for typical values.

Examples:
```powershell
engine --help
```

### `engine backups list [--game <id>] | show <id> | restore <id|latest> [--game <id>] [--dry-run]`
What it does: Runs this command operation.

Arguments and example values:
- `--game`: Game filter value. Example: DCS or IL2Game.
- `<id>`: Command argument. See examples for typical values.
- `<id|latest>`: Command argument. See examples for typical values.
- `--dry-run`: Plan mode only: do not click, launch, or change files; report what would run.

Examples:
```powershell
engine --help
```

### `engine games devices   (connected devices with VID:PID, HID name and DirectInput axes/buttons/hats)`
What it does: Runs this command operation.

Arguments and example values:
- No command-specific arguments.

Examples:
```powershell
engine --help
```

### `engine sync status [--game <id>] [--path <config>]   (per game the config files / registry values JoyPro manages, their fingerprint and whether the game changed them since JoyPro's last export, accept or apply)`
What it does: Per game the config files (or registry values) JoyPro manages, their fingerprint and whether the game changed them since JoyPro's last export, accept or apply (docs/sync-watcher.md).

Arguments and example values:
- `--game`: Game filter value. Example: DCS or IL2Game.
- `<id>`: Command argument. See examples for typical values.
- `--path`: Optional explicit path override. Use when you prefer option form over positional path.
- `<config>`: The game's config (file, folder or HKCU registry key) as engine sync status lists it; default: the one the export uses.

Examples:
```powershell
engine sync status --text
engine sync status --game DCS
```

### `engine sync accept --game <id> [--path <config>]   (the current config counts as known; nothing is imported)`
What it does: Runs this command operation.

Arguments and example values:
- `--game`: Game filter value. Example: DCS or IL2Game.
- `<id>`: Command argument. See examples for typical values.
- `--path`: Optional explicit path override. Use when you prefer option form over positional path.
- `<config>`: The game's config (file, folder or HKCU registry key) as engine sync status lists it; default: the one the export uses.

Examples:
```powershell
engine --help
```

### `engine sync diff --game <id> [--path <config>]   (what the game changed that JoyPro manages, compared with the profile: added, removed, changed-tuning, changed-modifiers, with candidate relations)`
What it does: Runs this command operation.

Arguments and example values:
- `--game`: Game filter value. Example: DCS or IL2Game.
- `<id>`: Command argument. See examples for typical values.
- `--path`: Optional explicit path override. Use when you prefer option form over positional path.
- `<config>`: The game's config (file, folder or HKCU registry key) as engine sync status lists it; default: the one the export uses.

Examples:
```powershell
engine --help
```

### `engine sync apply --game <id> --choices <file.json> [--path <config>]   (per change id: Target = relation name, "new" (+ Name) or "ignore"; then the config counts as known)`
What it does: Runs this command operation.

Arguments and example values:
- `--game`: Game filter value. Example: DCS or IL2Game.
- `<id>`: Command argument. See examples for typical values.
- `--choices`: For sync apply: JSON file with the user's choice per change id: {"Choices":[{"Id":"c1a2b3c4d5","Target":"Gear Toggle"},{"Id":"...","Target":"new","Name":"My Relation"},{"Id":"...","Target":"ignore"}]}.
- `<file.json>`: Path to a JSON file. Example: C:\JoyPro\sync-choices.json.
- `--path`: Optional explicit path override. Use when you prefer option form over positional path.
- `<config>`: The game's config (file, folder or HKCU registry key) as engine sync status lists it; default: the one the export uses.

Examples:
```powershell
engine --help
```

### `engine vjoy status   (virtual joystick mode: vJoy and HidHide installed, vJoy devices, HidHide state and JoyPro's session, games native/virtual, the mapping table, what vJoy must offer; read-only)`
What it does: Virtual joystick mode (docs/virtual-joystick.md): vJoy and HidHide installed, the vJoy devices (configuration and DirectInput name), HidHide's state, JoyPro's HidHide session (stale when its owner is gone), the games native/virtual, the mapping table and what vJoy must offer. Read-only.

Arguments and example values:
- No command-specific arguments.

How arguments interact:
- Virtual joystick mode: docs/virtual-joystick.md. JOYPRO_VJOY_FAKE=<folder> replaces vJoy and HidHide by files (tests).

Examples:
```powershell
engine --profile 'C:\JoyPro\profiles\pilot_main.pr0file' vjoy status --text
```

### `engine vjoy mode <game> native|virtual|xbox [--dry-run]   (per game: physical devices, the virtual joystick (vJoy, every used input gets a fixed vJoy slot) or the virtual Xbox 360 pad (ViGEmBus, for XInput-only or one-device games: the game's gamepad layout first, then sticks, triggers, buttons, D-pad))`
What it does: Switches a game between its physical devices (native), the virtual joystick (virtual, vJoy) and the virtual Xbox 360 pad (xbox, ViGEmBus: for games that only read XInput or one device; the game's gamepad layout pins actions to their controls, the rest fills sticks, triggers, 14 buttons and the D-pad, what does not fit is listed). Virtual gives every input the game's relations use (axes, buttons, hats, modifier buttons) a fixed vJoy slot; export, import and sync of the game then work on the vJoy slots.

Arguments and example values:
- `<game>`: Command argument. See examples for typical values.
- `--dry-run`: Plan mode only: do not click, launch, or change files; report what would run.

How arguments interact:
- Virtual joystick mode: docs/virtual-joystick.md. JOYPRO_VJOY_FAKE=<folder> replaces vJoy and HidHide by files (tests).

Examples:
```powershell
engine --profile 'C:\JoyPro\profiles\pilot_main.pr0file' vjoy mode DCS virtual --text
engine --profile 'C:\JoyPro\profiles\pilot_main.pr0file' vjoy mode DCS native --text
engine --profile 'C:\JoyPro\profiles\pilot_main.pr0file' vjoy mode HLLVietnam xbox --text
```

### `engine vjoy plan [--reset] [--prune]   (the mapping table and the vJoy configuration it needs; nothing is saved)`
What it does: Shows the mapping table and the vJoy configuration it needs without saving anything.

Arguments and example values:
- `--reset`: Command argument. See examples for typical values.
- `--prune`: Command argument. See examples for typical values.

How arguments interact:
- Virtual joystick mode: docs/virtual-joystick.md. JOYPRO_VJOY_FAKE=<folder> replaces vJoy and HidHide by files (tests).

Examples:
```powershell
engine --profile 'C:\JoyPro\profiles\pilot_main.pr0file' vjoy plan --text
```

### `engine vjoy map [--reset] [--prune] [--dry-run]   (saves the mapping table in the profile; slots never move unless --reset; --prune drops unused slots)`
What it does: Saves the mapping table in the profile: existing slots never move, new inputs get free slots (an axis keeps its own axis where free); --prune drops unused slots, --reset starts over.

Arguments and example values:
- `--reset`: Command argument. See examples for typical values.
- `--prune`: Command argument. See examples for typical values.
- `--dry-run`: Plan mode only: do not click, launch, or change files; report what would run.

How arguments interact:
- Virtual joystick mode: docs/virtual-joystick.md. JOYPRO_VJOY_FAKE=<folder> replaces vJoy and HidHide by files (tests).

Examples:
```powershell
engine --profile 'C:\JoyPro\profiles\pilot_main.pr0file' vjoy map --text
engine --profile 'C:\JoyPro\profiles\pilot_main.pr0file' vjoy map --prune --dry-run --text
```

### `engine vjoy settings [--first-device N] [--last-device N] [--auto-start on|off] [--hide-devices on|off] [--dry-run]   (which vJoy devices JoyPro may use, GUI auto start, HidHide on/off)`
What it does: Which vJoy devices JoyPro may use (--first-device/--last-device), the GUI's auto start, and whether HidHide hides the physical devices.

Arguments and example values:
- `--first-device`: Command argument. See examples for typical values.
- `--last-device`: Command argument. See examples for typical values.
- `--auto-start`: Command argument. See examples for typical values.
- `--hide-devices`: Command argument. See examples for typical values.
- `--dry-run`: Plan mode only: do not click, launch, or change files; report what would run.

How arguments interact:
- Virtual joystick mode: docs/virtual-joystick.md. JOYPRO_VJOY_FAKE=<folder> replaces vJoy and HidHide by files (tests).

Examples:
```powershell
engine --profile 'C:\JoyPro\profiles\pilot_main.pr0file' vjoy settings --first-device 2 --last-device 3 --text
```

### `engine vjoy exe <game> [--add <path>] [--remove <path>]   (the game executables HidHide blocks in virtual mode: found in the installation + your own)`
What it does: Lists (and adds/removes) the game executables HidHide blocks in virtual mode: found in the game's installation, plus your own.

Arguments and example values:
- `<game>`: Command argument. See examples for typical values.
- `--add`: Command argument. See examples for typical values.
- `<path>`: Filesystem path value. Example: C:\\JoyPro\\logs\\preview.png.
- `--remove`: Command argument. See examples for typical values.

How arguments interact:
- Virtual joystick mode: docs/virtual-joystick.md. JOYPRO_VJOY_FAKE=<folder> replaces vJoy and HidHide by files (tests).

Examples:
```powershell
engine vjoy exe DCS --text
engine vjoy exe DCS --add 'D:\DCS World\bin-mt\DCS.exe' --text
```

### `engine vjoy forwarder-config [--out <file>] [--xbox-game <game>] [--game <game>]   (the mapping table for JoyPro.VJoy.exe, the forwarding process; one Xbox game's pad table; with --game only that game's virtual functions)`
What it does: Writes the mapping table for JoyPro.VJoy.exe, the process that forwards the physical inputs to vJoy.

Arguments and example values:
- `--out`: Global option: write result output to file path instead of terminal.
- `<file>`: Command argument. See examples for typical values.
- `--xbox-game`: Command argument. See examples for typical values.
- `<game>`: Command argument. See examples for typical values.
- `--game`: Game filter value. Example: DCS or IL2Game.

How arguments interact:
- Virtual joystick mode: docs/virtual-joystick.md. JOYPRO_VJOY_FAKE=<folder> replaces vJoy and HidHide by files (tests).

Examples:
```powershell
engine --profile 'C:\JoyPro\profiles\pilot_main.pr0file' vjoy forwarder-config --out 'C:\JoyPro\forwarder.json'
```

### `engine vjoy functions types   (the virtual function types: axis bands, button cycle, toggle, switch pulses, short/long/double press, shift layer, merge, split, curve, axis ends, axes to hat, buttons to axis, encoder, turbo, macro, hat to buttons, buttons to hat)`
What it does: Runs this command operation.

Arguments and example values:
- No command-specific arguments.

How arguments interact:
- Virtual joystick mode: docs/virtual-joystick.md. JOYPRO_VJOY_FAKE=<folder> replaces vJoy and HidHide by files (tests).

Examples:
```powershell
engine --help
```

### `engine vjoy functions list [--game <game>]   (the virtual functions per game with inputs, outputs (bind device and input), problems, limit warnings and the physical devices for inputs)`
What it does: Runs this command operation.

Arguments and example values:
- `--game`: Game filter value. Example: DCS or IL2Game.
- `<game>`: Command argument. See examples for typical values.

How arguments interact:
- Virtual joystick mode: docs/virtual-joystick.md. JOYPRO_VJOY_FAKE=<folder> replaces vJoy and HidHide by files (tests).

Examples:
```powershell
engine --help
```

### `engine vjoy functions add --game <game> --type <type> [--name <name>] [--input role=<device>|<input>]... [--output role=<target>[@<vJoy device>][:hold|enter|leave|both]]... [--param key=value]... [--edges 0.1,0.9] [--count n] [--tuning <axis tuning>] [--keyboard on|off] [--spec <json>|@<file>] [--no-auto-outputs] [--dry-run]   (adds a function; outputs left empty get free vJoy buttons/axes/hats (or Xbox pad controls), reserved in the table)`
What it does: Runs this command operation.

Arguments and example values:
- `--game`: Game filter value. Example: DCS or IL2Game.
- `<game>`: Command argument. See examples for typical values.
- `--type`: Set relation type via value token (`axis` or `button`).
- `<type>`: Command argument. See examples for typical values.
- `--name`: Name value for commands that create/merge entities. Example: --name 'Merged Startup'.
- `<name>`: Name value for surrounding option. Example: A-10C_2 or LEFT VPC Throttle.
- `--input`: Input token. Example: --input JOY_BTN1 or --input joy0_b0.
- `<device>`: Command argument. See examples for typical values.
- `<input>`: Command argument. See examples for typical values.
- `--output`: Command argument. See examples for typical values.
- `<target>`: Command argument. See examples for typical values.
- `<vJoy device>`: Command argument. See examples for typical values.
- `--param`: A response model parameter as name=value, repeatable. Example: --param sensitivity=22 --param offset=10.
- `--edges`: Command argument. See examples for typical values.
- `--count`: Command argument. See examples for typical values.
- `--tuning`: Command argument. See examples for typical values.
- `<axis tuning>`: Command argument. See examples for typical values.
- `--keyboard`: Include keyboard clean-profile checks for supported DB commands.
- `--spec`: Command argument. See examples for typical values.
- `<json>`: Command argument. See examples for typical values.
- `<file>`: Command argument. See examples for typical values.
- `--no-auto-outputs`: Command argument. See examples for typical values.
- `--dry-run`: Plan mode only: do not click, launch, or change files; report what would run.

How arguments interact:
- Virtual joystick mode: docs/virtual-joystick.md. JOYPRO_VJOY_FAKE=<folder> replaces vJoy and HidHide by files (tests).

Examples:
```powershell
engine --help
```

### `engine vjoy functions set --game <game> --id <id> [same options as add] [--enabled on|off] [--auto-outputs] [--dry-run]   (changes a function; --spec replaces it)`
What it does: Runs this command operation.

Arguments and example values:
- `--game`: Game filter value. Example: DCS or IL2Game.
- `<game>`: Command argument. See examples for typical values.
- `--id`: Node ID. Example: --id d3007pnilu3007cd16vd1vpnilvu0.
- `<id>`: Command argument. See examples for typical values.
- `--enabled`: Command argument. See examples for typical values.
- `--auto-outputs`: Command argument. See examples for typical values.
- `--dry-run`: Plan mode only: do not click, launch, or change files; report what would run.
- `--spec`: Command argument. See examples for typical values.

How arguments interact:
- Virtual joystick mode: docs/virtual-joystick.md. JOYPRO_VJOY_FAKE=<folder> replaces vJoy and HidHide by files (tests).

Examples:
```powershell
engine --help
```

### `engine vjoy functions remove --game <game> --id <id> [--dry-run]   (removes a function; its outputs are free again)`
What it does: Runs this command operation.

Arguments and example values:
- `--game`: Game filter value. Example: DCS or IL2Game.
- `<game>`: Command argument. See examples for typical values.
- `--id`: Node ID. Example: --id d3007pnilu3007cd16vd1vpnilvu0.
- `<id>`: Command argument. See examples for typical values.
- `--dry-run`: Plan mode only: do not click, launch, or change files; report what would run.

How arguments interact:
- Virtual joystick mode: docs/virtual-joystick.md. JOYPRO_VJOY_FAKE=<folder> replaces vJoy and HidHide by files (tests).

Examples:
```powershell
engine --help
```

### `engine vjoy functions test (--game <game> --id <id> | --spec <json>|@<file>) [--stream "<ms>:<role>=<value>; ..."] [--rate Hz] [--duration ms]   (simulates the function with the forwarder's evaluation and lists every output change; nothing goes to vJoy, the pad or the keyboard)`
What it does: Runs this command operation.

Arguments and example values:
- `--game`: Game filter value. Example: DCS or IL2Game.
- `<game>`: Command argument. See examples for typical values.
- `--id`: Node ID. Example: --id d3007pnilu3007cd16vd1vpnilvu0.
- `<id>`: Command argument. See examples for typical values.
- `--spec`: Command argument. See examples for typical values.
- `<json>`: Command argument. See examples for typical values.
- `<file>`: Command argument. See examples for typical values.
- `--stream`: Command argument. See examples for typical values.
- `<ms>`: Command argument. See examples for typical values.
- `<role>`: Command argument. See examples for typical values.
- `<value>`: Generic value placeholder. Example for --input: JOY_BTN1 or joy0_b0.
- `--rate`: Command argument. See examples for typical values.
- `--duration`: Command argument. See examples for typical values.

How arguments interact:
- Virtual joystick mode: docs/virtual-joystick.md. JOYPRO_VJOY_FAKE=<folder> replaces vJoy and HidHide by files (tests).

Examples:
```powershell
engine --help
```

### `engine vjoy limits [--game <game>]   (what each game reads of one joystick: buttons, axes, hats, with confidence and source; vJoy offers 128 buttons, 8 axes, 4 hats)`
What it does: Runs this command operation.

Arguments and example values:
- `--game`: Game filter value. Example: DCS or IL2Game.
- `<game>`: Command argument. See examples for typical values.

How arguments interact:
- Virtual joystick mode: docs/virtual-joystick.md. JOYPRO_VJOY_FAKE=<folder> replaces vJoy and HidHide by files (tests).

Examples:
```powershell
engine --help
```

### `engine vjoy configure-vjoy [--max [--device N]] [--dry-run]   (runs vJoyConfig for the vJoy devices that lack axes, buttons or hats, or with --max sets 8 axes, 128 buttons, 4 hats; one administrator prompt; never shrinks a device; Windows must restart afterwards)`
What it does: Runs vJoyConfig (one administrator prompt) for the vJoy devices that lack axes, buttons or hats for the table; never makes a device smaller.

Arguments and example values:
- `--max`: Command argument. See examples for typical values.
- `--device`: Include one device per flag. Example: --device LEFT VPC Throttle.
- `--dry-run`: Plan mode only: do not click, launch, or change files; report what would run.

How arguments interact:
- Virtual joystick mode: docs/virtual-joystick.md. JOYPRO_VJOY_FAKE=<folder> replaces vJoy and HidHide by files (tests).
- Changes the PC (one Windows administrator prompt); --dry-run shows the commands and changes nothing. Exit code 2 = the prompt was declined.

Examples:
```powershell
engine --profile 'C:\JoyPro\profiles\pilot_main.pr0file' vjoy configure-vjoy --dry-run --text
```

### `engine vjoy apply-hidhide [--dry-run] [--owner-pid N] [--exe <game.exe>]... [--trusted <program.exe>]...   (hides the mapped physical devices from the virtual-mode games only; one administrator prompt; journal first)`
What it does: Hides the mapped physical devices from the virtual-mode games only (HidHide block list; an existing allow list of the user is kept), never hides vJoy, parks dormant entries. One administrator prompt; the journal is written first.

Arguments and example values:
- `--dry-run`: Plan mode only: do not click, launch, or change files; report what would run.
- `--owner-pid`: Command argument. See examples for typical values.
- `--exe`: Command argument. See examples for typical values.
- `<game.exe>`: Command argument. See examples for typical values.
- `--trusted`: Command argument. See examples for typical values.
- `<program.exe>`: Command argument. See examples for typical values.

How arguments interact:
- Virtual joystick mode: docs/virtual-joystick.md. JOYPRO_VJOY_FAKE=<folder> replaces vJoy and HidHide by files (tests).
- Changes the PC (one Windows administrator prompt); --dry-run shows the commands and changes nothing. Exit code 2 = the prompt was declined.

Examples:
```powershell
engine --profile 'C:\JoyPro\profiles\pilot_main.pr0file' vjoy apply-hidhide --dry-run --text
```

### `engine vjoy restore-hidhide [--dry-run]   (undoes exactly JoyPro's HidHide changes and restores the previous HidHide state; one administrator prompt)`
What it does: Undoes exactly JoyPro's HidHide changes and gives HidHide its previous state back (one administrator prompt).

Arguments and example values:
- `--dry-run`: Plan mode only: do not click, launch, or change files; report what would run.

How arguments interact:
- Virtual joystick mode: docs/virtual-joystick.md. JOYPRO_VJOY_FAKE=<folder> replaces vJoy and HidHide by files (tests).
- Changes the PC (one Windows administrator prompt); --dry-run shows the commands and changes nothing. Exit code 2 = the prompt was declined.

Examples:
```powershell
engine vjoy restore-hidhide --dry-run --text
engine vjoy restore-hidhide --text
```

### `engine vjoy recover [--dry-run] [--force]   (restores a session whose owner (the GUI) is gone; --force: also while it runs)`
What it does: Restores a HidHide session whose owner (the GUI) is gone, e.g. after a crash; --force also while the owner runs.

Arguments and example values:
- `--dry-run`: Plan mode only: do not click, launch, or change files; report what would run.
- `--force`: Command argument. See examples for typical values.

How arguments interact:
- Virtual joystick mode: docs/virtual-joystick.md. JOYPRO_VJOY_FAKE=<folder> replaces vJoy and HidHide by files (tests).
- Changes the PC (one Windows administrator prompt); --dry-run shows the commands and changes nothing. Exit code 2 = the prompt was declined.

Examples:
```powershell
engine vjoy recover --text
```

### `engine wardogs paths`
What it does: Lists the config files of this game found on this machine.

Arguments and example values:
- No command-specific arguments.

Examples:
```powershell
engine wardogs paths --text
```

### `engine wardogs catalog`
What it does: Lists the flight actions JoyPro manages for this game (from DB\<Game>\actions.tsv).

Arguments and example values:
- No command-specific arguments.

Examples:
```powershell
engine wardogs catalog --text
```

### `engine wardogs scan [<config-path>] [--path <config-path>]`
What it does: Reads the game's control file and shows the joystick binds it contains, without changing the profile.

Arguments and example values:
- `<config-path>`: Path to the game's control file. Optional: the engine uses the file it finds on this machine. Example: %LOCALAPPDATA%\Wardogs\Saved\Config\WindowsClient\GameUserSettings.ini.
- `--path`: Optional explicit path override. Use when you prefer option form over positional path.

Examples:
```powershell
engine wardogs scan --text
```

### `engine wardogs import [<config-path>] [--path <config-path>] [--replace] [--include-keyboard]`
What it does: Imports the game's joystick binds into the loaded profile (merged by default; --replace starts from an empty relation list).

Arguments and example values:
- `<config-path>`: Path to the game's control file. Optional: the engine uses the file it finds on this machine. Example: %LOCALAPPDATA%\Wardogs\Saved\Config\WindowsClient\GameUserSettings.ini.
- `--path`: Optional explicit path override. Use when you prefer option form over positional path.
- `--replace`: For game import: start from an empty relation list instead of merging into the loaded profile.
- `--include-keyboard`: Command argument. See examples for typical values.

Examples:
```powershell
engine --profile 'C:\JoyPro\profiles\pilot_main.pr0file' wardogs import --text
```

### `engine wardogs export [<config-path>] [--path <config-path>] [--mode clean|add|merge|merge-overwrite] [--dry-run] [--include-keyboard] [--nuke-untouched-devices]   (only aircraft/helicopter actions: clean and nuke release those on the written / unused devices)`
What it does: Writes the profile's binds for this game. Only the flight actions JoyPro manages are changed, the game must be closed, and the file is backed up to <file>.jpbak first.

Arguments and example values:
- `<config-path>`: Path to the game's control file. Optional: the engine uses the file it finds on this machine. Example: %LOCALAPPDATA%\Wardogs\Saved\Config\WindowsClient\GameUserSettings.ini.
- `--path`: Optional explicit path override. Use when you prefer option form over positional path.
- `--mode`: Export mode for DCS/IL2 export commands: clean, add, merge, merge-overwrite.
- `--dry-run`: Plan mode only: do not click, launch, or change files; report what would run.
- `--include-keyboard`: Command argument. See examples for typical values.
- `--nuke-untouched-devices`: DCS export only: delete untouched device .diff.lua/.jp files under Config\\Input (true/false).

Examples:
```powershell
engine --profile 'C:\JoyPro\profiles\pilot_main.pr0file' wardogs export --dry-run --text engine --profile 'C:\JoyPro\profiles\pilot_main.pr0file' wardogs export --mode merge-overwrite --text
```

### `engine wardogs refresh-db [<source-path>] [--dry-run]   (builds the catalog from the installed game, or adds its new actions; writes it only when it changes)`
What it does: Updates this game's action catalog from the game or its files.

Arguments and example values:
- `<source-path>`: Path to read actions from (game install or control file); optional.
- `--dry-run`: Plan mode only: do not click, launch, or change files; report what would run.

Examples:
```powershell
engine wardogs refresh-db --text
```

### `engine arma3 paths`
What it does: Lists the config files of this game found on this machine.

Arguments and example values:
- No command-specific arguments.

Examples:
```powershell
engine arma3 paths --text
```

### `engine arma3 catalog`
What it does: Lists the flight actions JoyPro manages for this game (from DB\<Game>\actions.tsv).

Arguments and example values:
- No command-specific arguments.

Examples:
```powershell
engine arma3 catalog --text
```

### `engine arma3 scan [<config-path>] [--path <config-path>]`
What it does: Reads the game's control file and shows the joystick binds it contains, without changing the profile.

Arguments and example values:
- `<config-path>`: Path to the game's control file. Optional: the engine uses the file it finds on this machine. Example: %LOCALAPPDATA%\Wardogs\Saved\Config\WindowsClient\GameUserSettings.ini.
- `--path`: Optional explicit path override. Use when you prefer option form over positional path.

Examples:
```powershell
engine arma3 scan --text
```

### `engine arma3 import [<config-path>] [--path <config-path>] [--replace] [--include-keyboard]`
What it does: Imports the game's joystick binds into the loaded profile (merged by default; --replace starts from an empty relation list).

Arguments and example values:
- `<config-path>`: Path to the game's control file. Optional: the engine uses the file it finds on this machine. Example: %LOCALAPPDATA%\Wardogs\Saved\Config\WindowsClient\GameUserSettings.ini.
- `--path`: Optional explicit path override. Use when you prefer option form over positional path.
- `--replace`: For game import: start from an empty relation list instead of merging into the loaded profile.
- `--include-keyboard`: Command argument. See examples for typical values.

Examples:
```powershell
engine --profile 'C:\JoyPro\profiles\pilot_main.pr0file' arma3 import --text
```

### `engine arma3 export [<config-path>] [--path <config-path>] [--mode clean|add|merge|merge-overwrite] [--dry-run] [--include-keyboard] [--nuke-untouched-devices]   (only aircraft/helicopter actions: clean and nuke release those on the written / unused devices)`
What it does: Writes the profile's binds for this game. Only the flight actions JoyPro manages are changed, the game must be closed, and the file is backed up to <file>.jpbak first.

Arguments and example values:
- `<config-path>`: Path to the game's control file. Optional: the engine uses the file it finds on this machine. Example: %LOCALAPPDATA%\Wardogs\Saved\Config\WindowsClient\GameUserSettings.ini.
- `--path`: Optional explicit path override. Use when you prefer option form over positional path.
- `--mode`: Export mode for DCS/IL2 export commands: clean, add, merge, merge-overwrite.
- `--dry-run`: Plan mode only: do not click, launch, or change files; report what would run.
- `--include-keyboard`: Command argument. See examples for typical values.
- `--nuke-untouched-devices`: DCS export only: delete untouched device .diff.lua/.jp files under Config\\Input (true/false).

Examples:
```powershell
engine --profile 'C:\JoyPro\profiles\pilot_main.pr0file' arma3 export --dry-run --text engine --profile 'C:\JoyPro\profiles\pilot_main.pr0file' arma3 export --mode merge-overwrite --text
```

### `engine arma3 refresh-db [<source-path>] [--dry-run]   (builds the catalog from the installed game, or adds its new actions; writes it only when it changes)`
What it does: Updates this game's action catalog from the game or its files.

Arguments and example values:
- `<source-path>`: Path to read actions from (game install or control file); optional.
- `--dry-run`: Plan mode only: do not click, launch, or change files; report what would run.

Examples:
```powershell
engine arma3 refresh-db --text
```

### `engine arma2oa paths`
What it does: Lists the config files of this game found on this machine.

Arguments and example values:
- No command-specific arguments.

Examples:
```powershell
engine arma2oa paths --text
```

### `engine arma2oa catalog`
What it does: Lists the flight actions JoyPro manages for this game (from DB\<Game>\actions.tsv).

Arguments and example values:
- No command-specific arguments.

Examples:
```powershell
engine arma2oa catalog --text
```

### `engine arma2oa scan [<config-path>] [--path <config-path>]`
What it does: Reads the game's control file and shows the joystick binds it contains, without changing the profile.

Arguments and example values:
- `<config-path>`: Path to the game's control file. Optional: the engine uses the file it finds on this machine. Example: %LOCALAPPDATA%\Wardogs\Saved\Config\WindowsClient\GameUserSettings.ini.
- `--path`: Optional explicit path override. Use when you prefer option form over positional path.

Examples:
```powershell
engine arma2oa scan --text
```

### `engine arma2oa import [<config-path>] [--path <config-path>] [--replace] [--include-keyboard]`
What it does: Imports the game's joystick binds into the loaded profile (merged by default; --replace starts from an empty relation list).

Arguments and example values:
- `<config-path>`: Path to the game's control file. Optional: the engine uses the file it finds on this machine. Example: %LOCALAPPDATA%\Wardogs\Saved\Config\WindowsClient\GameUserSettings.ini.
- `--path`: Optional explicit path override. Use when you prefer option form over positional path.
- `--replace`: For game import: start from an empty relation list instead of merging into the loaded profile.
- `--include-keyboard`: Command argument. See examples for typical values.

Examples:
```powershell
engine --profile 'C:\JoyPro\profiles\pilot_main.pr0file' arma2oa import --text
```

### `engine arma2oa export [<config-path>] [--path <config-path>] [--mode clean|add|merge|merge-overwrite] [--dry-run] [--include-keyboard] [--nuke-untouched-devices]   (only aircraft/helicopter actions: clean and nuke release those on the written / unused devices)`
What it does: Writes the profile's binds for this game. Only the flight actions JoyPro manages are changed, the game must be closed, and the file is backed up to <file>.jpbak first.

Arguments and example values:
- `<config-path>`: Path to the game's control file. Optional: the engine uses the file it finds on this machine. Example: %LOCALAPPDATA%\Wardogs\Saved\Config\WindowsClient\GameUserSettings.ini.
- `--path`: Optional explicit path override. Use when you prefer option form over positional path.
- `--mode`: Export mode for DCS/IL2 export commands: clean, add, merge, merge-overwrite.
- `--dry-run`: Plan mode only: do not click, launch, or change files; report what would run.
- `--include-keyboard`: Command argument. See examples for typical values.
- `--nuke-untouched-devices`: DCS export only: delete untouched device .diff.lua/.jp files under Config\\Input (true/false).

Examples:
```powershell
engine --profile 'C:\JoyPro\profiles\pilot_main.pr0file' arma2oa export --dry-run --text engine --profile 'C:\JoyPro\profiles\pilot_main.pr0file' arma2oa export --mode merge-overwrite --text
```

### `engine arma2oa refresh-db [<source-path>] [--dry-run]   (builds the catalog from the installed game, or adds its new actions; writes it only when it changes)`
What it does: Updates this game's action catalog from the game or its files.

Arguments and example values:
- `<source-path>`: Path to read actions from (game install or control file); optional.
- `--dry-run`: Plan mode only: do not click, launch, or change files; report what would run.

Examples:
```powershell
engine arma2oa refresh-db --text
```

### `engine arma2 paths`
What it does: Lists the config files of this game found on this machine.

Arguments and example values:
- No command-specific arguments.

Examples:
```powershell
engine arma2 paths --text
```

### `engine arma2 catalog`
What it does: Lists the flight actions JoyPro manages for this game (from DB\<Game>\actions.tsv).

Arguments and example values:
- No command-specific arguments.

Examples:
```powershell
engine arma2 catalog --text
```

### `engine arma2 scan [<config-path>] [--path <config-path>]`
What it does: Reads the game's control file and shows the joystick binds it contains, without changing the profile.

Arguments and example values:
- `<config-path>`: Path to the game's control file. Optional: the engine uses the file it finds on this machine. Example: %LOCALAPPDATA%\Wardogs\Saved\Config\WindowsClient\GameUserSettings.ini.
- `--path`: Optional explicit path override. Use when you prefer option form over positional path.

Examples:
```powershell
engine arma2 scan --text
```

### `engine arma2 import [<config-path>] [--path <config-path>] [--replace] [--include-keyboard]`
What it does: Imports the game's joystick binds into the loaded profile (merged by default; --replace starts from an empty relation list).

Arguments and example values:
- `<config-path>`: Path to the game's control file. Optional: the engine uses the file it finds on this machine. Example: %LOCALAPPDATA%\Wardogs\Saved\Config\WindowsClient\GameUserSettings.ini.
- `--path`: Optional explicit path override. Use when you prefer option form over positional path.
- `--replace`: For game import: start from an empty relation list instead of merging into the loaded profile.
- `--include-keyboard`: Command argument. See examples for typical values.

Examples:
```powershell
engine --profile 'C:\JoyPro\profiles\pilot_main.pr0file' arma2 import --text
```

### `engine arma2 export [<config-path>] [--path <config-path>] [--mode clean|add|merge|merge-overwrite] [--dry-run] [--include-keyboard] [--nuke-untouched-devices]   (only aircraft/helicopter actions: clean and nuke release those on the written / unused devices)`
What it does: Writes the profile's binds for this game. Only the flight actions JoyPro manages are changed, the game must be closed, and the file is backed up to <file>.jpbak first.

Arguments and example values:
- `<config-path>`: Path to the game's control file. Optional: the engine uses the file it finds on this machine. Example: %LOCALAPPDATA%\Wardogs\Saved\Config\WindowsClient\GameUserSettings.ini.
- `--path`: Optional explicit path override. Use when you prefer option form over positional path.
- `--mode`: Export mode for DCS/IL2 export commands: clean, add, merge, merge-overwrite.
- `--dry-run`: Plan mode only: do not click, launch, or change files; report what would run.
- `--include-keyboard`: Command argument. See examples for typical values.
- `--nuke-untouched-devices`: DCS export only: delete untouched device .diff.lua/.jp files under Config\\Input (true/false).

Examples:
```powershell
engine --profile 'C:\JoyPro\profiles\pilot_main.pr0file' arma2 export --dry-run --text engine --profile 'C:\JoyPro\profiles\pilot_main.pr0file' arma2 export --mode merge-overwrite --text
```

### `engine arma2 refresh-db [<source-path>] [--dry-run]   (builds the catalog from the installed game, or adds its new actions; writes it only when it changes)`
What it does: Updates this game's action catalog from the game or its files.

Arguments and example values:
- `<source-path>`: Path to read actions from (game install or control file); optional.
- `--dry-run`: Plan mode only: do not click, launch, or change files; report what would run.

Examples:
```powershell
engine arma2 refresh-db --text
```

### `engine reforger paths`
What it does: Lists the config files of this game found on this machine.

Arguments and example values:
- No command-specific arguments.

Examples:
```powershell
engine reforger paths --text
```

### `engine reforger catalog`
What it does: Lists the flight actions JoyPro manages for this game (from DB\<Game>\actions.tsv).

Arguments and example values:
- No command-specific arguments.

Examples:
```powershell
engine reforger catalog --text
```

### `engine reforger scan [<config-path>] [--path <config-path>]`
What it does: Reads the game's control file and shows the joystick binds it contains, without changing the profile.

Arguments and example values:
- `<config-path>`: Path to the game's control file. Optional: the engine uses the file it finds on this machine. Example: %LOCALAPPDATA%\Wardogs\Saved\Config\WindowsClient\GameUserSettings.ini.
- `--path`: Optional explicit path override. Use when you prefer option form over positional path.

Examples:
```powershell
engine reforger scan --text
```

### `engine reforger import [<config-path>] [--path <config-path>] [--replace] [--include-keyboard]`
What it does: Imports the game's joystick binds into the loaded profile (merged by default; --replace starts from an empty relation list).

Arguments and example values:
- `<config-path>`: Path to the game's control file. Optional: the engine uses the file it finds on this machine. Example: %LOCALAPPDATA%\Wardogs\Saved\Config\WindowsClient\GameUserSettings.ini.
- `--path`: Optional explicit path override. Use when you prefer option form over positional path.
- `--replace`: For game import: start from an empty relation list instead of merging into the loaded profile.
- `--include-keyboard`: Command argument. See examples for typical values.

Examples:
```powershell
engine --profile 'C:\JoyPro\profiles\pilot_main.pr0file' reforger import --text
```

### `engine reforger export [<config-path>] [--path <config-path>] [--mode clean|add|merge|merge-overwrite] [--dry-run] [--include-keyboard] [--nuke-untouched-devices]   (only aircraft/helicopter actions: clean and nuke release those on the written / unused devices)`
What it does: Writes the profile's binds for this game. Only the flight actions JoyPro manages are changed, the game must be closed, and the file is backed up to <file>.jpbak first.

Arguments and example values:
- `<config-path>`: Path to the game's control file. Optional: the engine uses the file it finds on this machine. Example: %LOCALAPPDATA%\Wardogs\Saved\Config\WindowsClient\GameUserSettings.ini.
- `--path`: Optional explicit path override. Use when you prefer option form over positional path.
- `--mode`: Export mode for DCS/IL2 export commands: clean, add, merge, merge-overwrite.
- `--dry-run`: Plan mode only: do not click, launch, or change files; report what would run.
- `--include-keyboard`: Command argument. See examples for typical values.
- `--nuke-untouched-devices`: DCS export only: delete untouched device .diff.lua/.jp files under Config\\Input (true/false).

Examples:
```powershell
engine --profile 'C:\JoyPro\profiles\pilot_main.pr0file' reforger export --dry-run --text engine --profile 'C:\JoyPro\profiles\pilot_main.pr0file' reforger export --mode merge-overwrite --text
```

### `engine reforger refresh-db [<source-path>] [--dry-run]   (builds the catalog from the installed game, or adds its new actions; writes it only when it changes)`
What it does: Updates this game's action catalog from the game or its files.

Arguments and example values:
- `<source-path>`: Path to read actions from (game install or control file); optional.
- `--dry-run`: Plan mode only: do not click, launch, or change files; report what would run.

Examples:
```powershell
engine reforger refresh-db --text
```

### `engine warthunder paths`
What it does: Lists the config files of this game found on this machine.

Arguments and example values:
- No command-specific arguments.

Examples:
```powershell
engine warthunder paths --text
```

### `engine warthunder catalog`
What it does: Lists the flight actions JoyPro manages for this game (from DB\<Game>\actions.tsv).

Arguments and example values:
- No command-specific arguments.

Examples:
```powershell
engine warthunder catalog --text
```

### `engine warthunder scan [<config-path>] [--path <config-path>]`
What it does: Reads the game's control file and shows the joystick binds it contains, without changing the profile.

Arguments and example values:
- `<config-path>`: Path to the game's control file. Optional: the engine uses the file it finds on this machine. Example: %LOCALAPPDATA%\Wardogs\Saved\Config\WindowsClient\GameUserSettings.ini.
- `--path`: Optional explicit path override. Use when you prefer option form over positional path.

Examples:
```powershell
engine warthunder scan --text
```

### `engine warthunder import [<config-path>] [--path <config-path>] [--replace] [--include-keyboard]`
What it does: Imports the game's joystick binds into the loaded profile (merged by default; --replace starts from an empty relation list).

Arguments and example values:
- `<config-path>`: Path to the game's control file. Optional: the engine uses the file it finds on this machine. Example: %LOCALAPPDATA%\Wardogs\Saved\Config\WindowsClient\GameUserSettings.ini.
- `--path`: Optional explicit path override. Use when you prefer option form over positional path.
- `--replace`: For game import: start from an empty relation list instead of merging into the loaded profile.
- `--include-keyboard`: Command argument. See examples for typical values.

Examples:
```powershell
engine --profile 'C:\JoyPro\profiles\pilot_main.pr0file' warthunder import --text
```

### `engine warthunder export [<config-path>] [--path <config-path>] [--mode clean|add|merge|merge-overwrite] [--dry-run] [--include-keyboard] [--nuke-untouched-devices]   (only aircraft/helicopter actions: clean and nuke release those on the written / unused devices)`
What it does: Writes the profile's binds for this game. Only the flight actions JoyPro manages are changed, the game must be closed, and the file is backed up to <file>.jpbak first.

Arguments and example values:
- `<config-path>`: Path to the game's control file. Optional: the engine uses the file it finds on this machine. Example: %LOCALAPPDATA%\Wardogs\Saved\Config\WindowsClient\GameUserSettings.ini.
- `--path`: Optional explicit path override. Use when you prefer option form over positional path.
- `--mode`: Export mode for DCS/IL2 export commands: clean, add, merge, merge-overwrite.
- `--dry-run`: Plan mode only: do not click, launch, or change files; report what would run.
- `--include-keyboard`: Command argument. See examples for typical values.
- `--nuke-untouched-devices`: DCS export only: delete untouched device .diff.lua/.jp files under Config\\Input (true/false).

Examples:
```powershell
engine --profile 'C:\JoyPro\profiles\pilot_main.pr0file' warthunder export --dry-run --text engine --profile 'C:\JoyPro\profiles\pilot_main.pr0file' warthunder export --mode merge-overwrite --text
```

### `engine warthunder refresh-db [<source-path>] [--dry-run] [--no-game-archives]   (builds the catalog from the installed game, or adds its new actions; writes it only when it changes); War Thunder: the full list from gui.vromfs.bin, with --no-game-archives only JoyPro's own list`
What it does: Updates this game's action catalog from the game or its files.

Arguments and example values:
- `<source-path>`: Path to read actions from (game install or control file); optional.
- `--dry-run`: Plan mode only: do not click, launch, or change files; report what would run.
- `--no-game-archives`: Command argument. See examples for typical values.

Examples:
```powershell
engine warthunder refresh-db --text
```

### `engine xplane12 paths`
What it does: Lists the config files of this game found on this machine.

Arguments and example values:
- No command-specific arguments.

Examples:
```powershell
engine xplane12 paths --text
```

### `engine xplane12 catalog`
What it does: Lists the flight actions JoyPro manages for this game (from DB\<Game>\actions.tsv).

Arguments and example values:
- No command-specific arguments.

Examples:
```powershell
engine xplane12 catalog --text
```

### `engine xplane12 scan [<config-path>] [--path <config-path>]`
What it does: Reads the game's control file and shows the joystick binds it contains, without changing the profile.

Arguments and example values:
- `<config-path>`: Path to the game's control file. Optional: the engine uses the file it finds on this machine. Example: %LOCALAPPDATA%\Wardogs\Saved\Config\WindowsClient\GameUserSettings.ini.
- `--path`: Optional explicit path override. Use when you prefer option form over positional path.

Examples:
```powershell
engine xplane12 scan --text
```

### `engine xplane12 import [<config-path>] [--path <config-path>] [--replace] [--include-keyboard]`
What it does: Imports the game's joystick binds into the loaded profile (merged by default; --replace starts from an empty relation list).

Arguments and example values:
- `<config-path>`: Path to the game's control file. Optional: the engine uses the file it finds on this machine. Example: %LOCALAPPDATA%\Wardogs\Saved\Config\WindowsClient\GameUserSettings.ini.
- `--path`: Optional explicit path override. Use when you prefer option form over positional path.
- `--replace`: For game import: start from an empty relation list instead of merging into the loaded profile.
- `--include-keyboard`: Command argument. See examples for typical values.

Examples:
```powershell
engine --profile 'C:\JoyPro\profiles\pilot_main.pr0file' xplane12 import --text
```

### `engine xplane12 export [<config-path>] [--path <config-path>] [--mode clean|add|merge|merge-overwrite] [--dry-run] [--include-keyboard] [--nuke-untouched-devices]   (flight only: clean takes over the devices it writes, nuke clears unused devices)`
What it does: Writes the profile's binds for this game. Only the flight actions JoyPro manages are changed, the game must be closed, and the file is backed up to <file>.jpbak first.

Arguments and example values:
- `<config-path>`: Path to the game's control file. Optional: the engine uses the file it finds on this machine. Example: %LOCALAPPDATA%\Wardogs\Saved\Config\WindowsClient\GameUserSettings.ini.
- `--path`: Optional explicit path override. Use when you prefer option form over positional path.
- `--mode`: Export mode for DCS/IL2 export commands: clean, add, merge, merge-overwrite.
- `--dry-run`: Plan mode only: do not click, launch, or change files; report what would run.
- `--include-keyboard`: Command argument. See examples for typical values.
- `--nuke-untouched-devices`: DCS export only: delete untouched device .diff.lua/.jp files under Config\\Input (true/false).

Examples:
```powershell
engine --profile 'C:\JoyPro\profiles\pilot_main.pr0file' xplane12 export --dry-run --text engine --profile 'C:\JoyPro\profiles\pilot_main.pr0file' xplane12 export --mode merge-overwrite --text
```

### `engine xplane12 refresh-db [<source-path>] [--dry-run]   (builds the catalog from the installed game, or adds its new actions; writes it only when it changes)`
What it does: Updates this game's action catalog from the game or its files.

Arguments and example values:
- `<source-path>`: Path to read actions from (game install or control file); optional.
- `--dry-run`: Plan mode only: do not click, launch, or change files; report what would run.

Examples:
```powershell
engine xplane12 refresh-db --text
```

### `engine xplane11 paths`
What it does: Lists the config files of this game found on this machine.

Arguments and example values:
- No command-specific arguments.

Examples:
```powershell
engine xplane11 paths --text
```

### `engine xplane11 catalog`
What it does: Lists the flight actions JoyPro manages for this game (from DB\<Game>\actions.tsv).

Arguments and example values:
- No command-specific arguments.

Examples:
```powershell
engine xplane11 catalog --text
```

### `engine xplane11 scan [<config-path>] [--path <config-path>]`
What it does: Reads the game's control file and shows the joystick binds it contains, without changing the profile.

Arguments and example values:
- `<config-path>`: Path to the game's control file. Optional: the engine uses the file it finds on this machine. Example: %LOCALAPPDATA%\Wardogs\Saved\Config\WindowsClient\GameUserSettings.ini.
- `--path`: Optional explicit path override. Use when you prefer option form over positional path.

Examples:
```powershell
engine xplane11 scan --text
```

### `engine xplane11 import [<config-path>] [--path <config-path>] [--replace] [--include-keyboard]`
What it does: Imports the game's joystick binds into the loaded profile (merged by default; --replace starts from an empty relation list).

Arguments and example values:
- `<config-path>`: Path to the game's control file. Optional: the engine uses the file it finds on this machine. Example: %LOCALAPPDATA%\Wardogs\Saved\Config\WindowsClient\GameUserSettings.ini.
- `--path`: Optional explicit path override. Use when you prefer option form over positional path.
- `--replace`: For game import: start from an empty relation list instead of merging into the loaded profile.
- `--include-keyboard`: Command argument. See examples for typical values.

Examples:
```powershell
engine --profile 'C:\JoyPro\profiles\pilot_main.pr0file' xplane11 import --text
```

### `engine xplane11 export [<config-path>] [--path <config-path>] [--mode clean|add|merge|merge-overwrite] [--dry-run] [--include-keyboard] [--nuke-untouched-devices]   (flight only: clean takes over the devices it writes, nuke clears unused devices)`
What it does: Writes the profile's binds for this game. Only the flight actions JoyPro manages are changed, the game must be closed, and the file is backed up to <file>.jpbak first.

Arguments and example values:
- `<config-path>`: Path to the game's control file. Optional: the engine uses the file it finds on this machine. Example: %LOCALAPPDATA%\Wardogs\Saved\Config\WindowsClient\GameUserSettings.ini.
- `--path`: Optional explicit path override. Use when you prefer option form over positional path.
- `--mode`: Export mode for DCS/IL2 export commands: clean, add, merge, merge-overwrite.
- `--dry-run`: Plan mode only: do not click, launch, or change files; report what would run.
- `--include-keyboard`: Command argument. See examples for typical values.
- `--nuke-untouched-devices`: DCS export only: delete untouched device .diff.lua/.jp files under Config\\Input (true/false).

Examples:
```powershell
engine --profile 'C:\JoyPro\profiles\pilot_main.pr0file' xplane11 export --dry-run --text engine --profile 'C:\JoyPro\profiles\pilot_main.pr0file' xplane11 export --mode merge-overwrite --text
```

### `engine xplane11 refresh-db [<source-path>] [--dry-run]   (builds the catalog from the installed game, or adds its new actions; writes it only when it changes)`
What it does: Updates this game's action catalog from the game or its files.

Arguments and example values:
- `<source-path>`: Path to read actions from (game install or control file); optional.
- `--dry-run`: Plan mode only: do not click, launch, or change files; report what would run.

Examples:
```powershell
engine xplane11 refresh-db --text
```

### `engine msfs2024 paths`
What it does: Lists the config files of this game found on this machine.

Arguments and example values:
- No command-specific arguments.

Examples:
```powershell
engine msfs2024 paths --text
```

### `engine msfs2024 catalog`
What it does: Lists the flight actions JoyPro manages for this game (from DB\<Game>\actions.tsv).

Arguments and example values:
- No command-specific arguments.

Examples:
```powershell
engine msfs2024 catalog --text
```

### `engine msfs2024 scan [<config-path>] [--path <config-path>] [--game-profile <name>]`
What it does: Reads the game's control file and shows the joystick binds it contains, without changing the profile.

Arguments and example values:
- `<config-path>`: Path to the game's control file. Optional: the engine uses the file it finds on this machine. Example: %LOCALAPPDATA%\Wardogs\Saved\Config\WindowsClient\GameUserSettings.ini.
- `--path`: Optional explicit path override. Use when you prefer option form over positional path.
- `--game-profile`: Command argument. See examples for typical values.
- `<name>`: Name value for surrounding option. Example: A-10C_2 or LEFT VPC Throttle.

Examples:
```powershell
engine msfs2024 scan --text
```

### `engine msfs2024 import [<config-path>] [--path <config-path>] [--replace] [--include-keyboard] [--game-profile <name>]`
What it does: Imports the game's joystick binds into the loaded profile (merged by default; --replace starts from an empty relation list).

Arguments and example values:
- `<config-path>`: Path to the game's control file. Optional: the engine uses the file it finds on this machine. Example: %LOCALAPPDATA%\Wardogs\Saved\Config\WindowsClient\GameUserSettings.ini.
- `--path`: Optional explicit path override. Use when you prefer option form over positional path.
- `--replace`: For game import: start from an empty relation list instead of merging into the loaded profile.
- `--include-keyboard`: Command argument. See examples for typical values.
- `--game-profile`: Command argument. See examples for typical values.
- `<name>`: Name value for surrounding option. Example: A-10C_2 or LEFT VPC Throttle.

Examples:
```powershell
engine --profile 'C:\JoyPro\profiles\pilot_main.pr0file' msfs2024 import --text
```

### `engine msfs2024 export [<config-path>] [--path <config-path>] [--mode clean|add|merge|merge-overwrite] [--dry-run] [--include-keyboard] [--nuke-untouched-devices] [--game-profile <name>] [--aircraft <aircraft-key>]   (flight only: clean takes over the devices it writes, nuke clears unused devices)`
What it does: Writes the profile's binds for this game. Only the flight actions JoyPro manages are changed, the game must be closed, and the file is backed up to <file>.jpbak first.

Arguments and example values:
- `<config-path>`: Path to the game's control file. Optional: the engine uses the file it finds on this machine. Example: %LOCALAPPDATA%\Wardogs\Saved\Config\WindowsClient\GameUserSettings.ini.
- `--path`: Optional explicit path override. Use when you prefer option form over positional path.
- `--mode`: Export mode for DCS/IL2 export commands: clean, add, merge, merge-overwrite.
- `--dry-run`: Plan mode only: do not click, launch, or change files; report what would run.
- `--include-keyboard`: Command argument. See examples for typical values.
- `--nuke-untouched-devices`: DCS export only: delete untouched device .diff.lua/.jp files under Config\\Input (true/false).
- `--game-profile`: Command argument. See examples for typical values.
- `<name>`: Name value for surrounding option. Example: A-10C_2 or LEFT VPC Throttle.
- `--aircraft`: MSFS export only: also write the relations limited to this aircraft (key from DB\\MSFS\\aircraft.tsv); the sim's presets apply to every aircraft.
- `<aircraft-key>`: MSFS aircraft key (package name, DB\\MSFS\\aircraft.tsv). Example: asobo-aircraft-c152.

Examples:
```powershell
engine --profile 'C:\JoyPro\profiles\pilot_main.pr0file' msfs2024 export --dry-run --text engine --profile 'C:\JoyPro\profiles\pilot_main.pr0file' msfs2024 export --mode merge-overwrite --text
```

### `engine msfs2024 refresh-db [<source-path>] [--dry-run]   (builds the catalog from the installed game, or adds its new actions; writes it only when it changes)`
What it does: Updates this game's action catalog from the game or its files.

Arguments and example values:
- `<source-path>`: Path to read actions from (game install or control file); optional.
- `--dry-run`: Plan mode only: do not click, launch, or change files; report what would run.

Examples:
```powershell
engine msfs2024 refresh-db --text
```

### `engine msfs2020 paths`
What it does: Lists the config files of this game found on this machine.

Arguments and example values:
- No command-specific arguments.

Examples:
```powershell
engine msfs2020 paths --text
```

### `engine msfs2020 catalog`
What it does: Lists the flight actions JoyPro manages for this game (from DB\<Game>\actions.tsv).

Arguments and example values:
- No command-specific arguments.

Examples:
```powershell
engine msfs2020 catalog --text
```

### `engine msfs2020 scan [<config-path>] [--path <config-path>] [--game-profile <name>]`
What it does: Reads the game's control file and shows the joystick binds it contains, without changing the profile.

Arguments and example values:
- `<config-path>`: Path to the game's control file. Optional: the engine uses the file it finds on this machine. Example: %LOCALAPPDATA%\Wardogs\Saved\Config\WindowsClient\GameUserSettings.ini.
- `--path`: Optional explicit path override. Use when you prefer option form over positional path.
- `--game-profile`: Command argument. See examples for typical values.
- `<name>`: Name value for surrounding option. Example: A-10C_2 or LEFT VPC Throttle.

Examples:
```powershell
engine msfs2020 scan --text
```

### `engine msfs2020 import [<config-path>] [--path <config-path>] [--replace] [--include-keyboard] [--game-profile <name>]`
What it does: Imports the game's joystick binds into the loaded profile (merged by default; --replace starts from an empty relation list).

Arguments and example values:
- `<config-path>`: Path to the game's control file. Optional: the engine uses the file it finds on this machine. Example: %LOCALAPPDATA%\Wardogs\Saved\Config\WindowsClient\GameUserSettings.ini.
- `--path`: Optional explicit path override. Use when you prefer option form over positional path.
- `--replace`: For game import: start from an empty relation list instead of merging into the loaded profile.
- `--include-keyboard`: Command argument. See examples for typical values.
- `--game-profile`: Command argument. See examples for typical values.
- `<name>`: Name value for surrounding option. Example: A-10C_2 or LEFT VPC Throttle.

Examples:
```powershell
engine --profile 'C:\JoyPro\profiles\pilot_main.pr0file' msfs2020 import --text
```

### `engine msfs2020 export [<config-path>] [--path <config-path>] [--mode clean|add|merge|merge-overwrite] [--dry-run] [--include-keyboard] [--nuke-untouched-devices] [--game-profile <name>] [--aircraft <aircraft-key>]   (flight only: clean takes over the devices it writes, nuke clears unused devices)`
What it does: Writes the profile's binds for this game. Only the flight actions JoyPro manages are changed, the game must be closed, and the file is backed up to <file>.jpbak first.

Arguments and example values:
- `<config-path>`: Path to the game's control file. Optional: the engine uses the file it finds on this machine. Example: %LOCALAPPDATA%\Wardogs\Saved\Config\WindowsClient\GameUserSettings.ini.
- `--path`: Optional explicit path override. Use when you prefer option form over positional path.
- `--mode`: Export mode for DCS/IL2 export commands: clean, add, merge, merge-overwrite.
- `--dry-run`: Plan mode only: do not click, launch, or change files; report what would run.
- `--include-keyboard`: Command argument. See examples for typical values.
- `--nuke-untouched-devices`: DCS export only: delete untouched device .diff.lua/.jp files under Config\\Input (true/false).
- `--game-profile`: Command argument. See examples for typical values.
- `<name>`: Name value for surrounding option. Example: A-10C_2 or LEFT VPC Throttle.
- `--aircraft`: MSFS export only: also write the relations limited to this aircraft (key from DB\\MSFS\\aircraft.tsv); the sim's presets apply to every aircraft.
- `<aircraft-key>`: MSFS aircraft key (package name, DB\\MSFS\\aircraft.tsv). Example: asobo-aircraft-c152.

Examples:
```powershell
engine --profile 'C:\JoyPro\profiles\pilot_main.pr0file' msfs2020 export --dry-run --text engine --profile 'C:\JoyPro\profiles\pilot_main.pr0file' msfs2020 export --mode merge-overwrite --text
```

### `engine msfs2020 refresh-db [<source-path>] [--dry-run]   (builds the catalog from the installed game, or adds its new actions; writes it only when it changes)`
What it does: Updates this game's action catalog from the game or its files.

Arguments and example values:
- `<source-path>`: Path to read actions from (game install or control file); optional.
- `--dry-run`: Plan mode only: do not click, launch, or change files; report what would run.

Examples:
```powershell
engine msfs2020 refresh-db --text
```

### `engine bms paths`
What it does: Lists the config files of this game found on this machine.

Arguments and example values:
- No command-specific arguments.

Examples:
```powershell
engine bms paths --text
```

### `engine bms catalog`
What it does: Lists the flight actions JoyPro manages for this game (from DB\<Game>\actions.tsv).

Arguments and example values:
- No command-specific arguments.

Examples:
```powershell
engine bms catalog --text
```

### `engine bms scan [<config-path>] [--path <config-path>]`
What it does: Reads the game's control file and shows the joystick binds it contains, without changing the profile.

Arguments and example values:
- `<config-path>`: Path to the game's control file. Optional: the engine uses the file it finds on this machine. Example: %LOCALAPPDATA%\Wardogs\Saved\Config\WindowsClient\GameUserSettings.ini.
- `--path`: Optional explicit path override. Use when you prefer option form over positional path.

Examples:
```powershell
engine bms scan --text
```

### `engine bms import [<config-path>] [--path <config-path>] [--replace] [--include-keyboard]`
What it does: Imports the game's joystick binds into the loaded profile (merged by default; --replace starts from an empty relation list).

Arguments and example values:
- `<config-path>`: Path to the game's control file. Optional: the engine uses the file it finds on this machine. Example: %LOCALAPPDATA%\Wardogs\Saved\Config\WindowsClient\GameUserSettings.ini.
- `--path`: Optional explicit path override. Use when you prefer option form over positional path.
- `--replace`: For game import: start from an empty relation list instead of merging into the loaded profile.
- `--include-keyboard`: Command argument. See examples for typical values.

Examples:
```powershell
engine --profile 'C:\JoyPro\profiles\pilot_main.pr0file' bms import --text
```

### `engine bms export [<config-path>] [--path <config-path>] [--mode clean|add|merge|merge-overwrite] [--dry-run] [--include-keyboard] [--nuke-untouched-devices]   (flight only: clean takes over the devices it writes, nuke clears unused devices)`
What it does: Writes the profile's binds for this game. Only the flight actions JoyPro manages are changed, the game must be closed, and the file is backed up to <file>.jpbak first.

Arguments and example values:
- `<config-path>`: Path to the game's control file. Optional: the engine uses the file it finds on this machine. Example: %LOCALAPPDATA%\Wardogs\Saved\Config\WindowsClient\GameUserSettings.ini.
- `--path`: Optional explicit path override. Use when you prefer option form over positional path.
- `--mode`: Export mode for DCS/IL2 export commands: clean, add, merge, merge-overwrite.
- `--dry-run`: Plan mode only: do not click, launch, or change files; report what would run.
- `--include-keyboard`: Command argument. See examples for typical values.
- `--nuke-untouched-devices`: DCS export only: delete untouched device .diff.lua/.jp files under Config\\Input (true/false).

Examples:
```powershell
engine --profile 'C:\JoyPro\profiles\pilot_main.pr0file' bms export --dry-run --text engine --profile 'C:\JoyPro\profiles\pilot_main.pr0file' bms export --mode merge-overwrite --text
```

### `engine bms refresh-db [<source-path>] [--dry-run]   (builds the catalog from the installed game, or adds its new actions; writes it only when it changes)`
What it does: Updates this game's action catalog from the game or its files.

Arguments and example values:
- `<source-path>`: Path to read actions from (game install or control file); optional.
- `--dry-run`: Plan mode only: do not click, launch, or change files; report what would run.

Examples:
```powershell
engine bms refresh-db --text
```

### `engine il2korea paths`
What it does: Lists the config files of this game found on this machine.

Arguments and example values:
- No command-specific arguments.

Examples:
```powershell
engine il2korea paths --text
```

### `engine il2korea catalog`
What it does: Lists the flight actions JoyPro manages for this game (from DB\<Game>\actions.tsv).

Arguments and example values:
- No command-specific arguments.

Examples:
```powershell
engine il2korea catalog --text
```

### `engine il2korea scan [<config-path>] [--path <config-path>]`
What it does: Reads the game's control file and shows the joystick binds it contains, without changing the profile.

Arguments and example values:
- `<config-path>`: Path to the game's control file. Optional: the engine uses the file it finds on this machine. Example: %LOCALAPPDATA%\Wardogs\Saved\Config\WindowsClient\GameUserSettings.ini.
- `--path`: Optional explicit path override. Use when you prefer option form over positional path.

Examples:
```powershell
engine il2korea scan --text
```

### `engine il2korea import [<config-path>] [--path <config-path>] [--replace] [--include-keyboard]`
What it does: Imports the game's joystick binds into the loaded profile (merged by default; --replace starts from an empty relation list).

Arguments and example values:
- `<config-path>`: Path to the game's control file. Optional: the engine uses the file it finds on this machine. Example: %LOCALAPPDATA%\Wardogs\Saved\Config\WindowsClient\GameUserSettings.ini.
- `--path`: Optional explicit path override. Use when you prefer option form over positional path.
- `--replace`: For game import: start from an empty relation list instead of merging into the loaded profile.
- `--include-keyboard`: Command argument. See examples for typical values.

Examples:
```powershell
engine --profile 'C:\JoyPro\profiles\pilot_main.pr0file' il2korea import --text
```

### `engine il2korea export [<config-path>] [--path <config-path>] [--mode clean|add|merge|merge-overwrite] [--dry-run] [--include-keyboard] [--nuke-untouched-devices]   (flight only: clean takes over the devices it writes, nuke clears unused devices)`
What it does: Writes the profile's binds for this game. Only the flight actions JoyPro manages are changed, the game must be closed, and the file is backed up to <file>.jpbak first.

Arguments and example values:
- `<config-path>`: Path to the game's control file. Optional: the engine uses the file it finds on this machine. Example: %LOCALAPPDATA%\Wardogs\Saved\Config\WindowsClient\GameUserSettings.ini.
- `--path`: Optional explicit path override. Use when you prefer option form over positional path.
- `--mode`: Export mode for DCS/IL2 export commands: clean, add, merge, merge-overwrite.
- `--dry-run`: Plan mode only: do not click, launch, or change files; report what would run.
- `--include-keyboard`: Command argument. See examples for typical values.
- `--nuke-untouched-devices`: DCS export only: delete untouched device .diff.lua/.jp files under Config\\Input (true/false).

Examples:
```powershell
engine --profile 'C:\JoyPro\profiles\pilot_main.pr0file' il2korea export --dry-run --text engine --profile 'C:\JoyPro\profiles\pilot_main.pr0file' il2korea export --mode merge-overwrite --text
```

### `engine il2korea refresh-db [<source-path>] [--dry-run]   (builds the catalog from the installed game, or adds its new actions; writes it only when it changes)`
What it does: Updates this game's action catalog from the game or its files.

Arguments and example values:
- `<source-path>`: Path to read actions from (game install or control file); optional.
- `--dry-run`: Plan mode only: do not click, launch, or change files; report what would run.

Examples:
```powershell
engine il2korea refresh-db --text
```

### `engine x4 paths`
What it does: Lists the config files of this game found on this machine.

Arguments and example values:
- No command-specific arguments.

Examples:
```powershell
engine x4 paths --text
```

### `engine x4 catalog`
What it does: Lists the flight actions JoyPro manages for this game (from DB\<Game>\actions.tsv).

Arguments and example values:
- No command-specific arguments.

Examples:
```powershell
engine x4 catalog --text
```

### `engine x4 scan [<config-path>] [--path <config-path>]`
What it does: Reads the game's control file and shows the joystick binds it contains, without changing the profile.

Arguments and example values:
- `<config-path>`: Path to the game's control file. Optional: the engine uses the file it finds on this machine. Example: %LOCALAPPDATA%\Wardogs\Saved\Config\WindowsClient\GameUserSettings.ini.
- `--path`: Optional explicit path override. Use when you prefer option form over positional path.

Examples:
```powershell
engine x4 scan --text
```

### `engine x4 import [<config-path>] [--path <config-path>] [--replace] [--include-keyboard]`
What it does: Imports the game's joystick binds into the loaded profile (merged by default; --replace starts from an empty relation list).

Arguments and example values:
- `<config-path>`: Path to the game's control file. Optional: the engine uses the file it finds on this machine. Example: %LOCALAPPDATA%\Wardogs\Saved\Config\WindowsClient\GameUserSettings.ini.
- `--path`: Optional explicit path override. Use when you prefer option form over positional path.
- `--replace`: For game import: start from an empty relation list instead of merging into the loaded profile.
- `--include-keyboard`: Command argument. See examples for typical values.

Examples:
```powershell
engine --profile 'C:\JoyPro\profiles\pilot_main.pr0file' x4 import --text
```

### `engine x4 export [<config-path>] [--path <config-path>] [--mode clean|add|merge|merge-overwrite] [--dry-run] [--include-keyboard] [--nuke-untouched-devices]   (only aircraft/helicopter actions: clean and nuke release those on the written / unused devices)`
What it does: Writes the profile's binds for this game. Only the flight actions JoyPro manages are changed, the game must be closed, and the file is backed up to <file>.jpbak first.

Arguments and example values:
- `<config-path>`: Path to the game's control file. Optional: the engine uses the file it finds on this machine. Example: %LOCALAPPDATA%\Wardogs\Saved\Config\WindowsClient\GameUserSettings.ini.
- `--path`: Optional explicit path override. Use when you prefer option form over positional path.
- `--mode`: Export mode for DCS/IL2 export commands: clean, add, merge, merge-overwrite.
- `--dry-run`: Plan mode only: do not click, launch, or change files; report what would run.
- `--include-keyboard`: Command argument. See examples for typical values.
- `--nuke-untouched-devices`: DCS export only: delete untouched device .diff.lua/.jp files under Config\\Input (true/false).

Examples:
```powershell
engine --profile 'C:\JoyPro\profiles\pilot_main.pr0file' x4 export --dry-run --text engine --profile 'C:\JoyPro\profiles\pilot_main.pr0file' x4 export --mode merge-overwrite --text
```

### `engine x4 refresh-db [<source-path>] [--dry-run]   (builds the catalog from the installed game, or adds its new actions; writes it only when it changes)`
What it does: Updates this game's action catalog from the game or its files.

Arguments and example values:
- `<source-path>`: Path to read actions from (game install or control file); optional.
- `--dry-run`: Plan mode only: do not click, launch, or change files; report what would run.

Examples:
```powershell
engine x4 refresh-db --text
```

### `engine elite paths`
What it does: Lists the config files of this game found on this machine.

Arguments and example values:
- No command-specific arguments.

Examples:
```powershell
engine elite paths --text
```

### `engine elite catalog`
What it does: Lists the flight actions JoyPro manages for this game (from DB\<Game>\actions.tsv).

Arguments and example values:
- No command-specific arguments.

Examples:
```powershell
engine elite catalog --text
```

### `engine elite scan [<config-path>] [--path <config-path>]`
What it does: Reads the game's control file and shows the joystick binds it contains, without changing the profile.

Arguments and example values:
- `<config-path>`: Path to the game's control file. Optional: the engine uses the file it finds on this machine. Example: %LOCALAPPDATA%\Wardogs\Saved\Config\WindowsClient\GameUserSettings.ini.
- `--path`: Optional explicit path override. Use when you prefer option form over positional path.

Examples:
```powershell
engine elite scan --text
```

### `engine elite import [<config-path>] [--path <config-path>] [--replace] [--include-keyboard]`
What it does: Imports the game's joystick binds into the loaded profile (merged by default; --replace starts from an empty relation list).

Arguments and example values:
- `<config-path>`: Path to the game's control file. Optional: the engine uses the file it finds on this machine. Example: %LOCALAPPDATA%\Wardogs\Saved\Config\WindowsClient\GameUserSettings.ini.
- `--path`: Optional explicit path override. Use when you prefer option form over positional path.
- `--replace`: For game import: start from an empty relation list instead of merging into the loaded profile.
- `--include-keyboard`: Command argument. See examples for typical values.

Examples:
```powershell
engine --profile 'C:\JoyPro\profiles\pilot_main.pr0file' elite import --text
```

### `engine elite export [<config-path>] [--path <config-path>] [--mode clean|add|merge|merge-overwrite] [--dry-run] [--include-keyboard] [--nuke-untouched-devices]   (only aircraft/helicopter actions: clean and nuke release those on the written / unused devices)`
What it does: Writes the profile's binds for this game. Only the flight actions JoyPro manages are changed, the game must be closed, and the file is backed up to <file>.jpbak first.

Arguments and example values:
- `<config-path>`: Path to the game's control file. Optional: the engine uses the file it finds on this machine. Example: %LOCALAPPDATA%\Wardogs\Saved\Config\WindowsClient\GameUserSettings.ini.
- `--path`: Optional explicit path override. Use when you prefer option form over positional path.
- `--mode`: Export mode for DCS/IL2 export commands: clean, add, merge, merge-overwrite.
- `--dry-run`: Plan mode only: do not click, launch, or change files; report what would run.
- `--include-keyboard`: Command argument. See examples for typical values.
- `--nuke-untouched-devices`: DCS export only: delete untouched device .diff.lua/.jp files under Config\\Input (true/false).

Examples:
```powershell
engine --profile 'C:\JoyPro\profiles\pilot_main.pr0file' elite export --dry-run --text engine --profile 'C:\JoyPro\profiles\pilot_main.pr0file' elite export --mode merge-overwrite --text
```

### `engine elite refresh-db [<source-path>] [--dry-run]   (builds the catalog from the installed game, or adds its new actions; writes it only when it changes)`
What it does: Updates this game's action catalog from the game or its files.

Arguments and example values:
- `<source-path>`: Path to read actions from (game install or control file); optional.
- `--dry-run`: Plan mode only: do not click, launch, or change files; report what would run.

Examples:
```powershell
engine elite refresh-db --text
```

### `engine vtolvr paths`
What it does: Lists the config files of this game found on this machine.

Arguments and example values:
- No command-specific arguments.

Examples:
```powershell
engine vtolvr paths --text
```

### `engine vtolvr catalog`
What it does: Lists the flight actions JoyPro manages for this game (from DB\<Game>\actions.tsv).

Arguments and example values:
- No command-specific arguments.

Examples:
```powershell
engine vtolvr catalog --text
```

### `engine vtolvr scan [<config-path>] [--path <config-path>]`
What it does: Reads the game's control file and shows the joystick binds it contains, without changing the profile.

Arguments and example values:
- `<config-path>`: Path to the game's control file. Optional: the engine uses the file it finds on this machine. Example: %LOCALAPPDATA%\Wardogs\Saved\Config\WindowsClient\GameUserSettings.ini.
- `--path`: Optional explicit path override. Use when you prefer option form over positional path.

Examples:
```powershell
engine vtolvr scan --text
```

### `engine vtolvr import [<config-path>] [--path <config-path>] [--replace] [--include-keyboard]`
What it does: Imports the game's joystick binds into the loaded profile (merged by default; --replace starts from an empty relation list).

Arguments and example values:
- `<config-path>`: Path to the game's control file. Optional: the engine uses the file it finds on this machine. Example: %LOCALAPPDATA%\Wardogs\Saved\Config\WindowsClient\GameUserSettings.ini.
- `--path`: Optional explicit path override. Use when you prefer option form over positional path.
- `--replace`: For game import: start from an empty relation list instead of merging into the loaded profile.
- `--include-keyboard`: Command argument. See examples for typical values.

Examples:
```powershell
engine --profile 'C:\JoyPro\profiles\pilot_main.pr0file' vtolvr import --text
```

### `engine vtolvr export [<config-path>] [--path <config-path>] [--mode clean|add|merge|merge-overwrite] [--dry-run] [--include-keyboard] [--nuke-untouched-devices]   (flight only: clean takes over the devices it writes, nuke clears unused devices)`
What it does: Writes the profile's binds for this game. Only the flight actions JoyPro manages are changed, the game must be closed, and the file is backed up to <file>.jpbak first.

Arguments and example values:
- `<config-path>`: Path to the game's control file. Optional: the engine uses the file it finds on this machine. Example: %LOCALAPPDATA%\Wardogs\Saved\Config\WindowsClient\GameUserSettings.ini.
- `--path`: Optional explicit path override. Use when you prefer option form over positional path.
- `--mode`: Export mode for DCS/IL2 export commands: clean, add, merge, merge-overwrite.
- `--dry-run`: Plan mode only: do not click, launch, or change files; report what would run.
- `--include-keyboard`: Command argument. See examples for typical values.
- `--nuke-untouched-devices`: DCS export only: delete untouched device .diff.lua/.jp files under Config\\Input (true/false).

Examples:
```powershell
engine --profile 'C:\JoyPro\profiles\pilot_main.pr0file' vtolvr export --dry-run --text engine --profile 'C:\JoyPro\profiles\pilot_main.pr0file' vtolvr export --mode merge-overwrite --text
```

### `engine vtolvr refresh-db [<source-path>] [--dry-run]   (builds the catalog from the installed game, or adds its new actions; writes it only when it changes)`
What it does: Updates this game's action catalog from the game or its files.

Arguments and example values:
- `<source-path>`: Path to read actions from (game install or control file); optional.
- `--dry-run`: Plan mode only: do not click, launch, or change files; report what would run.

Examples:
```powershell
engine vtolvr refresh-db --text
```

### `engine vtolvr mod status|prepare [--accept-license]   (the BYOJoystick mod on this PC: Mod Loader, Workshop item, BYOJoystick.dll with SHA-256 and the targets JoyPro reads; prepare backs up the BYOJ_<seat>.xml files and returns the Steam pages to open (Mod Loader, Workshop item 3276352374); Steam downloads and installs the mod, JoyPro ships none of it; refresh-db then reads the targets from the installed mod)`
What it does: Runs this command operation.

Arguments and example values:
- `--accept-license`: Command argument. See examples for typical values.
- `<seat>`: Command argument. See examples for typical values.

Examples:
```powershell
engine --help
```

### `engine vtolvr-native paths`
What it does: Lists the config files of this game found on this machine.

Arguments and example values:
- No command-specific arguments.

Examples:
```powershell
engine vtolvr-native paths --text
```

### `engine vtolvr-native catalog`
What it does: Lists the flight actions JoyPro manages for this game (from DB\<Game>\actions.tsv).

Arguments and example values:
- No command-specific arguments.

Examples:
```powershell
engine vtolvr-native catalog --text
```

### `engine vtolvr-native scan [<config-path>] [--path <config-path>]`
What it does: Reads the game's control file and shows the joystick binds it contains, without changing the profile.

Arguments and example values:
- `<config-path>`: Path to the game's control file. Optional: the engine uses the file it finds on this machine. Example: %LOCALAPPDATA%\Wardogs\Saved\Config\WindowsClient\GameUserSettings.ini.
- `--path`: Optional explicit path override. Use when you prefer option form over positional path.

Examples:
```powershell
engine vtolvr-native scan --text
```

### `engine vtolvr-native import [<config-path>] [--path <config-path>] [--replace] [--include-keyboard]`
What it does: Imports the game's joystick binds into the loaded profile (merged by default; --replace starts from an empty relation list).

Arguments and example values:
- `<config-path>`: Path to the game's control file. Optional: the engine uses the file it finds on this machine. Example: %LOCALAPPDATA%\Wardogs\Saved\Config\WindowsClient\GameUserSettings.ini.
- `--path`: Optional explicit path override. Use when you prefer option form over positional path.
- `--replace`: For game import: start from an empty relation list instead of merging into the loaded profile.
- `--include-keyboard`: Command argument. See examples for typical values.

Examples:
```powershell
engine --profile 'C:\JoyPro\profiles\pilot_main.pr0file' vtolvr-native import --text
```

### `engine vtolvr-native export [<config-path>] [--path <config-path>] [--mode clean|add|merge|merge-overwrite] [--dry-run] [--include-keyboard] [--nuke-untouched-devices]   (flight only: clean takes over the devices it writes, nuke clears unused devices)`
What it does: Writes the profile's binds for this game. Only the flight actions JoyPro manages are changed, the game must be closed, and the file is backed up to <file>.jpbak first.

Arguments and example values:
- `<config-path>`: Path to the game's control file. Optional: the engine uses the file it finds on this machine. Example: %LOCALAPPDATA%\Wardogs\Saved\Config\WindowsClient\GameUserSettings.ini.
- `--path`: Optional explicit path override. Use when you prefer option form over positional path.
- `--mode`: Export mode for DCS/IL2 export commands: clean, add, merge, merge-overwrite.
- `--dry-run`: Plan mode only: do not click, launch, or change files; report what would run.
- `--include-keyboard`: Command argument. See examples for typical values.
- `--nuke-untouched-devices`: DCS export only: delete untouched device .diff.lua/.jp files under Config\\Input (true/false).

Examples:
```powershell
engine --profile 'C:\JoyPro\profiles\pilot_main.pr0file' vtolvr-native export --dry-run --text engine --profile 'C:\JoyPro\profiles\pilot_main.pr0file' vtolvr-native export --mode merge-overwrite --text
```

### `engine vtolvr-native refresh-db [<source-path>] [--dry-run]   (builds the catalog from the installed game, or adds its new actions; writes it only when it changes)`
What it does: Updates this game's action catalog from the game or its files.

Arguments and example values:
- `<source-path>`: Path to read actions from (game install or control file); optional.
- `--dry-run`: Plan mode only: do not click, launch, or change files; report what would run.

Examples:
```powershell
engine vtolvr-native refresh-db --text
```

### `engine nuclearoption paths`
What it does: Lists the config files of this game found on this machine.

Arguments and example values:
- No command-specific arguments.

Examples:
```powershell
engine nuclearoption paths --text
```

### `engine nuclearoption catalog`
What it does: Lists the flight actions JoyPro manages for this game (from DB\<Game>\actions.tsv).

Arguments and example values:
- No command-specific arguments.

Examples:
```powershell
engine nuclearoption catalog --text
```

### `engine nuclearoption scan [<config-path>] [--path <config-path>]`
What it does: Reads the game's control file and shows the joystick binds it contains, without changing the profile.

Arguments and example values:
- `<config-path>`: Path to the game's control file. Optional: the engine uses the file it finds on this machine. Example: %LOCALAPPDATA%\Wardogs\Saved\Config\WindowsClient\GameUserSettings.ini.
- `--path`: Optional explicit path override. Use when you prefer option form over positional path.

Examples:
```powershell
engine nuclearoption scan --text
```

### `engine nuclearoption import [<config-path>] [--path <config-path>] [--replace] [--include-keyboard]`
What it does: Imports the game's joystick binds into the loaded profile (merged by default; --replace starts from an empty relation list).

Arguments and example values:
- `<config-path>`: Path to the game's control file. Optional: the engine uses the file it finds on this machine. Example: %LOCALAPPDATA%\Wardogs\Saved\Config\WindowsClient\GameUserSettings.ini.
- `--path`: Optional explicit path override. Use when you prefer option form over positional path.
- `--replace`: For game import: start from an empty relation list instead of merging into the loaded profile.
- `--include-keyboard`: Command argument. See examples for typical values.

Examples:
```powershell
engine --profile 'C:\JoyPro\profiles\pilot_main.pr0file' nuclearoption import --text
```

### `engine nuclearoption export [<config-path>] [--path <config-path>] [--mode clean|add|merge|merge-overwrite] [--dry-run] [--include-keyboard] [--nuke-untouched-devices]   (only aircraft/helicopter actions: clean and nuke release those on the written / unused devices)`
What it does: Writes the profile's binds for this game. Only the flight actions JoyPro manages are changed, the game must be closed, and the file is backed up to <file>.jpbak first.

Arguments and example values:
- `<config-path>`: Path to the game's control file. Optional: the engine uses the file it finds on this machine. Example: %LOCALAPPDATA%\Wardogs\Saved\Config\WindowsClient\GameUserSettings.ini.
- `--path`: Optional explicit path override. Use when you prefer option form over positional path.
- `--mode`: Export mode for DCS/IL2 export commands: clean, add, merge, merge-overwrite.
- `--dry-run`: Plan mode only: do not click, launch, or change files; report what would run.
- `--include-keyboard`: Command argument. See examples for typical values.
- `--nuke-untouched-devices`: DCS export only: delete untouched device .diff.lua/.jp files under Config\\Input (true/false).

Examples:
```powershell
engine --profile 'C:\JoyPro\profiles\pilot_main.pr0file' nuclearoption export --dry-run --text engine --profile 'C:\JoyPro\profiles\pilot_main.pr0file' nuclearoption export --mode merge-overwrite --text
```

### `engine nuclearoption refresh-db [<source-path>] [--dry-run]   (builds the catalog from the installed game, or adds its new actions; writes it only when it changes)`
What it does: Updates this game's action catalog from the game or its files.

Arguments and example values:
- `<source-path>`: Path to read actions from (game install or control file); optional.
- `--dry-run`: Plan mode only: do not click, launch, or change files; report what would run.

Examples:
```powershell
engine nuclearoption refresh-db --text
```

### `engine tinycombatarena paths`
What it does: Lists the config files of this game found on this machine.

Arguments and example values:
- No command-specific arguments.

Examples:
```powershell
engine tinycombatarena paths --text
```

### `engine tinycombatarena catalog`
What it does: Lists the flight actions JoyPro manages for this game (from DB\<Game>\actions.tsv).

Arguments and example values:
- No command-specific arguments.

Examples:
```powershell
engine tinycombatarena catalog --text
```

### `engine tinycombatarena scan [<config-path>] [--path <config-path>]`
What it does: Reads the game's control file and shows the joystick binds it contains, without changing the profile.

Arguments and example values:
- `<config-path>`: Path to the game's control file. Optional: the engine uses the file it finds on this machine. Example: %LOCALAPPDATA%\Wardogs\Saved\Config\WindowsClient\GameUserSettings.ini.
- `--path`: Optional explicit path override. Use when you prefer option form over positional path.

Examples:
```powershell
engine tinycombatarena scan --text
```

### `engine tinycombatarena import [<config-path>] [--path <config-path>] [--replace] [--include-keyboard]`
What it does: Imports the game's joystick binds into the loaded profile (merged by default; --replace starts from an empty relation list).

Arguments and example values:
- `<config-path>`: Path to the game's control file. Optional: the engine uses the file it finds on this machine. Example: %LOCALAPPDATA%\Wardogs\Saved\Config\WindowsClient\GameUserSettings.ini.
- `--path`: Optional explicit path override. Use when you prefer option form over positional path.
- `--replace`: For game import: start from an empty relation list instead of merging into the loaded profile.
- `--include-keyboard`: Command argument. See examples for typical values.

Examples:
```powershell
engine --profile 'C:\JoyPro\profiles\pilot_main.pr0file' tinycombatarena import --text
```

### `engine tinycombatarena export [<config-path>] [--path <config-path>] [--mode clean|add|merge|merge-overwrite] [--dry-run] [--include-keyboard] [--nuke-untouched-devices]   (flight only: clean takes over the devices it writes, nuke clears unused devices)`
What it does: Writes the profile's binds for this game. Only the flight actions JoyPro manages are changed, the game must be closed, and the file is backed up to <file>.jpbak first.

Arguments and example values:
- `<config-path>`: Path to the game's control file. Optional: the engine uses the file it finds on this machine. Example: %LOCALAPPDATA%\Wardogs\Saved\Config\WindowsClient\GameUserSettings.ini.
- `--path`: Optional explicit path override. Use when you prefer option form over positional path.
- `--mode`: Export mode for DCS/IL2 export commands: clean, add, merge, merge-overwrite.
- `--dry-run`: Plan mode only: do not click, launch, or change files; report what would run.
- `--include-keyboard`: Command argument. See examples for typical values.
- `--nuke-untouched-devices`: DCS export only: delete untouched device .diff.lua/.jp files under Config\\Input (true/false).

Examples:
```powershell
engine --profile 'C:\JoyPro\profiles\pilot_main.pr0file' tinycombatarena export --dry-run --text engine --profile 'C:\JoyPro\profiles\pilot_main.pr0file' tinycombatarena export --mode merge-overwrite --text
```

### `engine tinycombatarena refresh-db [<source-path>] [--dry-run]   (builds the catalog from the installed game, or adds its new actions; writes it only when it changes)`
What it does: Updates this game's action catalog from the game or its files.

Arguments and example values:
- `<source-path>`: Path to read actions from (game install or control file); optional.
- `--dry-run`: Plan mode only: do not click, launch, or change files; report what would run.

Examples:
```powershell
engine tinycombatarena refresh-db --text
```

### `engine grebels paths`
What it does: Lists the config files of this game found on this machine.

Arguments and example values:
- No command-specific arguments.

Examples:
```powershell
engine grebels paths --text
```

### `engine grebels catalog`
What it does: Lists the flight actions JoyPro manages for this game (from DB\<Game>\actions.tsv).

Arguments and example values:
- No command-specific arguments.

Examples:
```powershell
engine grebels catalog --text
```

### `engine grebels scan [<config-path>] [--path <config-path>]`
What it does: Reads the game's control file and shows the joystick binds it contains, without changing the profile.

Arguments and example values:
- `<config-path>`: Path to the game's control file. Optional: the engine uses the file it finds on this machine. Example: %LOCALAPPDATA%\Wardogs\Saved\Config\WindowsClient\GameUserSettings.ini.
- `--path`: Optional explicit path override. Use when you prefer option form over positional path.

Examples:
```powershell
engine grebels scan --text
```

### `engine grebels import [<config-path>] [--path <config-path>] [--replace] [--include-keyboard]`
What it does: Imports the game's joystick binds into the loaded profile (merged by default; --replace starts from an empty relation list).

Arguments and example values:
- `<config-path>`: Path to the game's control file. Optional: the engine uses the file it finds on this machine. Example: %LOCALAPPDATA%\Wardogs\Saved\Config\WindowsClient\GameUserSettings.ini.
- `--path`: Optional explicit path override. Use when you prefer option form over positional path.
- `--replace`: For game import: start from an empty relation list instead of merging into the loaded profile.
- `--include-keyboard`: Command argument. See examples for typical values.

Examples:
```powershell
engine --profile 'C:\JoyPro\profiles\pilot_main.pr0file' grebels import --text
```

### `engine grebels export [<config-path>] [--path <config-path>] [--mode clean|add|merge|merge-overwrite] [--dry-run] [--include-keyboard] [--nuke-untouched-devices]   (flight only: clean takes over the devices it writes, nuke clears unused devices)`
What it does: Writes the profile's binds for this game. Only the flight actions JoyPro manages are changed, the game must be closed, and the file is backed up to <file>.jpbak first.

Arguments and example values:
- `<config-path>`: Path to the game's control file. Optional: the engine uses the file it finds on this machine. Example: %LOCALAPPDATA%\Wardogs\Saved\Config\WindowsClient\GameUserSettings.ini.
- `--path`: Optional explicit path override. Use when you prefer option form over positional path.
- `--mode`: Export mode for DCS/IL2 export commands: clean, add, merge, merge-overwrite.
- `--dry-run`: Plan mode only: do not click, launch, or change files; report what would run.
- `--include-keyboard`: Command argument. See examples for typical values.
- `--nuke-untouched-devices`: DCS export only: delete untouched device .diff.lua/.jp files under Config\\Input (true/false).

Examples:
```powershell
engine --profile 'C:\JoyPro\profiles\pilot_main.pr0file' grebels export --dry-run --text engine --profile 'C:\JoyPro\profiles\pilot_main.pr0file' grebels export --mode merge-overwrite --text
```

### `engine grebels refresh-db [<source-path>] [--dry-run]   (builds the catalog from the installed game, or adds its new actions; writes it only when it changes)`
What it does: Updates this game's action catalog from the game or its files.

Arguments and example values:
- `<source-path>`: Path to read actions from (game install or control file); optional.
- `--dry-run`: Plan mode only: do not click, launch, or change files; report what would run.

Examples:
```powershell
engine grebels refresh-db --text
```

### `engine hllv paths`
What it does: Lists the config files of this game found on this machine.

Arguments and example values:
- No command-specific arguments.

Examples:
```powershell
engine hllv paths --text
```

### `engine hllv catalog`
What it does: Lists the flight actions JoyPro manages for this game (from DB\<Game>\actions.tsv).

Arguments and example values:
- No command-specific arguments.

Examples:
```powershell
engine hllv catalog --text
```

### `engine hllv scan [<config-path>] [--path <config-path>]`
What it does: Reads the game's control file and shows the joystick binds it contains, without changing the profile.

Arguments and example values:
- `<config-path>`: Path to the game's control file. Optional: the engine uses the file it finds on this machine. Example: %LOCALAPPDATA%\Wardogs\Saved\Config\WindowsClient\GameUserSettings.ini.
- `--path`: Optional explicit path override. Use when you prefer option form over positional path.

Examples:
```powershell
engine hllv scan --text
```

### `engine hllv import [<config-path>] [--path <config-path>] [--replace] [--include-keyboard]`
What it does: Imports the game's joystick binds into the loaded profile (merged by default; --replace starts from an empty relation list).

Arguments and example values:
- `<config-path>`: Path to the game's control file. Optional: the engine uses the file it finds on this machine. Example: %LOCALAPPDATA%\Wardogs\Saved\Config\WindowsClient\GameUserSettings.ini.
- `--path`: Optional explicit path override. Use when you prefer option form over positional path.
- `--replace`: For game import: start from an empty relation list instead of merging into the loaded profile.
- `--include-keyboard`: Command argument. See examples for typical values.

Examples:
```powershell
engine --profile 'C:\JoyPro\profiles\pilot_main.pr0file' hllv import --text
```

### `engine hllv export [<config-path>] [--path <config-path>] [--mode clean|add|merge|merge-overwrite] [--dry-run] [--include-keyboard] [--nuke-untouched-devices]   (only aircraft/helicopter actions: clean and nuke release those on the written / unused devices)`
What it does: Writes the profile's binds for this game. Only the flight actions JoyPro manages are changed, the game must be closed, and the file is backed up to <file>.jpbak first.

Arguments and example values:
- `<config-path>`: Path to the game's control file. Optional: the engine uses the file it finds on this machine. Example: %LOCALAPPDATA%\Wardogs\Saved\Config\WindowsClient\GameUserSettings.ini.
- `--path`: Optional explicit path override. Use when you prefer option form over positional path.
- `--mode`: Export mode for DCS/IL2 export commands: clean, add, merge, merge-overwrite.
- `--dry-run`: Plan mode only: do not click, launch, or change files; report what would run.
- `--include-keyboard`: Command argument. See examples for typical values.
- `--nuke-untouched-devices`: DCS export only: delete untouched device .diff.lua/.jp files under Config\\Input (true/false).

Examples:
```powershell
engine --profile 'C:\JoyPro\profiles\pilot_main.pr0file' hllv export --dry-run --text engine --profile 'C:\JoyPro\profiles\pilot_main.pr0file' hllv export --mode merge-overwrite --text
```

### `engine hllv refresh-db [<source-path>] [--dry-run] [--oodle-dll <path>]   (builds the catalog from the installed game, or adds its new actions; writes it only when it changes)`
What it does: Updates this game's action catalog from the game or its files.

Arguments and example values:
- `<source-path>`: Path to read actions from (game install or control file); optional.
- `--dry-run`: Plan mode only: do not click, launch, or change files; report what would run.
- `--oodle-dll`: Command argument. See examples for typical values.
- `<path>`: Filesystem path value. Example: C:\\JoyPro\\logs\\preview.png.

Examples:
```powershell
engine hllv refresh-db --text
```

### `engine hllv oodle [--path <game folder>]   (the game's own Oodle library, or one another installed game ships; refresh-db --oodle-dll <path> uses that one only with the user's consent, from where it is, never copied; without it JoyPro's own list of the pad keys)`
What it does: Runs this command operation.

Arguments and example values:
- `--path`: Optional explicit path override. Use when you prefer option form over positional path.
- `<game folder>`: Command argument. See examples for typical values.
- `--oodle-dll`: Command argument. See examples for typical values.
- `<path>`: Filesystem path value. Example: C:\\JoyPro\\logs\\preview.png.

Examples:
```powershell
engine --help
```

### `engine bf6 paths`
What it does: Lists the config files of this game found on this machine.

Arguments and example values:
- No command-specific arguments.

Examples:
```powershell
engine bf6 paths --text
```

### `engine bf6 catalog`
What it does: Lists the flight actions JoyPro manages for this game (from DB\<Game>\actions.tsv).

Arguments and example values:
- No command-specific arguments.

Examples:
```powershell
engine bf6 catalog --text
```

### `engine bf6 scan [<config-path>] [--path <config-path>]`
What it does: Reads the game's control file and shows the joystick binds it contains, without changing the profile.

Arguments and example values:
- `<config-path>`: Path to the game's control file. Optional: the engine uses the file it finds on this machine. Example: %LOCALAPPDATA%\Wardogs\Saved\Config\WindowsClient\GameUserSettings.ini.
- `--path`: Optional explicit path override. Use when you prefer option form over positional path.

Examples:
```powershell
engine bf6 scan --text
```

### `engine bf6 import [<config-path>] [--path <config-path>] [--replace] [--include-keyboard]`
What it does: Imports the game's joystick binds into the loaded profile (merged by default; --replace starts from an empty relation list).

Arguments and example values:
- `<config-path>`: Path to the game's control file. Optional: the engine uses the file it finds on this machine. Example: %LOCALAPPDATA%\Wardogs\Saved\Config\WindowsClient\GameUserSettings.ini.
- `--path`: Optional explicit path override. Use when you prefer option form over positional path.
- `--replace`: For game import: start from an empty relation list instead of merging into the loaded profile.
- `--include-keyboard`: Command argument. See examples for typical values.

Examples:
```powershell
engine --profile 'C:\JoyPro\profiles\pilot_main.pr0file' bf6 import --text
```

### `engine bf6 export [<config-path>] [--path <config-path>] [--mode clean|add|merge|merge-overwrite] [--dry-run] [--include-keyboard] [--nuke-untouched-devices]   (only aircraft/helicopter actions: clean and nuke release those on the written / unused devices)`
What it does: Writes the profile's binds for this game. Only the flight actions JoyPro manages are changed, the game must be closed, and the file is backed up to <file>.jpbak first.

Arguments and example values:
- `<config-path>`: Path to the game's control file. Optional: the engine uses the file it finds on this machine. Example: %LOCALAPPDATA%\Wardogs\Saved\Config\WindowsClient\GameUserSettings.ini.
- `--path`: Optional explicit path override. Use when you prefer option form over positional path.
- `--mode`: Export mode for DCS/IL2 export commands: clean, add, merge, merge-overwrite.
- `--dry-run`: Plan mode only: do not click, launch, or change files; report what would run.
- `--include-keyboard`: Command argument. See examples for typical values.
- `--nuke-untouched-devices`: DCS export only: delete untouched device .diff.lua/.jp files under Config\\Input (true/false).

Examples:
```powershell
engine --profile 'C:\JoyPro\profiles\pilot_main.pr0file' bf6 export --dry-run --text engine --profile 'C:\JoyPro\profiles\pilot_main.pr0file' bf6 export --mode merge-overwrite --text
```

### `engine bf6 refresh-db [<source-path>] [--dry-run]   (builds the catalog from the installed game, or adds its new actions; writes it only when it changes)`
What it does: Updates this game's action catalog from the game or its files.

Arguments and example values:
- `<source-path>`: Path to read actions from (game install or control file); optional.
- `--dry-run`: Plan mode only: do not click, launch, or change files; report what would run.

Examples:
```powershell
engine bf6 refresh-db --text
```

### `engine bf2042 paths`
What it does: Lists the config files of this game found on this machine.

Arguments and example values:
- No command-specific arguments.

Examples:
```powershell
engine bf2042 paths --text
```

### `engine bf2042 catalog`
What it does: Lists the flight actions JoyPro manages for this game (from DB\<Game>\actions.tsv).

Arguments and example values:
- No command-specific arguments.

Examples:
```powershell
engine bf2042 catalog --text
```

### `engine bf2042 scan [<config-path>] [--path <config-path>]`
What it does: Reads the game's control file and shows the joystick binds it contains, without changing the profile.

Arguments and example values:
- `<config-path>`: Path to the game's control file. Optional: the engine uses the file it finds on this machine. Example: %LOCALAPPDATA%\Wardogs\Saved\Config\WindowsClient\GameUserSettings.ini.
- `--path`: Optional explicit path override. Use when you prefer option form over positional path.

Examples:
```powershell
engine bf2042 scan --text
```

### `engine bf2042 import [<config-path>] [--path <config-path>] [--replace] [--include-keyboard]`
What it does: Imports the game's joystick binds into the loaded profile (merged by default; --replace starts from an empty relation list).

Arguments and example values:
- `<config-path>`: Path to the game's control file. Optional: the engine uses the file it finds on this machine. Example: %LOCALAPPDATA%\Wardogs\Saved\Config\WindowsClient\GameUserSettings.ini.
- `--path`: Optional explicit path override. Use when you prefer option form over positional path.
- `--replace`: For game import: start from an empty relation list instead of merging into the loaded profile.
- `--include-keyboard`: Command argument. See examples for typical values.

Examples:
```powershell
engine --profile 'C:\JoyPro\profiles\pilot_main.pr0file' bf2042 import --text
```

### `engine bf2042 export [<config-path>] [--path <config-path>] [--mode clean|add|merge|merge-overwrite] [--dry-run] [--include-keyboard] [--nuke-untouched-devices]   (only aircraft/helicopter actions: clean and nuke release those on the written / unused devices)`
What it does: Writes the profile's binds for this game. Only the flight actions JoyPro manages are changed, the game must be closed, and the file is backed up to <file>.jpbak first.

Arguments and example values:
- `<config-path>`: Path to the game's control file. Optional: the engine uses the file it finds on this machine. Example: %LOCALAPPDATA%\Wardogs\Saved\Config\WindowsClient\GameUserSettings.ini.
- `--path`: Optional explicit path override. Use when you prefer option form over positional path.
- `--mode`: Export mode for DCS/IL2 export commands: clean, add, merge, merge-overwrite.
- `--dry-run`: Plan mode only: do not click, launch, or change files; report what would run.
- `--include-keyboard`: Command argument. See examples for typical values.
- `--nuke-untouched-devices`: DCS export only: delete untouched device .diff.lua/.jp files under Config\\Input (true/false).

Examples:
```powershell
engine --profile 'C:\JoyPro\profiles\pilot_main.pr0file' bf2042 export --dry-run --text engine --profile 'C:\JoyPro\profiles\pilot_main.pr0file' bf2042 export --mode merge-overwrite --text
```

### `engine bf2042 refresh-db [<source-path>] [--dry-run]   (builds the catalog from the installed game, or adds its new actions; writes it only when it changes)`
What it does: Updates this game's action catalog from the game or its files.

Arguments and example values:
- `<source-path>`: Path to read actions from (game install or control file); optional.
- `--dry-run`: Plan mode only: do not click, launch, or change files; report what would run.

Examples:
```powershell
engine bf2042 refresh-db --text
```

### `engine ac8 paths`
What it does: Lists the config files of this game found on this machine.

Arguments and example values:
- No command-specific arguments.

Examples:
```powershell
engine ac8 paths --text
```

### `engine ac8 catalog`
What it does: Lists the flight actions JoyPro manages for this game (from DB\<Game>\actions.tsv).

Arguments and example values:
- No command-specific arguments.

Examples:
```powershell
engine ac8 catalog --text
```

### `engine ac8 scan [<config-path>] [--path <config-path>]`
What it does: Reads the game's control file and shows the joystick binds it contains, without changing the profile.

Arguments and example values:
- `<config-path>`: Path to the game's control file. Optional: the engine uses the file it finds on this machine. Example: %LOCALAPPDATA%\Wardogs\Saved\Config\WindowsClient\GameUserSettings.ini.
- `--path`: Optional explicit path override. Use when you prefer option form over positional path.

Examples:
```powershell
engine ac8 scan --text
```

### `engine ac8 import [<config-path>] [--path <config-path>] [--replace] [--include-keyboard]`
What it does: Imports the game's joystick binds into the loaded profile (merged by default; --replace starts from an empty relation list).

Arguments and example values:
- `<config-path>`: Path to the game's control file. Optional: the engine uses the file it finds on this machine. Example: %LOCALAPPDATA%\Wardogs\Saved\Config\WindowsClient\GameUserSettings.ini.
- `--path`: Optional explicit path override. Use when you prefer option form over positional path.
- `--replace`: For game import: start from an empty relation list instead of merging into the loaded profile.
- `--include-keyboard`: Command argument. See examples for typical values.

Examples:
```powershell
engine --profile 'C:\JoyPro\profiles\pilot_main.pr0file' ac8 import --text
```

### `engine ac8 export [<config-path>] [--path <config-path>] [--mode clean|add|merge|merge-overwrite] [--dry-run] [--include-keyboard] [--nuke-untouched-devices]   (only aircraft/helicopter actions: clean and nuke release those on the written / unused devices)`
What it does: Writes the profile's binds for this game. Only the flight actions JoyPro manages are changed, the game must be closed, and the file is backed up to <file>.jpbak first.

Arguments and example values:
- `<config-path>`: Path to the game's control file. Optional: the engine uses the file it finds on this machine. Example: %LOCALAPPDATA%\Wardogs\Saved\Config\WindowsClient\GameUserSettings.ini.
- `--path`: Optional explicit path override. Use when you prefer option form over positional path.
- `--mode`: Export mode for DCS/IL2 export commands: clean, add, merge, merge-overwrite.
- `--dry-run`: Plan mode only: do not click, launch, or change files; report what would run.
- `--include-keyboard`: Command argument. See examples for typical values.
- `--nuke-untouched-devices`: DCS export only: delete untouched device .diff.lua/.jp files under Config\\Input (true/false).

Examples:
```powershell
engine --profile 'C:\JoyPro\profiles\pilot_main.pr0file' ac8 export --dry-run --text engine --profile 'C:\JoyPro\profiles\pilot_main.pr0file' ac8 export --mode merge-overwrite --text
```

### `engine ac8 refresh-db [<source-path>] [--dry-run]   (builds the catalog from the installed game, or adds its new actions; writes it only when it changes)`
What it does: Updates this game's action catalog from the game or its files.

Arguments and example values:
- `<source-path>`: Path to read actions from (game install or control file); optional.
- `--dry-run`: Plan mode only: do not click, launch, or change files; report what would run.

Examples:
```powershell
engine ac8 refresh-db --text
```

### `engine ac7 paths`
What it does: Lists the config files of this game found on this machine.

Arguments and example values:
- No command-specific arguments.

Examples:
```powershell
engine ac7 paths --text
```

### `engine ac7 catalog`
What it does: Lists the flight actions JoyPro manages for this game (from DB\<Game>\actions.tsv).

Arguments and example values:
- No command-specific arguments.

Examples:
```powershell
engine ac7 catalog --text
```

### `engine ac7 scan [<config-path>] [--path <config-path>]`
What it does: Reads the game's control file and shows the joystick binds it contains, without changing the profile.

Arguments and example values:
- `<config-path>`: Path to the game's control file. Optional: the engine uses the file it finds on this machine. Example: %LOCALAPPDATA%\Wardogs\Saved\Config\WindowsClient\GameUserSettings.ini.
- `--path`: Optional explicit path override. Use when you prefer option form over positional path.

Examples:
```powershell
engine ac7 scan --text
```

### `engine ac7 import [<config-path>] [--path <config-path>] [--replace] [--include-keyboard]`
What it does: Imports the game's joystick binds into the loaded profile (merged by default; --replace starts from an empty relation list).

Arguments and example values:
- `<config-path>`: Path to the game's control file. Optional: the engine uses the file it finds on this machine. Example: %LOCALAPPDATA%\Wardogs\Saved\Config\WindowsClient\GameUserSettings.ini.
- `--path`: Optional explicit path override. Use when you prefer option form over positional path.
- `--replace`: For game import: start from an empty relation list instead of merging into the loaded profile.
- `--include-keyboard`: Command argument. See examples for typical values.

Examples:
```powershell
engine --profile 'C:\JoyPro\profiles\pilot_main.pr0file' ac7 import --text
```

### `engine ac7 export [<config-path>] [--path <config-path>] [--mode clean|add|merge|merge-overwrite] [--dry-run] [--include-keyboard] [--nuke-untouched-devices]   (flight only: clean takes over the devices it writes, nuke clears unused devices)`
What it does: Writes the profile's binds for this game. Only the flight actions JoyPro manages are changed, the game must be closed, and the file is backed up to <file>.jpbak first.

Arguments and example values:
- `<config-path>`: Path to the game's control file. Optional: the engine uses the file it finds on this machine. Example: %LOCALAPPDATA%\Wardogs\Saved\Config\WindowsClient\GameUserSettings.ini.
- `--path`: Optional explicit path override. Use when you prefer option form over positional path.
- `--mode`: Export mode for DCS/IL2 export commands: clean, add, merge, merge-overwrite.
- `--dry-run`: Plan mode only: do not click, launch, or change files; report what would run.
- `--include-keyboard`: Command argument. See examples for typical values.
- `--nuke-untouched-devices`: DCS export only: delete untouched device .diff.lua/.jp files under Config\\Input (true/false).

Examples:
```powershell
engine --profile 'C:\JoyPro\profiles\pilot_main.pr0file' ac7 export --dry-run --text engine --profile 'C:\JoyPro\profiles\pilot_main.pr0file' ac7 export --mode merge-overwrite --text
```

### `engine ac7 refresh-db [<source-path>] [--dry-run]   (builds the catalog from the installed game, or adds its new actions; writes it only when it changes)`
What it does: Updates this game's action catalog from the game or its files.

Arguments and example values:
- `<source-path>`: Path to read actions from (game install or control file); optional.
- `--dry-run`: Plan mode only: do not click, launch, or change files; report what would run.

Examples:
```powershell
engine ac7 refresh-db --text
```

### `engine pw paths`
What it does: Lists the config files of this game found on this machine.

Arguments and example values:
- No command-specific arguments.

Examples:
```powershell
engine pw paths --text
```

### `engine pw catalog`
What it does: Lists the flight actions JoyPro manages for this game (from DB\<Game>\actions.tsv).

Arguments and example values:
- No command-specific arguments.

Examples:
```powershell
engine pw catalog --text
```

### `engine pw scan [<config-path>] [--path <config-path>]`
What it does: Reads the game's control file and shows the joystick binds it contains, without changing the profile.

Arguments and example values:
- `<config-path>`: Path to the game's control file. Optional: the engine uses the file it finds on this machine. Example: %LOCALAPPDATA%\Wardogs\Saved\Config\WindowsClient\GameUserSettings.ini.
- `--path`: Optional explicit path override. Use when you prefer option form over positional path.

Examples:
```powershell
engine pw scan --text
```

### `engine pw import [<config-path>] [--path <config-path>] [--replace] [--include-keyboard]`
What it does: Imports the game's joystick binds into the loaded profile (merged by default; --replace starts from an empty relation list).

Arguments and example values:
- `<config-path>`: Path to the game's control file. Optional: the engine uses the file it finds on this machine. Example: %LOCALAPPDATA%\Wardogs\Saved\Config\WindowsClient\GameUserSettings.ini.
- `--path`: Optional explicit path override. Use when you prefer option form over positional path.
- `--replace`: For game import: start from an empty relation list instead of merging into the loaded profile.
- `--include-keyboard`: Command argument. See examples for typical values.

Examples:
```powershell
engine --profile 'C:\JoyPro\profiles\pilot_main.pr0file' pw import --text
```

### `engine pw export [<config-path>] [--path <config-path>] [--mode clean|add|merge|merge-overwrite] [--dry-run] [--include-keyboard] [--nuke-untouched-devices]   (flight only: clean takes over the devices it writes, nuke clears unused devices)`
What it does: Writes the profile's binds for this game. Only the flight actions JoyPro manages are changed, the game must be closed, and the file is backed up to <file>.jpbak first.

Arguments and example values:
- `<config-path>`: Path to the game's control file. Optional: the engine uses the file it finds on this machine. Example: %LOCALAPPDATA%\Wardogs\Saved\Config\WindowsClient\GameUserSettings.ini.
- `--path`: Optional explicit path override. Use when you prefer option form over positional path.
- `--mode`: Export mode for DCS/IL2 export commands: clean, add, merge, merge-overwrite.
- `--dry-run`: Plan mode only: do not click, launch, or change files; report what would run.
- `--include-keyboard`: Command argument. See examples for typical values.
- `--nuke-untouched-devices`: DCS export only: delete untouched device .diff.lua/.jp files under Config\\Input (true/false).

Examples:
```powershell
engine --profile 'C:\JoyPro\profiles\pilot_main.pr0file' pw export --dry-run --text engine --profile 'C:\JoyPro\profiles\pilot_main.pr0file' pw export --mode merge-overwrite --text
```

### `engine pw refresh-db [<source-path>] [--dry-run]   (builds the catalog from the installed game, or adds its new actions; writes it only when it changes)`
What it does: Updates this game's action catalog from the game or its files.

Arguments and example values:
- `<source-path>`: Path to read actions from (game install or control file); optional.
- `--dry-run`: Plan mode only: do not click, launch, or change files; report what would run.

Examples:
```powershell
engine pw refresh-db --text
```

### `engine il21946 paths`
What it does: Lists the config files of this game found on this machine.

Arguments and example values:
- No command-specific arguments.

Examples:
```powershell
engine il21946 paths --text
```

### `engine il21946 catalog`
What it does: Lists the flight actions JoyPro manages for this game (from DB\<Game>\actions.tsv).

Arguments and example values:
- No command-specific arguments.

Examples:
```powershell
engine il21946 catalog --text
```

### `engine il21946 scan [<config-path>] [--path <config-path>]`
What it does: Reads the game's control file and shows the joystick binds it contains, without changing the profile.

Arguments and example values:
- `<config-path>`: Path to the game's control file. Optional: the engine uses the file it finds on this machine. Example: %LOCALAPPDATA%\Wardogs\Saved\Config\WindowsClient\GameUserSettings.ini.
- `--path`: Optional explicit path override. Use when you prefer option form over positional path.

Examples:
```powershell
engine il21946 scan --text
```

### `engine il21946 import [<config-path>] [--path <config-path>] [--replace] [--include-keyboard]`
What it does: Imports the game's joystick binds into the loaded profile (merged by default; --replace starts from an empty relation list).

Arguments and example values:
- `<config-path>`: Path to the game's control file. Optional: the engine uses the file it finds on this machine. Example: %LOCALAPPDATA%\Wardogs\Saved\Config\WindowsClient\GameUserSettings.ini.
- `--path`: Optional explicit path override. Use when you prefer option form over positional path.
- `--replace`: For game import: start from an empty relation list instead of merging into the loaded profile.
- `--include-keyboard`: Command argument. See examples for typical values.

Examples:
```powershell
engine --profile 'C:\JoyPro\profiles\pilot_main.pr0file' il21946 import --text
```

### `engine il21946 export [<config-path>] [--path <config-path>] [--mode clean|add|merge|merge-overwrite] [--dry-run] [--include-keyboard] [--nuke-untouched-devices]   (flight only: clean takes over the devices it writes, nuke clears unused devices)`
What it does: Writes the profile's binds for this game. Only the flight actions JoyPro manages are changed, the game must be closed, and the file is backed up to <file>.jpbak first.

Arguments and example values:
- `<config-path>`: Path to the game's control file. Optional: the engine uses the file it finds on this machine. Example: %LOCALAPPDATA%\Wardogs\Saved\Config\WindowsClient\GameUserSettings.ini.
- `--path`: Optional explicit path override. Use when you prefer option form over positional path.
- `--mode`: Export mode for DCS/IL2 export commands: clean, add, merge, merge-overwrite.
- `--dry-run`: Plan mode only: do not click, launch, or change files; report what would run.
- `--include-keyboard`: Command argument. See examples for typical values.
- `--nuke-untouched-devices`: DCS export only: delete untouched device .diff.lua/.jp files under Config\\Input (true/false).

Examples:
```powershell
engine --profile 'C:\JoyPro\profiles\pilot_main.pr0file' il21946 export --dry-run --text engine --profile 'C:\JoyPro\profiles\pilot_main.pr0file' il21946 export --mode merge-overwrite --text
```

### `engine il21946 refresh-db [<source-path>] [--dry-run]   (builds the catalog from the installed game, or adds its new actions; writes it only when it changes)`
What it does: Updates this game's action catalog from the game or its files.

Arguments and example values:
- `<source-path>`: Path to read actions from (game install or control file); optional.
- `--dry-run`: Plan mode only: do not click, launch, or change files; report what would run.

Examples:
```powershell
engine il21946 refresh-db --text
```

### `engine il2codblitz paths`
What it does: Lists the config files of this game found on this machine.

Arguments and example values:
- No command-specific arguments.

Examples:
```powershell
engine il2codblitz paths --text
```

### `engine il2codblitz catalog`
What it does: Lists the flight actions JoyPro manages for this game (from DB\<Game>\actions.tsv).

Arguments and example values:
- No command-specific arguments.

Examples:
```powershell
engine il2codblitz catalog --text
```

### `engine il2codblitz scan [<config-path>] [--path <config-path>]`
What it does: Reads the game's control file and shows the joystick binds it contains, without changing the profile.

Arguments and example values:
- `<config-path>`: Path to the game's control file. Optional: the engine uses the file it finds on this machine. Example: %LOCALAPPDATA%\Wardogs\Saved\Config\WindowsClient\GameUserSettings.ini.
- `--path`: Optional explicit path override. Use when you prefer option form over positional path.

Examples:
```powershell
engine il2codblitz scan --text
```

### `engine il2codblitz import [<config-path>] [--path <config-path>] [--replace] [--include-keyboard]`
What it does: Imports the game's joystick binds into the loaded profile (merged by default; --replace starts from an empty relation list).

Arguments and example values:
- `<config-path>`: Path to the game's control file. Optional: the engine uses the file it finds on this machine. Example: %LOCALAPPDATA%\Wardogs\Saved\Config\WindowsClient\GameUserSettings.ini.
- `--path`: Optional explicit path override. Use when you prefer option form over positional path.
- `--replace`: For game import: start from an empty relation list instead of merging into the loaded profile.
- `--include-keyboard`: Command argument. See examples for typical values.

Examples:
```powershell
engine --profile 'C:\JoyPro\profiles\pilot_main.pr0file' il2codblitz import --text
```

### `engine il2codblitz export [<config-path>] [--path <config-path>] [--mode clean|add|merge|merge-overwrite] [--dry-run] [--include-keyboard] [--nuke-untouched-devices]   (flight only: clean takes over the devices it writes, nuke clears unused devices)`
What it does: Writes the profile's binds for this game. Only the flight actions JoyPro manages are changed, the game must be closed, and the file is backed up to <file>.jpbak first.

Arguments and example values:
- `<config-path>`: Path to the game's control file. Optional: the engine uses the file it finds on this machine. Example: %LOCALAPPDATA%\Wardogs\Saved\Config\WindowsClient\GameUserSettings.ini.
- `--path`: Optional explicit path override. Use when you prefer option form over positional path.
- `--mode`: Export mode for DCS/IL2 export commands: clean, add, merge, merge-overwrite.
- `--dry-run`: Plan mode only: do not click, launch, or change files; report what would run.
- `--include-keyboard`: Command argument. See examples for typical values.
- `--nuke-untouched-devices`: DCS export only: delete untouched device .diff.lua/.jp files under Config\\Input (true/false).

Examples:
```powershell
engine --profile 'C:\JoyPro\profiles\pilot_main.pr0file' il2codblitz export --dry-run --text engine --profile 'C:\JoyPro\profiles\pilot_main.pr0file' il2codblitz export --mode merge-overwrite --text
```

### `engine il2codblitz refresh-db [<source-path>] [--dry-run]   (builds the catalog from the installed game, or adds its new actions; writes it only when it changes)`
What it does: Updates this game's action catalog from the game or its files.

Arguments and example values:
- `<source-path>`: Path to read actions from (game install or control file); optional.
- `--dry-run`: Plan mode only: do not click, launch, or change files; report what would run.

Examples:
```powershell
engine il2codblitz refresh-db --text
```

### `engine il2cod paths`
What it does: Lists the config files of this game found on this machine.

Arguments and example values:
- No command-specific arguments.

Examples:
```powershell
engine il2cod paths --text
```

### `engine il2cod catalog`
What it does: Lists the flight actions JoyPro manages for this game (from DB\<Game>\actions.tsv).

Arguments and example values:
- No command-specific arguments.

Examples:
```powershell
engine il2cod catalog --text
```

### `engine il2cod scan [<config-path>] [--path <config-path>]`
What it does: Reads the game's control file and shows the joystick binds it contains, without changing the profile.

Arguments and example values:
- `<config-path>`: Path to the game's control file. Optional: the engine uses the file it finds on this machine. Example: %LOCALAPPDATA%\Wardogs\Saved\Config\WindowsClient\GameUserSettings.ini.
- `--path`: Optional explicit path override. Use when you prefer option form over positional path.

Examples:
```powershell
engine il2cod scan --text
```

### `engine il2cod import [<config-path>] [--path <config-path>] [--replace] [--include-keyboard]`
What it does: Imports the game's joystick binds into the loaded profile (merged by default; --replace starts from an empty relation list).

Arguments and example values:
- `<config-path>`: Path to the game's control file. Optional: the engine uses the file it finds on this machine. Example: %LOCALAPPDATA%\Wardogs\Saved\Config\WindowsClient\GameUserSettings.ini.
- `--path`: Optional explicit path override. Use when you prefer option form over positional path.
- `--replace`: For game import: start from an empty relation list instead of merging into the loaded profile.
- `--include-keyboard`: Command argument. See examples for typical values.

Examples:
```powershell
engine --profile 'C:\JoyPro\profiles\pilot_main.pr0file' il2cod import --text
```

### `engine il2cod export [<config-path>] [--path <config-path>] [--mode clean|add|merge|merge-overwrite] [--dry-run] [--include-keyboard] [--nuke-untouched-devices]   (flight only: clean takes over the devices it writes, nuke clears unused devices)`
What it does: Writes the profile's binds for this game. Only the flight actions JoyPro manages are changed, the game must be closed, and the file is backed up to <file>.jpbak first.

Arguments and example values:
- `<config-path>`: Path to the game's control file. Optional: the engine uses the file it finds on this machine. Example: %LOCALAPPDATA%\Wardogs\Saved\Config\WindowsClient\GameUserSettings.ini.
- `--path`: Optional explicit path override. Use when you prefer option form over positional path.
- `--mode`: Export mode for DCS/IL2 export commands: clean, add, merge, merge-overwrite.
- `--dry-run`: Plan mode only: do not click, launch, or change files; report what would run.
- `--include-keyboard`: Command argument. See examples for typical values.
- `--nuke-untouched-devices`: DCS export only: delete untouched device .diff.lua/.jp files under Config\\Input (true/false).

Examples:
```powershell
engine --profile 'C:\JoyPro\profiles\pilot_main.pr0file' il2cod export --dry-run --text engine --profile 'C:\JoyPro\profiles\pilot_main.pr0file' il2cod export --mode merge-overwrite --text
```

### `engine il2cod refresh-db [<source-path>] [--dry-run]   (builds the catalog from the installed game, or adds its new actions; writes it only when it changes)`
What it does: Updates this game's action catalog from the game or its files.

Arguments and example values:
- `<source-path>`: Path to read actions from (game install or control file); optional.
- `--dry-run`: Plan mode only: do not click, launch, or change files; report what would run.

Examples:
```powershell
engine il2cod refresh-db --text
```

### `engine fsx paths`
What it does: Lists the config files of this game found on this machine.

Arguments and example values:
- No command-specific arguments.

Examples:
```powershell
engine fsx paths --text
```

### `engine fsx catalog`
What it does: Lists the flight actions JoyPro manages for this game (from DB\<Game>\actions.tsv).

Arguments and example values:
- No command-specific arguments.

Examples:
```powershell
engine fsx catalog --text
```

### `engine fsx scan [<config-path>] [--path <config-path>]`
What it does: Reads the game's control file and shows the joystick binds it contains, without changing the profile.

Arguments and example values:
- `<config-path>`: Path to the game's control file. Optional: the engine uses the file it finds on this machine. Example: %LOCALAPPDATA%\Wardogs\Saved\Config\WindowsClient\GameUserSettings.ini.
- `--path`: Optional explicit path override. Use when you prefer option form over positional path.

Examples:
```powershell
engine fsx scan --text
```

### `engine fsx import [<config-path>] [--path <config-path>] [--replace] [--include-keyboard]`
What it does: Imports the game's joystick binds into the loaded profile (merged by default; --replace starts from an empty relation list).

Arguments and example values:
- `<config-path>`: Path to the game's control file. Optional: the engine uses the file it finds on this machine. Example: %LOCALAPPDATA%\Wardogs\Saved\Config\WindowsClient\GameUserSettings.ini.
- `--path`: Optional explicit path override. Use when you prefer option form over positional path.
- `--replace`: For game import: start from an empty relation list instead of merging into the loaded profile.
- `--include-keyboard`: Command argument. See examples for typical values.

Examples:
```powershell
engine --profile 'C:\JoyPro\profiles\pilot_main.pr0file' fsx import --text
```

### `engine fsx export [<config-path>] [--path <config-path>] [--mode clean|add|merge|merge-overwrite] [--dry-run] [--include-keyboard] [--nuke-untouched-devices]   (flight only: clean takes over the devices it writes, nuke clears unused devices)`
What it does: Writes the profile's binds for this game. Only the flight actions JoyPro manages are changed, the game must be closed, and the file is backed up to <file>.jpbak first.

Arguments and example values:
- `<config-path>`: Path to the game's control file. Optional: the engine uses the file it finds on this machine. Example: %LOCALAPPDATA%\Wardogs\Saved\Config\WindowsClient\GameUserSettings.ini.
- `--path`: Optional explicit path override. Use when you prefer option form over positional path.
- `--mode`: Export mode for DCS/IL2 export commands: clean, add, merge, merge-overwrite.
- `--dry-run`: Plan mode only: do not click, launch, or change files; report what would run.
- `--include-keyboard`: Command argument. See examples for typical values.
- `--nuke-untouched-devices`: DCS export only: delete untouched device .diff.lua/.jp files under Config\\Input (true/false).

Examples:
```powershell
engine --profile 'C:\JoyPro\profiles\pilot_main.pr0file' fsx export --dry-run --text engine --profile 'C:\JoyPro\profiles\pilot_main.pr0file' fsx export --mode merge-overwrite --text
```

### `engine fsx refresh-db [<source-path>] [--dry-run]   (builds the catalog from the installed game, or adds its new actions; writes it only when it changes)`
What it does: Updates this game's action catalog from the game or its files.

Arguments and example values:
- `<source-path>`: Path to read actions from (game install or control file); optional.
- `--dry-run`: Plan mode only: do not click, launch, or change files; report what would run.

Examples:
```powershell
engine fsx refresh-db --text
```

### `engine fsxse paths`
What it does: Lists the config files of this game found on this machine.

Arguments and example values:
- No command-specific arguments.

Examples:
```powershell
engine fsxse paths --text
```

### `engine fsxse catalog`
What it does: Lists the flight actions JoyPro manages for this game (from DB\<Game>\actions.tsv).

Arguments and example values:
- No command-specific arguments.

Examples:
```powershell
engine fsxse catalog --text
```

### `engine fsxse scan [<config-path>] [--path <config-path>]`
What it does: Reads the game's control file and shows the joystick binds it contains, without changing the profile.

Arguments and example values:
- `<config-path>`: Path to the game's control file. Optional: the engine uses the file it finds on this machine. Example: %LOCALAPPDATA%\Wardogs\Saved\Config\WindowsClient\GameUserSettings.ini.
- `--path`: Optional explicit path override. Use when you prefer option form over positional path.

Examples:
```powershell
engine fsxse scan --text
```

### `engine fsxse import [<config-path>] [--path <config-path>] [--replace] [--include-keyboard]`
What it does: Imports the game's joystick binds into the loaded profile (merged by default; --replace starts from an empty relation list).

Arguments and example values:
- `<config-path>`: Path to the game's control file. Optional: the engine uses the file it finds on this machine. Example: %LOCALAPPDATA%\Wardogs\Saved\Config\WindowsClient\GameUserSettings.ini.
- `--path`: Optional explicit path override. Use when you prefer option form over positional path.
- `--replace`: For game import: start from an empty relation list instead of merging into the loaded profile.
- `--include-keyboard`: Command argument. See examples for typical values.

Examples:
```powershell
engine --profile 'C:\JoyPro\profiles\pilot_main.pr0file' fsxse import --text
```

### `engine fsxse export [<config-path>] [--path <config-path>] [--mode clean|add|merge|merge-overwrite] [--dry-run] [--include-keyboard] [--nuke-untouched-devices]   (flight only: clean takes over the devices it writes, nuke clears unused devices)`
What it does: Writes the profile's binds for this game. Only the flight actions JoyPro manages are changed, the game must be closed, and the file is backed up to <file>.jpbak first.

Arguments and example values:
- `<config-path>`: Path to the game's control file. Optional: the engine uses the file it finds on this machine. Example: %LOCALAPPDATA%\Wardogs\Saved\Config\WindowsClient\GameUserSettings.ini.
- `--path`: Optional explicit path override. Use when you prefer option form over positional path.
- `--mode`: Export mode for DCS/IL2 export commands: clean, add, merge, merge-overwrite.
- `--dry-run`: Plan mode only: do not click, launch, or change files; report what would run.
- `--include-keyboard`: Command argument. See examples for typical values.
- `--nuke-untouched-devices`: DCS export only: delete untouched device .diff.lua/.jp files under Config\\Input (true/false).

Examples:
```powershell
engine --profile 'C:\JoyPro\profiles\pilot_main.pr0file' fsxse export --dry-run --text engine --profile 'C:\JoyPro\profiles\pilot_main.pr0file' fsxse export --mode merge-overwrite --text
```

### `engine fsxse refresh-db [<source-path>] [--dry-run]   (builds the catalog from the installed game, or adds its new actions; writes it only when it changes)`
What it does: Updates this game's action catalog from the game or its files.

Arguments and example values:
- `<source-path>`: Path to read actions from (game install or control file); optional.
- `--dry-run`: Plan mode only: do not click, launch, or change files; report what would run.

Examples:
```powershell
engine fsxse refresh-db --text
```

### `engine p3d4 paths`
What it does: Lists the config files of this game found on this machine.

Arguments and example values:
- No command-specific arguments.

Examples:
```powershell
engine p3d4 paths --text
```

### `engine p3d4 catalog`
What it does: Lists the flight actions JoyPro manages for this game (from DB\<Game>\actions.tsv).

Arguments and example values:
- No command-specific arguments.

Examples:
```powershell
engine p3d4 catalog --text
```

### `engine p3d4 scan [<config-path>] [--path <config-path>]`
What it does: Reads the game's control file and shows the joystick binds it contains, without changing the profile.

Arguments and example values:
- `<config-path>`: Path to the game's control file. Optional: the engine uses the file it finds on this machine. Example: %LOCALAPPDATA%\Wardogs\Saved\Config\WindowsClient\GameUserSettings.ini.
- `--path`: Optional explicit path override. Use when you prefer option form over positional path.

Examples:
```powershell
engine p3d4 scan --text
```

### `engine p3d4 import [<config-path>] [--path <config-path>] [--replace] [--include-keyboard]`
What it does: Imports the game's joystick binds into the loaded profile (merged by default; --replace starts from an empty relation list).

Arguments and example values:
- `<config-path>`: Path to the game's control file. Optional: the engine uses the file it finds on this machine. Example: %LOCALAPPDATA%\Wardogs\Saved\Config\WindowsClient\GameUserSettings.ini.
- `--path`: Optional explicit path override. Use when you prefer option form over positional path.
- `--replace`: For game import: start from an empty relation list instead of merging into the loaded profile.
- `--include-keyboard`: Command argument. See examples for typical values.

Examples:
```powershell
engine --profile 'C:\JoyPro\profiles\pilot_main.pr0file' p3d4 import --text
```

### `engine p3d4 export [<config-path>] [--path <config-path>] [--mode clean|add|merge|merge-overwrite] [--dry-run] [--include-keyboard] [--nuke-untouched-devices]   (flight only: clean takes over the devices it writes, nuke clears unused devices)`
What it does: Writes the profile's binds for this game. Only the flight actions JoyPro manages are changed, the game must be closed, and the file is backed up to <file>.jpbak first.

Arguments and example values:
- `<config-path>`: Path to the game's control file. Optional: the engine uses the file it finds on this machine. Example: %LOCALAPPDATA%\Wardogs\Saved\Config\WindowsClient\GameUserSettings.ini.
- `--path`: Optional explicit path override. Use when you prefer option form over positional path.
- `--mode`: Export mode for DCS/IL2 export commands: clean, add, merge, merge-overwrite.
- `--dry-run`: Plan mode only: do not click, launch, or change files; report what would run.
- `--include-keyboard`: Command argument. See examples for typical values.
- `--nuke-untouched-devices`: DCS export only: delete untouched device .diff.lua/.jp files under Config\\Input (true/false).

Examples:
```powershell
engine --profile 'C:\JoyPro\profiles\pilot_main.pr0file' p3d4 export --dry-run --text engine --profile 'C:\JoyPro\profiles\pilot_main.pr0file' p3d4 export --mode merge-overwrite --text
```

### `engine p3d4 refresh-db [<source-path>] [--dry-run]   (builds the catalog from the installed game, or adds its new actions; writes it only when it changes)`
What it does: Updates this game's action catalog from the game or its files.

Arguments and example values:
- `<source-path>`: Path to read actions from (game install or control file); optional.
- `--dry-run`: Plan mode only: do not click, launch, or change files; report what would run.

Examples:
```powershell
engine p3d4 refresh-db --text
```

### `engine p3d5 paths`
What it does: Lists the config files of this game found on this machine.

Arguments and example values:
- No command-specific arguments.

Examples:
```powershell
engine p3d5 paths --text
```

### `engine p3d5 catalog`
What it does: Lists the flight actions JoyPro manages for this game (from DB\<Game>\actions.tsv).

Arguments and example values:
- No command-specific arguments.

Examples:
```powershell
engine p3d5 catalog --text
```

### `engine p3d5 scan [<config-path>] [--path <config-path>]`
What it does: Reads the game's control file and shows the joystick binds it contains, without changing the profile.

Arguments and example values:
- `<config-path>`: Path to the game's control file. Optional: the engine uses the file it finds on this machine. Example: %LOCALAPPDATA%\Wardogs\Saved\Config\WindowsClient\GameUserSettings.ini.
- `--path`: Optional explicit path override. Use when you prefer option form over positional path.

Examples:
```powershell
engine p3d5 scan --text
```

### `engine p3d5 import [<config-path>] [--path <config-path>] [--replace] [--include-keyboard]`
What it does: Imports the game's joystick binds into the loaded profile (merged by default; --replace starts from an empty relation list).

Arguments and example values:
- `<config-path>`: Path to the game's control file. Optional: the engine uses the file it finds on this machine. Example: %LOCALAPPDATA%\Wardogs\Saved\Config\WindowsClient\GameUserSettings.ini.
- `--path`: Optional explicit path override. Use when you prefer option form over positional path.
- `--replace`: For game import: start from an empty relation list instead of merging into the loaded profile.
- `--include-keyboard`: Command argument. See examples for typical values.

Examples:
```powershell
engine --profile 'C:\JoyPro\profiles\pilot_main.pr0file' p3d5 import --text
```

### `engine p3d5 export [<config-path>] [--path <config-path>] [--mode clean|add|merge|merge-overwrite] [--dry-run] [--include-keyboard] [--nuke-untouched-devices]   (flight only: clean takes over the devices it writes, nuke clears unused devices)`
What it does: Writes the profile's binds for this game. Only the flight actions JoyPro manages are changed, the game must be closed, and the file is backed up to <file>.jpbak first.

Arguments and example values:
- `<config-path>`: Path to the game's control file. Optional: the engine uses the file it finds on this machine. Example: %LOCALAPPDATA%\Wardogs\Saved\Config\WindowsClient\GameUserSettings.ini.
- `--path`: Optional explicit path override. Use when you prefer option form over positional path.
- `--mode`: Export mode for DCS/IL2 export commands: clean, add, merge, merge-overwrite.
- `--dry-run`: Plan mode only: do not click, launch, or change files; report what would run.
- `--include-keyboard`: Command argument. See examples for typical values.
- `--nuke-untouched-devices`: DCS export only: delete untouched device .diff.lua/.jp files under Config\\Input (true/false).

Examples:
```powershell
engine --profile 'C:\JoyPro\profiles\pilot_main.pr0file' p3d5 export --dry-run --text engine --profile 'C:\JoyPro\profiles\pilot_main.pr0file' p3d5 export --mode merge-overwrite --text
```

### `engine p3d5 refresh-db [<source-path>] [--dry-run]   (builds the catalog from the installed game, or adds its new actions; writes it only when it changes)`
What it does: Updates this game's action catalog from the game or its files.

Arguments and example values:
- `<source-path>`: Path to read actions from (game install or control file); optional.
- `--dry-run`: Plan mode only: do not click, launch, or change files; report what would run.

Examples:
```powershell
engine p3d5 refresh-db --text
```

### `engine p3d6 paths`
What it does: Lists the config files of this game found on this machine.

Arguments and example values:
- No command-specific arguments.

Examples:
```powershell
engine p3d6 paths --text
```

### `engine p3d6 catalog`
What it does: Lists the flight actions JoyPro manages for this game (from DB\<Game>\actions.tsv).

Arguments and example values:
- No command-specific arguments.

Examples:
```powershell
engine p3d6 catalog --text
```

### `engine p3d6 scan [<config-path>] [--path <config-path>]`
What it does: Reads the game's control file and shows the joystick binds it contains, without changing the profile.

Arguments and example values:
- `<config-path>`: Path to the game's control file. Optional: the engine uses the file it finds on this machine. Example: %LOCALAPPDATA%\Wardogs\Saved\Config\WindowsClient\GameUserSettings.ini.
- `--path`: Optional explicit path override. Use when you prefer option form over positional path.

Examples:
```powershell
engine p3d6 scan --text
```

### `engine p3d6 import [<config-path>] [--path <config-path>] [--replace] [--include-keyboard]`
What it does: Imports the game's joystick binds into the loaded profile (merged by default; --replace starts from an empty relation list).

Arguments and example values:
- `<config-path>`: Path to the game's control file. Optional: the engine uses the file it finds on this machine. Example: %LOCALAPPDATA%\Wardogs\Saved\Config\WindowsClient\GameUserSettings.ini.
- `--path`: Optional explicit path override. Use when you prefer option form over positional path.
- `--replace`: For game import: start from an empty relation list instead of merging into the loaded profile.
- `--include-keyboard`: Command argument. See examples for typical values.

Examples:
```powershell
engine --profile 'C:\JoyPro\profiles\pilot_main.pr0file' p3d6 import --text
```

### `engine p3d6 export [<config-path>] [--path <config-path>] [--mode clean|add|merge|merge-overwrite] [--dry-run] [--include-keyboard] [--nuke-untouched-devices]   (flight only: clean takes over the devices it writes, nuke clears unused devices)`
What it does: Writes the profile's binds for this game. Only the flight actions JoyPro manages are changed, the game must be closed, and the file is backed up to <file>.jpbak first.

Arguments and example values:
- `<config-path>`: Path to the game's control file. Optional: the engine uses the file it finds on this machine. Example: %LOCALAPPDATA%\Wardogs\Saved\Config\WindowsClient\GameUserSettings.ini.
- `--path`: Optional explicit path override. Use when you prefer option form over positional path.
- `--mode`: Export mode for DCS/IL2 export commands: clean, add, merge, merge-overwrite.
- `--dry-run`: Plan mode only: do not click, launch, or change files; report what would run.
- `--include-keyboard`: Command argument. See examples for typical values.
- `--nuke-untouched-devices`: DCS export only: delete untouched device .diff.lua/.jp files under Config\\Input (true/false).

Examples:
```powershell
engine --profile 'C:\JoyPro\profiles\pilot_main.pr0file' p3d6 export --dry-run --text engine --profile 'C:\JoyPro\profiles\pilot_main.pr0file' p3d6 export --mode merge-overwrite --text
```

### `engine p3d6 refresh-db [<source-path>] [--dry-run]   (builds the catalog from the installed game, or adds its new actions; writes it only when it changes)`
What it does: Updates this game's action catalog from the game or its files.

Arguments and example values:
- `<source-path>`: Path to read actions from (game install or control file); optional.
- `--dry-run`: Plan mode only: do not click, launch, or change files; report what would run.

Examples:
```powershell
engine p3d6 refresh-db --text
```

### `engine layout render [--path <export-folder>] [--device <name>] [--devices <csv>] [--layout-map <joystick>=<path.layout>] [--game <name>] [--games <csv>] [--plane <name>] [--planes <csv>] [--include-overview true|false] [--include-planes true|false] [--skip-missing-layouts true|false] [--draw-markers true|false] [--draw-legend-table true|false] [--marker-size <n>] [--legend-width <n>] [--legend-max-rows <n>] [--legend-font-size <n>] [--modifier-colors <modifier=#RRGGBB,...>] [--button-symbol circle|square|triangle|diamond|bar] [--axis-symbol circle|square|triangle|diamond|bar] [--pov-symbol circle|square|triangle|diamond|bar] [--slider-symbol circle|square|triangle|diamond|bar] [--font <family>] [--font-size <n>] [--text-color <#RRGGBB|#AARRGGBB>] [--outline-color <color|none>] [--label-background <color|none>] [--style dark|light] [--print-width <px>] [--combine true|false] [--drawing-style lines|cel] [--drawing-preset joypro|manual|blueprint|print|custom] [--drawing-ink <#RRGGBB>] [--drawing-paper <#RRGGBB>] [--drawing-accent <#RRGGBB>]   (a --layout-map to a device layout .json with tables and lines draws JoyPro 1 style sheets; the --drawing-* options paint JoyPro's own drawings, default: the GUI's settings)`
What it does: Renders joystick layout images per game/plane (command descriptions) and overview images (relation names) from JoyPro .layout mappings.

Arguments and example values:
- `--path`: Optional explicit path override. Use when you prefer option form over positional path.
- `<export-folder>`: Command argument. See examples for typical values.
- `--device`: Include one device per flag. Example: --device LEFT VPC Throttle.
- `<name>`: Name value for surrounding option. Example: A-10C_2 or LEFT VPC Throttle.
- `--devices`: Include devices as CSV. Example: --devices VKB Gladiator NXT,LEFT VPC Throttle.
- `<csv>`: Comma-separated values. Example: 0.1,0.2,0.3 or VKB,LEFT VPC Throttle.
- `--layout-map`: Map joystick name to layout/image file path. Example: --layout-map 'VKB Gladiator NXT=C:\\JoyPro\\LayoutImages\\VKB.layout'.
- `<joystick>`: Command argument. See examples for typical values.
- `<path.layout>`: Command argument. See examples for typical values.
- `--game`: Game filter value. Example: DCS or IL2Game.
- `--games`: Game filter values as CSV. Example: DCS,IL2Game,StarCitizen.
- `--plane`: Include one plane/module per flag. Example: --plane A-10C_2.
- `--planes`: Include planes/modules as CSV. Example: --planes A-10C_2,FA-18C_hornet.
- `--include-overview`: For layout render: include overview image(s) in export root that show relation names on each input token (true/false).
- `--include-planes`: For layout render: include per-game/per-plane output images showing command descriptions (true/false).
- `--skip-missing-layouts`: For layout render: skip unmapped/missing joystick layouts as warnings (true) or fail the command (false).
- `--draw-markers`: Command argument. See examples for typical values.
- `--draw-legend-table`: Command argument. See examples for typical values.
- `--marker-size`: Command argument. See examples for typical values.
- `<n>`: Integer numeric value. Example: 45.
- `--legend-width`: Command argument. See examples for typical values.
- `--legend-max-rows`: Command argument. See examples for typical values.
- `--legend-font-size`: Command argument. See examples for typical values.
- `--modifier-colors`: Command argument. See examples for typical values.
- `<modifier=#RRGGBB,...>`: Command argument. See examples for typical values.
- `--button-symbol`: Command argument. See examples for typical values.
- `--axis-symbol`: Command argument. See examples for typical values.
- `--pov-symbol`: Command argument. See examples for typical values.
- `--slider-symbol`: Command argument. See examples for typical values.
- `--font`: Command argument. See examples for typical values.
- `<family>`: Command argument. See examples for typical values.
- `--font-size`: Command argument. See examples for typical values.
- `--text-color`: Command argument. See examples for typical values.
- `<#RRGGBB|#AARRGGBB>`: Command argument. See examples for typical values.
- `--outline-color`: Command argument. See examples for typical values.
- `<color|none>`: Command argument. See examples for typical values.
- `--label-background`: Command argument. See examples for typical values.
- `--style`: Command argument. See examples for typical values.
- `--print-width`: Command argument. See examples for typical values.
- `<px>`: Command argument. See examples for typical values.
- `--combine`: Command argument. See examples for typical values.
- `--drawing-style`: Command argument. See examples for typical values.
- `--drawing-preset`: Command argument. See examples for typical values.
- `--drawing-ink`: Command argument. See examples for typical values.
- `<#RRGGBB>`: Command argument. See examples for typical values.
- `--drawing-paper`: Command argument. See examples for typical values.
- `--drawing-accent`: Command argument. See examples for typical values.
- `--drawing-`: Command argument. See examples for typical values.

Examples:
```powershell
engine --help
```

### `engine layout callouts (--model <catalog id> | --layout <device-layout.json>) [--picture <file>] [--schematic] [--drawing-style lines|cel] [--drawing-preset <p>] [--drawing-ink|--drawing-paper|--drawing-accent <#RRGGBB>] [--arrange] [--device <name>] [--game <name> --plane <name>] [--sample] [--png <file>] [--light] [--print-width <px>] [--save <file.json>]   (tables around the device picture with orthogonal lines; --check-all [query] checks every model)`
What it does: Runs this command operation.

Arguments and example values:
- `--model`: Command argument. See examples for typical values.
- `<catalog id>`: Command argument. See examples for typical values.
- `--layout`: Command argument. See examples for typical values.
- `<device-layout.json>`: Command argument. See examples for typical values.
- `--picture`: Command argument. See examples for typical values.
- `<file>`: Command argument. See examples for typical values.
- `--schematic`: Command argument. See examples for typical values.
- `--drawing-style`: Command argument. See examples for typical values.
- `--drawing-preset`: Command argument. See examples for typical values.
- `<p>`: Command argument. See examples for typical values.
- `--drawing-ink`: Command argument. See examples for typical values.
- `--drawing-paper`: Command argument. See examples for typical values.
- `--drawing-accent`: Command argument. See examples for typical values.
- `<#RRGGBB>`: Command argument. See examples for typical values.
- `--arrange`: Command argument. See examples for typical values.
- `--device`: Include one device per flag. Example: --device LEFT VPC Throttle.
- `<name>`: Name value for surrounding option. Example: A-10C_2 or LEFT VPC Throttle.
- `--game`: Game filter value. Example: DCS or IL2Game.
- `--plane`: Include one plane/module per flag. Example: --plane A-10C_2.
- `--sample`: Command argument. See examples for typical values.
- `--png`: Command argument. See examples for typical values.
- `--light`: Command argument. See examples for typical values.
- `--print-width`: Command argument. See examples for typical values.
- `<px>`: Command argument. See examples for typical values.
- `--save`: Command argument. See examples for typical values.
- `<file.json>`: Path to a JSON file. Example: C:\JoyPro\sync-choices.json.
- `--check-all`: Command argument. See examples for typical values.

Examples:
```powershell
engine --help
```

### `engine layout titles --device <name>   (the game's own menu titles of the device's binds, per game and aircraft)`
What it does: Runs this command operation.

Arguments and example values:
- `--device`: Include one device per flag. Example: --device LEFT VPC Throttle.
- `<name>`: Name value for surrounding option. Example: A-10C_2 or LEFT VPC Throttle.

Examples:
```powershell
engine --help
```

### `engine layout suggest [--device <name>] [--devices <csv>] [--connected] [--profile-devices] [--legacy-layout-dir <path>] [--legacy-output-dir <path>] [--catalog <path.json>] [--apply true|false] [--apply-min-score <n>] [--backdrop <picture-file-or-image-url>]`
What it does: Runs this command operation.

Arguments and example values:
- `--device`: Include one device per flag. Example: --device LEFT VPC Throttle.
- `<name>`: Name value for surrounding option. Example: A-10C_2 or LEFT VPC Throttle.
- `--devices`: Include devices as CSV. Example: --devices VKB Gladiator NXT,LEFT VPC Throttle.
- `<csv>`: Comma-separated values. Example: 0.1,0.2,0.3 or VKB,LEFT VPC Throttle.
- `--connected`: For devices list, show connected devices only.
- `--profile-devices`: For devices list, show profile-referenced devices only.
- `--legacy-layout-dir`: Command argument. See examples for typical values.
- `<path>`: Filesystem path value. Example: C:\\JoyPro\\logs\\preview.png.
- `--legacy-output-dir`: Command argument. See examples for typical values.
- `--catalog`: Command argument. See examples for typical values.
- `<path.json>`: Path to JSON file. Example: C:\\JoyPro\\exports\\relations.json.
- `--apply`: Command argument. See examples for typical values.
- `--apply-min-score`: Command argument. See examples for typical values.
- `<n>`: Integer numeric value. Example: 45.
- `--backdrop`: Command argument. See examples for typical values.
- `<picture-file-or-image-url>`: Command argument. See examples for typical values.

Examples:
```powershell
engine --help
```

### `engine layout identify [--device <name>] [--devices <csv>] [--connected]   (vendor/model from VID:PID and HID name, picture search link)`
What it does: Runs this command operation.

Arguments and example values:
- `--device`: Include one device per flag. Example: --device LEFT VPC Throttle.
- `<name>`: Name value for surrounding option. Example: A-10C_2 or LEFT VPC Throttle.
- `--devices`: Include devices as CSV. Example: --devices VKB Gladiator NXT,LEFT VPC Throttle.
- `<csv>`: Comma-separated values. Example: 0.1,0.2,0.3 or VKB,LEFT VPC Throttle.
- `--connected`: For devices list, show connected devices only.

Examples:
```powershell
engine --help
```

### `engine layout template-inspect --source <path.layout-or-image> [--max-markers <n>]`
What it does: Runs this command operation.

Arguments and example values:
- `--source`: Command argument. See examples for typical values.
- `<path.layout-or-image>`: Command argument. See examples for typical values.
- `--max-markers`: Command argument. See examples for typical values.
- `<n>`: Integer numeric value. Example: 45.

Examples:
```powershell
engine --help
```

### `engine layout template-save --source <path.layout-or-image> --out <path.layout> [--markers-json <file.json> | --markers <json-array-or-object>] [--tables-json <file.json> | --tables <json-array-or-object>] [--connectors-json <file.json> | --connectors <json-array-or-object>] [--replace-all true|false]`
What it does: Runs this command operation.

Arguments and example values:
- `--source`: Command argument. See examples for typical values.
- `<path.layout-or-image>`: Command argument. See examples for typical values.
- `--out`: Global option: write result output to file path instead of terminal.
- `<path.layout>`: Command argument. See examples for typical values.
- `--markers-json`: Command argument. See examples for typical values.
- `<file.json>`: Path to a JSON file. Example: C:\JoyPro\sync-choices.json.
- `--markers`: Command argument. See examples for typical values.
- `<json-array-or-object>`: Command argument. See examples for typical values.
- `--tables-json`: Command argument. See examples for typical values.
- `--tables`: Command argument. See examples for typical values.
- `--connectors-json`: Command argument. See examples for typical values.
- `--connectors`: Command argument. See examples for typical values.
- `--replace-all`: Command argument. See examples for typical values.

Examples:
```powershell
engine --help
```

### `engine devices list [--connected] [--profile-devices]`
What it does: Lists connected devices and/or devices referenced in profile data.

Arguments and example values:
- `--connected`: For devices list, show connected devices only.
- `--profile-devices`: For devices list, show profile-referenced devices only.

How arguments interact:
- No flag shows both connected and profile devices.
- --connected and --profile-devices let you limit one side.

Examples:
```powershell
engine devices list --connected --text
engine devices list --profile-devices --text
```

### `engine devices inputs [--include-off]   (aliases: catalog, input-catalog)`
What it does: Lists full supported input-token catalogs (joystick axes/buttons/POV and keyboard key names for DCS/IL2).

Arguments and example values:
- `--include-off`: For devices inputs, include _OFF release variants for joystick axis/button/POV tokens.

How arguments interact:
- Outputs canonical joystick and keyboard token catalogs in JSON (default) or text (--text).
- Use --include-off to include _OFF release variants for joystick inputs.
- Keyboard section includes DCS-style tokens and IL2 `key_*` tokens for `binds set --format il2`.

Examples:
```powershell
engine devices inputs --text
engine devices inputs --include-off --text
```

### `engine devices exchange <old-device> <new-device> [--old <name>] [--new <name>] [--confirm true|false]   (alias: swap; renames the device, inputs stay)`
What it does: Replaces one device name with another across profile binds, modifiers, and device metadata.

Arguments and example values:
- `<old-device>`: Source device name to replace. Example: LEFT VPC Throttle {B6781E40-0000-0000-0000-504944564944}.
- `<new-device>`: Target device name to assign. Example: RIGHT VPC Throttle {C7122B40-0000-0000-0000-504944564944}.
- `--old`: Source device name for devices exchange. Example: --old 'LEFT VPC Throttle'.
- `<name>`: Name value for surrounding option. Example: A-10C_2 or LEFT VPC Throttle.
- `--new`: Target device name for devices exchange. Example: --new 'RIGHT VPC Throttle'.
- `--confirm`: Controls suggestion confirmation (true) vs auto-accept (false).

How arguments interact:
- Accepts positional <old-device> <new-device> or named options --old/--new.
- Use --confirm false to auto-accept closest device suggestions for fuzzy names.
- Updates binds, modifiers, and profile device metadata keys in one operation.

Examples:
```powershell
engine --profile 'C:\JoyPro\profiles\pilot_main.pr0file' devices exchange 'LEFT VPC Throttle' 'RIGHT VPC Throttle' --confirm false --text
```

### `engine devices replace <old-device> <new-device> [--mode auto|same|layout] [--map OLD=NEW]... [--merge add|keep|replace] [--grip <device>=<grip id>]... [--dry-run]   (moves binds and modifiers input by input: same ids, or by layout from the device catalog; --dry-run previews)`
What it does: Runs this command operation.

Arguments and example values:
- `<old-device>`: Source device name to replace. Example: LEFT VPC Throttle {B6781E40-0000-0000-0000-504944564944}.
- `<new-device>`: Target device name to assign. Example: RIGHT VPC Throttle {C7122B40-0000-0000-0000-504944564944}.
- `--mode`: Export mode for DCS/IL2 export commands: clean, add, merge, merge-overwrite.
- `--map`: Command argument. See examples for typical values.
- `--merge`: Merge import results into existing in-memory profile.
- `--grip`: Command argument. See examples for typical values.
- `<device>`: Command argument. See examples for typical values.
- `<grip id>`: Command argument. See examples for typical values.
- `--dry-run`: Plan mode only: do not click, launch, or change files; report what would run.

Examples:
```powershell
engine --help
```

### `engine devices auto-remap [--dry-run]   (alias: remap; a profile device that is not connected gets the connected device of the same product with a new GUID)`
What it does: Runs this command operation.

Arguments and example values:
- `--dry-run`: Plan mode only: do not click, launch, or change files; report what would run.

Examples:
```powershell
engine --help
```

### `engine devices remove <device-name> [--device <name>] [--confirm true|false]   (aliases: purge, clear)`
What it does: Removes all references to one device from profile binds, modifiers, and device metadata.

Arguments and example values:
- `<device-name>`: Device name token. Example: VKB Gladiator NXT {B4280440-0000-0000-0000-504944564944}.
- `--device`: Include one device per flag. Example: --device LEFT VPC Throttle.
- `<name>`: Name value for surrounding option. Example: A-10C_2 or LEFT VPC Throttle.
- `--confirm`: Controls suggestion confirmation (true) vs auto-accept (false).

How arguments interact:
- Removes one target device at a time.
- Removes matching bind assignments and modifier references, then prunes profile metadata entries for that device.

Examples:
```powershell
engine --profile 'C:\JoyPro\profiles\pilot_main.pr0file' devices remove 'LEFT VPC Throttle' --confirm false --text
```

### `engine relations list [filter]`
What it does: Lists relations (logical actions) in the profile.

Arguments and example values:
- `filter`: Text filter (case-insensitive substring). Example values: 'A-10', 'Hornet', 'VKB'.

Examples:
```powershell
engine relations list 'Gear' --text
```

### `engine relations show <relation-name>`
What it does: Shows one relation in detail.

Arguments and example values:
- `<relation-name>`: Name of target relation. Example: Gear Toggle.

Examples:
```powershell
engine relations show 'Gear Toggle' --text
```

### `engine relations add <name> [--axis|--button|--type axis|button] [--node game:id[:plane1|plane2]] [--from relation] [--group g] [--device d --input i --modifier m]`
What it does: Creates a relation with node targets and optional bind/group data.

Arguments and example values:
- `<name>`: Name value for surrounding option. Example: A-10C_2 or LEFT VPC Throttle.
- `--axis`: Set relation type to axis (explicit override).
- `--button`: Set relation type to button (explicit override).
- `--type`: Set relation type via value token (`axis` or `button`).
- `--node`: Node token format: game:id[:plane1|plane2]. Example: DCS:d3007pnilu3007cd16vd1vpnilvu0:F-16C_50|FA-18C_hornet.
- `--from`: Copy structure from existing relation(s).
- `--group`: Attach relation group tag. Example: --group Startup.
- `--device`: Include one device per flag. Example: --device LEFT VPC Throttle.
- `--input`: Input token. Example: --input JOY_BTN1 or --input joy0_b0.
- `--modifier`: Modifier key token. Example: --modifier LShift.

How arguments interact:
- You need at least one source: --node, --id/--game, or --from.
- Type is inferred from source relation (--from) or node IDs when possible.
- Use --axis / --button / --type only when you want an explicit override.

Examples:
```powershell
engine relations add 'Gear Toggle' --node DCS:d3007pnilu3007cd16vd1vpnilvu0 --group 'Startup' --text
engine relations add 'Pitch Axis' --type axis --node IL2Game:mc_pitch --text
```

### `engine relations group list [relation-name]`
What it does: Lists groups on one relation, or all groups across all relations.

Arguments and example values:
- No command-specific arguments.

How arguments interact:
- Without a relation name, lists all groups and where they are used.

Examples:
```powershell
engine relations group list --text
engine relations group list 'Gear Toggle' --text
```

### `engine relations group add <relation-name> <group1> [group2] [...]`
What it does: Adds group tags to a relation.

Arguments and example values:
- `<relation-name>`: Name of target relation. Example: Gear Toggle.
- `<group1>`: First group/tag name. Example: Startup.

Examples:
```powershell
engine relations group add 'Gear Toggle' 'Startup' 'Common' --text
```

### `engine relations group remove <relation-name> <group1> [group2] [...] [--all]`
What it does: Removes group tags from a relation.

Arguments and example values:
- `<relation-name>`: Name of target relation. Example: Gear Toggle.
- `<group1>`: First group/tag name. Example: Startup.
- `--all`: Apply operation to all matches (or select all relations/devices where supported).

How arguments interact:
- Use --all to clear all values instead of specifying individual names.

Examples:
```powershell
engine relations group remove 'Gear Toggle' 'Startup' --text
engine relations group remove 'Gear Toggle' --all --text
```

### `engine relations group rename <old-group> <new-group>`
What it does: Renames a group across all relations that currently use it.

Arguments and example values:
- `<old-group>`: Existing group name to rename. Example: Startup.
- `<new-group>`: New group name to apply. Example: Core Startup.

How arguments interact:
- Renames group usage across all relations that contain the old name.

Examples:
```powershell
engine relations group rename 'Startup' 'Core Startup' --text
```

### `engine relations item add <relation-name> --node game:id[:plane1|plane2] [--node ...]  (or --id/--game/--planes)`
What it does: Adds node targets to an existing relation.

Arguments and example values:
- `<relation-name>`: Name of target relation. Example: Gear Toggle.
- `--node`: Node token format: game:id[:plane1|plane2]. Example: DCS:d3007pnilu3007cd16vd1vpnilvu0:F-16C_50|FA-18C_hornet.
- `--id`: Node ID. Example: --id d3007pnilu3007cd16vd1vpnilvu0.
- `--game`: Game filter value. Example: DCS or IL2Game.
- `--planes`: Include planes/modules as CSV. Example: --planes A-10C_2,FA-18C_hornet.

How arguments interact:
- At least one target (--node or --id) is required.

Examples:
```powershell
engine relations item add 'Gear Toggle' --id d3007pnilu3007cd16vd1vpnilvu0 --game DCS --planes F-16C_50 --text
```

### `engine relations item remove <relation-name> --node game:id[:plane1|plane2] [--node ...] [--all]`
What it does: Removes node targets from an existing relation.

Arguments and example values:
- `<relation-name>`: Name of target relation. Example: Gear Toggle.
- `--node`: Node token format: game:id[:plane1|plane2]. Example: DCS:d3007pnilu3007cd16vd1vpnilvu0:F-16C_50|FA-18C_hornet.
- `--all`: Apply operation to all matches (or select all relations/devices where supported).

How arguments interact:
- Use target selectors or --all.

Examples:
```powershell
engine relations item remove 'Gear Toggle' --id d3007pnilu3007cd16vd1vpnilvu0 --game DCS --text
```

### `engine relations item enable <relation-name> --node game:id[:plane1|plane2] [--node ...]`
What it does: Enables selected node targets on a relation.

Arguments and example values:
- `<relation-name>`: Name of target relation. Example: Gear Toggle.
- `--node`: Node token format: game:id[:plane1|plane2]. Example: DCS:d3007pnilu3007cd16vd1vpnilvu0:F-16C_50|FA-18C_hornet.

How arguments interact:
- At least one target (--node or --id) is required.

Examples:
```powershell
engine relations item enable 'Gear Toggle' --node DCS:d3007pnilu3007cd16vd1vpnilvu0:F-16C_50 --text
```

### `engine relations item disable <relation-name> --node game:id[:plane1|plane2] [--node ...]`
What it does: Disables selected node targets on a relation.

Arguments and example values:
- `<relation-name>`: Name of target relation. Example: Gear Toggle.
- `--node`: Node token format: game:id[:plane1|plane2]. Example: DCS:d3007pnilu3007cd16vd1vpnilvu0:F-16C_50|FA-18C_hornet.

How arguments interact:
- At least one target (--node or --id) is required.

Examples:
```powershell
engine relations item disable 'Gear Toggle' --node DCS:d3007pnilu3007cd16vd1vpnilvu0:F-16C_50 --text
```

### `engine relations mass delete [--relation <name>] [--relations <csv>] [--query <text>] [--all]`
What it does: Deletes many relations in one command using explicit names, query filter, or --all.

Arguments and example values:
- `--relation`: Select one relation per flag for mass operations. Example: --relation 'Trim Pitch'.
- `<name>`: Name value for surrounding option. Example: A-10C_2 or LEFT VPC Throttle.
- `--relations`: Select relations as CSV for mass operations. Example: --relations 'Trim Pitch,Trim Roll'.
- `<csv>`: Comma-separated values. Example: 0.1,0.2,0.3 or VKB,LEFT VPC Throttle.
- `--query`: Select relations by substring query for mass operations. Example: --query Startup.
- `<text>`: Command argument. See examples for typical values.
- `--all`: Apply operation to all matches (or select all relations/devices where supported).

How arguments interact:
- Selection supports explicit --relation/--relations, filter --query, and --all.

Examples:
```powershell
engine relations mass delete --query 'Temporary' --text
engine relations mass delete --all --text
```

### `engine relations mass duplicate [--relation <name>] [--relations <csv>] [--query <text>] [--all]`
What it does: Duplicates many relations in one command.

Arguments and example values:
- `--relation`: Select one relation per flag for mass operations. Example: --relation 'Trim Pitch'.
- `<name>`: Name value for surrounding option. Example: A-10C_2 or LEFT VPC Throttle.
- `--relations`: Select relations as CSV for mass operations. Example: --relations 'Trim Pitch,Trim Roll'.
- `<csv>`: Comma-separated values. Example: 0.1,0.2,0.3 or VKB,LEFT VPC Throttle.
- `--query`: Select relations by substring query for mass operations. Example: --query Startup.
- `<text>`: Command argument. See examples for typical values.
- `--all`: Apply operation to all matches (or select all relations/devices where supported).

How arguments interact:
- Selection supports explicit --relation/--relations, filter --query, and --all.

Examples:
```powershell
engine relations mass duplicate --relations 'Trim Pitch,Trim Roll' --text
```

### `engine relations mass merge <name> [--relation <name>] [--relations <csv>] [--query <text>] [--all]`
What it does: Merges selected relations into one new relation name.

Arguments and example values:
- `<name>`: Name value for surrounding option. Example: A-10C_2 or LEFT VPC Throttle.
- `--relation`: Select one relation per flag for mass operations. Example: --relation 'Trim Pitch'.
- `--relations`: Select relations as CSV for mass operations. Example: --relations 'Trim Pitch,Trim Roll'.
- `<csv>`: Comma-separated values. Example: 0.1,0.2,0.3 or VKB,LEFT VPC Throttle.
- `--query`: Select relations by substring query for mass operations. Example: --query Startup.
- `<text>`: Command argument. See examples for typical values.
- `--all`: Apply operation to all matches (or select all relations/devices where supported).

How arguments interact:
- Selection supports explicit --relation/--relations, filter --query, and --all.
- Selected relations must all be axis or all button.

Examples:
```powershell
engine relations mass merge 'Merged Trim' --relations 'Trim Pitch,Trim Roll' --text
```

### `engine relations mass split <postfix> [--plane <game:plane|plane>] [--planes <csv>] [--relation <name>] [--relations <csv>] [--query <text>] [--all]`
What it does: Splits selected planes out of selected relations into new postfix relations.

Arguments and example values:
- `<postfix>`: Suffix appended for split output relation names. Example: CarrierOps.
- `--plane`: Include one plane/module per flag. Example: --plane A-10C_2.
- `<game:plane|plane>`: Command argument. See examples for typical values.
- `--planes`: Include planes/modules as CSV. Example: --planes A-10C_2,FA-18C_hornet.
- `<csv>`: Comma-separated values. Example: 0.1,0.2,0.3 or VKB,LEFT VPC Throttle.
- `--relation`: Select one relation per flag for mass operations. Example: --relation 'Trim Pitch'.
- `<name>`: Name value for surrounding option. Example: A-10C_2 or LEFT VPC Throttle.
- `--relations`: Select relations as CSV for mass operations. Example: --relations 'Trim Pitch,Trim Roll'.
- `--query`: Select relations by substring query for mass operations. Example: --query Startup.
- `<text>`: Command argument. See examples for typical values.
- `--all`: Apply operation to all matches (or select all relations/devices where supported).

How arguments interact:
- Selection supports explicit --relation/--relations, filter --query, and --all.
- Selected relations must all be axis or all button.
- Planes can be passed as PlaneName or Game:PlaneName.

Examples:
```powershell
engine relations mass split 'Carrier' --plane 'DCS:F/A-18C_hornet' --query 'Startup' --text
```

### `engine relations mass add-prefix <value> [--relation <name>] [--relations <csv>] [--query <text>] [--all]`
What it does: Adds a prefix to many relation names at once.

Arguments and example values:
- `<value>`: Generic value placeholder. Example for --input: JOY_BTN1 or joy0_b0.
- `--relation`: Select one relation per flag for mass operations. Example: --relation 'Trim Pitch'.
- `<name>`: Name value for surrounding option. Example: A-10C_2 or LEFT VPC Throttle.
- `--relations`: Select relations as CSV for mass operations. Example: --relations 'Trim Pitch,Trim Roll'.
- `<csv>`: Comma-separated values. Example: 0.1,0.2,0.3 or VKB,LEFT VPC Throttle.
- `--query`: Select relations by substring query for mass operations. Example: --query Startup.
- `<text>`: Command argument. See examples for typical values.
- `--all`: Apply operation to all matches (or select all relations/devices where supported).

How arguments interact:
- Selection supports explicit --relation/--relations, filter --query, and --all.

Examples:
```powershell
engine relations mass add-prefix 'A10_' --query 'A-10' --text
```

### `engine relations mass add-postfix <value> [--relation <name>] [--relations <csv>] [--query <text>] [--all]`
What it does: Adds a postfix to many relation names at once.

Arguments and example values:
- `<value>`: Generic value placeholder. Example for --input: JOY_BTN1 or joy0_b0.
- `--relation`: Select one relation per flag for mass operations. Example: --relation 'Trim Pitch'.
- `<name>`: Name value for surrounding option. Example: A-10C_2 or LEFT VPC Throttle.
- `--relations`: Select relations as CSV for mass operations. Example: --relations 'Trim Pitch,Trim Roll'.
- `<csv>`: Comma-separated values. Example: 0.1,0.2,0.3 or VKB,LEFT VPC Throttle.
- `--query`: Select relations by substring query for mass operations. Example: --query Startup.
- `<text>`: Command argument. See examples for typical values.
- `--all`: Apply operation to all matches (or select all relations/devices where supported).

How arguments interact:
- Selection supports explicit --relation/--relations, filter --query, and --all.

Examples:
```powershell
engine relations mass add-postfix '_ALT' --relations 'Trim Pitch,Trim Roll' --text
```

### `engine relations mass remove-part <value> [--relation <name>] [--relations <csv>] [--query <text>] [--all]`
What it does: Removes a substring from many relation names at once.

Arguments and example values:
- `<value>`: Generic value placeholder. Example for --input: JOY_BTN1 or joy0_b0.
- `--relation`: Select one relation per flag for mass operations. Example: --relation 'Trim Pitch'.
- `<name>`: Name value for surrounding option. Example: A-10C_2 or LEFT VPC Throttle.
- `--relations`: Select relations as CSV for mass operations. Example: --relations 'Trim Pitch,Trim Roll'.
- `<csv>`: Comma-separated values. Example: 0.1,0.2,0.3 or VKB,LEFT VPC Throttle.
- `--query`: Select relations by substring query for mass operations. Example: --query Startup.
- `<text>`: Command argument. See examples for typical values.
- `--all`: Apply operation to all matches (or select all relations/devices where supported).

How arguments interact:
- Selection supports explicit --relation/--relations, filter --query, and --all.

Examples:
```powershell
engine relations mass remove-part 'TEMP_' --query 'TEMP_' --text
```

### `engine relations mass export <path.rl|path.json> [--format rl|json] [--relation <name>] [--relations <csv>] [--query <text>] [--all]`
What it does: Exports selected relations in legacy .rl or JSON format.

Arguments and example values:
- `<path.rl|path.json>`: Path to relation export target (.rl legacy binary or .json). Example: C:\\JoyPro\\exports\\selection.rl.
- `--format`: Format selector. Example values: dcs/il2 for binds unbind, rl/json for relations mass export.
- `--relation`: Select one relation per flag for mass operations. Example: --relation 'Trim Pitch'.
- `<name>`: Name value for surrounding option. Example: A-10C_2 or LEFT VPC Throttle.
- `--relations`: Select relations as CSV for mass operations. Example: --relations 'Trim Pitch,Trim Roll'.
- `<csv>`: Comma-separated values. Example: 0.1,0.2,0.3 or VKB,LEFT VPC Throttle.
- `--query`: Select relations by substring query for mass operations. Example: --query Startup.
- `<text>`: Command argument. See examples for typical values.
- `--all`: Apply operation to all matches (or select all relations/devices where supported).

How arguments interact:
- Selection supports explicit --relation/--relations, filter --query, and --all.
- Default format is .rl unless extension is .json or --format json is set.

Examples:
```powershell
engine relations mass export 'C:\\JoyPro\\exports\\selection.rl' --query 'Startup' --text
engine relations mass export 'C:\\JoyPro\\exports\\selection.json' --all --format json --text
```

### `engine relations export <path.json>`
What it does: Exports relations to JSON.

Arguments and example values:
- `<path.json>`: Path to JSON file. Example: C:\\JoyPro\\exports\\relations.json.

Examples:
```powershell
engine relations export 'C:\JoyPro\exports\relations.json'
```

### `engine relations import <path.json>`
What it does: Imports relations from JSON.

Arguments and example values:
- `<path.json>`: Path to JSON file. Example: C:\\JoyPro\\exports\\relations.json.

Examples:
```powershell
engine relations import 'C:\JoyPro\exports\relations.json'
```

### `engine relations edit   (no args; exports temp relations JSON, opens editor, re-imports on close; uses --editor or notepad)`
What it does: Opens relations JSON in an external editor for round-trip editing.

Arguments and example values:
- `--editor`: Global option: editor command for relations edit and binds edit (use {file} placeholder).

Examples:
```powershell
engine --editor 'code --wait {file}' relations edit
```

### `engine relations suggest <relation>|--relation <relation> [--min high|medium|low] [--limit N] [--game g1|g2] | --all [--missing [--per-relation N]] [--min ...]`
What it does: Runs this command operation.

Arguments and example values:
- `<relation>`: Command argument. See examples for typical values.
- `--relation`: Select one relation per flag for mass operations. Example: --relation 'Trim Pitch'.
- `--min`: Command argument. See examples for typical values.
- `--limit`: Command argument. See examples for typical values.
- `--game`: Game filter value. Example: DCS or IL2Game.
- `--all`: Apply operation to all matches (or select all relations/devices where supported).
- `--missing`: For db list, show only missing entries.
- `--per-relation`: Command argument. See examples for typical values.

Examples:
```powershell
engine --help
```

### `engine relations suggest apply <relation> [--add game:id:plane1|plane2]... [--never game:id:plane1|plane2]... [--remove game:id:planes]... [--move game:id:planes=>relation]... [--take game:id:planes]...`
What it does: Runs this command operation.

Arguments and example values:
- `<relation>`: Command argument. See examples for typical values.
- `--add`: Command argument. See examples for typical values.
- `--never`: Command argument. See examples for typical values.
- `--remove`: Command argument. See examples for typical values.
- `--move`: Command argument. See examples for typical values.
- `--take`: Command argument. See examples for typical values.

Examples:
```powershell
engine --help
```

### `engine relations search "<words>" [--game g1|g2] [--plane p1|p2] [--category <name>] [--axis|--button] [--limit N] [--per-game N] [--batch <file>]`
What it does: Runs this command operation.

Arguments and example values:
- `<words>`: Command argument. See examples for typical values.
- `--game`: Game filter value. Example: DCS or IL2Game.
- `--plane`: Include one plane/module per flag. Example: --plane A-10C_2.
- `--category`: Command argument. See examples for typical values.
- `<name>`: Name value for surrounding option. Example: A-10C_2 or LEFT VPC Throttle.
- `--axis`: Set relation type to axis (explicit override).
- `--button`: Set relation type to button (explicit override).
- `--limit`: Command argument. See examples for typical values.
- `--per-game`: Command argument. See examples for typical values.
- `--batch`: Command argument. See examples for typical values.
- `<file>`: Command argument. See examples for typical values.

Examples:
```powershell
engine --help
```

### `engine relations browse [--game <game> [--plane p1|p2]] [--axis|--button]   (alias: relations catalog)`
What it does: Runs this command operation.

Arguments and example values:
- `--game`: Game filter value. Example: DCS or IL2Game.
- `<game>`: Command argument. See examples for typical values.
- `--plane`: Include one plane/module per flag. Example: --plane A-10C_2.
- `--axis`: Set relation type to axis (explicit override).
- `--button`: Set relation type to button (explicit override).

Examples:
```powershell
engine --help
```

### `engine relations resolve "<query>" [--game g1|g2] [--plane p1|p2] [--axis|--button] [--per-game N]`
What it does: Runs this command operation.

Arguments and example values:
- `<query>`: Command argument. See examples for typical values.
- `--game`: Game filter value. Example: DCS or IL2Game.
- `--plane`: Include one plane/module per flag. Example: --plane A-10C_2.
- `--axis`: Set relation type to axis (explicit override).
- `--button`: Set relation type to button (explicit override).
- `--per-game`: Command argument. See examples for typical values.

Examples:
```powershell
engine --help
```

### `engine relations functions [--category <id>] [--query "<words>"] [--coverage] [--dump <file.tsv>]`
What it does: Runs this command operation.

Arguments and example values:
- `--category`: Command argument. See examples for typical values.
- `<id>`: Command argument. See examples for typical values.
- `--query`: Select relations by substring query for mass operations. Example: --query Startup.
- `<words>`: Command argument. See examples for typical values.
- `--coverage`: Command argument. See examples for typical values.
- `--dump`: Command argument. See examples for typical values.
- `<file.tsv>`: Command argument. See examples for typical values.

Examples:
```powershell
engine --help
```

### `engine relations chat <relation>|--relation <relation> "<message>" [--llm off|auto|on] [--show-prompt] [--apply]`
What it does: Runs this command operation.

Arguments and example values:
- `<relation>`: Command argument. See examples for typical values.
- `--relation`: Select one relation per flag for mass operations. Example: --relation 'Trim Pitch'.
- `<message>`: Command argument. See examples for typical values.
- `--llm`: Command argument. See examples for typical values.
- `--show-prompt`: Command argument. See examples for typical values.
- `--apply`: Command argument. See examples for typical values.

Examples:
```powershell
engine --help
```

### `engine relations assistant "<message>" [--relation <name>] [--llm auto|on|off] [--max-steps N] [--show-prompt]`
What it does: Runs this command operation.

Arguments and example values:
- `<message>`: Command argument. See examples for typical values.
- `--relation`: Select one relation per flag for mass operations. Example: --relation 'Trim Pitch'.
- `<name>`: Name value for surrounding option. Example: A-10C_2 or LEFT VPC Throttle.
- `--llm`: Command argument. See examples for typical values.
- `--max-steps`: Command argument. See examples for typical values.
- `--show-prompt`: Command argument. See examples for typical values.

Examples:
```powershell
engine --help
```

### `engine relations rename <relation> <new name>`
What it does: Runs this command operation.

Arguments and example values:
- `<relation>`: Command argument. See examples for typical values.
- `<new name>`: Command argument. See examples for typical values.

Examples:
```powershell
engine --help
```

### `engine relations aircraft <relation> [--node game:id]`
What it does: Runs this command operation.

Arguments and example values:
- `<relation>`: Command argument. See examples for typical values.
- `--node`: Node token format: game:id[:plane1|plane2]. Example: DCS:d3007pnilu3007cd16vd1vpnilvu0:F-16C_50|FA-18C_hornet.

Examples:
```powershell
engine --help
```

### `engine relations aircraft set <relation> --node game:id [--on p1|p2] [--off p3|p4]`
What it does: Runs this command operation.

Arguments and example values:
- `<relation>`: Command argument. See examples for typical values.
- `--node`: Node token format: game:id[:plane1|plane2]. Example: DCS:d3007pnilu3007cd16vd1vpnilvu0:F-16C_50|FA-18C_hornet.
- `--on`: Command argument. See examples for typical values.
- `--off`: Command argument. See examples for typical values.

Examples:
```powershell
engine --help
```

### `engine relations check-aircraft [--relation <name>] [--apply] [--include-same-function]`
What it does: Runs this command operation.

Arguments and example values:
- `--relation`: Select one relation per flag for mass operations. Example: --relation 'Trim Pitch'.
- `<name>`: Name value for surrounding option. Example: A-10C_2 or LEFT VPC Throttle.
- `--apply`: Command argument. See examples for typical values.
- `--include-same-function`: Command argument. See examples for typical values.

Examples:
```powershell
engine --help
```

### `engine relations meanings [--game g] [--top N]`
What it does: Runs this command operation.

Arguments and example values:
- `--game`: Game filter value. Example: DCS or IL2Game.
- `--top`: Command argument. See examples for typical values.

Examples:
```powershell
engine --help
```

### `engine relations auto-group [--preset all|wing|era|purist] [--no-substitutes] [--planes p1|p2|Game] [--replace] [--dry-run] [--no-map]`
What it does: Runs this command operation.

Arguments and example values:
- `--preset`: Command argument. See examples for typical values.
- `--no-substitutes`: Command argument. See examples for typical values.
- `--planes`: Include planes/modules as CSV. Example: --planes A-10C_2,FA-18C_hornet.
- `--replace`: For game import: start from an empty relation list instead of merging into the loaded profile.
- `--dry-run`: Plan mode only: do not click, launch, or change files; report what would run.
- `--no-map`: Command argument. See examples for typical values.

Examples:
```powershell
engine --help
```

### `engine relations from-text "<request>" [--games g1|g2] [--planes p1|p2] [--llm off|auto|on] [--substitutes] [--select <relation>]... [--skip-node game:id:plane1|plane2]... [--name <name>] [--min-confidence 0..1] [--dry-run]`
What it does: Runs this command operation.

Arguments and example values:
- `<request>`: Command argument. See examples for typical values.
- `--games`: Game filter values as CSV. Example: DCS,IL2Game,StarCitizen.
- `--planes`: Include planes/modules as CSV. Example: --planes A-10C_2,FA-18C_hornet.
- `--llm`: Command argument. See examples for typical values.
- `--substitutes`: Command argument. See examples for typical values.
- `--select`: Command argument. See examples for typical values.
- `<relation>`: Command argument. See examples for typical values.
- `--skip-node`: Command argument. See examples for typical values.
- `--name`: Name value for commands that create/merge entities. Example: --name 'Merged Startup'.
- `<name>`: Name value for surrounding option. Example: A-10C_2 or LEFT VPC Throttle.
- `--min-confidence`: Command argument. See examples for typical values.
- `--dry-run`: Plan mode only: do not click, launch, or change files; report what would run.

Examples:
```powershell
engine --help
```

### `engine relations from-text --check   (tests the vocabulary in DB/Functions/language.json against the catalog)`
What it does: Runs this command operation.

Arguments and example values:
- `--check`: Command argument. See examples for typical values.

Examples:
```powershell
engine --help
```

### `engine relations from-text "<request>" --show-prompt [--prompt-model <model>|local]`
What it does: Runs this command operation.

Arguments and example values:
- `<request>`: Command argument. See examples for typical values.
- `--show-prompt`: Command argument. See examples for typical values.
- `--prompt-model`: Command argument. See examples for typical values.
- `<model>`: Command argument. See examples for typical values.

Examples:
```powershell
engine --help
```

### `engine llm status|recommend [--ram-gb N] [--vram-gb N] [--gpu <name>] [--gpu-vendor nvidia|amd|intel] [--cores N] [--avx2 true|false]`
What it does: Runs this command operation.

Arguments and example values:
- `--ram-gb`: Command argument. See examples for typical values.
- `--vram-gb`: Command argument. See examples for typical values.
- `--gpu`: Command argument. See examples for typical values.
- `<name>`: Name value for surrounding option. Example: A-10C_2 or LEFT VPC Throttle.
- `--gpu-vendor`: Command argument. See examples for typical values.
- `--cores`: Command argument. See examples for typical values.
- `--avx2`: Command argument. See examples for typical values.

Examples:
```powershell
engine --help
```

### `engine llm download [<model>] --accept-license [--runtime auto|cpu|vulkan|cuda] [--no-runtime]`
What it does: Runs this command operation.

Arguments and example values:
- `<model>`: Command argument. See examples for typical values.
- `--accept-license`: Command argument. See examples for typical values.
- `--runtime`: Command argument. See examples for typical values.
- `--no-runtime`: Command argument. See examples for typical values.

Examples:
```powershell
engine --help
```

### `engine llm remove [<model>] [--all]   (--all also removes the runtime and switches the model off)`
What it does: Runs this command operation.

Arguments and example values:
- `<model>`: Command argument. See examples for typical values.
- `--all`: Apply operation to all matches (or select all relations/devices where supported).

Examples:
```powershell
engine --help
```

### `engine llm enable [<model>|local] | engine llm disable   (use the local model for relations from-text)`
What it does: Runs this command operation.

Arguments and example values:
- `<model>`: Command argument. See examples for typical values.

Examples:
```powershell
engine --help
```

### `engine llm cancel   (stops a running download; the next download resumes it)`
What it does: Runs this command operation.

Arguments and example values:
- No command-specific arguments.

Examples:
```powershell
engine --help
```

### `engine llm find-local [--root <folder>]...`
What it does: Runs this command operation.

Arguments and example values:
- `--root`: Command argument. See examples for typical values.
- `<folder>`: Command argument. See examples for typical values.

Examples:
```powershell
engine --help
```

### `engine llm use-local [--server <llama-server.exe>] [--model <file.gguf>] [--gpu-layers auto|all|N] [--context N] [--template server|chatml] | --clear`
What it does: Runs this command operation.

Arguments and example values:
- `--server`: Command argument. See examples for typical values.
- `<llama-server.exe>`: Command argument. See examples for typical values.
- `--model`: Command argument. See examples for typical values.
- `<file.gguf>`: Command argument. See examples for typical values.
- `--gpu-layers`: Command argument. See examples for typical values.
- `--context`: Command argument. See examples for typical values.
- `--template`: Command argument. See examples for typical values.
- `--clear`: Command argument. See examples for typical values.

Examples:
```powershell
engine --help
```

### `engine llm fake-server [--port N] [--port-file <path>] [--response <file>] [--serve <folder>] [--log <file>] [--lifetime <s>]`
What it does: Runs this command operation.

Arguments and example values:
- `--port`: Command argument. See examples for typical values.
- `--port-file`: Command argument. See examples for typical values.
- `<path>`: Filesystem path value. Example: C:\\JoyPro\\logs\\preview.png.
- `--response`: Command argument. See examples for typical values.
- `<file>`: Command argument. See examples for typical values.
- `--serve`: Command argument. See examples for typical values.
- `<folder>`: Command argument. See examples for typical values.
- `--log`: Command argument. See examples for typical values.
- `--lifetime`: Command argument. See examples for typical values.
- `<s>`: Command argument. See examples for typical values.

Examples:
```powershell
engine --help
```

### `engine setup devices [--grip <device>=<grip id>]... [--role <device|VID:PID>=<role>]... [--add-device <catalog id>]...`
What it does: Runs this command operation.

Arguments and example values:
- `--grip`: Command argument. See examples for typical values.
- `<device>`: Command argument. See examples for typical values.
- `<grip id>`: Command argument. See examples for typical values.
- `--role`: Command argument. See examples for typical values.
- `<device|VID:PID>`: Command argument. See examples for typical values.
- `<role>`: Command argument. See examples for typical values.
- `--add-device`: Command argument. See examples for typical values.
- `<catalog id>`: Command argument. See examples for typical values.

Examples:
```powershell
engine --help
```

### `engine setup aircraft --games <g1,g2>   (the aircraft / games for the priority list)`
What it does: Runs this command operation.

Arguments and example values:
- `--games`: Game filter values as CSV. Example: DCS,IL2Game,StarCitizen.
- `<g1,g2>`: Command argument. See examples for typical values.

Examples:
```powershell
engine --help
```

### `engine setup plan --games <g1,g2> [--priorities "A>B|C>D"] [--priority "B|C"]... [--modifiers 0..8] [--prefer-fewer|--use-all-modifiers]`
What it does: Runs this command operation.

Arguments and example values:
- `--games`: Game filter values as CSV. Example: DCS,IL2Game,StarCitizen.
- `<g1,g2>`: Command argument. See examples for typical values.
- `--priorities`: Command argument. See examples for typical values.
- `--priority`: Command argument. See examples for typical values.
- `--modifiers`: Command argument. See examples for typical values.
- `--prefer-fewer`: Command argument. See examples for typical values.
- `--use-all-modifiers`: Command argument. See examples for typical values.

Examples:
```powershell
engine --help
```

### `engine setup apply --plan <plan.json> [--dry-run] [--force]   (writes the plan's relations, binds and modifiers in one step)`
What it does: Runs this command operation.

Arguments and example values:
- `--plan`: Command argument. See examples for typical values.
- `<plan.json>`: Command argument. See examples for typical values.
- `--dry-run`: Plan mode only: do not click, launch, or change files; report what would run.
- `--force`: Command argument. See examples for typical values.

Examples:
```powershell
engine --help
```

### `engine setup render --plan <plan.json> --out-dir <folder> [--aircraft <name>] [--light] [--width <px>]   (a callout sheet per device)`
What it does: Runs this command operation.

Arguments and example values:
- `--plan`: Command argument. See examples for typical values.
- `<plan.json>`: Command argument. See examples for typical values.
- `--out-dir`: Command argument. See examples for typical values.
- `<folder>`: Command argument. See examples for typical values.
- `--aircraft`: MSFS export only: also write the relations limited to this aircraft (key from DB\\MSFS\\aircraft.tsv); the sim's presets apply to every aircraft.
- `<name>`: Name value for surrounding option. Example: A-10C_2 or LEFT VPC Throttle.
- `--light`: Command argument. See examples for typical values.
- `--width`: Command argument. See examples for typical values.
- `<px>`: Command argument. See examples for typical values.

Examples:
```powershell
engine --help
```

### `engine relations auto-assign [--grip <device|VID:PID>=<grip id>]... [--add-device <catalog id>]... [--ignore-device <device>]... [--relation <name>]... [--max-modifiers 0|1|2] [--overwrite [--include-missing]] [--no-style] [--explain <relation>]... [--dry-run]`
What it does: Runs this command operation.

Arguments and example values:
- `--grip`: Command argument. See examples for typical values.
- `<device|VID:PID>`: Command argument. See examples for typical values.
- `<grip id>`: Command argument. See examples for typical values.
- `--add-device`: Command argument. See examples for typical values.
- `<catalog id>`: Command argument. See examples for typical values.
- `--ignore-device`: Exclude one device per flag. Example: --ignore-device Keyboard.
- `<device>`: Command argument. See examples for typical values.
- `--relation`: Select one relation per flag for mass operations. Example: --relation 'Trim Pitch'.
- `<name>`: Name value for surrounding option. Example: A-10C_2 or LEFT VPC Throttle.
- `--max-modifiers`: Command argument. See examples for typical values.
- `--overwrite`: Command argument. See examples for typical values.
- `--include-missing`: Command argument. See examples for typical values.
- `--no-style`: Command argument. See examples for typical values.
- `--explain`: Command argument. See examples for typical values.
- `<relation>`: Command argument. See examples for typical values.
- `--dry-run`: Plan mode only: do not click, launch, or change files; report what would run.

Examples:
```powershell
engine --help
```

### `engine devices kb [list [query] | show <id> | identify [--grip <device>=<grip id>]... [--add-device <id>]...]   (device catalog for Auto Assign)`
What it does: Runs this command operation.

Arguments and example values:
- `<id>`: Command argument. See examples for typical values.
- `--grip`: Command argument. See examples for typical values.
- `<device>`: Command argument. See examples for typical values.
- `<grip id>`: Command argument. See examples for typical values.
- `--add-device`: Command argument. See examples for typical values.

Examples:
```powershell
engine --help
```

### `engine devices reference [list [query] | identify --vidpid <VID:PID> --name <HID name> [--instance <name>] [--buttons <n>] [--hats <n>] [--axes X,Y,RZ] | show <id> [--base <id>] [--svg <file>] [--light] [--no-badges] | check [query] | photos [query] [--coverage] | photo-add --model <id> --url <https photo on the maker's host> --view <front|side|back|top|base|three-quarter|sheet> [--page <url>] [--confidence <c>] [--shows a,b] [--sha256 <hex>] | layout-check | drawings [query] [--check] [--model <id> [--view <v>] --svg <file> | --png <file>] [--light] [--drawing-style lines|cel] [--drawing-preset <p>]]   (device reference pictures: catalog, identification and the question to ask, JoyPro's schematic with markers, photo references, curated marker positions, JoyPro's own drawings of the devices)`
What it does: Runs this command operation.

Arguments and example values:
- `--vidpid`: Command argument. See examples for typical values.
- `<VID:PID>`: Command argument. See examples for typical values.
- `--name`: Name value for commands that create/merge entities. Example: --name 'Merged Startup'.
- `<HID name>`: Command argument. See examples for typical values.
- `--instance`: Command argument. See examples for typical values.
- `<name>`: Name value for surrounding option. Example: A-10C_2 or LEFT VPC Throttle.
- `--buttons`: Command argument. See examples for typical values.
- `<n>`: Integer numeric value. Example: 45.
- `--hats`: Command argument. See examples for typical values.
- `--axes`: Command argument. See examples for typical values.
- `<id>`: Command argument. See examples for typical values.
- `--base`: Command argument. See examples for typical values.
- `--svg`: Command argument. See examples for typical values.
- `<file>`: Command argument. See examples for typical values.
- `--light`: Command argument. See examples for typical values.
- `--no-badges`: Command argument. See examples for typical values.
- `--coverage`: Command argument. See examples for typical values.
- `--model`: Command argument. See examples for typical values.
- `--url`: Command argument. See examples for typical values.
- `<https photo on the maker's host>`: Command argument. See examples for typical values.
- `--view`: Command argument. See examples for typical values.
- `<front|side|back|top|base|three-quarter|sheet>`: Command argument. See examples for typical values.
- `--page`: Command argument. See examples for typical values.
- `<url>`: Command argument. See examples for typical values.
- `--confidence`: Command argument. See examples for typical values.
- `<c>`: Command argument. See examples for typical values.
- `--shows`: Command argument. See examples for typical values.
- `--sha256`: Command argument. See examples for typical values.
- `<hex>`: Command argument. See examples for typical values.
- `--check`: Command argument. See examples for typical values.
- `<v>`: Command argument. See examples for typical values.
- `--png`: Command argument. See examples for typical values.
- `--drawing-style`: Command argument. See examples for typical values.
- `--drawing-preset`: Command argument. See examples for typical values.
- `<p>`: Command argument. See examples for typical values.

Examples:
```powershell
engine --help
```

### `engine cockpits [list [query] [--game G] [--era E] [--role R] [--nation N] [--category fixed-wing|helicopter|space|fiction|real] | search <text> [--limit n] | get <aircraft> [<variant>] [--json] | check | render <aircraft> [<variant>] --png <file> [--device <d>] [--style dark|light] [--print-width <px>] [--drawing-only] | compare <aircraft> [<variant>] [--game G] | photos [query] [--coverage] | coverage | drawings | pictures build [--game DCS|XPlane12|MSFS2020|IL2Game|NuclearOption|all] [--aircraft <name>,...] [--force] [--out <folder>] | pictures status | pictures check [--game G] [--aircraft <name>,...] [--write] [--drop]]`
What it does: Runs this command operation.

Arguments and example values:
- `--game`: Game filter value. Example: DCS or IL2Game.
- `--era`: Command argument. See examples for typical values.
- `--role`: Command argument. See examples for typical values.
- `--nation`: Command argument. See examples for typical values.
- `--category`: Command argument. See examples for typical values.
- `<text>`: Command argument. See examples for typical values.
- `--limit`: Command argument. See examples for typical values.
- `<aircraft>`: Command argument. See examples for typical values.
- `<variant>`: Command argument. See examples for typical values.
- `--json`: Command argument. See examples for typical values.
- `--png`: Command argument. See examples for typical values.
- `<file>`: Command argument. See examples for typical values.
- `--device`: Include one device per flag. Example: --device LEFT VPC Throttle.
- `<d>`: Command argument. See examples for typical values.
- `--style`: Command argument. See examples for typical values.
- `--print-width`: Command argument. See examples for typical values.
- `<px>`: Command argument. See examples for typical values.
- `--drawing-only`: Command argument. See examples for typical values.
- `--coverage`: Command argument. See examples for typical values.
- `--aircraft`: MSFS export only: also write the relations limited to this aircraft (key from DB\\MSFS\\aircraft.tsv); the sim's presets apply to every aircraft.
- `<name>`: Name value for surrounding option. Example: A-10C_2 or LEFT VPC Throttle.
- `--force`: Command argument. See examples for typical values.
- `--out`: Global option: write result output to file path instead of terminal.
- `<folder>`: Command argument. See examples for typical values.
- `--write`: Command argument. See examples for typical values.
- `--drop`: Command argument. See examples for typical values.

Examples:
```powershell
engine --help
```

### `engine binds list [filter]`
What it does: Lists bind entries (hardware mapping layer).

Arguments and example values:
- `filter`: Text filter (case-insensitive substring). Example values: 'A-10', 'Hornet', 'VKB'.

Examples:
```powershell
engine binds list 'Throttle' --text
```

### `engine binds show <bind-or-relation>`
What it does: Shows one bind in detail.

Arguments and example values:
- `<bind-or-relation>`: Relation name or bind selector. Example: Trim Pitch.

Examples:
```powershell
engine binds show 'Gear Toggle' --text
```

### `engine binds set <bind-or-relation> [--device <name>] [--input <value>] [--format dcs|il2] [--modifier <name>] [--clear-modifiers] [--clear-input] [--inverted true|false] [--slider true|false] [--curvature <csv>] [--deadzone <value>] [--saturation-x <value>] [--saturation-y <value>] [--double-tap true|false]`
What it does: Creates or updates one relation bind (device/input/modifiers and optional tuning values).

Arguments and example values:
- `<bind-or-relation>`: Relation name or bind selector. Example: Trim Pitch.
- `--device`: Include one device per flag. Example: --device LEFT VPC Throttle.
- `<name>`: Name value for surrounding option. Example: A-10C_2 or LEFT VPC Throttle.
- `--input`: Input token. Example: --input JOY_BTN1 or --input joy0_b0.
- `<value>`: Generic value placeholder. Example for --input: JOY_BTN1 or joy0_b0.
- `--format`: Format selector. Example values: dcs/il2 for binds unbind, rl/json for relations mass export.
- `--modifier`: Modifier key token. Example: --modifier LShift.
- `--clear-modifiers`: Clears all modifiers from the selected bind.
- `--clear-input`: Clears bind input token(s) (button/axis) while keeping the bind object.
- `--inverted`: Set axis inversion flag on selected bind (true/false).
- `--slider`: Set axis slider flag on selected bind (true/false).
- `--curvature`: Set curvature list. Example: --curvature 0.1,0.2,0.3.
- `<csv>`: Comma-separated values. Example: 0.1,0.2,0.3 or VKB,LEFT VPC Throttle.
- `--deadzone`: Set deadzone numeric value. Example: --deadzone 0.15.
- `--saturation-x`: Set saturation X numeric value. Example: --saturation-x 0.9.
- `--saturation-y`: Set saturation Y numeric value. Example: --saturation-y 0.8.
- `--double-tap`: Set double-tap behavior flag (true/false).

How arguments interact:
- If relation has no bind yet, this command creates one.
- Axis tuning options apply only to axis relations.
- --format is only meaningful when --input is provided.

Examples:
```powershell
engine binds set 'Gear Toggle' --device 'VKB Gladiator NXT' --input JOY_BTN1 --text
engine binds set 'Pitch Axis' --input JOY_Y --inverted true --curvature 0.1,0.2 --deadzone 0.05 --saturation-x 0.9 --saturation-y 0.9 --text
```

### `engine binds clear <bind-or-relation> [--input-only]`
What it does: Unbinds one relation (remove bind entirely or clear only input token).

Arguments and example values:
- `<bind-or-relation>`: Relation name or bind selector. Example: Trim Pitch.
- `--input-only`: For binds clear, clear only input tokens instead of removing whole bind.

How arguments interact:
- Default behavior removes the entire bind from that relation.
- Use --input-only to keep bind metadata and clear only button/axis input.

Examples:
```powershell
engine binds clear 'Gear Toggle' --text
engine binds clear 'Gear Toggle' --input-only --text
```

### `engine binds modifier list [bind-or-relation]`
What it does: Lists modifiers on one bind, or all modifiers across all binds.

Arguments and example values:
- No command-specific arguments.

How arguments interact:
- Without a bind/relation selector, lists all modifier names and usage counts.

Examples:
```powershell
engine binds modifier list --text
engine binds modifier list 'Gear Toggle' --text
```

### `engine binds modifier add <bind-or-relation> <mod1> [mod2] [...]`
What it does: Adds one or more modifiers to a bind.

Arguments and example values:
- `<bind-or-relation>`: Relation name or bind selector. Example: Trim Pitch.
- `<mod1>`: First modifier key. Example: LShift.

Examples:
```powershell
engine binds modifier add 'Gear Toggle' LShift RCtrl --text
```

### `engine binds modifier remove <bind-or-relation> <mod1> [mod2] [...] [--all]`
What it does: Removes one or more modifiers (or all) from a bind.

Arguments and example values:
- `<bind-or-relation>`: Relation name or bind selector. Example: Trim Pitch.
- `<mod1>`: First modifier key. Example: LShift.
- `--all`: Apply operation to all matches (or select all relations/devices where supported).

How arguments interact:
- Use --all to clear all values instead of specifying individual names.

Examples:
```powershell
engine binds modifier remove 'Gear Toggle' LShift --text
engine binds modifier remove 'Gear Toggle' --all --text
```

### `engine binds modifier rename <old-modifier> <new-modifier> [bind-or-relation]`
What it does: Renames a modifier across all binds, or only one selected bind.

Arguments and example values:
- `<old-modifier>`: Existing modifier name to rename. Example: LAlt.
- `<new-modifier>`: New modifier name to apply. Example: LAlt_Hold.

How arguments interact:
- If you omit bind/relation selector, rename is applied across all binds.

Examples:
```powershell
engine binds modifier rename 'LAlt' 'LAlt_Hold' --text
engine binds modifier rename 'LAlt' 'LAlt_Hold' 'Gear Toggle' --text
```

### `engine ffb models`
What it does: Lists the games whose force feedback settings JoyPro manages and their settings (DCS ffDiffs per device and aircraft, IL-2 startup.cfg game-wide).

Arguments and example values:
- No command-specific arguments.

Examples:
```powershell
engine ffb models --text
```

### `engine ffb show [--device <device>] [--game <game>] [--plane <plane>]`
What it does: Lists the stored force feedback settings; with --device and --game, what that device gets in the game (and aircraft).

Arguments and example values:
- `--device`: Include one device per flag. Example: --device LEFT VPC Throttle.
- `<device>`: Command argument. See examples for typical values.
- `--game`: Game filter value. Example: DCS or IL2Game.
- `<game>`: Command argument. See examples for typical values.
- `--plane`: Include one plane/module per flag. Example: --plane A-10C_2.
- `<plane>`: Command argument. See examples for typical values.

Examples:
```powershell
engine ffb show --device 'VPforce Rhino FFB Joystick {...}' --game DCS --plane F-16C_50 --text
```

### `engine ffb set --device <device>|* --game <game> [--plane <plane>] --param <name>=<value>...`
What it does: Runs this command operation.

Arguments and example values:
- `--device`: Include one device per flag. Example: --device LEFT VPC Throttle.
- `<device>`: Command argument. See examples for typical values.
- `--game`: Game filter value. Example: DCS or IL2Game.
- `<game>`: Command argument. See examples for typical values.
- `--plane`: Include one plane/module per flag. Example: --plane A-10C_2.
- `<plane>`: Command argument. See examples for typical values.
- `--param`: A response model parameter as name=value, repeatable. Example: --param sensitivity=22 --param offset=10.
- `<name>`: Name value for surrounding option. Example: A-10C_2 or LEFT VPC Throttle.
- `<value>`: Generic value placeholder. Example for --input: JOY_BTN1 or joy0_b0.

Examples:
```powershell
engine --help
```

### `engine ffb clear --device <device>|* --game <game> [--plane <plane>] [--param <name>]...`
What it does: Runs this command operation.

Arguments and example values:
- `--device`: Include one device per flag. Example: --device LEFT VPC Throttle.
- `<device>`: Command argument. See examples for typical values.
- `--game`: Game filter value. Example: DCS or IL2Game.
- `<game>`: Command argument. See examples for typical values.
- `--plane`: Include one plane/module per flag. Example: --plane A-10C_2.
- `<plane>`: Command argument. See examples for typical values.
- `--param`: A response model parameter as name=value, repeatable. Example: --param sensitivity=22 --param offset=10.
- `<name>`: Name value for surrounding option. Example: A-10C_2 or LEFT VPC Throttle.

Examples:
```powershell
engine --help
```

### `engine ffb telemetry status   (JoyPro's telemetry force feedback: settings, game setup, process, last status)`
What it does: JoyPro's own telemetry force feedback (JoyPro.Ffb): the settings, per game whether its telemetry is set up and the game runs, the JoyPro.Ffb process and its last status line.

Arguments and example values:
- No command-specific arguments.

Examples:
```powershell
engine ffb telemetry status --text
```

### `engine ffb telemetry effects`
What it does: Lists the effect names of the built-in force feedback graph (buffet, gforce, gear, flaps, speedbrake, runway, guns, hits, shake, spring) that the per-game switches refer to.

Arguments and example values:
- No command-specific arguments.

Examples:
```powershell
engine ffb telemetry effects --text
```

### `engine ffb telemetry config show`
What it does: Shows the telemetry force feedback settings file (<data>\Ffb\Telemetry\telemetry-ffb.json) with every default filled in.

Arguments and example values:
- No command-specific arguments.

Examples:
```powershell
engine ffb telemetry config show --text
```

### `engine ffb telemetry config set [--game DCS|IL2Game|IL2Korea|WarThunder] [--plane <aircraft>] [--effect <name>] | [--device {guid}] --param <key>=<value>...`
What it does: Sets telemetry force feedback settings: global (enabled, device, strength, max-force = the hard force cap, invert-x/y, tick-rate, timeout), per game (enabled, port, graph, strength, url, poll-hz), per aircraft (graph, strength) or per effect (enabled, strength).

Arguments and example values:
- `--game`: Game filter value. Example: DCS or IL2Game.
- `--plane`: Include one plane/module per flag. Example: --plane A-10C_2.
- `<aircraft>`: Command argument. See examples for typical values.
- `--effect`: Command argument. See examples for typical values.
- `<name>`: Name value for surrounding option. Example: A-10C_2 or LEFT VPC Throttle.
- `--device`: Include one device per flag. Example: --device LEFT VPC Throttle.
- `--param`: A response model parameter as name=value, repeatable. Example: --param sensitivity=22 --param offset=10.
- `<key>`: Command argument. See examples for typical values.
- `<value>`: Generic value placeholder. Example for --input: JOY_BTN1 or joy0_b0.

Examples:
```powershell
engine ffb telemetry config set --param enabled=true --param max-force=0.5 --text
engine ffb telemetry config set --game DCS --effect buffet --param strength=0.7 --text
engine ffb telemetry config set --game DCS --plane F-16C_50 --param graph=viper.jpffb.json --text
```

### `engine ffb telemetry config clear [--game <game>] [--plane <aircraft>] [--effect <name>] | [--device {guid}]`
What it does: Resets a scope of the telemetry force feedback settings to the defaults (a game, an aircraft, an effect, or everything).

Arguments and example values:
- `--game`: Game filter value. Example: DCS or IL2Game.
- `<game>`: Command argument. See examples for typical values.
- `--plane`: Include one plane/module per flag. Example: --plane A-10C_2.
- `<aircraft>`: Command argument. See examples for typical values.
- `--effect`: Command argument. See examples for typical values.
- `<name>`: Name value for surrounding option. Example: A-10C_2 or LEFT VPC Throttle.
- `--device`: Include one device per flag. Example: --device LEFT VPC Throttle.

Examples:
```powershell
engine ffb telemetry config clear --game DCS --plane F-16C_50 --text
```

### `engine ffb telemetry setup dcs|il2|il2korea|warthunder [--remove] [--dry-run] [--path <Saved Games\DCS | IL-2 folder | startup.cfg>]   (DCS Hooks script / IL-2 startup.cfg telemetry target; backed up)`
What it does: Sets a game up to send telemetry to JoyPro: DCS gets Saved Games\DCS*\Scripts\Hooks\JoyProFfb.lua (Export.lua is not touched), IL-2 one addrN line in [KEY = telemetrydevice] of data\startup.cfg; War Thunder needs nothing. Backed up; --remove undoes it; --dry-run lists the changes.

Arguments and example values:
- `--remove`: Command argument. See examples for typical values.
- `--dry-run`: Plan mode only: do not click, launch, or change files; report what would run.
- `--path`: Optional explicit path override. Use when you prefer option form over positional path.
- `<Saved Games\DCS | IL-2 folder | startup.cfg>`: Command argument. See examples for typical values.

Examples:
```powershell
engine ffb telemetry setup dcs --dry-run --text
engine ffb telemetry setup il2 --text
engine ffb telemetry setup il2 --remove --text
```

### `engine telemetry games   (telemetry recording, docs/telemetry-recording.md: the games, what JoyPro listens to, ACMI possible)`
What it does: Runs this command operation.

Arguments and example values:
- No command-specific arguments.

Examples:
```powershell
engine --help
```

### `engine telemetry config show`
What it does: Runs this command operation.

Arguments and example values:
- No command-specific arguments.

Examples:
```powershell
engine --help
```

### `engine telemetry config set [--game <game>] --param enabled|folder|max-total-mb|keep-days|sample-hz|keep-raw|session-timeout=<value>...`
What it does: Runs this command operation.

Arguments and example values:
- `--game`: Game filter value. Example: DCS or IL2Game.
- `<game>`: Command argument. See examples for typical values.
- `--param`: A response model parameter as name=value, repeatable. Example: --param sensitivity=22 --param offset=10.
- `<value>`: Generic value placeholder. Example for --input: JOY_BTN1 or joy0_b0.

Examples:
```powershell
engine --help
```

### `engine telemetry sessions [--game <game>] [--folder <folder>]   (recorded sessions, newest first)`
What it does: Runs this command operation.

Arguments and example values:
- `--game`: Game filter value. Example: DCS or IL2Game.
- `<game>`: Command argument. See examples for typical values.
- `--folder`: Command argument. See examples for typical values.
- `<folder>`: Command argument. See examples for typical values.

Examples:
```powershell
engine --help
```

### `engine telemetry show <session> [--folder <folder>]   (<session> = path, file name, latest or a number of the list)`
What it does: Runs this command operation.

Arguments and example values:
- `<session>`: Command argument. See examples for typical values.
- `--folder`: Command argument. See examples for typical values.
- `<folder>`: Command argument. See examples for typical values.

Examples:
```powershell
engine --help
```

### `engine telemetry export <session> --format acmi|acmi-zip|csv [--to <file or folder>] [--max-hz <n>] [--no-per-channel]   (Tacview ACMI 2.2 / CSV)`
What it does: Writes the profile's binds for this game. Only the flight actions JoyPro manages are changed, the game must be closed, and the file is backed up to <file>.jpbak first.

Arguments and example values:
- `<session>`: Command argument. See examples for typical values.
- `--format`: Format selector. Example values: dcs/il2 for binds unbind, rl/json for relations mass export.
- `--to`: Command argument. See examples for typical values.
- `<file or folder>`: Command argument. See examples for typical values.
- `--max-hz`: Command argument. See examples for typical values.
- `<n>`: Integer numeric value. Example: 45.
- `--no-per-channel`: Command argument. See examples for typical values.

Examples:
```powershell
engine --profile 'C:\JoyPro\profiles\pilot_main.pr0file' telemetry export --dry-run --text engine --profile 'C:\JoyPro\profiles\pilot_main.pr0file' telemetry export --mode merge-overwrite --text
```

### `engine telemetry preview <session> [--channel <key>]... [--points <n>]`
What it does: Runs this command operation.

Arguments and example values:
- `<session>`: Command argument. See examples for typical values.
- `--channel`: Command argument. See examples for typical values.
- `<key>`: Command argument. See examples for typical values.
- `--points`: Number of preview samples from -1 to 1. Example: --points 21.
- `<n>`: Integer numeric value. Example: 45.

Examples:
```powershell
engine --help
```

### `engine telemetry delete <session>...`
What it does: Runs this command operation.

Arguments and example values:
- `<session>`: Command argument. See examples for typical values.

Examples:
```powershell
engine --help
```

### `engine telemetry recover | prune   (finish sessions cut off by a crash | apply the retention settings)`
What it does: Runs this command operation.

Arguments and example values:
- No command-specific arguments.

Examples:
```powershell
engine --help
```

### `engine telemetry record --game <game> [--seconds <n>] [--folder <folder>] [--sample-hz <n>] [--no-raw] [--no-game-check]   (records now; Ctrl+C ends it)`
What it does: Runs this command operation.

Arguments and example values:
- `--game`: Game filter value. Example: DCS or IL2Game.
- `<game>`: Command argument. See examples for typical values.
- `--seconds`: Command argument. See examples for typical values.
- `<n>`: Integer numeric value. Example: 45.
- `--folder`: Command argument. See examples for typical values.
- `<folder>`: Command argument. See examples for typical values.
- `--sample-hz`: Command argument. See examples for typical values.
- `--no-raw`: Command argument. See examples for typical values.
- `--no-game-check`: Command argument. See examples for typical values.

Examples:
```powershell
engine --help
```

### `engine binds tuning models`
What it does: Lists the axis response models (DCS, IL-2 Korea, generic) and their parameters.

Arguments and example values:
- No command-specific arguments.

Examples:
```powershell
engine binds tuning models --text
```

### `engine binds tuning show <bind-or-relation> [--game <game>] [--plane <plane>]`
What it does: Shows the axis tuning of a bind per scope (base, game, plane, family) and, with --game, what that game (and plane) uses.

Arguments and example values:
- `<bind-or-relation>`: Relation name or bind selector. Example: Trim Pitch.
- `--game`: Game filter value. Example: DCS or IL2Game.
- `<game>`: Command argument. See examples for typical values.
- `--plane`: Include one plane/module per flag. Example: --plane A-10C_2.
- `<plane>`: Command argument. See examples for typical values.

Examples:
```powershell
engine binds tuning show 'Pitch Axis' --game IL2Korea --text
```

### `engine binds tuning set <bind-or-relation> --scope base|game:<game>|plane:<game>:<plane>|family:<family> [--param <name>=<value>]... [--inverted true|false] [--slider true|false] [--deadzone <value>] [--saturation-x <value>] [--saturation-y <value>] [--curvature <csv>] [--curve-points <x:y,...>|none]`
What it does: Sets axis tuning for one scope: base (all games), game:<game>, plane:<game>:<plane> or family:<family>. A game without its own tuning gets the base curve fitted to its model.

Arguments and example values:
- `<bind-or-relation>`: Relation name or bind selector. Example: Trim Pitch.
- `--scope`: Axis tuning scope: base, game:<game>, plane:<game>:<plane> or family:<family>. Example: --scope game:IL2Korea.
- `<game>`: Command argument. See examples for typical values.
- `<plane>`: Command argument. See examples for typical values.
- `<family>`: Command argument. See examples for typical values.
- `--param`: A response model parameter as name=value, repeatable. Example: --param sensitivity=22 --param offset=10.
- `<name>`: Name value for surrounding option. Example: A-10C_2 or LEFT VPC Throttle.
- `<value>`: Generic value placeholder. Example for --input: JOY_BTN1 or joy0_b0.
- `--inverted`: Set axis inversion flag on selected bind (true/false).
- `--slider`: Set axis slider flag on selected bind (true/false).
- `--deadzone`: Set deadzone numeric value. Example: --deadzone 0.15.
- `--saturation-x`: Set saturation X numeric value. Example: --saturation-x 0.9.
- `--saturation-y`: Set saturation Y numeric value. Example: --saturation-y 0.8.
- `--curvature`: Set curvature list. Example: --curvature 0.1,0.2,0.3.
- `<csv>`: Comma-separated values. Example: 0.1,0.2,0.3 or VKB,LEFT VPC Throttle.
- `--curve-points`: A curve with any number of stops (x:y pairs in 0..1, x increasing), or none to drop them (X-Plane's response curve). Example: --curve-points 0:0,0.4:0.3,1:1.
- `<x:y,...>`: Command argument. See examples for typical values.

Examples:
```powershell
engine binds tuning set 'Pitch Axis' --scope game:IL2Korea --param sensitivity=22 --param deadZone0=3 --text
engine binds tuning set 'Pitch Axis' --scope plane:DCS:F-16C_50 --curvature 0.2 --deadzone 0.02 --text
```

### `engine binds tuning clear <bind-or-relation> --scope game:<game>|plane:<game>:<plane>|family:<family>`
What it does: Removes the tuning of one scope; the scope inherits again.

Arguments and example values:
- `<bind-or-relation>`: Relation name or bind selector. Example: Trim Pitch.
- `--scope`: Axis tuning scope: base, game:<game>, plane:<game>:<plane> or family:<family>. Example: --scope game:IL2Korea.
- `<game>`: Command argument. See examples for typical values.
- `<plane>`: Command argument. See examples for typical values.
- `<family>`: Command argument. See examples for typical values.

Examples:
```powershell
engine binds tuning clear 'Pitch Axis' --scope game:IL2Korea --text
```

### `engine binds tuning preview <bind-or-relation> --game <game> [--plane <plane>] [--points <n>]`
What it does: Prints the response curve a game (and plane) applies to the bind: stick position -> what the game uses.

Arguments and example values:
- `<bind-or-relation>`: Relation name or bind selector. Example: Trim Pitch.
- `--game`: Game filter value. Example: DCS or IL2Game.
- `<game>`: Command argument. See examples for typical values.
- `--plane`: Include one plane/module per flag. Example: --plane A-10C_2.
- `<plane>`: Command argument. See examples for typical values.
- `--points`: Number of preview samples from -1 to 1. Example: --points 21.
- `<n>`: Integer numeric value. Example: 45.

Examples:
```powershell
engine binds tuning preview 'Pitch Axis' --game DCS --plane F-16C_50 --points 11 --text
```

### `engine binds detent show <bind-or-relation>   (detent mode: settings, the device axis calibration, the aircraft grouped by afterburner start, per game the curve it gets and how exactly)`
What it does: Detent mode of a throttle relation: its settings, the calibration of the bound device axis, the aircraft grouped by where their afterburner starts (game data or the relation's own value) and per game the curve it gets and how exactly the game expresses it.

Arguments and example values:
- `<bind-or-relation>`: Relation name or bind selector. Example: Trim Pitch.

Examples:
```powershell
engine binds detent show 'Throttle' --text
```

### `engine binds detent set <bind-or-relation> [--on|--off] [--no-ab full|linear] [--unknown full|linear] [--margin <0..0.1>] [--target <game>:<plane>=ab:<0..1>|full|linear|default]...`
What it does: Switches detent mode on or off, sets what aircraft without afterburner get (full power at the detent or the relation's curve), the margin below the afterburner, and an aircraft's own afterburner start (--target <game>:<plane>=ab:0.78|full|linear|default).

Arguments and example values:
- `<bind-or-relation>`: Relation name or bind selector. Example: Trim Pitch.
- `--on`: Command argument. See examples for typical values.
- `--off`: Command argument. See examples for typical values.
- `--no-ab`: Command argument. See examples for typical values.
- `--unknown`: Command argument. See examples for typical values.
- `--margin`: Command argument. See examples for typical values.
- `<0..0.1>`: Command argument. See examples for typical values.
- `--target`: Command argument. See examples for typical values.
- `<game>`: Command argument. See examples for typical values.
- `<plane>`: Command argument. See examples for typical values.
- `<0..1>`: Command argument. See examples for typical values.

Examples:
```powershell
engine binds detent set 'Throttle' --on --no-ab full --text
engine binds detent set 'Throttle' --target DCS:F-16C_50=ab:0.775 --target DCS:A-10C=full --text
```

### `engine binds detent calibrate <bind-or-relation> --idle <0..1> --detent <0..1> [--full <0..1>|none] [--cutoff <0..1>|none] [--source wizard|manual]   (raw axis positions of the bound device axis)`
What it does: Stores where the bound device axis has its idle, afterburner detent, full and cut-off positions (raw axis 0..1; the GUI's wizard reads them live). Every throttle relation on that axis uses it.

Arguments and example values:
- `<bind-or-relation>`: Relation name or bind selector. Example: Trim Pitch.
- `--idle`: Command argument. See examples for typical values.
- `<0..1>`: Command argument. See examples for typical values.
- `--detent`: Command argument. See examples for typical values.
- `--full`: Command argument. See examples for typical values.
- `--cutoff`: Command argument. See examples for typical values.
- `--source`: Command argument. See examples for typical values.

Examples:
```powershell
engine binds detent calibrate 'Throttle' --idle 0.02 --detent 0.79 --full none --cutoff none --source manual --text
```

### `engine binds detent uncalibrate <bind-or-relation>`
What it does: Removes the detent calibration of the bound device axis.

Arguments and example values:
- `<bind-or-relation>`: Relation name or bind selector. Example: Trim Pitch.

Examples:
```powershell
engine binds detent uncalibrate 'Throttle' --text
```

### `engine binds detent gates [--game <game>] [--plane <plane>]   (the shipped throttle gate data: afterburner start per aircraft, source, confidence)`
What it does: Lists the shipped throttle gate data (DB\Detents\throttle-gates.json): per aircraft where its afterburner and idle cut-off are on the game's throttle axis, with source and confidence.

Arguments and example values:
- `--game`: Game filter value. Example: DCS or IL2Game.
- `<game>`: Command argument. See examples for typical values.
- `--plane`: Include one plane/module per flag. Example: --plane A-10C_2.
- `<plane>`: Command argument. See examples for typical values.

Examples:
```powershell
engine binds detent gates --game DCS --text
engine binds detent gates --game DCS --plane F-14 --text
```

### `engine binds detent merge [--relations <csv>] [--into <relation>] [--ab-source profile|game] [--apply]   (per-aircraft throttle relations of one device axis into one relation in detent mode; without --apply only the plan)`
What it does: Merges per-aircraft throttle relations of one device axis into one relation in detent mode: the device's detent and each aircraft's afterburner start are read from the old curves. Without --apply only the plan; profile history undo takes a merge back.

Arguments and example values:
- `--relations`: Select relations as CSV for mass operations. Example: --relations 'Trim Pitch,Trim Roll'.
- `<csv>`: Comma-separated values. Example: 0.1,0.2,0.3 or VKB,LEFT VPC Throttle.
- `--into`: Command argument. See examples for typical values.
- `<relation>`: Command argument. See examples for typical values.
- `--ab-source`: Command argument. See examples for typical values.
- `--apply`: Command argument. See examples for typical values.

Examples:
```powershell
engine binds detent merge --text
engine binds detent merge --relations 'ThrottleLeft,ThrottleLeft29,ThrottleLeftNonAB' --into ThrottleLeft --apply --text
```

### `engine binds unbind <device::input|input> [--device <name>] [--input <value>] [--format dcs|il2] [--game DCS|IL2Game] [--all]`
What it does: Removes bind links by input token (DCS or IL2 format).

Arguments and example values:
- `<device::input|input>`: Input selector. Example: VKB Gladiator NXT::JOY_BTN1 or JOY_BTN1.
- `--device`: Include one device per flag. Example: --device LEFT VPC Throttle.
- `<name>`: Name value for surrounding option. Example: A-10C_2 or LEFT VPC Throttle.
- `--input`: Input token. Example: --input JOY_BTN1 or --input joy0_b0.
- `<value>`: Generic value placeholder. Example for --input: JOY_BTN1 or joy0_b0.
- `--format`: Format selector. Example values: dcs/il2 for binds unbind, rl/json for relations mass export.
- `--game`: Game filter value. Example: DCS or IL2Game.
- `--all`: Apply operation to all matches (or select all relations/devices where supported).

How arguments interact:
- Positional value supports device::input shorthand.
- If matches are ambiguous, use --all or narrow with --device / --game.

Examples:
```powershell
engine binds unbind 'VKB Gladiator NXT::JOY_BTN1' --format dcs --text
engine binds unbind --input joy0_b0 --format il2 --all --text
```

### `engine binds export <path.json>`
What it does: Exports binds to JSON.

Arguments and example values:
- `<path.json>`: Path to JSON file. Example: C:\\JoyPro\\exports\\relations.json.

Examples:
```powershell
engine binds export 'C:\JoyPro\exports\binds.json'
```

### `engine binds import <path.json>`
What it does: Imports binds from JSON.

Arguments and example values:
- `<path.json>`: Path to JSON file. Example: C:\\JoyPro\\exports\\relations.json.

Examples:
```powershell
engine binds import 'C:\JoyPro\exports\binds.json'
```

### `engine binds edit   (no args; exports temp binds JSON, opens editor, re-imports on close; uses --editor or notepad)`
What it does: Opens binds JSON in an external editor for round-trip editing.

Arguments and example values:
- `--editor`: Global option: editor command for relations edit and binds edit (use {file} placeholder).

Examples:
```powershell
engine --editor 'code --wait {file}' binds edit
```


## How Output Works

- Default output is JSON (script/automation friendly).
- Add `--text` for human-readable terminal output.

Use `--text` when you are manually checking results.
Use default JSON when piping into tools or scripts.

## Load Correction Mechanics

JoyPro Engine can apply load-time correction mechanics while loading profiles.

Default behavior:
- `--load-corrections true` (default)
- `--auto-add-db-items true` (default)
- `--remap-missing-devices true` (default)
- `--confirm-device-remap true` (default)
- `--correction-mode balanced` (default)
- `--correction-auto-threshold` unset (mode-based default)
- `--confirm-corrections true` (default)

What this does:
- For active planes only, checks each relation node against DB data during profile load.
- If the same node ID still exists for that plane, only metadata is refreshed (title/category) and no remap scoring is used.
- If the node ID is missing for that active plane, tries scored replacement candidates.
- Repairs modifier reformer strings from known modifier key combos.
- Normalizes joystick naming and cleans stale joystick metadata maps.
- Detects devices referenced by binds/modifiers that are not currently connected and suggests closest replacements.
- Uses confidence scoring for ID/name drift recovery and records every correction decision.
- Uses plane-family alias fallback only when the original plane no longer exists in current DB data.

How to disable globally for one run:
```powershell
engine --load-corrections false summary
```

How to keep corrections but auto-accept device remaps (non-interactive):
```powershell
engine --confirm-device-remap false profile load "C:\profiles\my_setup.pr0file"
```

How to change correction strictness:
```powershell
engine --correction-mode strict summary
engine --correction-mode balanced summary
engine --correction-mode aggressive summary
```

How to set explicit auto-apply threshold:
```powershell
engine --correction-auto-threshold 98 profile load "C:\profiles\my_setup.pr0file"
```

How to write a correction report:
```powershell
engine --correction-report "C:\logs\joypro_corrections.json" summary
engine --correction-report "C:\logs\joypro_corrections.txt" summary
```

## Command Families Explained

### `summary` / `profile`

Use these to open/save your master mapping file (`.pr0file`).

- `engine summary`
  Prints quick counts (relations, binds, modifiers).
- `engine profile load <path.pr0file>`
  Loads an existing master mapping file into memory.
- `engine profile save <path.pr0file>`
  Writes current in-memory state to a `.pr0file`.
- `engine profile save-last`
  Saves to JoyPro's "last used" profile location.

### `devices`

Use this to compare physically connected hardware and what your profile currently references.

- `engine devices list --connected --text`
  Shows currently connected joystick devices.
- `engine devices list --profile-devices --text`
  Shows devices referenced by loaded profile.

### `db`

Use this to inspect JoyPro's bundled DB content.

- `engine db summary`
  DCS clean-profile health summary.
- `engine db list [filter]`
  DCS module list (optionally filtered).
- `engine db check`
  Validation check for required DCS clean-profile files.
- `engine db games [filter]`
  Lists available canonical game IDs from DB (for example `DCS`, `IL2Game`, `StarCitizen`).
- `engine db planes [filter] [--game <name>]`
  Lists available plane/module names from DB, optionally narrowed by game.

### `dcs`

Use these commands for DCS Saved Games input data.

- `engine dcs scan ...`
  Parse and preview import result without applying profile changes.
- `engine dcs import ...`
  Parse and apply to the in-memory master mapping.
- `engine dcs export ...`
  Write output files back in DCS-compatible format (`.diff.lua` + `.jp` backup).
- `engine dcs defaults cut ...`
  Back up joystick defaults and move stick-specific game-folder defaults into backup storage.
- `engine dcs defaults restore ...`
  Restore joystick defaults from backup storage back into DCS game-folder aircraft input folders.

### `il2`

Use these commands for IL2 input data.

- `engine il2 scan ...`
  Parse and preview IL2 input candidates.
- `engine il2 import ...`
  Apply parsed controls into the in-memory master mapping.
- `engine il2 export ...`
  Export profile controls into IL2-compatible outputs (mode-dependent).

### `relations`

Use these commands to manage logical actions and where they map in game data.

- `list` / `show`
  Inspect relations.
- `add`
  Create relation from nodes or from other relations (type can be inferred or set explicitly).
- `group list/add/remove/rename`
  Organize relations by tags.
- `item add/remove/enable/disable`
  Add or control specific node targets.
- `export/import/edit`
  Roundtrip relations as JSON or edit externally.

### `binds`

Use these commands to manage hardware mappings and modifiers.

- `list` / `show`
  Inspect current binds.
- `set` / `clear`
  Create, replace, tune, or remove one relation bind directly.
- `modifier list/add/remove/rename`
  Add or remove key modifiers for a bind.
- `unbind`
  Remove bind links by input token (DCS or IL2 format supported).
- `export/import/edit`
  Roundtrip binds as JSON or edit externally.

## Filter Rules And Import Behavior

- DCS/IL2 import flags are valid for `scan` and `import`, not `export`.
- If you do not pass `--device` or `--devices`, all devices are considered.
- `--ignore-device` and `--ignore-devices` exclude devices from candidates.
- If include and ignore match the same device, ignore wins.
- `engine db games` gives canonical game names for `engine db planes --game`.

## Device Name Resolution (`--confirm`)

When a device name does not exactly match, engine can suggest the closest match.

- Default (`--confirm true`, or omitted):
  Ask for confirmation (`y/N`) before applying the closest match.
- `--confirm false`:
  Auto-accept closest match without prompting.

Match preference order:
1. Devices already in profile.
2. Currently connected devices.
3. Game data files (`DCS Config\Input` or `IL2 data\input\devices.txt`).

## DCS Workflows And Examples

### Basic scan/import
```powershell
# Preview what engine would import
engine dcs scan "C:\Users\<you>\Saved Games\DCS.openbeta" --text

# Import everything found
engine dcs import "C:\Users\<you>\Saved Games\DCS.openbeta" --text
```

### Plane filtering
```powershell
# Import A-10C_2 only, including defaults
engine dcs import "C:\Users\<you>\Saved Games\DCS.openbeta" --plane "A-10C_2" --include-defaults --text

# Import A-10C_2 and F/A-18C including defaults
engine dcs import "C:\Users\<you>\Saved Games\DCS.openbeta" --plane "A-10C_2" --plane "FA-18C_hornet" --include-defaults --text
```

### Device include/ignore filtering
```powershell
# Include only these devices
engine dcs import "C:\Users\<you>\Saved Games\DCS.openbeta" --device "VKB" --device "LEFT VPC Throttle" --text

# Import all devices except these
engine dcs import "C:\Users\<you>\Saved Games\DCS.openbeta" --ignore-devices "Keyboard,VKB" --text
```

### Merge into existing profile
```powershell
engine --profile "C:\profiles\base.pr0file" dcs import "C:\Users\<you>\Saved Games\DCS.openbeta" --merge --save-profile "C:\profiles\merged.pr0file" --text
```

### Axis option toggles
```powershell
engine dcs import "C:\Users\<you>\Saved Games\DCS.openbeta" `
  --import-inverted --import-slider --import-curvature --import-deadzone --import-saturation-x --import-saturation-y --text
```

### Export modes and cleanup
```powershell
# Clean export (replace each exported target file with JoyPro output only)
engine dcs export "C:\Users\<you>\Saved Games\DCS.openbeta" --mode clean --text

# Merge and keep existing when IDs collide
engine dcs export "C:\Users\<you>\Saved Games\DCS.openbeta" --mode merge --text

# Merge and overwrite existing when IDs collide (default)
engine dcs export "C:\Users\<you>\Saved Games\DCS.openbeta" --mode merge-overwrite --text

# Merge and allow multiple binds on collisions
engine dcs export "C:\Users\<you>\Saved Games\DCS.openbeta" --mode add --text

# Preserve keyboard defaults and nuke untouched joystick files
engine dcs export "C:\Users\<you>\Saved Games\DCS.openbeta" --mode merge-overwrite --keep-keyboard-defaults true --nuke-untouched-devices true --text
```

## IL2 Workflows And Examples

```powershell
# Scan all IL2 devices
engine il2 scan "D:\IL-2 Sturmovik Great Battles" --text

# Import only selected devices
engine il2 import "D:\IL-2 Sturmovik Great Battles" --device "VKB" --device "LEFT VPC Throttle" --text

# Import everything except ignored devices
engine il2 import "D:\IL-2 Sturmovik Great Battles" --ignore-devices "VKB,LEFT VPC Throttle" --text

# Save to a profile
engine il2 import "D:\IL-2 Sturmovik Great Battles" --save-profile "C:\profiles\il2_import.pr0file" --text
```

## Device Suggestion Examples

```powershell
# Ask before accepting closest match (default)
engine dcs import "C:\Users\<you>\Saved Games\DCS.openbeta" --device "LEFT VPC Throtle" --text

# Auto-accept closest match
engine dcs import "C:\Users\<you>\Saved Games\DCS.openbeta" --device "LEFT VPC Throtle" --confirm false --text

# Same behavior also works for ignored-device filters
engine il2 scan "D:\IL-2 Sturmovik Great Battles" --ignore-device "Tst Stick" --confirm false --text
```

## Relations And Binds Examples

```powershell
# List and inspect
engine relations list --text
engine relations show "My Relation" --text
engine binds list --text
engine binds show "My Relation" --text

# Add relation + node (type inferred from node ID unless you override)
engine relations add "Gear Toggle" --node DCS:d3007pnilu3007cd16vd1vpnilvu0 --text

# Manage groups
engine relations group list --text
engine relations group add "Gear Toggle" "Startup" "Common" --text
engine relations group remove "Gear Toggle" "Startup" --text
engine relations group rename "Startup" "Core Startup" --text

# Manage modifiers
engine binds modifier list --text
engine binds modifier add "Gear Toggle" LShift --text
engine binds modifier rename "LShift" "LShift_Hold" --text
engine binds modifier remove "Gear Toggle" LShift_Hold --text

# Set/replace bind and tune axis values
engine binds set "Gear Toggle" --device "VKB Gladiator NXT" --input JOY_BTN1 --text
engine binds set "Pitch Axis" --input JOY_Y --inverted true --curvature 0.1,0.2 --deadzone 0.05 --saturation-x 0.9 --saturation-y 0.9 --text
engine binds clear "Gear Toggle" --input-only --text
engine binds clear "Gear Toggle" --text

# Manage node items
engine relations item add "Gear Toggle" --node DCS:d3007pnilu3007cd16vd1vpnilvu0:F-16C_50|FA-18C_hornet --text
engine relations item disable "Gear Toggle" --id d3007pnilu3007cd16vd1vpnilvu0 --game DCS --planes F-16C_50 --text

# Unbind by DCS or IL2 style token
engine binds unbind --input JOY_BTN1 --format dcs --all --text
engine binds unbind --input joy0_b0 --format il2 --all --text
```

## DB And Devices Examples

```powershell
engine db summary --text
engine db games --text
engine db planes --text
engine db planes --game DCS "A-10" --text
engine db planes --game IL2Game --text
engine db check --text
engine devices list --connected --text
engine devices list --profile-devices --text
```

## Common Beginner Path

Recommended first workflow:
1. `engine devices list --connected --text`
2. `engine dcs scan "<saved-games-path>" --text` or `engine il2 scan "<il2-root>" --text`
3. Import with simple filters (`--plane`, `--device`, `--ignore-device`) if needed.
4. `engine relations list --text` and `engine binds list --text` to verify.
5. Save profile with `engine profile save <file.pr0file>`.
6. Export to game folder once you are happy.

## Keeping This Wiki Up To Date

1. Regenerate after command/parser/help changes:
   `powershell -ExecutionPolicy Bypass -File .\tools\update_cli_wiki.ps1`
2. Validate in CI/local:
   `powershell -ExecutionPolicy Bypass -File .\tools\update_cli_wiki.ps1 -CheckOnly`
3. Optional full regression:
   `powershell -ExecutionPolicy Bypass -File .\tests\engine_smoke_test.ps1 -Configuration Debug -Platform x64`
