# Fate of the Galaxy (FreeSpace Open)

Tags: **[SRC]** FreeSpace Open's source, release 26.0.1 (`code/controlconfig/controlsconfig.h`, `controlsconfigcommon.cpp`,
`presets.cpp`, `code/pilotfile/plr.cpp`, `csg.cpp`, `code/io/joy.h`, `joy-sdl.cpp`, `code/osapi/osregistry.cpp`, `osapi.cpp`)
· **[OBS]** seen in the files of a real installation (Fate of the Galaxy 2.0.2 on FSO 26.0.1 SDL3, portable Knossos.NET,
2026-11-21; read only, nothing started) · **[INF]** inferred · **[UNVERIFIED]** needs one check in the running game.

Fate of the Galaxy (FotG) is a Star Wars total conversion for FreeSpace Open (FSO), installed and started with Knossos.NET.
JoyPro supports it through a FreeSpace Open backend (`engine/Games/FreeSpaceOpenIntegration.cs`, `engine/Games/FreeSpaceOpen/`)
that reads the mod's own control table; other FSO mods could use the same backend later. CLI area: `engine fotg ...`
(`scan`, `import`, `export`, `catalog`, `refresh-db`), game id `FateOfTheGalaxy`.

## Files

| What | Where |
|---|---|
| Knossos portable | a `kn_portable` folder next to `Knossos.NET.exe`, on any drive (JoyPro looks at every fixed drive and two folder levels below it, plus Desktop, Documents, Downloads, Games) [OBS] |
| Mod (installation) | `<library>\FotG_2\FotG_2-<version>\` with `mod.json` and one folder per package (`FG_Core`, `FG_UI`, ...); the library is `kn_portable\Library` (portable) or the `base_path` of `%APPDATA%\KnossosNET\settings.json` (old Knossos: `%APPDATA%\knossos\settings.json`) [OBS, INF] |
| Control table | `FG_Core\data\tables\Controlconfigdefaults.tbl` (loose files; `.vp` archives of the package folders are read too) [OBS] |
| Menu texts | `data\tables\strings.tbl` and `*-lcl.tbm`, `#English` part (`str_fotg_1-lcl.tbm` renames controls) [OBS, SRC] |
| User data | `kn_portable\HardLightProductions\FreeSpaceOpen\` (portable: Knossos sets `FSO_PREFERENCES_PATH`), else `%APPDATA%\HardLightProductions\FreeSpaceOpen\` [OBS, SRC `osapi.cpp`] |
| Pilot | `data\players\<callsign>.json` (PLR version 4); `fs2_open.ini` `[Default] LastPlayer` names the last one [OBS, SRC] |
| Campaign save | `data\players\<callsign>.<campaign>.csg` (binary, CSG version 8) [OBS, SRC] |
| Controls preset | `data\players\presets\<name>.json`; JoyPro writes `JoyPro_FotG.json` [SRC] |
| Mod settings | `data\FotG_2_settings.ini` (the mod folder name without its version): `[Input] Joystick`, `Joystick1..3`, `JoystickSensitivity`, `JoystickDeadZone` [OBS, SRC `osregistry.cpp`] |

## How FSO keeps the controls [SRC]

- Since PLR 4 / CSG 7 the pilot and each campaign save only **name** a preset: pilot `sections.controls.preset`, CSG section
  `0x0004` (a NUL-terminated string). The pilot's old `controls` / `axes` arrays hold the defaults for old builds and are not
  read. When a campaign is loaded the game switches to the campaign save's preset, so the export sets the name in both.
- A preset file: `{"signature": 1599361872 (0x5f545350, "PST_"), "version": 0, "actions": [{"bind": "<IoActionId name>", "primary": {"cid", "flags",
  "input"}, "secondary": {...}}, ...]}`. A control the file does not list is unbound; JoyPro writes all 140 controls in id order
  as FSO's `save_preset_file` does (jansson, indent 4).
- `cid`: `CID_NONE`, `CID_KEYBOARD`, `CID_MOUSE`, `CID_JOY0` .. `CID_JOY3` (four devices). `flags`: `NONE` or `AXIS_BTN`,
  `RELATIVE`, `INVERTED`, `AXIS`, `HAT`, `BALL` joined by `, `. `input`: `KEY_T` with `ALT-` / `SHIFT-` in front (no Ctrl), a
  mouse button name, a joystick button `0..127`, the first hat as `128` back, `129` forward, `130` left, `131` right
  (`hatBtnToEnum`), an axis `X_AXIS .. V_AXIS`.
- Joystick axes count over the axes the device **has** (SDL's DirectInput order X, Y, Z, Rx, Ry, Rz, Slider, Slider): on pedals
  with X, Y and Rz the Rz axis is `Z_AXIS` [SRC SDL, UNVERIFIED on real hardware]. 128 buttons, one hat (no diagonals), 8 axes.
- Two places per control (primary, secondary). The export keeps keys and mouse binds and uses a free place or the place of
  the same device; when both hold something else the bind is not written and the export names what holds them.
- The game finds a preset only when its binds differ from every earlier one (the default, the table's presets, preset files
  before it by name); otherwise it drops it and the pilot falls back to the defaults. The export then names that other preset
  in the pilot instead.
- `controlconfigdefaults.tbl`: `#ControlConfigOverride` `$Name: default` replaces the default preset and sets the menu text
  (`$Text`, `$Has XStr`), the tab (`$Category`) and the switched-off flag (`+Disable` / `$Disable:`; an override item without it
  is on). Fate of the Galaxy switches off, among others, the afterburner, countermeasures, sliding thrust, autopilot and
  subspace navigation, and uses the custom controls for the targeting computer, mouse flight, S-foils and the stationary
  target mode [OBS]. Switched-off controls do nothing in the game; JoyPro's catalog leaves them out.

