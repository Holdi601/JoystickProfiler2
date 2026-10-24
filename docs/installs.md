# Installed games

JoyPro checks which of its games are installed, and where. A game that is neither found nor set by you is blocked:

- **Commands that refuse it:** import, scan, export, DB refresh, the DCS stick-defaults commands and restoring a backup. The engine refuses them with a message saying how to set the folder.
- **In the GUI:** its buttons are off (their tooltips and the card say why). The registry game lists show "(not installed)". The export page has no tile for it and names it in a hint.

`engine games installs` also counts the actions JoyPro knows of each game (`Actions`: its command list, or JoyPro's own
table for ACE COMBAT 7/8 and Battlefield 6/2042; DCS: the aircraft lists) and says why there are none
(`ActionsNote`). A game with no action anywhere that is not installed (nothing to build its list from) is left off the
Start page's cards; Settings > Games keeps it with the reason. An installed game without a list stays on the cards:
Update builds the list from its files.

Paths, catalogs, lists and everything that only changes the profile keep working.

GUI: Settings > **Games**. Every supported game is listed with its box art, "Installed" / "Not installed", the folder, how it was found, and where it keeps its controls. **Choose folder...** makes a folder the game's installation; **Use detected** forgets it again. **Hide** hides the game; the hidden games are listed under "Hidden games", each with **Show again**. **Detect again** checks once more (after installing a game). **Run setup again** opens the first-start setup.

CLI:

- `engine games installs [--game <id>]`
- `engine games install set <game> <folder>` (the folder must exist)
- `engine games install clear <game>` (also `unset`)
- `engine games install disable <game> [<game> ...]` / `enable <game> [<game> ...]` (games you do not have; also comma-separated)
- `engine games config set <game> <file or folder>` / `clear <game>` (your config location)

Your choices are saved in the JoyPro data folder, `%LOCALAPPDATA%\JoyPro` (or `JOYPRO_DATA_DIR`):

| File | Holds |
|---|---|
| `game-installs.json` | your installation folders |
| `game-configs.json` | your config locations |
| `games-disabled.json` | the games you do not have |

The GUI reads all three through `engine games installs`, so the CLI and the GUI always agree.

## Game data

The command lists JoyPro builds from your games are your data, not part of the program. They live in the game-data
folder, `%LOCALAPPDATA%\JoyPro\GameData` (`<JOYPRO_DATA_DIR>\GameData` in tests), so a JoyPro update or rebuild never
replaces them and an install under Program Files needs no admin rights to refresh them:

| Folder | Holds | Where |
|---|---|---|
| `DB\Functions`, `DB\Devices`, `DB\Llm`, `DB\DevicePhotos`, `DB\DeviceLayouts`, `TOOLS\Conversions`, `LayoutCatalog` | JoyPro's own data | the program folder (engine.exe's folder) |
| `DB\<game>` (command lists, `aircraft.tsv`, `seats.tsv`, ..., `source-version.txt`) | built from your games | `GameData\DB\<game>` |
| `CleanProfile\DCS`, `KeyboardCleanProfile\DCS` | DCS's default binds, built by `dcs refresh clean` | `GameData\CleanProfile\DCS`, `GameData\KeyboardCleanProfile\DCS` |

**Reading:** per game folder, the game-data folder's copy whenever it exists; else the program folder's. A release
ships no game lists, so there the program folder has none; a development build carries `EngineOnly_Runtime`'s lists
there, and the smoke tests use them.

