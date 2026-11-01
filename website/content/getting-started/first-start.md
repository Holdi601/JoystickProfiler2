---
title: First start
status: full
order: 2
summary: The "Your games" window of the first start, linking portable installs and other config folders, building the command lists, and your first profile.
---
:::video first-start "The first start: your games"
:::

:::layer beginner
## "Welcome to JoyPro2: your games"

On the first start JoyPro looks for your games (in the background, a few seconds) and opens a window with one row per
supported game:

![The first-start setup: found, linked and missing games](shot:first-start)

- **Found** games are ticked, with their folder and how they were found (Steam, a known place, the Windows apps list).
- **Untick** a found game you do not have: it is hidden everywhere in JoyPro.
- A game JoyPro did **not find** stays unticked and is **not** hidden: once you install it, JoyPro finds it. If you
  have it somewhere else (a portable install, another drive), tick it and press **Browse...** to link its folder.
- **Controls saved in > Browse...** is for a different settings folder, for example `Saved Games\DCS.openbeta`.

Press **Done**. **Later** (or closing the window) changes nothing. You can open the window again at any time:
Settings > Games > **Run setup again**.

## Then: the command lists

JoyPro needs each game's list of commands (their names and ids) and builds it once from your installation. The Start
page shows a notice with **Build List** per game or **Build All Missing Lists**. After a game update a card says
"Game updated: update its command list" with **Update**.

## Your first profile

You do not need to create a profile first. The quickest start is an import: on the Start page, press **Import** on a
game card ([[page:flows/import.html|Import a game]]); JoyPro makes a profile for you. Or press **New profile** in the
bar at the top and build relations from scratch ([[page:flows/relations.html|Build relations]]).

There is no Save button: the profile saves itself after every change ("Saved" in the top bar), and **Undo** / **Redo**
and **History** work even after a restart. Ctrl+S saves at once.
:::

:::layer advanced
## Details

- The folder you link is checked at once against the game's marker files (`bin\DCS.exe`, `bin\game\Il-2.exe`,
  `X4.exe` ...): "Marker found" or "Marker not found, but the folder is kept as you chose it". **Use detected** forgets
  the link.
- Games that keep their controls in the installation (IL-2, IL-2 Korea, IL-2 1946, Falcon BMS, X-Plane, VTOL VR with
  BYOJoystick) show the location read-only; registry games (Nuclear Option) too.
- **Done** writes everything through the engine: [[cli:games install set]] / `clear`, [[cli:games config set]] /
  `clear`, [[cli:games install disable]] / `enable`, then detects again.
- A hidden game counts as not installed everywhere (export tiles, watcher, filters, the HUD ...); your relations for it
  stay in the profile. Settings > Games lists hidden games with **Show again**.
- Building a list: DCS runs its own Lua with the installed modules (no game window); IL-2 reads the key mapping files
  the game writes; Star Citizen reads its default profile from the game data; X-Plane reads `Commands.txt`; other
  games their own files ([[game:README|command lists on a fresh install]]).
:::

:::layer technical
## How it works inside

- The GUI runs one [[cli:games installs]] at start (no polling) and shows the window once (`gameSetupDone` in
  `%APPDATA%\JoyPro2\ui-settings.json`). View model: `MainWindowViewModel.GameSetup.cs`, window
  `Views/GameSetupWindow.axaml`.
- Detection per game (`engine\GameInstalls.cs` and each `GameIntegration`): Steam library folders and app ids, known
  install places, the Windows uninstall and apps lists, and the user's own link, which detection never replaces.
- Command lists: [[cli:db check-updates]] reports `no-catalog` for a game without a list; the build runs the game's
  `refresh-db` ([[cli:dcs refresh id]] + [[cli:dcs refresh clean]] for DCS, [[cli:db refresh-il2]],
  [[cli:db refresh-starcitizen]], `engine <game> refresh-db`) and writes only into `%LOCALAPPDATA%\JoyPro2\GameData`.
:::
