# Export

The Export page starts with **Export to games**: one tile per game the profile has relations for, with the game's
cover, how many of its relations are bound, and an **Export now** button. Tick the games and press **Export ticked
games** to export them one after the other; the page and the status line then say "Exported 5 of 6 games, 1 failed:
<game> (<reason>)". A tile starts ticked only when the game has bound relations and was started once (its control
files exist, `engine games installs`: ConfigPaths); a tick the user changed stays. Each tile shows the result of its
last export ("Exported 14:02" or the engine's error, e.g. "Failed: War Thunder file not found: ..."). **Undo last
export** is enabled once a backup of that game exists (an export made it; the backups list below is read again after
every export).

- **DCS** opens with **Aircraft...**: every aircraft (and input layer such as Default or UiLayer) of the profile with
  its picture, its module name and how many of its relations are bound. Only the ticked aircraft are exported; the
  other aircraft folders in Saved Games stay as they are.
- The export mode is step 2 of the page, as four described choices, plus "Also clear my other devices" (nuke). Since
  2.5.0 the GUI's default is "Replace on my devices" + "Also clear my other devices" (clean + nuke, as in JoyPro 1),
  only in the aircraft of the export ("clean all aircraft" off), keyboard defaults kept. A mode the user chose
  stays: `export-settings.json` keeps `ModeChosenByUser`; an old file with merge, no nuke and no marker gets the new
  default, any other mode is kept. The engine's default when a command line leaves out `--mode` stays
  `merge-overwrite`. The modes (DCS: [Export modes](games/dcs.md#export-modes)):
  - "Only add" (`add`) never deletes anything. A profile bind whose button (device, input and modifiers, in that
    aircraft) the game uses for another function is not written: the export lists it (`SkippedTakenInputs`: game,
    aircraft, device, input, modifiers, relation, action, the game's action) and the profile marks it (below).
  - "Update, keep the game's binds" (`merge`) keeps the game's other binds and removes only a game bind on a button
    the profile puts a different function on. The profile action's old button elsewhere stays: after a Move the old
    button keeps the action in the game (the Move panel says so in this mode) until an export with "my profile wins"
    or "Replace".
  - "Update, my profile wins" (`merge-overwrite`) does what merge does, and every action the profile binds loses its
    other buttons in the game in that aircraft (also on its other devices), so a Move fully reaches the game.
  - "Replace on my devices" (`clean`) gives the written devices only the profile's binds, in the exported aircraft;
    nuke clears the connected devices the profile does not use there (not a device that is not connected, not the
    import's ignored devices, not the vJoy device JoyPro tunnels into).
  Clean and nuke ask before the export (Settings > Confirmations: `games.export-removes-binds`). Paths and keyboard
  options are under **Options and paths (for experts)**. Every export backs up the game files first
  ([backups](backups.md)); **Undo last export** on a tile restores the newest backup of that game (`engine backups
  restore latest --game <id>`), byte for byte also after clean + nuke.
- **First export to a game** this profile never imported from or exported to, when the game has binds of its own
  (`engine profile game-state --game <id>`: `Imported`, `Exported` from the profile's `SyncedGames`, or the sync record
  of an earlier export from this profile; `GameHasBinds`): the GUI asks once "Export replaces the binds in <game> with
  your profile (a backup is made, Undo export brings them back). Import first?" with "Import first", "Export anyway",
  "Cancel" and "Don't ask again" (Settings > Confirmations: `games.first-export-replaces`).
- **Not exported marks** (`engine export-marks list|dismiss`): the profile keeps "not exported: input taken in <game>
  by <action> (<date>)" per relation and aircraft (`Pr0file.ExportSkipMarks`). The relation shows a warning chip on the
  action row and the aircraft; the Check has a "Not written to the game" section ("Not written to <game>: <button> is
  used there for <other action>") with "Export with 'Update' to replace it", "Move to another button" and "Keep as is"
  (dismiss). Each export replaces the marks of its game and aircraft with what it skipped this time, so a mark clears
  when a later export writes the bind or the button is free. Every exporter fills the same result field
  (`SkippedTakenInputs`, engine/ExportSkippedBinds.cs); the export command marks them.
- Games without an exporter show "no export for this game".
- Games that are not installed on this PC get no tile; the page names them. Set their folder in Settings > Games
  ([installed games](installs.md)).
- Every export keeps JoyPro's own data of the binds next to the game file, so a later import restores relation
  names, groups, modifiers and tuning ([re-import](reimport.md)).

## Export modes

The same rules hold for every game except DCS ([DCS](games/dcs.md#export-modes)). "Same input" means the same device,
the same button or axis and the same modifiers, in the same aircraft (or ship, vehicle) when the game keeps binds per
aircraft.

| Mode | What happens in the game |
|---|---|
| **Only add** (`add`) | Never removes or changes a bind of the game, also not with "Also clear my other devices" (that is not used with add). A profile bind whose input the game already uses for another action is not written, so no input gets two actions; the export lists each one (game, aircraft, device, input, the profile's action and the game's action; `SkippedTakenInputs` in the engine's result, "Binds Not Written, Button Taken In The Game" in its text). |
| **Update, keep the game's binds** (`merge`) | Writes the profile's binds and keeps the game's other binds. A game bind on an input the profile gives another action is removed (conflict). An action you moved in JoyPro keeps its old input too, so it then sits on both; the Check and the watcher show that. Games with one slot per action (Star Citizen joystick, Wardogs, Hell Let Loose) put the new input into that slot. |
| **Update, my profile wins** (`merge-overwrite`) | Like merge, and every action the profile binds loses its other inputs in the game (for that aircraft), so a moved action fully moves. Only inputs of connected joysticks count, and keyboard inputs only with the keyboard option. |
| **Replace on my devices** (`clean`) | The devices the export writes keep only the profile's binds (for the aircraft in the export). |
| **Also clear my other devices** (nuke) | Connected devices the export does not write lose their binds (for the aircraft in the export). |

Never touched, in any mode: devices that are not connected, devices the import cannot read or skips (for example two
devices of one model in a game that cannot tell them apart), mouse and gamepad binds, binds the import cannot read
(IL-2's mouse lines, half-axis binds in Elite Dangerous, unknown tokens), keyboard binds without the keyboard option,
and aircraft that are not in the export. In games with infantry, ground vehicles or menus (Star Citizen, War Thunder,
Arma, Arma Reforger, Wardogs, Hell Let Loose, Battlefield) JoyPro touches only the flight actions; everything else stays
byte-identical.

Import, then export with "Replace on my devices" and "Also clear my other devices" leaves the game as it was: the
game's active defaults of an imported device come into the profile on import (Star Citizen's joystick defaults on js1,
for example), and an export writes only what really differs from the game's defaults, never a default removed and
added again. The smoke test checks this with synthetic files (`engine\tests\smoke\roundtrip.ps1`).

## Remembered settings

The page remembers its last settings in `export-settings.json` (the GUI's settings folder, `%APPDATA%\JoyPro2`): the
export mode, "Also clear my other devices", the options per game (DCS and Star Citizen keyboard defaults, DCS clean
in every aircraft, Star Citizen local order, the dry run of the other games) and the paths of **Options and paths**
(per registry game its own file or folder) for every profile alike; and per profile the games and DCS aircraft you
ticked or unticked yourself. They come back at the next start and when you open the profile again. Only your own
ticks are kept: a tick set back to what JoyPro would tick is forgotten, so the other tiles keep following the game
(bound relations, started once). A remembered path is used only while it exists. "Export" on a Start page card (only
that game for now) and a dry-run preview are not remembered. Clean and nuke still ask before every export.

CLI:

- `engine profile games` lists the games (DCS: aircraft) of the profile with relations and bound relations, and which
  exporter writes them (`dcs`, `il2`, `starcitizen`, `registry`).
- `engine dcs export [--path <Saved Games\DCS>] [--planes <a|b|c>] ...` exports only these aircraft (exact names as
  in the profile). An aircraft without bound relations gives a warning.

## Pictures

`engine media [games|aircraft|sources] [--no-internet-pictures]` lists the pictures the GUI shows. Nothing is scraped
from websites, and JoyPro ships no game artwork:

| What | Source |
|---|---|
| Game covers | Per game, the first that works: 1. the picture Steam keeps on this PC for a game of the user's library (`steam\appcache\librarycache\<app id>`, also its hash sub folders; portrait cover first, else the wide header); 2. the cover downloaded before (`<data>\Media\Covers\<GameId>.<ext>`, with `<GameId>.source`: kind and address); 3. a download from the game's fixed web source (`engine/EngineGameCovers.cs`, `engine media sources`): Steam's store CDN by app id (library capsule, then main capsule, then header) for Steam games and FSX (the Steam Edition's art), the publisher's site for Star Citizen (RSI's key art, then its logo), Falcon BMS (falcon-bms.com app icon) and Prepar3D v4/v5/v6 (prepar3d.com product marks); 4. the icon of the game's executable (Star Citizen, Falcon BMS, DCS standalone; kept in `<data>\Media`). Games with none get a JoyPro badge with the game's name ([branding](branding/README.md)). A logo (publisher mark, executable icon) is drawn on the badge, a cover fills the tile; both on every game card, installed or not. |
| Download rules | https only, 10 s timeout, at most 5 MB, `Content-Type: image/*` and a JPEG, PNG or WebP file; up to 4 at a time; a failed game is tried again a day later. Off with `--no-internet-pictures` (the GUI passes it while Settings > Privacy "Game covers from the internet" is off; the device photos have their own per-device consent) or `JOYPRO_NO_INTERNET_PICTURES=1` (tests): then neither a download nor a downloaded cover is used. `JOYPRO_COVER_CATALOG=<json>` replaces the list (tests: `engine\tests\smoke\gamecovers.ps1` with the loopback fake server). |
| DCS aircraft | The icon and mission editor picture every DCS module ships in its own folder (`Skins\...\icon.png`, `ME\BackGround*.png`), found through the module's `entry.lua`. |

The GUI shows them next to the export tiles, the relation entries and the suggestions.
