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
- The export mode is step 2 of the page, as four described choices: "Update, keep the game's binds" (`merge`, the
  GUI's default), "Update, my profile wins" (`merge-overwrite`, the engine's default when a command line leaves out
  `--mode`), "Only add" (`add`) and "Replace on my devices" (`clean`), plus "Also clear my other devices" (nuke). In
  DCS every mode removes the DCS default binds of the devices the export writes, in every aircraft ([DCS](games/dcs.md#export-modes); the optional switches that change the DCS install itself are in Settings > DCS: default binds);
  the choices say so. Clean and nuke ask before the export. Paths and keyboard options are under
  **Options and paths (for experts)**. Every export backs up the game files first ([backups](backups.md)); **Undo
  last export** on a tile restores the newest backup of that game (`engine backups restore latest --game <id>`).
- Games without an exporter show "no export for this game".
- Games that are not installed on this PC get no tile; the page names them. Set their folder in Settings > Games
  ([installed games](installs.md)).
- Every export keeps JoyPro's own data of the binds next to the game file, so a later import restores relation
  names, groups, modifiers and tuning ([re-import](reimport.md)).

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