## Devices [SRC, OBS]

`[Input] Joystick={"guid":"<SDL GUID>","id":<device index>}` and `Joystick1..3` pick Device 1-4 (Joy-0..3). The SDL GUID is
bus `0x0003`, the CRC-16/ARC of `"<HID manufacturer> <HID product>"` (as the device reports them, e.g. `Wooting Wooting Two HE
(ARM)`, `VPforce Rhino FFB Joystick`), USB vendor id, 0, product id, 0, version 0 (little endian, upper-case hex). JoyPro builds
the same GUID for all four devices of the real installation [OBS 2026-11-21: `engine fotg scan` reports "SDL GUID built by
JoyPro: same" per slot]. Fate of the Galaxy fills the four slots with the connected devices the first time the game starts
(`y_graphics-sct.lua`, `_FGD_joysticks.txt`) and shows them in its options as Device 1-4. JoyPro matches a slot by vendor and
product id; among several interfaces or identical devices the one with inputs, then the name CRC, then the saved index
[UNVERIFIED for two identical sticks]. A device the profile uses that has no slot gets a free one (its GUID, id -1); with all
four slots taken the bind is skipped with a hint to pick the device in the game's options. Without the in-game options FSO reads `fs2_open.ini`
`[Default] Joy0GUID` / `Joy0ID` ...

## Axis settings [SRC]

- Per bind only `INVERTED`. Dead zone (0..45) and sensitivity (0..9) are one setting for all joystick axes: the pilot's
  `settings.joy_dead_zone_size` / `joy_sensitivity` (win when the pilot loads) and the mod settings' `JoystickDeadZone` /
  `JoystickSensitivity`. `engine ffb set --device * --game FateOfTheGalaxy --param deadZone=<0..45>` / `sensitivity=<0..9>`
  sets them; the export writes both places.
- Response (`joy_get_scaled_reading`): dead zone = size x 2 % of the half travel (10 = 20 %), the rest rescaled, then
  `p * s + p^5 * (1 - s)` with `s = sensitivity / 9`. The absolute throttle skips both: its raw travel 0..1 becomes -10..110 %
  and is clipped. Axis model `fso` (`engine/AxisModels/FreeSpaceOpen.cs`).
- Invert is JoyPro's direction: the throttle axes get `INVERTED` for a lever that reads low when pushed forward, as most HOTAS
  throttles do in DirectInput; stick axes are written as they are [INF from the source, UNVERIFIED in the game].

## Export, import, watcher

- Export (`engine fotg export [<pilot.json>|<user data>|<Knossos folder>]`): writes `presets\JoyPro_FotG.json`, sets the
  pilot's preset and the preset of its campaign saves (backups `.jpbak` first), and the mod settings for a new device slot or
  game settings. Clean takes over the devices it writes (all FSO controls are flight controls), nuke clears the other connected
  devices of the four, keys only with `--include-keyboard`. Close the game first: it writes the pilot when it exits.
- No pilot yet: the export writes the preset and says to pick `JoyPro_FotG` in the game's controls menu (or to export again
  after creating a pilot).
