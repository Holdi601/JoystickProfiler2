# Tiny Combat Arena (Why485 / MicroProse, Unity)

Status: **integrated, experimental: `engine tca`** (aliases `tinycombatarena`, `tinycombat`;
`engine/Games/TinyCombatArenaIntegration.cs`) with `paths`, `catalog`, `scan`, `import`, `export` (clean / add / merge /
merge-overwrite, `--nuke-untouched-devices`, `--dry-run`) and `refresh-db`. Built and checked in game on a test PC
(game 0.17.2.3, Steam app 1347550, `<SteamLibrary>\steamapps\common\TinyCombatArena`, process `Arena`) on 2026-10-11.

Tags: **[OBS]** seen on a test PC · **[SRC]** the installed game's own files (`Arena_Data`, `Assembly-CSharp.dll`) ·
**[INF]** inferred.

## Where the binds are
- **Unity PlayerPrefs in the registry**: `HKCU\Software\TinyCombat\Arena` [OBS]. The game ships Rewired's
  `UserDataStore_File` (`RewiredSaveData.json`) as well [SRC], but its scene uses `UserDataStore_PlayerPrefs`: after the
  first start and a bind in the mapper no JSON file exists, the registry key holds everything. The earlier guess
  (`LocalLow\TinyCombat\Arena\RewiredSaveData.json`, Steam Cloud) was wrong.
- Same encoding as Nuclear Option (docs/games/nuclear-option.md): value name = key + `_h<djb2-xor hash>`, Rewired values
  are REG_BINARY UTF-8 + one 0x00; Rewired's long value names (255+ characters) need the registry API.
- Keys [OBS]: `RewiredSaveData|playerName=Player|dataType=ControllerMap|kv=2|controllerMapType=JoystickMap|categoryId=<map
  category>|layoutId=0|hardwareGuid=00000000-…|hardwareIdentifier=WindowsRawInput<HID product without spaces><PID><VID>|duplicate=0`
  plus `…ControllerMap_KnownActionIds…`, keyboard/mouse maps, `CalibrationMap` per device, `InputBehavior`,
  `RewiredSaveData_ControllerAssignments` (JSON). The player is **`Player`** (Nuclear Option: `Player0`).
- **One value per device and map category.** The Control Mapper's tabs are Rewired map categories: 0 System, 3 Flight,
  8 Weapons, 11 Camera, 4 View Select, 9 Map UI (10 UI and 12 System (Hardcoded) are not assignable). The game saved a
  map of every connected device in each of the six when the mapper was first opened [OBS]. Which action category goes
  to which map category: Default → 0, Flight Control → 3, Weapon Control → 8, Camera Control → 11, View Control → 4,
  Map Control → 9 (the category ids and names are in the InputManager [SRC]; Flight → 3, Weapons → 8 and View Select → 4
  confirmed in game).
- **Unknown joysticks get no default binds**: every saved map of the user's 8 devices (VKB, VPC, WinWing, MFG, Rhino,
  Wooting, vJoy) was empty (`sourceMapId -1`) [OBS].
- Element ids (Rewired's Unknown Controller): axes 0-5 = X, Y, Z, Rx, Ry, Rz (the mapper shows "Axis <id>"), button n =
  element 31 + n (shown "Button <n-1>"), hat 1 Up = element 160 (shown "Hat 0 Up"); Right/Down/Left follow Rewired's
  order 161/162/163, hat h at 160 + 4(h-1). The mapper writes `axisRange 1` on button elements (Nuclear Option's default
  maps: 0); JoyPro writes 1 and compares buttons without it.
- The game's own control options (joystick curve, rudder curve, throttle speed, mouse flight) are `Settings\Controls.cfg`
  under `%USERPROFILE%\AppData\LocalLow\TinyCombat\Arena` or Steam Cloud [SRC `GameSettings`]; JoyPro does not write
  them. Steam Cloud holds `Audio/Camera/Gameplay/Hud.cfg` (`userdata\<id>\1347550\remote`), no binds.
- Anti-cheat: none.

## Catalog
`engine tca refresh-db [--path <game folder|Arena_Data|.assets file>]` reads the Rewired InputManager (in
`Arena_Data\sharedassets1.assets` in 0.17; the first data file that parses wins): 76 actions, int32 id, name, type,
three descriptive strings, 6 int32 (index 4 = userAssignable, 5 = action category), then the action categories with
four strings and int32 id, userAssignable (`RewiredLayout.TinyCombatArena`). The 11 actions the player cannot assign
(menu navigation, Hardpoint, Afterburner, the hard-coded Escape) are left out. Axis actions get `.Positive` /
`.Negative` pole rows titled with the game's pole names. The shipped `DB\TinyCombatArena\actions.tsv` (100 rows) was
generated that way from 0.17.2.3.

## Export rules
Flight-only game (`ExclusiveFlightSim`): clean releases every other assignable action on the written devices, in every
map category; nuke releases them on the other connected devices (one of a model) and gives devices without saved maps
empty ones (unknown joysticks have no default binds here, so that changes nothing the game does; Nuclear Option no
longer does it).
merge / merge-overwrite / add as in Nuclear Option. Keyboard binds and hat diagonals are not written; a device needs to
be connected (maps are keyed by its HID product and VID/PID); two devices of one model are skipped. Backup: the
player's controller-map values go into the backup store before every write. Close the game first: it saves all maps
when the mapper's Done is pressed.

## In-game check (`tools\e2e\games\TinyCombatArenaSettings.ps1`)
Random per run: Pitch/Roll/Yaw on three vJoy axes, Fire Gun and Cockpit on vJoy buttons, Throttle Up or Down on a
hat-1 direction; the mapper's vJoy tab shows them on Flight, Weapons and View Select; Throttle Axis is bound in the game
(field click, ~3 s centring timer, then the axis sweep); after the game quits, `engine tca import` reads all seven back.
First run 2026-10-11: 20/21 (OCR read "Hat O Up"; fixed in the script).

Menu facts: the pixel font is mostly unreadable by OCR, so the main menu and buttons are fixed points scaled from
3840x2160. A start after an update shows "NEW VERSION" (CONTINUE) and "Help make Tiny Combat Arena" (CONTINUE); the very
first start also asks Quick Game Setup and Head Tracking (answered JOYSTICK/HOTAS and NO on 2026-10-11). Settings >
SYSTEM has "CLEAR ALL SAVE DATA": the script never clicks there.

Open bug reports of the game: twist and throttle axes not registering on a Logitech Extreme 3D and a T.16000M (WEB);
those are Rewired-recognized devices (definition GUID instead of zeros), which JoyPro does not handle yet.