**Writing:** every refresh (`dcs refresh id|clean`, `db refresh-il2`, `db refresh-starcitizen`, `<game> refresh-db`,
the GUI's Build List and Update) and every recorded build (`db check-updates`, `source-version.txt`) writes only into
the game-data folder. A refresh of one aircraft first copies the program folder's list of that game, so the other
aircraft stay.

**Migration:** every engine start compares the program folder with the game-data folder, per game (DB folder and clean
profiles together); only copies, never deletes:

- the game-data folder has no copy of a folder: it is copied over (lists an older JoyPro refreshed into the program
  folder are kept this way, before the next update or rebuild replaces them);
- the program folder's `source-version.txt` is newer (a higher game version or Steam build; the same version with more
  DCS modules; else a later "recorded" time when other fields differ): the program folder's list is copied over the
  game-data copy; the old copy is first saved to `GameData\Replaced\<yyyyMMdd-HHmmss>\`. A list without a build stamp
  is never replaced.

`engine db paths` shows both folders and, per game folder, where it is read from (`game-data` or `program`) and its
recorded build; `engine db summary` shows the DCS list and clean profile folders in use.

## First start

The first time JoyPro2 starts, it detects the games in the background (one `engine games installs`, no polling) and then opens **Your games**:

- **What it found:** a summary line ("JoyPro found 3 of its 19 games on this PC: DCS World (steam), ...") and one row per supported game with **Found** (with the folder and how it was found: steam, known place, Windows apps list), **Linked by you** or **Not found**.
- **I have this game:** ticked for the games JoyPro found or you linked. Untick a found game you do not have: it is hidden on **Done**. The games JoyPro did not find start unticked and are *not* hidden: they stay "not installed", so JoyPro finds them by itself once they are installed. Tick one to link its folder, or tick **I don't have it (hide it everywhere)** under it to hide it anyway.
- **Installation > Browse...:** links a folder, for example a portable install or one on another drive. The folder is checked at once against the game's marker files, the same ones the engine checks (`bin\DCS.exe`, `bin\game\Il-2.exe`, `X4.exe`, ...): "Marker found: ..." or "Marker not found (...), but the folder is kept as you chose it". **Use detected** forgets the link.
- **Controls saved in > Browse...:** the config location, for example `Saved Games\DCS.openbeta` instead of `Saved Games\DCS`, a Star Citizen branch's profile folder, or another War Thunder `machine.blk`. Games that keep their controls in the installation (IL-2, IL-2 Korea, IL-2 1946, Falcon BMS, X-Plane, VTOL VR with BYOJoystick) show the location read-only: it follows the installation folder. Games that keep them in the Windows registry (Nuclear Option) show it read-only too.
- **Done** writes everything through the engine (`games install set|clear`, `games config set|clear`, `games install disable|enable`) and detects again. Only the found or linked games you unticked and the ones marked **I don't have it** are hidden. **Later** (or closing the window) changes nothing and hides nothing.

The setup is shown once (`gameSetupDone` in `%APPDATA%\JoyPro2\ui-settings.json`); users of an earlier JoyPro2 see it once too, with everything detected or linked ticked. Settings > Games > **Run setup again** shows it again at any time.

## Hidden games

Games you do not have or are not interested in: hide them with the hide button (crossed-out eye) on the game's card on
the Start page, **Hide** in Settings > Games, or in the first-start setup. A hidden game counts as not installed,
wherever it is found, so commands refuse it ("You said you do not have ... (Settings > Games)"); your relations and
binds for it stay in the profile. The GUI leaves it out of:

- the Start page's game cards, the "games installed" count, "Watch all" and the game config watcher (the engine's
  `sync status` leaves it out, also for the tray helper), and the setup wizard's games,
- the export page (no tile, no "not installed" warning; a short note names it),
- the game lists of Start > Other Games and Settings (registry games),
- the game picker of the relation node search,
- the DB games list (Settings) and the game choices of the layout picture,
- the Axis Tuning scopes and the force feedback game list,
- the backup list and its game filter,
- the "not installed" hints; the Start page lists it under "Hidden games (n)" below the cards and Settings > Games
  under "Hidden games", each with **Show again**.

Your link and config location are kept; **Show again** (or `engine games install enable <game>`) brings the game back. If you install such a game later, press **Show again**: detection alone does not bring it back.

## Portable installs and other drives

A linked folder is the game's installation everywhere: import, export, scan, DB refresh (also the headless DCS refresh and the catalog refreshes that read the installation), backups and the config paths of games that keep their controls in the installation. Detection never replaces it: as long as the link is set, the game's installation is that folder and nothing else (only MSFS's catalog refresh also reads the package folders it finds next to it), also when the folder is gone (the game then shows "The folder you set does not exist (any more)" and stays blocked until you link it again or press **Use detected**). The GUI's own DCS and Star Citizen paths (Saved Games, install folder, profile folder) follow the link and the config location too.

## How a game is found

1. **Your folder:** when set, it replaces detection (and a config location you set comes first wherever JoyPro looks for the game's controls). For the games that keep their controls inside the installation, the config path comes from it too:

   | Game | Config inside the installation |
   |---|---|
   | IL-2 | `data\input` |
   | Star Citizen | `LIVE\USER\Client\0\Profiles\default` |
   | X-Plane 11, X-Plane 12 | `Output\preferences` |
   | Falcon BMS | `User\Config` |
   | VTOL VR (BYOJoystick) | `SaveData` |

2. **Steam:** the game's app manifest in any Steam library (`steamapps\appmanifest_<app id>.acf`).
3. **Known places:**
   - DCS: its registry keys and default folders;
   - IL-2 and Star Citizen: JoyPro's discovery;
   - Falcon BMS: the `Benchmark Sims` registry key and `<drive>:\Falcon BMS*`;
   - X-Plane 12 / 11: the folders the installer lists in `%LOCALAPPDATA%\x-plane_install_12.txt` / `_11.txt` (each version its own);
   - MSFS 2024 / 2020: the Microsoft Store package folder (`Microsoft.Limitless` / `Microsoft.FlightSimulator`) and the sim's `%APPDATA%` folder;
   - War Thunder and Elite Dangerous: their launcher folders;
   - Arma: Bohemia's registry keys;
   - IL-2 Korea standalone: `Program Files\IL2Series` and `<drive>:\IL2Series`, `<drive>:\Games\IL2Series` (and a few other usual names) on every fixed drive.
4. **Windows' apps list:** uninstall entries with the game's name (or a known uninstall key, e.g. the IL-2 Korea installer's `{222bc1b4-be6e-4451-9291-ef5aeb7c64a2}_is1`) and install folder (`InstallLocation`, or Inno Setup's `Inno Setup: App Path`).

A folder found in steps 3 and 4 must contain the game's executable (for example `bin\DCS.exe`, `X-Plane.exe`, `arma3_x64.exe`). A Steam manifest is enough on its own. A folder you set counts even without the executable; the list then notes that the executable is missing.

What you link does not have to be the game folder exactly: when the executable is not in it, JoyPro takes a subfolder that has it (`game` first: the IL-2 Korea standalone keeps the game in `<launcher folder>\game`), else a parent folder (up to four levels) that has it. So the game's folder, its launcher folder, `bin\game`, `data\input`, the game's `.exe` or the launcher's `.exe` all link the same installation (Settings > Games: **Choose folder...** or **Choose .exe...**; the setup window: **Browse...** or **.exe...**). `engine games install set` accepts the same, says which folder it uses, and a folder saved earlier as an `.exe` path still works. Places found in steps 3 and 4 are only searched downwards (a launcher folder), never in parent folders.

| Game | Steam app ids | Executable checked |
|---|---|---|
| DCS World | 223750 (or standalone) | `bin\DCS.exe`, `bin-mt\DCS.exe` |
| IL-2 Sturmovik: Great Battles | 307960 | `bin\game\Il-2.exe` |
| IL-2 Series: Korea | 247970 | `bin\game\IL2Series.exe` |
| Star Citizen | (RSI launcher) | `LIVE\Bin64\StarCitizen.exe` |
| Microsoft Flight Simulator 2024 (`MSFS2024`) | 2537590 (or Store) | `FlightSimulator2024.exe`, Store `UserCfg.opt` |
| Microsoft Flight Simulator 2020 (`MSFS2020`) | 1250410 (or Store) | `FlightSimulator.exe`, Store `UserCfg.opt` |
| X-Plane 12 (`XPlane12`) | 2014780 | `X-Plane.exe` |
| X-Plane 11 (`XPlane11`) | 269950 | `X-Plane.exe` |
| War Thunder | 236390 | `win64\aces.exe`, `launcher.exe` |
| Arma 3 | 107410 | `arma3_x64.exe` |
| Arma 2 / Operation Arrowhead | 33900 / 33930 | `arma2.exe` / `ArmA2OA.exe` |
| Arma Reforger | 1874880 | `ArmaReforgerSteam.exe` |
| WARDOGS | 1867240 | `WardogsLauncher-Shipping.exe` |
| X4: Foundations | 392160 | `X4.exe` |
| Elite Dangerous | 359320 | `EDLaunch.exe` |
| Falcon BMS | (own installer) | `Bin\x64\Falcon BMS.exe`, `Launcher.exe` |
| VTOL VR | 667970 | `VTOLVR.exe` |
| IL-2 Sturmovik: 1946 (`IL21946`) | 15320 | `il2fb.exe` |
| IL-2 Sturmovik: Cliffs of Dover (`IL2CoD`) / Blitz (`IL2CoDBlitz`) | 63950 / 754530 | `Launcher.exe` / `Launcher64.exe` |
| FSX (`FSX`) / FSX: Steam Edition (`FSXSE`) | (boxed, registry `Flight Simulator\10.0`) / 314160 | `fsx.exe` |
| Prepar3D v4 / v5 / v6 (`P3D4`, `P3D5`, `P3D6`) | (own installer, registry `Lockheed Martin\Prepar3D vN` `SetupPath`) | `Prepar3D.exe` |