- Import / scan read the preset the pilot names (a preset file, a table preset or the mod's defaults).
- The config watcher watches the pilot and the mod's settings; when you change binds in the game, FSO saves them as a new
  preset you name, and the pilot names it.
- `engine fotg refresh-db [<Knossos or mod folder>]` rebuilds the catalog from the installed mod (the update check notices a
  new mod version or a changed control table).

## Auto Group, Auto Setup, Auto Assign, search

- Function table `DB/Functions/FateOfTheGalaxy.tsv` (space class, with Star Citizen, Elite Dangerous and X4). Besides the
  flight axes, weapons, targeting, energy and views it puts forward / reverse thrust (held, with X4's accelerate /
  decelerate; the throttle axis does the same) and the S-foils (`CUSTOM_CONTROL_3`, with Elite's hardpoints: weapons
  ready / travel) into the shared relations, both with frequency `last`: Auto Setup gives them an input only when no
  view, jump, comms or essential wants it ([setup-wizard.md](../setup-wizard.md)). `FateOfTheGalaxy.rejected.tsv` keeps the function map
  from putting FSO's held Reverse Thrust into Elite's reverse-throttle toggle. Set Throttle to Zero is the space brake /
  full stop of the search (like Elite's Set Speed to 0%).
- Not in the table on purpose (the setup eval measured it, same seed, head tracking): deselect target
  (`STOP_TARGETING_SHIP`) and untarget subsystem took the comms menu's and the view toggle's buttons on a 12-button
  stick, and augment forward / rear shield made the shields a set that needs a free hat, so small setups lost Equalize
  Shields too. The search still finds them (function map: radar unlock, subsystem reset, shield front / back).
- No JoyPro shift layers for relations with a FotG action (`EngineGameSupport.ModifierSupport` = no): FSO has no
  joystick modifiers, so a shifted bind could not be written. Keys keep Alt / Shift. This holds for the snap views'
  and the flares' shifted-hat fallbacks too (they skipped the check before; it also kept X-Plane and Arma binds off
  shift layers), and the setup eval's `modifiers` gate fails any shifted bind in such a game.
- The axis curve preview (GUI and engine) draws with the game's dead zone and sensitivity: `binds tuning show` sends
  them as `GameSettings`, the model reads them through `AxisModels.GameSetting`.
- Left out of the shared relations on purpose (their own relation by hand or the relation editor): the friendly,
  cargo, turret, bomber, escort and "newest / last sender / target's target / nearest pursuer" target cycles (one
  action per slot: the hostile and next / previous target take the hat), throttle steps (max, 1/3, 2/3), energy
  decrease, firing-rate cycles, auto targeting / auto speed matching toggles, the targeting computer, mouse flight mode,
  stationary target mode, active pause, floodlight, HUD toggles, the wingman orders other than the comms menu and
  "engage", the comms menu keys, free look, camera lock / distance / target view, time compression, radar range cycle
  and the multiplayer messages. No other space game of the table has a matching function, or the action is a menu key.

## Unverified (needs a real file or one game start)

- That FSO 26.0.1 SDL3 loads the written preset and the pilot switches to it (format from the source; checked against the
  pilot and campaign save of a real installation, but no preset file of a real installation exists yet).
- The axis numbering on real devices without all eight axes, and the throttle direction.
- That the game takes a device JoyPro put into a free slot (the GUID itself matches the game's own for four real devices).
- Two identical devices.
